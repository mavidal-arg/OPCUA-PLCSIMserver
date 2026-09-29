using System;
using System.Collections.Generic;
using System.Linq;

namespace PlcSimServer.Model
{
    /// <summary>Tipo de dato de un tag (se mapea 1:1 a un BuiltInType de OPC UA).</summary>
    public enum TagType { Boolean, Double, String }

    /// <summary>Quién originó una escritura. Solo las escrituras OPC respetan el flag Writable.</summary>
    public enum WriteSource { Ui, Opc, Sim }

    public enum WriteResult { Ok, NotWritable, TypeMismatch, OutOfRange, InvalidState }

    /// <summary>Definición estática de un tag del PLC simulado.</summary>
    public sealed class TagDef
    {
        public required string Equipment { get; init; }
        public required string Name { get; init; }
        public required TagType Type { get; init; }
        public bool Writable { get; init; }
        public double? Min { get; init; }
        public double? Max { get; init; }
        public string Unit { get; init; } = "";
        public string[]? AllowedValues { get; init; }
        public string Description { get; init; } = "";

        /// <summary>Identificador string del NodeId, p.ej. "Motor1.RPM".</summary>
        public string Path => Equipment + "." + Name;
    }

    /// <summary>Valor vivo de un tag.</summary>
    public sealed class Tag
    {
        internal Tag(TagDef def, object initial)
        {
            Def = def;
            Value = initial;
            Timestamp = DateTime.UtcNow;
        }

        public TagDef Def { get; }
        public object Value { get; internal set; }
        public DateTime Timestamp { get; internal set; }

        /// <summary>Si está forzado, la simulación no lo sobreescribe.</summary>
        public bool Forced { get; internal set; }

        /// <summary>Se incrementa en cada cambio; el NodeManager lo usa para publicar solo lo que cambió.</summary>
        public long Version { get; internal set; }
    }

    /// <summary>
    /// Fuente única de verdad del PLC simulado. La ventana, la simulación y el servidor OPC UA
    /// leen y escriben acá. Thread-safe con un lock interno; nunca se llama a código externo
    /// mientras se tiene el lock (evita deadlocks con el lock del NodeManager).
    /// </summary>
    public sealed class PlantModel
    {
        public const string Motor1 = "Motor1";
        public const string Motor2 = "Motor2";
        public const string LazoTemp = "LazoTemp";
        public const string MT1 = "MT1";
        public const string MT2 = "MT2";
        public const string Inversor = "InversorSolar";

        public static readonly string[] Equipments = [Motor1, Motor2, LazoTemp, MT1, MT2, Inversor];

        private readonly object m_lock = new();
        private readonly Dictionary<string, Tag> m_tags = new(StringComparer.Ordinal);
        private readonly List<Tag> m_ordered = [];

