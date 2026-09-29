using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server;
using PlcSimServer.Model;

namespace PlcSimServer.Opc
{
    /// <summary>
    /// Expone el <see cref="PlantModel"/> como espacio de direcciones OPC UA.
    /// Basado en el patrón de Quickstarts.ReferenceServer.ReferenceNodeManager del SDK de la
    /// OPC Foundation (CreateFolder / CreateVariable / CreateAddressSpace / OnSimpleWriteValue).
    ///
    /// Árbol:  Objects / PlcSim / {Motor1, Motor2, LazoTemp, MT1, MT2, InversorSolar} / variables
    /// NodeIds: ns=2;s=Motor1.RPM  (el índice de namespace lo asigna el servidor; con un solo
    ///          namespace propio es 2).
    /// </summary>
    public sealed class PlcNodeManager : CustomNodeManager2
    {
        public const string NamespaceUri = "urn:icsh:lab:plc-sim";

        private readonly PlantModel m_model;
        private readonly Dictionary<string, (BaseDataVariableState Node, long Version)> m_variables = new(StringComparer.Ordinal);

        public PlcNodeManager(IServerInternal server, ApplicationConfiguration configuration, PlantModel model)
            : base(server, configuration, server.Telemetry.CreateLogger<PlcNodeManager>(), NamespaceUri)
        {
            m_model = model;
            SystemContext.NodeIdFactory = this;
        }

        public override NodeId New(ISystemContext context, NodeState node) => node.NodeId;

        public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
        {
            lock (Lock)
            {
                if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out IList<IReference>? references))
                {
                    externalReferences[ObjectIds.ObjectsFolder] = references = [];
                }

                FolderState root = CreateFolder(null, "PlcSim", "PlcSim");
                root.Description = "PLC simulado - Taller OPC UA ICSH";
                root.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
                references.Add(new NodeStateReference(ReferenceTypes.Organizes, false, root.NodeId));
                root.EventNotifier = EventNotifiers.SubscribeToEvents;
                AddRootNotifier(root);

                foreach (string equipment in PlantModel.Equipments)
                {
                    FolderState folder = CreateFolder(root, equipment, equipment);
                    foreach (Tag tag in m_model.TagsOf(equipment))
                    {
                        BaseDataVariableState variable = CreateVariable(folder, tag);
                        m_variables[tag.Def.Path] = (variable, tag.Version);
                    }
                }

                AddPredefinedNode(SystemContext, root);
            }
        }

        /// <summary>
        /// Copia al espacio de direcciones los tags que cambiaron desde la última llamada.
        /// ClearChangeMasks dispara las notificaciones a las suscripciones de los clientes.
        /// </summary>
        public void SyncFromModel()
        {
            // El snapshot se toma fuera del Lock del NodeManager: nunca se sostienen ambos locks a la vez.
            var snapshot = m_model.Snapshot();

            lock (Lock)
            {
                foreach (var (tag, value, timestamp, version) in snapshot)
                {
                    if (!m_variables.TryGetValue(tag.Def.Path, out var entry) || entry.Version == version)
                    {
                        continue;
                    }
                    entry.Node.Value = value;
                    entry.Node.Timestamp = timestamp;
                    entry.Node.StatusCode = StatusCodes.Good;
                    entry.Node.ClearChangeMasks(SystemContext, false);
                    m_variables[tag.Def.Path] = (entry.Node, version);
                }
            }
        }

        private FolderState CreateFolder(NodeState? parent, string path, string name)
        {
            var folder = new FolderState(parent)
            {
                SymbolicName = name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = ObjectTypeIds.FolderType,
                NodeId = new NodeId(path, NamespaceIndex),
                BrowseName = new QualifiedName(name, NamespaceIndex),
                DisplayName = new LocalizedText("es", name),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                EventNotifier = EventNotifiers.None
            };
            parent?.AddChild(folder);
            return folder;
        }

        private BaseDataVariableState CreateVariable(NodeState parent, Tag tag)
        {
            TagDef def = tag.Def;
            byte access = def.Writable ? AccessLevels.CurrentReadOrWrite : AccessLevels.CurrentRead;

            var variable = new BaseDataVariableState(parent)
            {
                SymbolicName = def.Name,
                ReferenceTypeId = ReferenceTypes.Organizes,
                TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
                NodeId = new NodeId(def.Path, NamespaceIndex),
                BrowseName = new QualifiedName(def.Name, NamespaceIndex),
                DisplayName = new LocalizedText("es", def.Name),
                Description = new LocalizedText("es", BuildDescription(def)),
                WriteMask = AttributeWriteMask.None,
                UserWriteMask = AttributeWriteMask.None,
                DataType = def.Type switch
                {
                    TagType.Boolean => DataTypeIds.Boolean,
                    TagType.Double => DataTypeIds.Double,
                    _ => DataTypeIds.String
                },
                ValueRank = ValueRanks.Scalar,
                AccessLevel = access,
                UserAccessLevel = access,
                MinimumSamplingInterval = 100,
                Historizing = false,
                Value = tag.Value,
                StatusCode = StatusCodes.Good,
                Timestamp = tag.Timestamp
            };

            if (def.Writable)
            {
                variable.OnSimpleWriteValue = OnWriteTag;
            }

            parent.AddChild(variable);
            return variable;
        }

        /// <summary>
        /// Escritura de un cliente OPC UA (p.ej. Node-RED). Se valida contra el modelo, que aplica
        /// rangos y enclavamientos; si se rechaza, el cliente recibe el StatusCode correspondiente.
        /// </summary>
        private ServiceResult OnWriteTag(ISystemContext context, NodeState node, ref object value)
        {
            string path = (string)node.NodeId.Identifier;
            string who = (context as ISessionSystemContext)?.UserIdentity?.DisplayName ?? "Anonymous";

            WriteResult result = m_model.Write(path, value, WriteSource.Opc, who);
            if (result == WriteResult.Ok)
            {
                // El modelo quedó con el valor; el próximo SyncFromModel no debe re-notificar.
                Tag tag = m_model.GetTag(path);
                if (m_variables.TryGetValue(path, out var entry))
                {
                    m_variables[path] = (entry.Node, tag.Version);
                }
                return ServiceResult.Good;
            }

            return result switch
            {
                WriteResult.NotWritable => StatusCodes.BadNotWritable,
                WriteResult.TypeMismatch => StatusCodes.BadTypeMismatch,
                WriteResult.OutOfRange => StatusCodes.BadOutOfRange,
                WriteResult.InvalidState => StatusCodes.BadInvalidState,
                _ => StatusCodes.Bad
            };
        }

        private static string BuildDescription(TagDef def)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(def.Description)) parts.Add(def.Description);
            if (!string.IsNullOrEmpty(def.Unit)) parts.Add("[" + def.Unit + "]");
            if (def.Min.HasValue || def.Max.HasValue) parts.Add($"rango {def.Min}..{def.Max}");
            parts.Add(def.Writable ? "RW" : "R");
            return string.Join(" ", parts);
        }
    }
}
