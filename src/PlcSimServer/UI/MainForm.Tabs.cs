using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using PlcSimServer.Model;

namespace PlcSimServer.UI
{
    /// <summary>Construcción de las pestañas por equipo y de la pestaña de resumen.</summary>
    public sealed partial class MainForm
    {
        private static readonly Color ColorRun = Color.FromArgb(0x1E, 0x8E, 0x3E);
        private static readonly Color ColorStop = Color.FromArgb(0x5F, 0x63, 0x68);
        private static readonly Color ColorFault = Color.FromArgb(0xC5, 0x22, 0x1F);
        private static readonly Color ColorClosed = Color.FromArgb(0xC5, 0x22, 0x1F); // convención eléctrica: rojo = cerrado/energizado
        private static readonly Color ColorOpen = Color.FromArgb(0x1E, 0x8E, 0x3E);   // verde = abierto

        // ------------------------------------------------------------------ Resumen

        private TabPage BuildSummaryTab()
        {
            var page = new TabPage("Resumen");
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window
            };
            grid.Columns.Add("NodeId", "NodeId");
            grid.Columns.Add("Tipo", "Tipo");
            grid.Columns.Add("Acceso", "Acceso");
            grid.Columns.Add("Valor", "Valor");
            grid.Columns.Add("Unidad", "Unidad");
            grid.Columns.Add("Forzado", "Forzado");
            grid.Columns["NodeId"]!.FillWeight = 180;
            grid.Columns["Valor"]!.DefaultCellStyle.Font = new Font("Consolas", 10f, FontStyle.Bold);

            foreach (Tag tag in m_model.Tags)
            {
                int row = grid.Rows.Add($"ns=2;s={tag.Def.Path}", tag.Def.Type, tag.Def.Writable ? "RW" : "R", "", tag.Def.Unit, "");
                grid.Rows[row].Tag = tag;
                if (tag.Def.Writable)
                {
                    grid.Rows[row].DefaultCellStyle.BackColor = Color.FromArgb(0xEE, 0xF4, 0xFF);
                }
            }

            m_refreshers.Add(() =>
            {
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var tag = (Tag)row.Tag!;
                    row.Cells["Valor"].Value = PlantModel.Format(m_model.Get(tag.Def.Path));
                    row.Cells["Forzado"].Value = tag.Forced ? "FORZADO" : "";
                    row.Cells["Forzado"].Style.ForeColor = ColorFault;
                }
            });