        public PlantModel()
        {
            foreach (string motor in new[] { Motor1, Motor2 })
            {
                Add(new TagDef { Equipment = motor, Name = "Status", Type = TagType.String, Description = "Run / Stop / Falla" }, "Stop");
                Add(new TagDef { Equipment = motor, Name = "RPM", Type = TagType.Double, Unit = "rpm" }, 0.0);
                Add(new TagDef { Equipment = motor, Name = "Corriente_A", Type = TagType.Double, Unit = "A" }, 0.0);
                Add(new TagDef { Equipment = motor, Name = "Falla", Type = TagType.Boolean }, false);
                Add(new TagDef { Equipment = motor, Name = "Comando_Run", Type = TagType.Boolean, Writable = true, Description = "true = marcha, false = parada" }, false);
            }

            Add(new TagDef { Equipment = LazoTemp, Name = "PV_Temp_C", Type = TagType.Double, Unit = "°C" }, 25.0);
            Add(new TagDef { Equipment = LazoTemp, Name = "Modo", Type = TagType.String, Description = "Auto / Manual" }, "Auto");
            Add(new TagDef { Equipment = LazoTemp, Name = "SP_Temp_C", Type = TagType.Double, Writable = true, Min = 0, Max = 150, Unit = "°C" }, 60.0);
            Add(new TagDef { Equipment = LazoTemp, Name = "Salida_Valvula_Pct", Type = TagType.Double, Writable = true, Min = 0, Max = 100, Unit = "%", Description = "Escribible solo en modo Manual" }, 0.0);
            Add(new TagDef { Equipment = LazoTemp, Name = "Modo_Cmd", Type = TagType.String, Writable = true, AllowedValues = ["Auto", "Manual"], Description = "Auto / Manual" }, "Auto");

            foreach (string mt in new[] { MT1, MT2 })
            {
                Add(new TagDef { Equipment = mt, Name = "Posicion", Type = TagType.String, Description = "Open / Closed" }, "Open");
                Add(new TagDef { Equipment = mt, Name = "kV", Type = TagType.Double, Unit = "kV" }, 0.0);
                Add(new TagDef { Equipment = mt, Name = "A", Type = TagType.Double, Unit = "A" }, 0.0);
                Add(new TagDef { Equipment = mt, Name = "MW", Type = TagType.Double, Unit = "MW" }, 0.0);
                Add(new TagDef
                {
                    Equipment = mt, Name = "Comando_Open_Close", Type = TagType.Boolean, Writable = true,
                    Description = mt == MT2 ? "true = cerrar, false = abrir (enclavado: requiere MT1 cerrado)" : "true = cerrar, false = abrir"
                }, false);
            }

            Add(new TagDef { Equipment = Inversor, Name = "DC_V", Type = TagType.Double, Unit = "V" }, 0.0);
            Add(new TagDef { Equipment = Inversor, Name = "DC_A", Type = TagType.Double, Unit = "A" }, 0.0);
            Add(new TagDef { Equipment = Inversor, Name = "AC_Power_kW", Type = TagType.Double, Unit = "kW" }, 0.0);
            Add(new TagDef { Equipment = Inversor, Name = "Eficiencia_Pct", Type = TagType.Double, Unit = "%" }, 0.0);
            Add(new TagDef { Equipment = Inversor, Name = "Falla", Type = TagType.Boolean }, false);
            Add(new TagDef { Equipment = Inversor, Name = "Curtailment_Setpoint_Pct", Type = TagType.Double, Writable = true, Min = 0, Max = 100, Unit = "%", Description = "Límite de potencia en % de la nominal" }, 100.0);
        }

        /// <summary>Mensajes para el panel de log de la ventana (escrituras, rechazos, etc.).</summary>
        public event Action<string>? Log;

        /// <summary>
        /// Validación de reglas de proceso (enclavamientos). La registra la simulación.
        /// Recibe (path, valor nuevo, origen) y devuelve null si está permitido o un mensaje de rechazo.
        /// </summary>
        public Func<string, object, WriteSource, string?>? Interlock { get; set; }

        public IReadOnlyList<Tag> Tags => m_ordered;

        public IEnumerable<Tag> TagsOf(string equipment) => m_ordered.Where(t => t.Def.Equipment == equipment);

        public Tag GetTag(string path) => m_tags[path];

        public object Get(string path)
        {
            lock (m_lock) { return m_tags[path].Value; }
        }

        public double GetDouble(string path) => Convert.ToDouble(Get(path));
        public bool GetBool(string path) => (bool)Get(path);
        public string GetString(string path) => (string)Get(path);

        /// <summary>Copia consistente (path, valor, timestamp, versión) de todos los tags.</summary>
        public List<(Tag Tag, object Value, DateTime Timestamp, long Version)> Snapshot()
        {
            lock (m_lock)
            {
                return m_ordered.Select(t => (t, t.Value, t.Timestamp, t.Version)).ToList();
            }
        }

