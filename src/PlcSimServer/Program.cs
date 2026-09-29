using System;
using System.Collections.Concurrent;
using System.Windows.Forms;
using PlcSimServer.Model;
using PlcSimServer.Opc;
using PlcSimServer.Simulation;
using PlcSimServer.UI;

namespace PlcSimServer
{
    internal static class Program
    {
        /// <summary>
        /// PLC Sim Server — servidor OPC UA con ventana para el Taller OPC UA de ICSH.
        /// Modelo (PlantModel) ← simulación + ventana + escrituras OPC → publicado por OPC UA.
        /// </summary>
        [STAThread]
        private static void Main()
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var logQueue = new ConcurrentQueue<string>();
            var model = new PlantModel();
            using var engine = new SimulationEngine(model);
            var host = new OpcHost(model, logQueue);

            Application.Run(new MainForm(model, engine, host, logQueue));
        }
    }
}
