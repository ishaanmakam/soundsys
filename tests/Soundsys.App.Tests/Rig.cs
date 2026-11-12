using Soundsys.App.ViewModels;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Settings;
using Soundsys.Engine.Simulation;
using Soundsys.Engine.Updates;

namespace Soundsys.App.Tests;

/// <summary>
/// A simulated world (virtual time, stepped by the test) with the real engine and the real view model on top.
/// The test thread's SynchronizationContext is cleared for the rig's lifetime so awaits resume on the thread pool
/// instead of racing virtual time (see EngineTests.Drive).
/// </summary>
internal sealed class Rig : IDisposable
{
    public const string Desk = "sim:jbl-flip-6";
    public const string Bed = "sim:jbl-charge-5";
    public const string Laptop = "sim:surface-speakers";
    public const string IPhone = "sim:phone-iphone";
    public const string Pixel = "sim:phone-pixel";

    private readonly SynchronizationContext? _previousContext;

    public Rig(
        bool empty = false,
        AppSettings? settings = null,
        Func<CancellationToken, Task<UpdateCheckResult>>? checkForUpdates = null,
        bool exposeSimulation = true,
        DateTimeOffset? now = null)
    {
        _previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        Scheduler = new SimScheduler();
        Backend = new SimulatedBackend(Scheduler);
        Store = new InMemorySettingsStore(settings);
        Engine = new SyncEngine(Backend, Scheduler, Store, new FakePower());
        if (empty)
        {
            Engine.UpdateSettings(s =>
            {
                s.SpeakerAId = null;
                s.SpeakerBId = null;
            });
        }

        Services = new AppServices
        {
            Version = "0.1.0-alpha",
            Startup = Startup,
            CheckForUpdates = checkForUpdates,
            OpenUri = Opened.Add,
            Restart = Restarts.Add,
            SaveUseSimulated = SavedSimulated.Add,
            UseSimulatedPreference = false,
            Simulation = exposeSimulation ? Backend : null,
            Now = () => now ?? new DateTimeOffset(2026, 11, 12, 3, 0, 0, TimeSpan.Zero),
        };
        Vm = new MainViewModel(Engine, Services);
    }

    public SimScheduler Scheduler { get; }

    public SimulatedBackend Backend { get; }

    public InMemorySettingsStore Store { get; }

    public SyncEngine Engine { get; }

    public AppServices Services { get; }

    public MainViewModel Vm { get; }

    public InMemoryStartupRegistration Startup { get; } = new();

    public List<string> Opened { get; } = [];

    public List<bool> Restarts { get; } = [];

    public List<bool> SavedSimulated { get; } = [];

    /// <summary>Advances virtual time, polling the view model at 10 Hz like the app does.</summary>
    public void Run(double seconds)
    {
        var end = Scheduler.Now + seconds;
        while (Scheduler.Now < end - 1e-9)
        {
            Scheduler.RunFor(Math.Min(0.1, end - Scheduler.Now));
            Vm.Refresh();
        }
    }

    /// <summary>Polls at 10 Hz until <paramref name="condition"/> holds; returns the virtual seconds it took.</summary>
    public double RunUntil(Func<bool> condition, double maxSeconds = 10, string? what = null)
    {
        var start = Scheduler.Now;
        while (!condition())
        {
            if (Scheduler.Now - start > maxSeconds)
            {
                throw new TimeoutException($"Not true after {maxSeconds} virtual s: {what ?? "condition"} (headline '{Vm.Headline}', detail '{Vm.Detail}').");
            }

            Scheduler.RunFor(0.1);
            Vm.Refresh();
        }

        return Scheduler.Now - start;
    }

    /// <summary>Steps virtual time until the task completes (engine work may finish on the thread pool).</summary>
    public void Await(Task task, double maxSeconds = 30)
    {
        var end = Scheduler.Now + maxSeconds;
        while (!task.IsCompleted && Scheduler.Now < end)
        {
            Scheduler.RunFor(0.01);
        }

        if (!task.Wait(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException($"Task not finished after {maxSeconds} virtual s (state {Engine.GetStatus().State}, step '{Engine.GetStatus().CalibrationStep}').");
        }

        Vm.Refresh();
    }

    /// <summary>Lets fire-and-forget engine calls (sliders, toggles) land, then polls.</summary>
    public void Settle(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not true after 10 s: {what}");
            }

            Thread.Sleep(5);
        }

        Vm.Refresh();
    }

    public void StartLaptopMode()
    {
        Engine.SetMode(SourceMode.Laptop);
        Assert.True(Engine.Start());
        Run(2);
    }

    public void StartPhonePlaying()
    {
        Engine.SetMode(SourceMode.Phone);
        Assert.True(Engine.Start());
        Run(0.5);
        Backend.Phones.SimulatePlay(IPhone);
        Run(2);
    }

    public void Dispose()
    {
        Vm.Dispose();
        Engine.Dispose();
        Scheduler.Dispose();
        SynchronizationContext.SetSynchronizationContext(_previousContext);
    }

    private sealed class FakePower : IPowerManager
    {
        public void SetKeepAwake(bool keepAwake)
        {
        }
    }
}
