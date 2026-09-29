using System;
using System.Threading;
using PlcSimServer.Model;

namespace PlcSimServer.Simulation
{
    /// <summary>
    /// Lógica de proceso del PLC simulado. Corre cada <see cref="TickMs"/> ms en un hilo del pool:
    /// motores con rampa, lazo de temperatura de primer orden con PI, interruptores MT con
    /// enclavamiento e inversor solar con curva diaria sintética.
    /// </summary>
    public sealed class SimulationEngine : IDisposable
    {
        public const int TickMs = 250;

        private const double MotorRampRpmPerSec = 300;
        private const double NominalKv = 23.0;
        private const double InverterNominalKw = 100.0;
        private const double AmbientC = 25.0;
        private const double TempTauSec = 20.0;

        private readonly PlantModel m_model;
        private readonly Random m_rng = new();
        private readonly Timer m_timer;
        private readonly DateTime m_start = DateTime.UtcNow;
        private double m_piIntegral;
        private int m_busy;

        public SimulationEngine(PlantModel model)
        {
            m_model = model;
            m_model.Interlock = CheckInterlock;
            m_timer = new Timer(_ => OnTick(), null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>Si está en pausa, los valores quedan congelados pero se siguen publicando.</summary>
        public bool Paused { get; set; }

        /// <summary>Duración de un "día solar" simulado, en segundos (default 10 min).</summary>
        public double SolarDaySeconds { get; set; } = 600;

        /// <summary>Hora simulada 0..24 del inversor.</summary>
        public double SolarHour { get; private set; }

        /// <summary>Se dispara después de cada ciclo (con o sin pausa); el servidor OPC publica acá.</summary>
        public event Action? Ticked;

        public void Start() => m_timer.Change(0, TickMs);

        public void Dispose() => m_timer.Dispose();

        private void OnTick()
        {
            // Evita solapamiento si un ciclo tarda más que el período.
            if (Interlocked.Exchange(ref m_busy, 1) == 1)
            {
                return;
            }
            try
            {
                if (!Paused)
                {
                    Step(TickMs / 1000.0);
                }
                Ticked?.Invoke();
            }
            finally
            {
                Interlocked.Exchange(ref m_busy, 0);
            }
        }

        private void Step(double dt)
        {
            StepMotor(PlantModel.Motor1, 1480, dt);
            StepMotor(PlantModel.Motor2, 1780, dt);
            StepTemperatureLoop(dt);
            StepBreakers();
            StepInverter();
        }

        // ---------------------------------------------------------------- Motores

        private void StepMotor(string m, double nominalRpm, double dt)
        {
            bool fault = m_model.GetBool(m + ".Falla");
            if (fault && m_model.GetBool(m + ".Comando_Run"))
            {
                // Disparo por falla: el PLC baja el comando, como haría un relé de protección.
                m_model.SetSim(m + ".Comando_Run", false);
            }

            bool run = !fault && m_model.GetBool(m + ".Comando_Run");
            double rpm = m_model.GetDouble(m + ".RPM");
            double target = run ? nominalRpm : 0;
            double step = MotorRampRpmPerSec * dt * (run ? 1 : 1.5);
            rpm = rpm < target ? Math.Min(target, rpm + step) : Math.Max(target, rpm - step);

            // Corriente: vacío + carga proporcional a la velocidad + pico mientras acelera.
            bool accelerating = run && rpm < nominalRpm - 1;
            double current = rpm <= 0.5 ? 0 : 8 + 34 * (rpm / nominalRpm) + (accelerating ? 70 * (1 - rpm / nominalRpm) : 0);

            m_model.SetSim(m + ".RPM", Math.Round(rpm + (rpm > 0 && !accelerating ? Noise(2) : 0), 1));
            m_model.SetSim(m + ".Corriente_A", Math.Round(current + (current > 0 ? Noise(0.4) : 0), 2));
            m_model.SetSim(m + ".Status", fault ? "Falla" : rpm > 5 ? "Run" : "Stop");
        }

        // ---------------------------------------------------------------- Lazo de temperatura

        private void StepTemperatureLoop(double dt)
        {
            const string l = PlantModel.LazoTemp;
            string mode = m_model.GetString(l + ".Modo_Cmd");
            m_model.SetSim(l + ".Modo", mode);

            double pv = m_model.GetDouble(l + ".PV_Temp_C");
            double sp = m_model.GetDouble(l + ".SP_Temp_C");
            double valve = m_model.GetDouble(l + ".Salida_Valvula_Pct");

            if (mode == "Auto")
            {
                // PI con anti-windup (clamp de la integral).
                const double kp = 2.0, ki = 0.12;
                double error = sp - pv;
                m_piIntegral = Math.Clamp(m_piIntegral + error * dt, -100 / ki, 100 / ki);
                valve = Math.Clamp(kp * error + ki * m_piIntegral, 0, 100);
                m_model.SetSim(l + ".Salida_Valvula_Pct", Math.Round(valve, 1));
            }
            else
            {
                // Bumpless: al volver a Auto el PI arranca desde la válvula actual.
                m_piIntegral = valve / 0.12;
            }

            // Planta de primer orden: 0 % válvula → ambiente, 100 % → 145 °C.
            double steadyState = AmbientC + 1.2 * valve;
            pv += (steadyState - pv) * dt / TempTauSec;
            m_model.SetSim(l + ".PV_Temp_C", Math.Round(pv + Noise(0.05), 2));
        }

        // ---------------------------------------------------------------- Interruptores MT

        private void StepBreakers()
        {
            bool mt1Closed = m_model.GetBool(PlantModel.MT1 + ".Comando_Open_Close");
            bool mt2Closed = m_model.GetBool(PlantModel.MT2 + ".Comando_Open_Close");

            // Enclavamiento en cascada: si abre MT1, MT2 abre también.
            if (!mt1Closed && mt2Closed)
            {
                m_model.SetSim(PlantModel.MT2 + ".Comando_Open_Close", false);
                mt2Closed = false;
            }

            // MT1: alimentador de carga (motores + carga base).
            double loadA = mt1Closed
                ? 45 + 0.35 * (m_model.GetDouble(PlantModel.Motor1 + ".Corriente_A") + m_model.GetDouble(PlantModel.Motor2 + ".Corriente_A"))
                : 0;
            UpdateBreaker(PlantModel.MT1, mt1Closed, NominalKv + Noise(0.08), loadA, 0.92);

            // MT2: alimentador del inversor solar (se completa en StepInverter con la potencia real).
            double solarKw = m_model.GetDouble(PlantModel.Inversor + ".AC_Power_kW");
            double kv2 = mt1Closed ? NominalKv + Noise(0.08) : 0;
            double solarA = mt2Closed && kv2 > 0 ? solarKw / (Math.Sqrt(3) * kv2 * 0.98) : 0;
            UpdateBreaker(PlantModel.MT2, mt2Closed, kv2, solarA, 0.98);
        }

        private void UpdateBreaker(string mt, bool closed, double kv, double amps, double pf)
        {
            m_model.SetSim(mt + ".Posicion", closed ? "Closed" : "Open");
            m_model.SetSim(mt + ".kV", Math.Round(Math.Max(0, kv), 2));
            m_model.SetSim(mt + ".A", Math.Round(amps, 2));
            m_model.SetSim(mt + ".MW", Math.Round(Math.Sqrt(3) * kv * amps * pf / 1000.0, 3));
        }

        // ---------------------------------------------------------------- Inversor solar

        private void StepInverter()
        {
            const string i = PlantModel.Inversor;
            double dayFraction = (DateTime.UtcNow - m_start).TotalSeconds % SolarDaySeconds / SolarDaySeconds;
            SolarHour = dayFraction * 24;

            // Irradiancia: campana entre las 06:00 y las 18:00.
            double irradiance = dayFraction is > 0.25 and < 0.75
                ? Math.Sin(Math.PI * (dayFraction - 0.25) / 0.5)
                : 0;
            irradiance = Math.Max(0, irradiance * (1 + Noise(0.03)));

            bool fault = m_model.GetBool(i + ".Falla");
            bool gridOk = m_model.GetString(PlantModel.MT2 + ".Posicion") == "Closed"; // anti-isla
            double curtailment = m_model.GetDouble(i + ".Curtailment_Setpoint_Pct");

            double available = InverterNominalKw * irradiance;
            double acKw = fault || !gridOk ? 0 : Math.Min(available, InverterNominalKw * curtailment / 100.0);
            double load = acKw / InverterNominalKw;
            double efficiency = acKw > 0.5 ? 94.5 + 3.0 * Math.Sqrt(load) + Noise(0.1) : 0;
            double dcV = irradiance > 0.02 ? 580 + 70 * irradiance + Noise(1.5) : 0;
            double dcKw = efficiency > 0 ? acKw / (efficiency / 100.0) : 0;
            double dcA = dcV > 0 ? dcKw * 1000 / dcV : 0;

            m_model.SetSim(i + ".DC_V", Math.Round(dcV, 1));
            m_model.SetSim(i + ".DC_A", Math.Round(dcA, 2));
            m_model.SetSim(i + ".AC_Power_kW", Math.Round(acKw, 2));
            m_model.SetSim(i + ".Eficiencia_Pct", Math.Round(efficiency, 2));
        }

        // ---------------------------------------------------------------- Enclavamientos

        private string? CheckInterlock(string path, object value, WriteSource source)
        {
            if (path == PlantModel.MT2 + ".Comando_Open_Close" && value is true &&
                !m_model.GetBool(PlantModel.MT1 + ".Comando_Open_Close"))
            {
                return "enclavamiento: MT2 no puede cerrar con MT1 abierto";
            }

            if (path == PlantModel.LazoTemp + ".Salida_Valvula_Pct" && source != WriteSource.Sim &&
                m_model.GetString(PlantModel.LazoTemp + ".Modo_Cmd") == "Auto")
            {
                return "la válvula solo se opera en modo Manual";
            }

            return null;
        }

        private double Noise(double amplitude) => (m_rng.NextDouble() * 2 - 1) * amplitude;
    }
}
