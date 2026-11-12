using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Soundsys.App.ViewModels;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Simulation;
using Soundsys.Engine.Updates;
using Soundsys.Windows;

namespace Soundsys.App.Hosting;

/// <summary>
/// Composition root. Simulated (<c>--simulated</c> or the saved preference): a real-time <see cref="SimScheduler"/> and
/// <see cref="SimulatedBackend"/>, entirely in memory and silent, with their own settings file so pretend speaker IDs
/// never overwrite the real ones. Otherwise: WASAPI backend, QPC timers, keep-awake and the Run-key startup entry.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private readonly IAudioBackend _backend;
    private readonly SimScheduler? _simScheduler;
    private readonly PowerManager? _power;
    private readonly JsonSettingsStore _mainStore;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _disposed;

    private AppHost(SyncEngine engine, IAudioBackend backend, SimScheduler? simScheduler, PowerManager? power,
        IStartupRegistration startup, JsonSettingsStore mainStore, bool preferSimulated)
    {
        Engine = engine;
        _backend = backend;
        _simScheduler = simScheduler;
        _power = power;
        Startup = startup;
        _mainStore = mainStore;
        PreferSimulated = preferSimulated;
    }

    public static string SimulatedSettingsPath =>
        Path.Combine(Path.GetDirectoryName(JsonSettingsStore.DefaultPath)!, "settings.simulated.json");

    public SyncEngine Engine { get; }

    public IStartupRegistration Startup { get; }

    public bool IsSimulated => _simScheduler is not null;

    public SimulatedBackend? Simulation => _backend as SimulatedBackend;

    /// <summary>The saved "Use simulated devices" preference.</summary>
    public bool PreferSimulated { get; private set; }

    /// <summary>Informational version without build metadata, e.g. "0.1.0-alpha".</summary>
    public static string Version
    {
        get
        {
            var info = typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return SemVer.Normalize(info ?? typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
        }
    }

    public static AppHost Create(CommandLine commandLine)
    {
        var mainStore = new JsonSettingsStore(JsonSettingsStore.DefaultPath);
        var prefs = mainStore.Load();
        if (commandLine.Simulated || prefs.UseSimulatedDevices)
        {
            var scheduler = new SimScheduler();
            scheduler.EventFailed += (_, ex) => Trace.TraceError($"soundsys simulation: {ex}");
            var backend = new SimulatedBackend(scheduler);
            var engine = new SyncEngine(backend, scheduler, new JsonSettingsStore(SimulatedSettingsPath));
            scheduler.StartRealTime();
            return new AppHost(engine, backend, scheduler, null, new InMemoryStartupRegistration(), mainStore, prefs.UseSimulatedDevices);
        }

        var real = new WindowsAudioBackend();
        var power = new PowerManager();
        try
        {
            var engine = new SyncEngine(real, new ThreadTimerScheduler(QpcClock.Instance), mainStore, power);
            return new AppHost(engine, real, null, power, new StartupRegistration(), mainStore, prefs.UseSimulatedDevices);
        }
        catch
        {
            power.Dispose();
            real.Dispose();
            throw;
        }
    }

    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
        new UpdateChecker(_http).CheckAsync(Version, cancellationToken);

    /// <summary>Saves the preference in the main settings file (read at the next start).</summary>
    public void SaveUseSimulated(bool value)
    {
        PreferSimulated = value;
        if (IsSimulated)
        {
            var s = _mainStore.Load();
            s.UseSimulatedDevices = value;
            _mainStore.Save(s);
        }
        else
        {
            Engine.UpdateSettings(s => s.UseSimulatedDevices = value);
        }
    }

    /// <summary>Stops audio and restores everything the engine changed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Try(Engine.Dispose);
        Try(_backend.Dispose);
        Try(() => _simScheduler?.Dispose());
        Try(() => _power?.Dispose());
        _http.Dispose();

        static void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Trace.TraceError($"soundsys shutdown: {ex}");
            }
        }
    }
}
