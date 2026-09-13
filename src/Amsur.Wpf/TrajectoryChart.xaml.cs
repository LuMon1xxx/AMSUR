using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Amsur.Application;

namespace Amsur.Wpf;

// E6 — лёгкий график «лучшая оценка → время» без сторонних библиотек.
// Рисует ТОЛЬКО полученные точки (разрывы при bestSoft==null, не нули);
// масштабы — только из фактических данных. Без анимаций.
public partial class TrajectoryChart : UserControl
{
    private TrajectorySnapshot? _last;

    public TrajectoryChart()
    {
        InitializeComponent();
        Loaded += (_, _) => Render(_last);
        SizeChanged += (_, _) => Render(_last);
    }

    public void Render(TrajectorySnapshot? snapshot)
    {
        _last = snapshot;
        Plot.Children.Clear();
        if (snapshot is not { HasData: true } || Plot.ActualWidth < 50)
        {
            EmptyText.Visibility = Visibility.Visible;
            Plot.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        Plot.Visibility = Visibility.Visible;

        const double padL = 44, padR = 8, padT = 18, padB = 20;
        double w = Math.Max(50, Plot.ActualWidth - padL - padR);
        double h = Math.Max(40, Plot.ActualHeight - padT - padB);

        var pts = snapshot.Points;
        double tMax = Math.Max(1, pts.Max(p => p.Elapsed.TotalSeconds));
        var valued = pts.Where(p => p.BestSoft.HasValue).ToList();
        if (valued.Count == 0)
        {
            // События есть, значений нет (feasible ещё не было): только ось времени.
            DrawTimeAxis(padL, w, h, padT, tMax);
            foreach (var m in snapshot.Markers) DrawMarker(m, padL, w, padT, h, tMax);
            return;
        }
        long vMin = valued.Min(p => p.BestSoft!.Value);
        long vMax = valued.Max(p => p.BestSoft!.Value);
        double span = Math.Max(1, (vMax - vMin) * 1.2);
        double lo = vMin - span * 0.1, hi = vMax + span * 0.1;

        double X(TimeSpan t) => padL + (t.TotalSeconds / tMax) * w;
        double Y(long v) => padT + (1 - ((v - lo) / (hi - lo))) * h;

        // Линия — отрезками между соседними точками СО значением (разрыв, не интерполяция).
        TrajectoryPoint? prev = null;
        foreach (var p in pts)
        {
            if (prev?.BestSoft.HasValue == true && p.BestSoft.HasValue)
            {
                Plot.Children.Add(new Line
                {
                    X1 = X(prev.Elapsed), Y1 = Y(prev.BestSoft.Value),
                    X2 = X(p.Elapsed), Y2 = Y(p.BestSoft.Value),
                    Stroke = Brushes.SteelBlue, StrokeThickness = 2,
                });
            }
            prev = p;
        }
        // Точки-значения.
        foreach (var p in valued)
        {
            Plot.Children.Add(new Ellipse
            {
                Width = 5, Height = 5, Fill = Brushes.SteelBlue,
            }.WithPos(X(p.Elapsed) - 2.5, Y(p.BestSoft!.Value) - 2.5));
        }

        DrawTimeAxis(padL, w, h, padT, tMax);
        // Подписи оси значений: только фактические min/max.
        Plot.Children.Add(Text($"{vMax}", padL - 40, padT - 8, 36));
        Plot.Children.Add(Text($"{vMin}", padL - 40, padT + h - 8, 36));
        Plot.Children.Add(Text("меньше — лучше", padL, padT + h + 4, 200, italic: true));

        foreach (var m in snapshot.Markers)
            DrawMarker(m, padL, w, padT, h, tMax);
    }

    private void DrawTimeAxis(double padL, double w, double h, double padT, double tMax)
    {
        Plot.Children.Add(new Line
        {
            X1 = padL, Y1 = padT + h, X2 = padL + w, Y2 = padT + h,
            Stroke = Brushes.Gray, StrokeThickness = 1,
        });
        Plot.Children.Add(Text("0 с", padL - 4, padT + h + 4, 60));
        Plot.Children.Add(Text($"{tMax:F0} с", padL + w - 30, padT + h + 4, 60));
    }

    private void DrawMarker(TrajectoryMarker m, double padL, double w,
        double padT, double h, double tMax)
    {
        double x = padL + (m.Elapsed.TotalSeconds / Math.Max(1, tMax)) * w;
        bool terminal = m.Label is "Готово" or "Остановлено";
        Plot.Children.Add(new Line
        {
            X1 = x, Y1 = padT - 4, X2 = x, Y2 = padT + h,
            Stroke = terminal ? Brushes.DarkGreen : Brushes.Gray,
            StrokeThickness = terminal ? 2 : 1,
            StrokeDashArray = terminal ? null : [4, 3],
        });
        var label = Text(m.Label, Math.Min(x + 3, padL + w - 90), 0, 120, terminal);
        Plot.Children.Add(label);
    }

    private static TextBlock Text(string s, double x, double y, double width,
        bool bold = false, bool italic = false) =>
        new()
        {
            Text = s, FontSize = 10, Opacity = 0.85, Width = width,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
            Margin = new Thickness(x, y, 0, 0),
        };
}

file static class CanvasPos
{
    public static Ellipse WithPos(this Ellipse e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }
}
