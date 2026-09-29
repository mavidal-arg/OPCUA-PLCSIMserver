using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using PlcSimServer.Model;
using PlcSimServer.Opc;
using PlcSimServer.Simulation;

namespace PlcSimServer.UI
{
    /// <summary>
    /// Ventana principal: una pestaña de resumen con todos los tags y una pestaña por equipo para
    /// operar el PLC simulado a mano. Todo lo que se cambia acá se publica por OPC UA y lo ve el
    /// cliente (Node-RED → MQTT → HMI del Tier0).
    ///
    /// La UI nunca toca el modelo desde otros hilos: un timer de WinForms (250 ms) lee el modelo
    /// y refresca los controles; las escrituras de la UI van por PlantModel.Write.
    /// </summary>
    public sealed partial class MainForm : Form
    {
        private const int MaxLogLines = 1000;

        private readonly PlantModel m_model;
        private readonly SimulationEngine m_engine;
        private readonly OpcHost m_host;
        private readonly ConcurrentQueue<string> m_logQueue;
        private readonly System.Windows.Forms.Timer m_uiTimer = new() { Interval = SimulationEngine.TickMs };

        /// <summary>Acciones de refresco registradas por cada pestaña; se ejecutan en cada tick de UI.</summary>
        private readonly List<Action> m_refreshers = [];

        /// <summary>true mientras se actualizan controles desde el modelo (evita re-escribir).</summary>
        private bool m_updating;

        private readonly Label m_serverState = new() { AutoSize = true, Padding = new Padding(8, 4, 8, 4), Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        private readonly Button m_btnServer = new() { AutoSize = true, Text = "Detener servidor" };
        private readonly Button m_btnPause = new() { AutoSize = true, Text = "Pausar simulación" };
        private readonly Label m_sessions = new() { AutoSize = true, Padding = new Padding(8, 8, 8, 0) };
        private readonly ListBox m_log = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), IntegralHeight = false, HorizontalScrollbar = true };
        private readonly ToolStripStatusLabel m_endpoint = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

        public MainForm(PlantModel model, SimulationEngine engine, OpcHost host, ConcurrentQueue<string> logQueue)
        {
            m_model = model;
            m_engine = engine;
            m_host = host;
            m_logQueue = logQueue;

            Text = "PLC Sim Server — Taller OPC UA ICSH";
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(1100, 780);
            MinimumSize = new Size(900, 640);
            StartPosition = FormStartPosition.CenterScreen;

            BuildLayout();

            m_uiTimer.Tick += (_, _) => RefreshUi();
            Load += async (_, _) => await OnLoadAsync();
            FormClosing += OnFormClosing;
        }

        private void BuildLayout()
        {
            // --- barra superior
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = false };
            m_btnServer.Click += async (_, _) => await ToggleServerAsync();
            m_btnPause.Click += (_, _) =>
            {
                m_engine.Paused = !m_engine.Paused;
                m_btnPause.Text = m_engine.Paused ? "Reanudar simulación" : "Pausar simulación";
                Log(m_engine.Paused ? "[UI] simulación en pausa (los valores quedan congelados pero se siguen publicando)" : "[UI] simulación reanudada");
            };
            top.Controls.AddRange([m_serverState, m_btnServer, m_btnPause, m_sessions]);

            // --- pestañas
            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            tabs.TabPages.Add(BuildSummaryTab());
            tabs.TabPages.Add(BuildMotorTab(PlantModel.Motor1, 1480));
            tabs.TabPages.Add(BuildMotorTab(PlantModel.Motor2, 1780));
            tabs.TabPages.Add(BuildLoopTab());
            tabs.TabPages.Add(BuildBreakerTab(PlantModel.MT1));
            tabs.TabPages.Add(BuildBreakerTab(PlantModel.MT2));
            tabs.TabPages.Add(BuildInverterTab());

            // --- log
            var logGroup = new GroupBox { Text = "Log (escrituras OPC / UI, sesiones, eventos del servidor)", Dock = DockStyle.Fill };
            logGroup.Controls.Add(m_log);
            var clear = new Button { Text = "Limpiar log", AutoSize = true, Dock = DockStyle.Right };
            clear.Click += (_, _) => m_log.Items.Clear();
            var logTools = new Panel { Dock = DockStyle.Top, Height = 30 };
            logTools.Controls.Add(clear);
            logGroup.Controls.Add(logTools);
            logTools.BringToFront();
            m_log.BringToFront();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(tabs);
            split.Panel2.Controls.Add(logGroup);