            // Panel para escribir / forzar el tag seleccionado.
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
            var selected = new Label { AutoSize = true, Padding = new Padding(0, 7, 8, 0), Text = "Seleccione un tag" };
            var input = new TextBox { Width = 140 };
            var btnWrite = new Button { Text = "Escribir", AutoSize = true };
            var btnForce = new Button { Text = "Forzar", AutoSize = true };
            var btnRelease = new Button { Text = "Liberar forzado", AutoSize = true };
            var help = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(8, 7, 0, 0),
                Text = "Escribir: solo tags RW (igual que un cliente). Forzar: congela cualquier tag frente a la simulación (bool: true/false)."
            };
            bar.Controls.AddRange([selected, input, btnWrite, btnForce, btnRelease, help]);

            Tag? Current() => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as Tag : null;

            grid.SelectionChanged += (_, _) =>
            {
                Tag? tag = Current();
                if (tag == null) return;
                selected.Text = tag.Def.Path + ":";
                input.Text = PlantModel.Format(m_model.Get(tag.Def.Path));
            };
            btnWrite.Click += (_, _) =>
            {
                Tag? tag = Current();
                if (tag == null) return;
                if (!tag.Def.Writable)
                {
                    MessageBox.Show(this, "El tag es de solo lectura; use Forzar.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (TryParse(tag.Def, input.Text, out object? value)) UiWrite(tag.Def.Path, value!);
            };
            btnForce.Click += (_, _) =>
            {
                Tag? tag = Current();
                if (tag != null && TryParse(tag.Def, input.Text, out object? value)) m_model.Force(tag.Def.Path, value!);
            };
            btnRelease.Click += (_, _) =>
            {
                Tag? tag = Current();
                if (tag != null && tag.Forced) m_model.Unforce(tag.Def.Path);
            };

            page.Controls.Add(grid);
            page.Controls.Add(bar);
            return page;
        }

        private bool TryParse(TagDef def, string text, out object? value)
        {
            text = text.Trim();
            value = null;
            switch (def.Type)
            {
                case TagType.Boolean:
                    if (text is "1" or "true" or "True" or "TRUE") value = true;
                    else if (text is "0" or "false" or "False" or "FALSE") value = false;
                    break;
                case TagType.Double:
                    if (double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) value = d;
                    break;
                default:
                    value = text;
                    break;
            }
            if (value == null)
            {
                MessageBox.Show(this, $"Valor inválido para {def.Path} (tipo {def.Type}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ Motores

        private TabPage BuildMotorTab(string m, double nominalRpm)
        {
            var (page, left, right) = NewEquipmentPage(m, $"Motor de inducción — {nominalRpm:0} rpm nominales");

            left.Controls.Add(StatusIndicator(m + ".Status", v => v switch
            {
                "Run" => ("EN MARCHA", ColorRun),
                "Falla" => ("FALLA", ColorFault),
                _ => ("DETENIDO", ColorStop)
            }));

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
            var start = BigButton("▶ Arrancar", ColorRun);
            var stop = BigButton("■ Parar", ColorStop);
            start.Click += (_, _) => UiWrite(m + ".Comando_Run", true);
            stop.Click += (_, _) => UiWrite(m + ".Comando_Run", false);
            buttons.Controls.AddRange([start, stop]);
            left.Controls.Add(buttons);

            var rpmBar = new ProgressBar { Width = 330, Height = 18, Maximum = (int)(nominalRpm * 1.05) };
            m_refreshers.Add(() => rpmBar.Value = Math.Clamp((int)m_model.GetDouble(m + ".RPM"), 0, rpmBar.Maximum));
            left.Controls.Add(rpmBar);

            left.Controls.Add(Readouts(m + ".RPM", m + ".Corriente_A", m + ".Comando_Run", m + ".Falla"));
            left.Controls.Add(FaultCheckBox(m + ".Falla", "Inyectar falla (dispara el motor y baja Comando_Run)"));

            var trend = new TrendPanel(0, nominalRpm * 1.1) { Dock = DockStyle.Fill, Title = "Velocidad" };
            trend.AddSeries("RPM", Color.SteelBlue);
            m_refreshers.Add(() => trend.Push(m_model.GetDouble(m + ".RPM")));
            right.Controls.Add(trend);
            return page;
        }

        // ------------------------------------------------------------------ Lazo de temperatura

        private TabPage BuildLoopTab()
        {
            const string l = PlantModel.LazoTemp;
            var (page, left, right) = NewEquipmentPage(l, "Lazo de temperatura — planta de 1er orden con PI");

            var modeRow = new FlowLayoutPanel { AutoSize = true };
            modeRow.Controls.Add(new Label { Text = "Modo:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            var mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            mode.Items.AddRange(["Auto", "Manual"]);
            mode.SelectedIndexChanged += (_, _) => UiWrite(l + ".Modo_Cmd", (string)mode.SelectedItem!);
            m_refreshers.Add(() => { if (!mode.DroppedDown) mode.SelectedItem = m_model.GetString(l + ".Modo_Cmd"); });
            modeRow.Controls.Add(mode);
            left.Controls.Add(modeRow);

            left.Controls.Add(CommitTrackBar(l + ".SP_Temp_C", "Setpoint (°C)", 0, 150));
            TrackBar valve = CommitTrackBar(l + ".Salida_Valvula_Pct", "Salida válvula (%) — solo en Manual", 0, 100).Controls.OfType<TrackBar>().First();
            left.Controls.Add(valve.Parent!);
            m_refreshers.Add(() => valve.Enabled = m_model.GetString(l + ".Modo_Cmd") == "Manual");

            left.Controls.Add(Readouts(l + ".PV_Temp_C", l + ".SP_Temp_C", l + ".Salida_Valvula_Pct", l + ".Modo"));

            var trend = new TrendPanel(0, 150) { Dock = DockStyle.Fill, Title = "Lazo" };
            trend.AddSeries("PV °C", Color.Firebrick);
            trend.AddSeries("SP °C", Color.Black);
            trend.AddSeries("Válvula %", Color.SteelBlue);
            m_refreshers.Add(() => trend.Push(
                m_model.GetDouble(l + ".PV_Temp_C"),
                m_model.GetDouble(l + ".SP_Temp_C"),
                m_model.GetDouble(l + ".Salida_Valvula_Pct")));
            right.Controls.Add(trend);
            return page;
        }

        // ------------------------------------------------------------------ Interruptores MT

        private TabPage BuildBreakerTab(string mt)
        {
            string subtitle = mt == PlantModel.MT1
                ? "Interruptor MT 23 kV — alimentador de carga (motores + carga base)"
                : "Interruptor MT 23 kV — alimentador del inversor solar. Enclavamiento: requiere MT1 cerrado";
            var (page, left, right) = NewEquipmentPage(mt, subtitle);

            left.Controls.Add(StatusIndicator(mt + ".Posicion", v => v == "Closed" ? ("CERRADO", ColorClosed) : ("ABIERTO", ColorOpen)));

            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 8) };
            var close = BigButton("Cerrar", ColorClosed);
            var open = BigButton("Abrir", ColorOpen);
            close.Click += (_, _) => UiWrite(mt + ".Comando_Open_Close", true);
            open.Click += (_, _) => UiWrite(mt + ".Comando_Open_Close", false);
            buttons.Controls.AddRange([close, open]);
            left.Controls.Add(buttons);

            left.Controls.Add(Readouts(mt + ".Posicion", mt + ".kV", mt + ".A", mt + ".MW", mt + ".Comando_Open_Close"));

            var trend = new TrendPanel(0, mt == PlantModel.MT1 ? 100 : 5) { Dock = DockStyle.Fill, Title = "Corriente" };
            trend.AddSeries("A", Color.DarkOrange);
            m_refreshers.Add(() => trend.Push(m_model.GetDouble(mt + ".A")));
            right.Controls.Add(trend);
            return page;
        }

        // ------------------------------------------------------------------ Inversor

        private TabPage BuildInverterTab()
        {
            const string i = PlantModel.Inversor;
            var (page, left, right) = NewEquipmentPage(i, "Inversor 100 kW — curva diaria sintética. Requiere MT2 cerrado (anti-isla)");

            left.Controls.Add(CommitTrackBar(i + ".Curtailment_Setpoint_Pct", "Curtailment (% de potencia nominal)", 0, 100));
            left.Controls.Add(FaultCheckBox(i + ".Falla", "Inyectar falla del inversor"));

            var dayRow = new FlowLayoutPanel { AutoSize = true };
            dayRow.Controls.Add(new Label { Text = "Duración del día simulado (s):", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            var day = new NumericUpDown { Minimum = 60, Maximum = 86400, Increment = 60, Value = (decimal)m_engine.SolarDaySeconds, Width = 90 };
            day.ValueChanged += (_, _) => m_engine.SolarDaySeconds = (double)day.Value;
            dayRow.Controls.Add(day);
            left.Controls.Add(dayRow);

            left.Controls.Add(Readouts(i + ".AC_Power_kW", i + ".DC_V", i + ".DC_A", i + ".Eficiencia_Pct", i + ".Curtailment_Setpoint_Pct", i + ".Falla"));

            var trend = new TrendPanel(0, 110) { Dock = DockStyle.Fill, Title = "Potencia" };
            trend.AddSeries("AC kW", Color.Goldenrod);
            trend.AddSeries("Límite curtailment kW", Color.Gray);
            m_refreshers.Add(() => trend.Push(m_model.GetDouble(i + ".AC_Power_kW"), m_model.GetDouble(i + ".Curtailment_Setpoint_Pct")));
            right.Controls.Add(trend);
            return page;
        }

        // ------------------------------------------------------------------ helpers

        private static (TabPage Page, FlowLayoutPanel Left, Panel Right) NewEquipmentPage(string name, string subtitle)
        {
            var page = new TabPage(name);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new Label { Text = subtitle, AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
            layout.Controls.Add(title, 0, 0);
            layout.SetColumnSpan(title, 2);

            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 0, 0) };
            layout.Controls.Add(left, 0, 1);
            layout.Controls.Add(right, 1, 1);
            page.Controls.Add(layout);
            return (page, left, right);
        }

        private Label StatusIndicator(string path, Func<string, (string Text, Color Color)> map)
        {
            var label = new Label
            {
                Width = 330, Height = 54, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 18f, FontStyle.Bold), ForeColor = Color.White
            };
            m_refreshers.Add(() =>
            {
                var (text, color) = map(m_model.GetString(path));
                label.Text = text;
                label.BackColor = color;
            });
            return label;
        }

        private static Button BigButton(string text, Color color) => new()
        {
            Text = text, Width = 160, Height = 44, FlatStyle = FlatStyle.Flat,
            BackColor = color, ForeColor = Color.White, Font = new Font("Segoe UI", 11f, FontStyle.Bold)
        };

        /// <summary>Tabla nombre / valor en vivo, con indicador de forzado.</summary>
        private TableLayoutPanel Readouts(params string[] paths)
        {
            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 10), CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            foreach (string path in paths)
            {
                Tag tag = m_model.GetTag(path);
                var name = new Label { Text = tag.Def.Name + (tag.Def.Writable ? "  (RW)" : ""), AutoSize = true, Padding = new Padding(2, 4, 2, 4) };
                var value = new Label { AutoSize = true, Padding = new Padding(2, 4, 2, 4), Font = new Font("Consolas", 11f, FontStyle.Bold) };
                m_refreshers.Add(() =>
                {
                    value.Text = PlantModel.Format(m_model.Get(path)) + (tag.Def.Unit.Length > 0 ? " " + tag.Def.Unit : "") + (tag.Forced ? "  [F]" : "");
                    value.ForeColor = tag.Forced ? ColorFault : Color.Black;
                });
                table.Controls.Add(name);
                table.Controls.Add(value);
            }
            return table;
        }

        private CheckBox FaultCheckBox(string path, string text)
        {
            var check = new CheckBox { Text = text, AutoSize = true, ForeColor = ColorFault, Margin = new Padding(0, 6, 0, 6) };
            check.CheckedChanged += (_, _) => UiWrite(path, check.Checked);
            m_refreshers.Add(() => check.Checked = m_model.GetBool(path));
            return check;
        }

        /// <summary>
        /// TrackBar que escribe al soltar el mouse o con el teclado (no en cada paso del arrastre),
        /// y que se sincroniza con el modelo cuando otro cliente cambia el valor.
        /// </summary>
        private Panel CommitTrackBar(string path, string caption, int min, int max)
        {
            var panel = new Panel { Width = 360, Height = 78, Margin = new Padding(0, 4, 0, 4) };
            var label = new Label { Text = caption, AutoSize = true, Location = new Point(0, 0) };
            var value = new Label { AutoSize = true, Location = new Point(300, 26), Font = new Font("Consolas", 11f, FontStyle.Bold) };
            var bar = new TrackBar
            {
                Minimum = min, Maximum = max, TickFrequency = (max - min) / 10, LargeChange = (max - min) / 10,
                Width = 295, Location = new Point(0, 20)
            };
            bool dragging = false;
            bar.MouseDown += (_, _) => dragging = true;
            bar.MouseUp += (_, _) => { dragging = false; UiWrite(path, (double)bar.Value); };
            bar.KeyUp += (_, _) => UiWrite(path, (double)bar.Value);
            bar.ValueChanged += (_, _) => value.Text = bar.Value.ToString(CultureInfo.InvariantCulture);
            m_refreshers.Add(() =>
            {
                if (!dragging)
                {
                    bar.Value = Math.Clamp((int)Math.Round(m_model.GetDouble(path)), min, max);
                }
            });
            panel.Controls.AddRange([label, bar, value]);
            return panel;
        }
    }
}
