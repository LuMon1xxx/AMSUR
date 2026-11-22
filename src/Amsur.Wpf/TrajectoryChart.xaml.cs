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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Render(_last);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
        {
            app.ThemeChanged -= OnAppThemeChanged;
            app.ThemeChanged += OnAppThemeChanged;
        }
        Render(_last);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.ThemeChanged -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(string _) => Render(_last);

    private Brush ResBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    public void Render(TrajectorySnapshot? snapshot)
    {
        _last = snapshot;
        Plot.Children.Clear();
        if (snapshot is not { HasData: true })
        {
            EmptyText.Visibility = Visibility.Visible;
            Plot.Visibility = Visibility.Collapsed;
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;
        Plot.Visibility = Visibility.Visible;
        // Canvas только что стал видимым и ещё не прошёл layout: ActualWidth = 0.
        // Берём ширину контрола, иначе первый же рендер навсегда оставляет пустоту.
        double plotWidth = Plot.ActualWidth >= 50
            ? Plot.ActualWidth
            : Math.Max(50, ActualWidth - 26);

        const double padL = 44, padR = 8, padT = 18, padB = 20;
        double w = Math.Max(50, plotWidth - padL - padR);
        double h = Math.Max(40, Plot.ActualHeight - padT - padB);

        _lastLabelX = double.NegativeInfinity;
        var pts = snapshot.Points;
        double tMax = Math.Max(1, pts.Max(p => p.Elapsed.TotalSeconds));
        var lineBrush = ResBrush("BAccentText", Brushes.Teal);
        var axisBrush = ResBrush("BTextMuted", Brushes.Gray);
        var doneBrush = ResBrush("BGood", Brushes.SeaGreen);
        var labelBrush = ResBrush("BTextSoft", Brushes.DimGray);

        var valued = pts.Where(p => p.BestSoft.HasValue).ToList();
        if (valued.Count == 0)
        {
            // События есть, значений нет (feasible ещё не было): только ось времени.
            DrawTimeAxis(padL, w, h, padT, tMax, axisBrush, labelBrush);
            foreach (var m in snapshot.Markers) DrawMarker(m, padL, w, padT, h, tMax, axisBrush, doneBrush, labelBrush);
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
                    Stroke = lineBrush, StrokeThickness = 2,
                });
            }
            prev = p;
        }
        // Точки-значения.
        foreach (var p in valued)
        {
            Plot.Children.Add(new Ellipse
            {
                Width = 5, Height = 5, Fill = lineBrush,
            }.WithPos(X(p.Elapsed) - 2.5, Y(p.BestSoft!.Value) - 2.5));
        }

        DrawTimeAxis(padL, w, h, padT, tMax, axisBrush, labelBrush);
        // Подписи оси значений: только фактические min/max.
        Plot.Children.Add(Text($"{vMax}", padL - 40, padT - 8, 36, labelBrush));
        Plot.Children.Add(Text($"{vMin}", padL - 40, padT + h - 8, 36, labelBrush));
        Plot.Children.Add(Text("меньше — лучше", padL + Math.Max(36, (w - 96) / 2), padT + h + 4, 140, labelBrush, italic: true));

        foreach (var m in snapshot.Markers)
            DrawMarker(m, padL, w, padT, h, tMax, axisBrush, doneBrush, labelBrush);
    }

    private void DrawTimeAxis(double padL, double w, double h, double padT, double tMax,
        Brush axisBrush, Brush labelBrush)
    {
        Plot.Children.Add(new Line
        {
            X1 = padL, Y1 = padT + h, X2 = padL + w, Y2 = padT + h,
            Stroke = axisBrush, StrokeThickness = 1,
        });
        Plot.Children.Add(Text("0 с", padL - 4, padT + h + 4, 60, labelBrush));
        Plot.Children.Add(Text($"{tMax:F0} с", padL + w - 30, padT + h + 4, 60, labelBrush));
    }

    private double _lastLabelX = double.NegativeInfinity;

    private void DrawMarker(TrajectoryMarker m, double padL, double w,
        double padT, double h, double tMax, Brush axisBrush, Brush doneBrush, Brush labelBrush)
    {
        double x = padL + (m.Elapsed.TotalSeconds / Math.Max(1, tMax)) * w;
        bool terminal = m.Label is "Готово" or "Остановлено";
        Plot.Children.Add(new Line
        {
            X1 = x, Y1 = padT - 4, X2 = x, Y2 = padT + h,
            Stroke = terminal ? doneBrush : axisBrush,
            StrokeThickness = terminal ? 2 : 1,
            StrokeDashArray = terminal ? null : [4, 3],
        });
        if (x - _lastLabelX >= 46)
        {
            string text = m.Label.StartsWith("Запуск ", StringComparison.Ordinal)
                ? "№" + m.Label["Запуск ".Length..]
                : m.Label;
            Plot.Children.Add(Text(text, Math.Min(x + 3, padL + w - 90), 0, 120, labelBrush, terminal));
            _lastLabelX = x;
        }
    }

    private static TextBlock Text(string s, double x, double y, double width, Brush foreground,
        bool bold = false, bool italic = false) =>
        new()
        {
            Text = s, FontSize = 10, Opacity = 0.9, Width = width,
            Foreground = foreground,
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
