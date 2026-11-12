using System.Windows;
using System.Windows.Threading;

namespace Soundsys.App.Tests;

/// <summary>
/// One long-lived STA thread with a running dispatcher for every WPF test (WPF objects and the Application are tied to
/// the thread that created them, and there can be only one Application per process).
/// </summary>
internal static class WpfThread
{
    private static readonly Lazy<Dispatcher> Dispatcher = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Invoke(Action action) => Dispatcher.Value.Invoke(action, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromMinutes(2));

    public static T Invoke<T>(Func<T> func) => Dispatcher.Value.Invoke(func, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromMinutes(2));

    /// <summary>Our App (resources from App.xaml) without running its startup.</summary>
    public static App EnsureApp()
    {
        if (Application.Current is App app)
        {
            return app;
        }

        app = new App();
        app.InitializeComponent();
        return app;
    }

    /// <summary>Runs queued dispatcher work (bindings, layout) down to ContextIdle.</summary>
    public static void DoEvents()
    {
        var frame = new DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static Dispatcher Start()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        return dispatcher!;
    }
}
