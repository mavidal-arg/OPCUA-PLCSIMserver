using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;
using PlcSimServer.Model;

namespace PlcSimServer.Opc
{
    /// <summary>
    /// Servidor OPC UA del PLC simulado. Adaptado de Quickstarts.ReferenceServer.ReferenceServer
    /// del SDK de la OPC Foundation: crea el NodeManager propio y valida usuarios.
    /// </summary>
    public sealed class PlcServer : StandardServer
    {
        /// <summary>Usuario de laboratorio (además de Anonymous). Ver README.</summary>
        public const string LabUser = "lab";
        public const string LabPassword = "lab2026";

        private readonly PlantModel m_model;

        public PlcServer(PlantModel model)
        {
            m_model = model;
        }

        public PlcNodeManager? NodeManager { get; private set; }

        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            NodeManager = new PlcNodeManager(server, configuration, m_model);
            return new MasterNodeManager(server, configuration, null, new INodeManager[] { NodeManager });
        }

        protected override ServerProperties LoadServerProperties()
        {
            return new ServerProperties
            {
                ManufacturerName = "ICSH - Taller OPC UA",
                ProductName = "PLC Sim Server",
                ProductUri = "urn:icsh:lab:plc-sim",
                SoftwareVersion = Utils.GetAssemblySoftwareVersion(),
                BuildNumber = Utils.GetAssemblyBuildNumber(),
                BuildDate = Utils.GetAssemblyTimestamp()
            };
        }

        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);
            server.SessionManager.ImpersonateUser += SessionManager_ImpersonateUser;
            server.SessionManager.SessionCreated += (session, _) =>
                m_model.Info($"[OPC] sesión creada: {session.SessionDiagnostics.SessionName}");
            server.SessionManager.SessionClosing += (session, _) =>
                m_model.Info($"[OPC] sesión cerrada: {session.SessionDiagnostics.SessionName}");
        }

        /// <summary>Acepta Anonymous y el usuario de laboratorio; rechaza el resto.</summary>
        private void SessionManager_ImpersonateUser(ISession session, ImpersonateEventArgs args)
        {
            if (args.NewIdentity is UserNameIdentityToken userNameToken)
            {
                if (userNameToken.UserName == LabUser &&
                    Utils.IsEqual(userNameToken.DecryptedPassword, System.Text.Encoding.UTF8.GetBytes(LabPassword)))
                {
                    args.Identity = new RoleBasedIdentity(new UserIdentity(userNameToken), [Role.AuthenticatedUser]);
                    m_model.Info($"[OPC] usuario autenticado: {userNameToken.UserName}");
                    return;
                }
                m_model.Info($"[OPC] login rechazado para usuario '{userNameToken.UserName}'");
                throw ServiceResultException.Create(StatusCodes.BadUserAccessDenied, "Usuario o clave inválidos.");
            }

            if (args.NewIdentity is AnonymousIdentityToken or null)
            {
                args.Identity = new RoleBasedIdentity(new UserIdentity(), [Role.Anonymous]);
                return;
            }

            throw ServiceResultException.Create(StatusCodes.BadIdentityTokenInvalid, "Tipo de token no soportado: {0}.", args.NewIdentity);
        }
    }
}
