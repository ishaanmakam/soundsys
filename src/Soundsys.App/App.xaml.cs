using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Soundsys.App.Hosting;
using Soundsys.App.Theme;
using Soundsys.App.Tray;
using Soundsys.App.ViewModels;

namespace Soundsys.App;

/// <summary>
/// Startup: single instance, theme, composition (<see cref="AppHost"/>), view model, window, tray and the 10 Hz poll.
/// The window hides to the tray on close; Quit disposes the engine (stops audio and restores everything).
/// </summary>
public partial class App : Application
{
    private static readonly TimeSpan VisiblePoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HiddenPoll = TimeSpan.FromMilliseconds(500);

    private CommandLine _commandLine = CommandLine.Parse([]);
    private SingleInstance? _instance;
    private AppHost? _host;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private DispatcherTimer? _poll;
    private bool _quitting;
    private bool _hideHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Start();
        _commandLine = CommandLine.Parse(e.Args);
        _instance = SingleInstance.TryAcquire(waitForPrevious: _commandLine.Restarted);
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Trace.TraceError($"soundsys unobserved task: {args.Exception}");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Trace.TraceError($"soundsys fatal: {args.ExceptionObject}");
        SessionEnding += (_, _) => Quit();

        ThemeManager.FollowSystem(this);

        try
        {
            _host = AppHost.Create(_commandLine);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"soundsys startup: {ex}");
            MessageBox.Show(
                "soundsys couldn't start its audio engine. Restart the laptop and try again. If it keeps happening, the log is at "
                + Log.FilePath + ".",
                "soundsys",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _instance.Dispose();
            Shutdown();
            return;
        }

        var services = new AppServices
        {
            Version = AppHost.Version,
            Startup = _host.Startup,
            CheckForUpdates = _host.CheckForUpdatesAsync,
            OpenUri = Shell.OpenUri,
            Restart = Restart,
            SaveUseSimulated = _host.SaveUseSimulated,
            UseSimulatedPreference = _host.PreferSimulated,
            Simulation = _host.Simulation,
            Post = action => Dispatcher.BeginInvoke(action),
        };

        _vm = new MainViewModel(_host.Engine, services);
        _window = new MainWindow(_vm);
        _window.Closing += OnWindowClosing;
        _window.IsVisibleChanged += (_, _) => UpdatePollRate();
        _tray = new TrayIcon(_vm, ShowWindow, Quit);
        _vm.SettingsPage.UpdateFound += (_, result) => _tray?.ShowBalloon("Update available", result.Message);

        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = VisiblePoll };
        _poll.Tick += (_, _) => _vm.Refresh();
        _poll.Start();

        _instance.ListenForShow(() => Dispatcher.BeginInvoke(ShowWindow));
        if (!_commandLine.Tray)
        {
            ShowWindow();
        }

        UpdatePollRate();
        _ = _vm.SettingsPage.CheckOnStartupAsync();
    }

    private void ShowWindow()
    {
        if (_window is null || _quitting)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
            _window.PlaceNearTray();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        _vm?.Refresh();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        e.Cancel = true;
        _window?.Hide();
        if (!_hideHintShown)
        {
            _hideHintShown = true;
            _tray?.ShowBalloon("soundsys", "soundsys is still running in the tray");
        }
    }

    private void UpdatePollRate()
    {
        if (_poll is not null)
        {
            _poll.Interval = _window?.IsVisible == true ? VisiblePoll : HiddenPoll;
        }
    }

    /// <summary>Starts a fresh copy (same arguments, simulated as chosen), then exits.</summary>
    private void Restart(bool simulated)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in _commandLine.ForRestart(simulated))
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
        }
        catch (Exception ex)
        {
            _vm?.ReportUnexpected(ex);
            return;
        }

        Quit();
    }

    private void Quit()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _poll?.Stop();
        _tray?.Dispose();
        _window?.Close();
        _vm?.Dispose();
        _host?.Dispose();
        _instance?.Dispose();
        Trace.TraceInformation("soundsys stopped");
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ReportUnexpected(e.Exception);
        }
        else
        {
            Trace.TraceError($"soundsys unexpected: {e.Exception}");
        }

        e.Handled = true;
    }
}
