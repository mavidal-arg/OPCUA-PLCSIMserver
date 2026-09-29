using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Configuration;
using PlcSimServer.Model;

namespace PlcSimServer.Opc
{
    /// <summary>
    /// Ciclo de vida del servidor OPC UA: carga de configuración, certificado self-signed,
    /// arranque y parada. Adaptado de Applications/ConsoleReferenceServer/UAServer.cs del SDK.
    /// Permite detener y volver a iniciar el servidor desde la ventana (ejercicio de corte de
    /// comunicación para el laboratorio).
    /// </summary>
    public sealed class OpcHost : IAsyncDisposable
    {
        private const string ConfigFile = "PlcSimServer.Config.xml";

        private readonly PlantModel m_model;
        private readonly ITelemetryContext m_telemetry;
        private ApplicationInstance? m_application;
        private PlcServer? m_server;

        public OpcHost(PlantModel model, ConcurrentQueue<string> logQueue)
        {
            m_model = model;
            m_telemetry = DefaultTelemetry.Create(builder =>
            {
                // Solo advertencias/errores del SDK: los eventos de interés (escrituras, sesiones)
                // los reporta el propio modelo al log de la ventana.
                builder.SetMinimumLevel(LogLevel.Warning);
                builder.AddProvider(new QueueLoggerProvider(logQueue));
            });
        }

        public bool IsRunning => m_server != null;

        public string[] Endpoints { get; private set; } = [];

        public int SessionCount
        {
            get
            {
                try
                {
                    return m_server?.CurrentInstance.SessionManager.GetSessions().Count ?? 0;
                }
                catch (Exception)
                {
                    return 0;
                }
            }
        }

        public async Task StartAsync()
        {
            if (m_server != null)
            {
                return;
            }

            if (m_application == null)
            {
                ApplicationInstance.MessageDlg = new SilentMessageDlg();
                m_application = new ApplicationInstance(m_telemetry)
                {
                    ApplicationName = "PLC Sim Server",
                    ApplicationType = ApplicationType.Server,
                    ConfigSectionName = "PlcSimServer"
                };
                string path = Path.Combine(AppContext.BaseDirectory, ConfigFile);
                await m_application.LoadApplicationConfigurationAsync(path, false).ConfigureAwait(false);

                // Crea el certificado de aplicación self-signed la primera vez (en %LocalAppData%\PlcSimServer\pki).
                bool ok = await m_application.CheckApplicationInstanceCertificatesAsync(false).ConfigureAwait(false);
                if (!ok)
                {
                    throw new InvalidOperationException("Certificado de aplicación inválido.");
                }
            }

            // Se crea una instancia nueva en cada arranque: un StandardServer detenido no se reutiliza.
            var server = new PlcServer(m_model);
            await m_application.StartAsync(server).ConfigureAwait(false);
            m_server = server;
            Endpoints = server.GetEndpoints().Select(e => $"{e.EndpointUrl}  [{e.SecurityMode}, {SecurityPolicies.GetDisplayName(e.SecurityPolicyUri)}]").Distinct().ToArray();
        }

        public async Task StopAsync()
        {
            PlcServer? server = m_server;
            if (server == null)
            {
                return;
            }
            m_server = null;
            await server.StopAsync().ConfigureAwait(false);
            server.Dispose();
        }

        /// <summary>Publica en OPC UA los cambios del modelo (llamado después de cada ciclo de simulación).</summary>
        public void Publish()
        {
            try
            {
                m_server?.NodeManager?.SyncFromModel();
            }
            catch (ObjectDisposedException)
            {
                // carrera normal durante StopAsync
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            (m_telemetry as IDisposable)?.Dispose();
        }

        /// <summary>Responde "sí" a las preguntas del SDK (p.ej. crear certificado) sin mostrar diálogos.</summary>
        private sealed class SilentMessageDlg : IApplicationMessageDlg
        {
            public override void Message(string text, bool ask) { }
            public override Task<bool> ShowAsync() => Task.FromResult(true);
        }

        /// <summary>Logger mínimo que encola los mensajes para el panel de log de la ventana.</summary>
        private sealed class QueueLoggerProvider(ConcurrentQueue<string> queue) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new QueueLogger(queue, categoryName);
            public void Dispose() { }
        }

        private sealed class QueueLogger(ConcurrentQueue<string> queue, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string shortCategory = category[(category.LastIndexOf('.') + 1)..];
                string level = logLevel >= LogLevel.Warning ? logLevel.ToString().ToUpperInvariant() + " " : "";
                queue.Enqueue($"[SDK {shortCategory}] {level}{formatter(state, exception)}{(exception != null ? " - " + exception.Message : "")}");
            }
        }
    }
}
