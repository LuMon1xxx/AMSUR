using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Amsur.Application;
using Amsur.Wpf;
using Amsur.Wpf.Views;

namespace Amsur.Tests;

// Живой рендер MainWindow в обеих темах (Light + Dark) без DWM/PrintWindow,
// плюс при необходимости сохранение 12 скриншотов 1920x1080 для визуального аудита.
public sealed class UiVisualCaptureTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"amsur-vis-{Guid.NewGuid():N}");

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void MainWindow_AllViewsRenderInLightAndDark_AndCaptureShotsWhenConfigured()
    {
        UiTestHost.Run(async () =>
        {
            Directory.CreateDirectory(_dir);
            var app = UiTestHost.EnsureApp();
            MainWindow? main = null;
            try
            {
                app.ApplyTheme("light");
                var session = new AppSession(_dir);
                await session.InitAsync();
                typeof(App).GetProperty("Session")!.SetValue(app, session);

                await session.ImportLoadAsync(DemoSchoolTests.DemoRows(), days: 5, slots: 7);
                session.SetGenerateMode("QUICK");

                main = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000,
                    ShowInTaskbar = false,
                };
                main.Show();
                await FlushAsync(main, 100);
                main.WindowState = WindowState.Normal;
                main.Width = 1536;
                main.Height = 864;
                main.UpdateLayout();
                await FlushAsync(main, 150);

                string? shotsDir = Environment.GetEnvironmentVariable("AMSUR_CAPTURE_SHOTS_DIR");
                if (!string.IsNullOrWhiteSpace(shotsDir))
                    Directory.CreateDirectory(shotsDir);

                // --- 1. Генерация на демо-данных в светлой теме ---
                main.OpenGenerate(session.Data!.ToProblemInput());
                GenerateView? genView = null;
                GenerateViewModel? genVm = null;
                for (int i = 0; i < 200; i++)
                {
                    genView = main.ViewHost.Content as GenerateView;
                    genVm = genView?.DataContext as GenerateViewModel;
                    if (genVm is { CanStop: false, Top5.Cards.Count: > 0 }) break;
                    await FlushAsync(main, 100);
                }
                Assert.NotNull(genView);
                Assert.NotNull(genVm);
                Assert.True(genVm!.Top5.Cards.Count > 0, "Expected Top-5 candidate cards after QUICK run");

                await FlushAsync(main, 450);
                Capture(main, shotsDir, "light-03-generate.png");

                // Проверяем кнопку «Принять» на ВСЕХ карточках Top-5 (не только Rank 1),
                // плюс дополнительно снимаем прокрученную секцию карточек Top-5.
                var acceptBtns = FindVisualChildren<Button>(genView!)
                    .Where(b => b.IsVisible && b.Content is string s && s.Contains("Принять этот вариант"))
                    .ToList();
                Assert.Equal(genVm.Top5.Cards.Count, acceptBtns.Count);
                var genScroll = FindVisualChildren<ScrollViewer>(genView!).FirstOrDefault();
                if (genScroll is not null)
                {
                    genScroll.ScrollToVerticalOffset(480);
                    await FlushAsync(main, 250);
                    Capture(main, shotsDir, "light-03b-generate-cards.png");
                    genScroll.ScrollToVerticalOffset(0);
                    await FlushAsync(main, 150);
                }

                var targetCard = genVm.Top5.Cards.Count >= 2 ? genVm.Top5.Cards[1] : genVm.Top5.Cards[0];
                genVm.RequestAccept(targetCard);

                for (int i = 0; i < 40 && string.IsNullOrWhiteSpace(genVm.ActiveVersionText); i++)
                    await FlushAsync(main, 100);
                Assert.False(string.IsNullOrWhiteSpace(genVm.ActiveVersionText));
                var expectedQ = QualityRating.FromBreakdown(targetCard.Candidate.Breakdown);
                Assert.NotNull(session.LastQuality);
                Assert.Equal(expectedQ.Label, session.LastQuality!.Label);
                Assert.Equal(expectedQ.Improvements, session.LastQuality.Improvements);

                // --- 2. Остальные экраны в светлой теме ---
                main.NavigateTo("Dashboard");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "light-01-dashboard.png");

                main.NavigateTo("Data");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "light-02-data.png");

                await NavigateToScheduleLoadedAsync(main);
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "light-04-schedule.png");

                main.NavigateTo("Export");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "light-05-export.png");

                main.NavigateTo("Settings");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "light-06-settings.png");

                // --- 3. Живое переключение на тёмную тему через UI-переключатель ---
                var themeBox = (CheckBox)main.FindName("ThemeBox")!;
                themeBox.IsChecked = true;
                for (int i = 0; i < 20 && session.ThemeName != "dark"; i++)
                    await FlushAsync(main, 100);
                Assert.Equal("dark", session.ThemeName);
                Assert.Equal(Color.FromRgb(0x11, 0x13, 0x15), (Color)app.Resources["CBg"]);
                Assert.Equal(Color.FromRgb(0xEC, 0xEF, 0xF3), ((SolidColorBrush)app.Resources["BText"]).Color);

                main.NavigateTo("Dashboard");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "dark-01-dashboard.png");

                main.NavigateTo("Data");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "dark-02-data.png");

                main.NavigateTo("Generate");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "dark-03-generate.png");
                if (main.ViewHost.Content is GenerateView darkGen)
                {
                    var darkScroll = FindVisualChildren<ScrollViewer>(darkGen).FirstOrDefault();
                    if (darkScroll is not null)
                    {
                        darkScroll.ScrollToVerticalOffset(480);
                        await FlushAsync(main, 250);
                        Capture(main, shotsDir, "dark-03b-generate-cards.png");
                        darkScroll.ScrollToVerticalOffset(0);
                        await FlushAsync(main, 150);
                    }
                }

                await NavigateToScheduleLoadedAsync(main);
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "dark-04-schedule.png");

                main.NavigateTo("Export");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "dark-05-export.png");

                main.NavigateTo("Settings");
                await FlushAsync(main, 500);
                Capture(main, shotsDir, "dark-06-settings.png");

                // --- 4. Обратный живой откат dark → light через UI-переключатель ---
                themeBox.IsChecked = false;
                for (int i = 0; i < 20 && session.ThemeName != "light"; i++)
                    await FlushAsync(main, 100);
                Assert.Equal("light", session.ThemeName);
                Assert.Equal(Color.FromRgb(0xF5, 0xF5, 0xF3), (Color)app.Resources["CBg"]);
                Assert.Equal(Color.FromRgb(0x18, 0x18, 0x1B), ((SolidColorBrush)app.Resources["BText"]).Color);
            }
            finally
            {
                app.ApplyTheme("light");
                main?.Close();
            }
        });
    }

    private static async Task FlushAsync(Window window, int delayMs)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private static async Task NavigateToScheduleLoadedAsync(MainWindow main)
    {
        main.NavigateTo("Schedule");
        for (int i = 0; i < 40; i++)
        {
            if (main.ViewHost.Content is ScheduleView sv && sv.StatusText.Text != "Загрузка…")
                break;
            await FlushAsync(main, 100);
        }
    }

    private static void Capture(Window main, string? shotsDir, string fileName)
    {
        var root = VisualTreeHelper.GetChild(main, 0) as FrameworkElement ?? (FrameworkElement)main;
        double wDip = root.ActualWidth > 0 ? root.ActualWidth : main.ActualWidth;
        double hDip = root.ActualHeight > 0 ? root.ActualHeight : main.ActualHeight;
        int pw = Math.Max(1, (int)Math.Round(wDip * 1.25));
        int ph = Math.Max(1, (int)Math.Round(hDip * 1.25));

        var rtb = new RenderTargetBitmap(pw, ph, 120, 120, PixelFormats.Pbgra32);
        rtb.Render(root);

        int stride = pw * 4;
        var pixels = new byte[stride * ph];
        rtb.CopyPixels(pixels, stride, 0);
        var distinct = new HashSet<int>();
        for (int y = 0; y < ph; y += Math.Max(1, ph / 36))
        {
            for (int x = 0; x < pw; x += Math.Max(1, pw / 64))
            {
                int off = y * stride + x * 4;
                int rgb = (pixels[off + 2] << 16) | (pixels[off + 1] << 8) | pixels[off];
                distinct.Add(rgb);
            }
        }
        Assert.True(distinct.Count > 15, $"{fileName}: rendered image looks blank ({distinct.Count} colors)");

        if (!string.IsNullOrWhiteSpace(shotsDir))
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(shotsDir, fileName));
            enc.Save(fs);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }
}