        /// <summary>
        /// Escritura desde la ventana o desde un cliente OPC UA. Valida tipo, rango, valores
        /// permitidos y enclavamientos. Las escrituras de la UI pueden tocar tags de solo lectura
        /// (p.ej. inyectar una falla); las de OPC no.
        /// </summary>
        public WriteResult Write(string path, object value, WriteSource source, string? who = null)
        {
            if (!m_tags.TryGetValue(path, out Tag? tag))
            {
                return WriteResult.NotWritable;
            }

            TagDef def = tag.Def;
            string origin = source == WriteSource.Opc ? $"OPC{(who != null ? " " + who : "")}" : "UI";

            if (source == WriteSource.Opc && !def.Writable)
            {
                Emit($"[{origin}] RECHAZADO {path}: variable de solo lectura");
                return WriteResult.NotWritable;
            }

            object? normalized = Normalize(def, value);
            if (normalized == null)
            {
                Emit($"[{origin}] RECHAZADO {path} = {value}: tipo incorrecto (se espera {def.Type})");
                return WriteResult.TypeMismatch;
            }

            if (def.Type == TagType.Double)
            {
                double d = (double)normalized;
                if ((def.Min.HasValue && d < def.Min) || (def.Max.HasValue && d > def.Max))
                {
                    Emit($"[{origin}] RECHAZADO {path} = {d}: fuera de rango [{def.Min}..{def.Max}]");
                    return WriteResult.OutOfRange;
                }
            }

            if (def.AllowedValues != null && !def.AllowedValues.Contains((string)normalized))
            {
                Emit($"[{origin}] RECHAZADO {path} = {normalized}: valores permitidos {string.Join("/", def.AllowedValues)}");
                return WriteResult.OutOfRange;
            }

            string? reason = Interlock?.Invoke(path, normalized, source);
            if (reason != null)
            {
                Emit($"[{origin}] RECHAZADO {path} = {normalized}: {reason}");
                return WriteResult.InvalidState;
            }

            lock (m_lock)
            {
                SetLocked(tag, normalized);
            }

            Emit($"[{origin}] {path} = {Format(normalized)}");
            return WriteResult.Ok;
        }

        /// <summary>Actualización desde la simulación. No pisa tags forzados.</summary>
        public void SetSim(string path, object value)
        {
            lock (m_lock)
            {
                Tag tag = m_tags[path];
                if (!tag.Forced)
                {
                    SetLocked(tag, value);
                }
            }
        }

        /// <summary>Fuerza un valor (lo congela frente a la simulación). Útil para probar alarmas en el HMI.</summary>
        public void Force(string path, object value)
        {
            Tag tag = m_tags[path];
            object? normalized = Normalize(tag.Def, value);
            if (normalized == null)
            {
                return;
            }
            lock (m_lock)
            {
                tag.Forced = true;
                SetLocked(tag, normalized);
            }
            Emit($"[UI] FORZADO {path} = {Format(normalized)}");
        }

        public void Unforce(string path)
        {
            lock (m_lock)
            {
                m_tags[path].Forced = false;
            }
            Emit($"[UI] liberado forzado de {path}");
        }

        public static string Format(object value) => value switch
        {
            double d => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => value?.ToString() ?? ""
        };

        private void Add(TagDef def, object initial)
        {
            var tag = new Tag(def, initial);
            m_tags.Add(def.Path, tag);
            m_ordered.Add(tag);
        }

        private static void SetLocked(Tag tag, object value)
        {
            if (Equals(tag.Value, value))
            {
                return;
            }
            tag.Value = value;
            tag.Timestamp = DateTime.UtcNow;
            tag.Version++;
        }

        private static object? Normalize(TagDef def, object value)
        {
            try
            {
                return def.Type switch
                {
                    TagType.Boolean => value is bool b ? b : null,
                    TagType.Double => value is bool or string ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
                    TagType.String => value as string,
                    _ => null
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Mensaje informativo al panel de log.</summary>
        public void Info(string message) => Emit(message);

        private void Emit(string message) => Log?.Invoke(message);
    }
}
