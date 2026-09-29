using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace PlcSimServer.UI
{
    /// <summary>Gráfico de tendencia mínimo (últimas N muestras) para una o más series.</summary>
    public sealed class TrendPanel : Control
    {
        private readonly List<(string Name, Color Color, Queue<double> Data)> m_series = [];
        private readonly int m_capacity;

        public TrendPanel(double min, double max, int capacity = 240)
        {
            Min = min;
            Max = max;
            m_capacity = capacity;
            DoubleBuffered = true;
            BackColor = Color.White;
            MinimumSize = new Size(200, 120);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Min { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Max { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Title { get; set; } = "";

        public void AddSeries(string name, Color color) => m_series.Add((name, color, new Queue<double>()));

        public void Push(params double[] values)
        {
            for (int i = 0; i < values.Length && i < m_series.Count; i++)
            {
                Queue<double> q = m_series[i].Data;
                q.Enqueue(values[i]);
                while (q.Count > m_capacity) q.Dequeue();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var plot = new Rectangle(44, 22, Width - 54, Height - 34);
            if (plot.Width < 10 || plot.Height < 10) return;

            using var gridPen = new Pen(Color.Gainsboro);
            using var axisFont = new Font(Font.FontFamily, 8f);
            for (int i = 0; i <= 4; i++)
            {
                int y = plot.Bottom - i * plot.Height / 4;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                double v = Min + (Max - Min) * i / 4;
                g.DrawString(v.ToString("0.#"), axisFont, Brushes.DimGray, 2, y - 7);
            }
            g.DrawRectangle(Pens.Silver, plot);

            float legendX = plot.Left;
            using (var titleFont = new Font(Font, FontStyle.Bold))
            {
                g.DrawString(Title, titleFont, Brushes.Black, legendX, 2);
                legendX += g.MeasureString(Title, titleFont).Width + 12;
            }

            foreach (var (name, color, data) in m_series)
            {
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, legendX, 7, 10, 10);
                g.DrawString(name, axisFont, Brushes.Black, legendX + 13, 5);
                legendX += g.MeasureString(name, axisFont).Width + 30;

                if (data.Count < 2) continue;
                double[] values = data.ToArray();
                PointF[] points = values.Select((v, i) => new PointF(
                    plot.Right - (float)(values.Length - 1 - i) * plot.Width / (m_capacity - 1),
                    (float)(plot.Bottom - (Math.Clamp(v, Min, Max) - Min) / (Max - Min) * plot.Height))).ToArray();
                using var pen = new Pen(color, 2f);
                g.DrawLines(pen, points);
            }
        }
    }
}
