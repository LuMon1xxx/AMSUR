using System.Windows;
using System.Windows.Threading;
using Amsur.Wpf;

namespace Amsur.Tests;

// P-D5/D-48: общий UI-поток для ВСЕХ интерфейсных тестов.
// Причина: DynamicResource в теме + общий Application несовместимы
// с несколькими STA-потоками (VerifyAccess при резолве через потоки).
// Поэтому: один фоновый STA-поток с вечным Dispatcher на весь прогон,
// один App на нём, тесты маршалируются очередью и ждут.
// D-48-гард в App.OnStartup не даёт ctor-посту поднять прод-окно.
static class UiTestHost
{
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;
    private static bool _appLoaded;
    private static readonly object InitLock = new();

    private static void EnsureThread()
    {
        lock (InitLock)
        {
            if (_thread is not null) return;
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run(); // живёт до конца процесса
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            ready.Wait();
        }
    }

    public static void Run(Func<Task> body)
    {
        EnsureThread();
        Exception? error = null;
        var done = new ManualResetEventSlim();
        _dispatcher!.BeginInvoke(new Action(async () =>
        {
            try { await body(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }));
        if (!done.Wait(TimeSpan.FromMinutes(5)))
            throw new Xunit.Sdk.XunitException("UI thread hung");
        if (error is not null) throw error;
    }

    public static App EnsureApp()
    {
        // Только внутри Run (на UI-потоке). Тема грузится один раз.
        var app = System.Windows.Application.Current as App ?? new App();
        if (!_appLoaded)
        {
            app.InitializeComponent();
            _appLoaded = true;
        }
        // ПОСЛЕ InitializeComponent: XAML перетирает ShutdownMode своим
        // OnMainWindowClose — для тестов нужен Explicit (иначе первое закрытое
        // окно гасит общий Application; в проде режим из App.xaml не меняется).
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return app;
    }
}