            var status = new StatusStrip();
            status.Items.Add(m_endpoint);

            Controls.Add(split);
            Controls.Add(top);
            Controls.Add(status);

            Shown += (_, _) => split.SplitterDistance = Math.Max(300, split.Height - 190);
        }

        private async Task OnLoadAsync()
        {
            m_model.Log += Log;
            m_engine.Ticked += m_host.Publish;
            m_engine.Start();
            m_uiTimer.Start();
            await StartServerAsync();
        }

        private async Task StartServerAsync()
        {
            m_btnServer.Enabled = false;
            try
            {
                Log("[UI] iniciando servidor OPC UA...");
                await m_host.StartAsync();
                foreach (string ep in m_host.Endpoints)
                {
                    Log("[OPC] escuchando en " + ep);
                }
            }
            catch (Exception ex)
            {
                Log("[OPC] ERROR al iniciar: " + ex.Message);
                MessageBox.Show(this,
                    "No se pudo iniciar el servidor OPC UA:\n\n" + ex.Message +
                    "\n\n¿Hay otro servidor usando el puerto 4840?",
                    "PLC Sim Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                m_btnServer.Enabled = true;
            }
        }

        private async Task ToggleServerAsync()
        {
            if (m_host.IsRunning)
            {
                m_btnServer.Enabled = false;
                Log("[UI] deteniendo servidor OPC UA (los clientes pierden la conexión)...");
                try
                {
                    await m_host.StopAsync();
                    Log("[OPC] servidor detenido");
                }
                catch (Exception ex)
                {
                    Log("[OPC] ERROR al detener: " + ex.Message);
                }
                finally
                {
                    m_btnServer.Enabled = true;
                }
            }
            else
            {
                await StartServerAsync();
            }
        }

        private void OnFormClosing(object? sender, FormClosingEventArgs e)
        {
            m_uiTimer.Stop();
            m_engine.Ticked -= m_host.Publish;
            m_model.Log -= Log;
            m_engine.Dispose();
            // Parada ordenada del servidor (avisa a los clientes) con un límite de tiempo.
            Task.Run(async () => await m_host.DisposeAsync()).Wait(TimeSpan.FromSeconds(5));
        }

        private void Log(string message) => m_logQueue.Enqueue(message);

        private void RefreshUi()
        {
            // log
            if (!m_logQueue.IsEmpty)
            {
                m_log.BeginUpdate();
                while (m_logQueue.TryDequeue(out string? line))
                {
                    m_log.Items.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
                }
                while (m_log.Items.Count > MaxLogLines)
                {
                    m_log.Items.RemoveAt(0);
                }
                m_log.TopIndex = m_log.Items.Count - 1;
                m_log.EndUpdate();
            }

            // estado del servidor
            bool running = m_host.IsRunning;
            m_serverState.Text = running ? "● SERVIDOR EN LÍNEA" : "● SERVIDOR DETENIDO";
            m_serverState.BackColor = running ? Color.FromArgb(0xD7, 0xF5, 0xDD) : Color.FromArgb(0xFA, 0xD4, 0xD4);
            m_serverState.ForeColor = running ? Color.DarkGreen : Color.DarkRed;
            m_btnServer.Text = running ? "Detener servidor" : "Iniciar servidor";
            m_sessions.Text = $"Sesiones OPC: {m_host.SessionCount}    Hora solar simulada: {TimeSpan.FromHours(m_engine.SolarHour):hh\\:mm}";
            m_endpoint.Text = running && m_host.Endpoints.Length > 0
                ? "Endpoints: " + string.Join("   |   ", m_host.Endpoints)
                : "Servidor detenido";

            m_updating = true;
            try
            {
                foreach (Action refresh in m_refreshers)
                {
                    refresh();
                }
            }
            finally
            {
                m_updating = false;
            }
        }

        /// <summary>Escritura desde la UI; muestra el rechazo si un enclavamiento lo impide.</summary>
        private void UiWrite(string path, object value)
        {
            if (m_updating)
            {
                return;
            }
            WriteResult result = m_model.Write(path, value, WriteSource.Ui);
            if (result != WriteResult.Ok)
            {
                System.Media.SystemSounds.Exclamation.Play();
            }
        }
    }
}
