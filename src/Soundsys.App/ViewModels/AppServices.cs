using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Simulation;
using Soundsys.Engine.Updates;

namespace Soundsys.App.ViewModels;

/// <summary>What the view models need from the app shell (fakes in tests).</summary>
public sealed class AppServices
{
    /// <summary>Display version, e.g. "0.1.0-alpha".</summary>
    public string Version { get; init; } = "0.0.0";

    public IStartupRegistration Startup { get; init; } = new InMemoryStartupRegistration();

    /// <summary>Checks GitHub Releases (null = updates unavailable).</summary>
    public Func<CancellationToken, Task<UpdateCheckResult>>? CheckForUpdates { get; init; }

    /// <summary>Opens a URL or ms-settings: URI.</summary>
    public Action<string> OpenUri { get; init; } = _ => { };

    /// <summary>Restarts soundsys (argument: use simulated devices).</summary>
    public Action<bool> Restart { get; init; } = _ => { };

    /// <summary>Saves the "Use simulated devices" preference for the next start.</summary>
    public Action<bool> SaveUseSimulated { get; init; } = _ => { };

    /// <summary>The preference as saved (may differ from what is running until a restart).</summary>
    public bool UseSimulatedPreference { get; init; }

    /// <summary>The simulated world, when running simulated.</summary>
    public SimulatedBackend? Simulation { get; init; }

    /// <summary>Runs an action on the UI thread (tests: inline).</summary>
    public Action<Action> Post { get; init; } = a => a();

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;
}

/// <summary>"Start with Windows" stand-in for simulated mode and tests (touches nothing).</summary>
public sealed class InMemoryStartupRegistration : IStartupRegistration
{
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}
