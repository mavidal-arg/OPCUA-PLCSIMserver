using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace PlcSimServer.TestClient
{
    /// <summary>
    /// Cliente de prueba del PLC Sim Server (basado en Applications/ConsoleReferenceClient del SDK).
    ///
    ///   dotnet run -- [endpoint] [--user lab --password lab2026] [--watch segundos]
    ///
    /// Sin --watch: browse de las carpetas, lectura de todos los tags y prueba de escritura de cada
    /// variable RW (incluye enclavamiento MT2 y valor fuera de rango). Devuelve 0 si todo pasa.
    /// Con --watch: se suscribe a todos los tags e imprime los cambios (para ver lo que se mueve en la ventana).
    /// </summary>
    internal static class Program
    {
        private const string NamespaceUri = "urn:icsh:lab:plc-sim";
        private static int s_failures;
        private static ushort s_ns;

        private static async Task<int> Main(string[] args)
        {
            string endpointUrl = args.FirstOrDefault(a => a.StartsWith("opc.tcp://", StringComparison.Ordinal)) ?? "opc.tcp://localhost:4840/plc-sim";
            string? user = Arg(args, "--user");
            string? password = Arg(args, "--password");
            int watchSeconds = int.TryParse(Arg(args, "--watch"), out int w) ? w : 0;

            ITelemetryContext telemetry = DefaultTelemetry.Create(b => b.SetMinimumLevel(LogLevel.Warning));

            var application = new ApplicationInstance(telemetry)
            {
                ApplicationName = "PLC Sim TestClient",
                ApplicationType = ApplicationType.Client
            };
            string pki = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlcSimServer", "testclient-pki");
#pragma warning disable CS0618 // sobrecarga simple suficiente para un cliente de prueba
            ApplicationConfiguration config = await application
                .Build("urn:localhost:ICSH:PlcSimTestClient", "urn:icsh:lab:plc-sim-testclient")
                .AsClient()
                .AddSecurityConfiguration("CN=PLC Sim TestClient, O=ICSH", pki)
                .SetAutoAcceptUntrustedCertificates(true)
                .CreateAsync();
#pragma warning restore CS0618
            await application.CheckApplicationInstanceCertificatesAsync(false);

            Console.WriteLine($"Conectando a {endpointUrl} (seguridad: None, usuario: {user ?? "Anonymous"})...");
            EndpointDescription endpointDescription = await CoreClientUtils.SelectEndpointAsync(config, endpointUrl, false, telemetry)
                ?? throw new InvalidOperationException("El servidor no ofrece un endpoint sin seguridad.");
            var endpoint = new ConfiguredEndpoint(null, endpointDescription, EndpointConfiguration.Create(config));
            IUserIdentity identity = user != null
                ? new UserIdentity(user, System.Text.Encoding.UTF8.GetBytes(password ?? ""))
                : new UserIdentity();

            using ISession session = await new DefaultSessionFactory(telemetry)
                .CreateAsync(config, endpoint, false, false, "PlcSimTestClient", 60000, identity, null);

            s_ns = (ushort)session.NamespaceUris.GetIndex(NamespaceUri);
            Console.WriteLine($"Conectado. Namespace '{NamespaceUri}' = ns={s_ns}");

            if (watchSeconds > 0)
            {
                await WatchAsync(session, watchSeconds);
            }
            else
            {
                await BrowseAndReadAsync(session);
                await WriteTestsAsync(session);
                Console.WriteLine();
                Console.WriteLine(s_failures == 0 ? "RESULTADO: OK — todas las pruebas pasaron." : $"RESULTADO: {s_failures} prueba(s) fallaron.");
            }

            await session.CloseAsync();
            return s_failures == 0 ? 0 : 1;
        }

        private static NodeId Id(string path) => new(path, s_ns);

        private static async Task BrowseAndReadAsync(ISession session)
        {
            var browser = new Browser(session)
            {
                BrowseDirection = BrowseDirection.Forward,
                NodeClassMask = (int)NodeClass.Object | (int)NodeClass.Variable,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true
            };

            Console.WriteLine("\n== Browse Objects/PlcSim ==");
            ReferenceDescriptionCollection folders = await browser.BrowseAsync(Id("PlcSim"));
            Check(folders.Count == 6, $"6 carpetas de equipo (encontradas {folders.Count})");

            foreach (ReferenceDescription folder in folders)
            {
                Console.WriteLine($"[{folder.DisplayName.Text}]");
                ReferenceDescriptionCollection variables = await browser.BrowseAsync(ExpandedNodeId.ToNodeId(folder.NodeId, session.NamespaceUris));
                foreach (ReferenceDescription v in variables)
                {
                    var nodeId = ExpandedNodeId.ToNodeId(v.NodeId, session.NamespaceUris);
                    DataValue dv = await session.ReadValueAsync(nodeId);
                    Check(StatusCode.IsGood(dv.StatusCode), $"lectura {nodeId}", quiet: true);
                    Console.WriteLine($"   {nodeId,-45} = {dv.Value}");
                }
            }
        }

        private static async Task WriteTestsAsync(ISession session)
        {
            Console.WriteLine("\n== Escrituras ==");

            // Motor: arranque y verificación de rampa
            await ExpectWrite(session, "Motor1.Comando_Run", true, StatusCodes.Good);
            await Task.Delay(1500);
            double rpm = Convert.ToDouble((await session.ReadValueAsync(Id("Motor1.RPM"))).Value);
            Check(rpm > 100, $"Motor1 acelera después de Comando_Run=true (RPM={rpm:0})");
            Check((string)(await session.ReadValueAsync(Id("Motor1.Status"))).Value == "Run", "Motor1.Status = Run");
            await ExpectWrite(session, "Motor1.Comando_Run", false, StatusCodes.Good);
            await ExpectWrite(session, "Motor2.Comando_Run", false, StatusCodes.Good);

            // Variables de solo lectura
            await ExpectWrite(session, "Motor1.RPM", 1000.0, StatusCodes.BadNotWritable);

            // Lazo de temperatura
            await ExpectWrite(session, "LazoTemp.SP_Temp_C", 75.0, StatusCodes.Good);
            await ExpectWrite(session, "LazoTemp.SP_Temp_C", 500.0, StatusCodes.BadOutOfRange);
            await ExpectWrite(session, "LazoTemp.Modo_Cmd", "Auto", StatusCodes.Good);
            await ExpectWrite(session, "LazoTemp.Salida_Valvula_Pct", 40.0, StatusCodes.BadInvalidState); // en Auto no se escribe
            await ExpectWrite(session, "LazoTemp.Modo_Cmd", "Manual", StatusCodes.Good);
            await ExpectWrite(session, "LazoTemp.Salida_Valvula_Pct", 40.0, StatusCodes.Good);
            await ExpectWrite(session, "LazoTemp.Modo_Cmd", "Cualquiera", StatusCodes.BadOutOfRange);
            await ExpectWrite(session, "LazoTemp.Modo_Cmd", "Auto", StatusCodes.Good);
            await ExpectWrite(session, "LazoTemp.SP_Temp_C", 60.0, StatusCodes.Good);

            // Interruptores MT + enclavamiento
            await ExpectWrite(session, "MT1.Comando_Open_Close", false, StatusCodes.Good);
            await Task.Delay(600);
            await ExpectWrite(session, "MT2.Comando_Open_Close", true, StatusCodes.BadInvalidState); // MT1 abierto
            await ExpectWrite(session, "MT1.Comando_Open_Close", true, StatusCodes.Good);
            await ExpectWrite(session, "MT2.Comando_Open_Close", true, StatusCodes.Good);
            await Task.Delay(600);
            Check((string)(await session.ReadValueAsync(Id("MT2.Posicion"))).Value == "Closed", "MT2.Posicion = Closed");

            // Inversor
            await ExpectWrite(session, "InversorSolar.Curtailment_Setpoint_Pct", 50.0, StatusCodes.Good);
            await ExpectWrite(session, "InversorSolar.Curtailment_Setpoint_Pct", 120.0, StatusCodes.BadOutOfRange);
            await ExpectWrite(session, "InversorSolar.Curtailment_Setpoint_Pct", 100.0, StatusCodes.Good);

            // Tipo incorrecto (el SDK valida el DataType antes de llegar al modelo)
            await ExpectWrite(session, "Motor1.Comando_Run", 1.0, StatusCodes.BadTypeMismatch);
        }

        private static async Task ExpectWrite(ISession session, string path, object value, StatusCode expected)
        {
            var nodes = new WriteValueCollection
            {
                new WriteValue { NodeId = Id(path), AttributeId = Attributes.Value, Value = new DataValue(new Variant(value)) }
            };
            WriteResponse response = await session.WriteAsync(null, nodes, default);
            StatusCode result = response.Results[0];
            Check(result.Code == expected.Code, $"write {path} = {value} → {StatusCodes.GetBrowseName(result.Code)} (esperado {StatusCodes.GetBrowseName(expected.Code)})");
        }

        private static async Task WatchAsync(ISession session, int seconds)
        {
            var subscription = new Subscription(session.DefaultSubscription)
            {
                DisplayName = "PlcSim watch",
                PublishingEnabled = true,
                PublishingInterval = 500,
                KeepAliveCount = 10
            };
            session.AddSubscription(subscription);
            await subscription.CreateAsync();

            var browser = new Browser(session) { NodeClassMask = (int)NodeClass.Object | (int)NodeClass.Variable, ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences };
            foreach (ReferenceDescription folder in await browser.BrowseAsync(Id("PlcSim")))
            {
                foreach (ReferenceDescription v in await browser.BrowseAsync(ExpandedNodeId.ToNodeId(folder.NodeId, session.NamespaceUris)))
                {
                    var item = new MonitoredItem(subscription.DefaultItem)
                    {
                        StartNodeId = ExpandedNodeId.ToNodeId(v.NodeId, session.NamespaceUris),
                        AttributeId = Attributes.Value,
                        SamplingInterval = 250,
                        QueueSize = 10
                    };
                    item.Notification += (mi, e) =>
                    {
                        if (e.NotificationValue is MonitoredItemNotification n)
                        {
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {mi.StartNodeId,-45} = {n.Value.Value}");
                        }
                    };
                    subscription.AddItem(item);
                }
            }
            await subscription.ApplyChangesAsync();
            Console.WriteLine($"Suscripto a {subscription.MonitoredItemCount} tags durante {seconds} s...");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }

        private static void Check(bool ok, string description, bool quiet = false)
        {
            if (!ok) s_failures++;
            if (!ok || !quiet) Console.WriteLine($"  [{(ok ? "OK" : "FALLA")}] {description}");
        }

        private static string? Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
