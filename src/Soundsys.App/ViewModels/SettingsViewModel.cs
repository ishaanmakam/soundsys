using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundsys.Engine.Updates;

namespace Soundsys.App.ViewModels;

/// <summary>The Settings tab: startup, auto-start, keep awake, updates, version (and the hidden Developer section).</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public const int UnlockClicks = 5;
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    private readonly EngineRunner _runner;
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private bool _loading;
    private int _versionClicks;

    public SettingsViewModel(EngineRunner runner, AppServices services, MainViewModel main)
    {
        _runner = runner;
        _services = services;
        _main = main;
        Developer = new DeveloperViewModel(runner, services, main);
        var s = runner.Engine.Settings;
        _loading = true;
        try
        {
            StartWithWindows = SafeIsEnabled();
            AutoStart = s.AutoStartWhenSpeakersOn;
            KeepAwake = s.KeepAwake;
            CheckAutomatically = s.CheckForUpdates;
            DeveloperUnlocked = s.DeveloperOptionsUnlocked || runner.Engine.IsSimulated;
        }
        finally
        {
            _loading = false;
        }

        VersionText = $"Version {services.Version}";
    }

    /// <summary>An update was found (the tray shows a balloon).</summary>
    public event EventHandler<UpdateCheckResult>? UpdateFound;

    public DeveloperViewModel Developer { get; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool AutoStart { get; set; }

    [ObservableProperty]
    public partial bool KeepAwake { get; set; }

    [ObservableProperty]
    public partial bool CheckAutomatically { get; set; }

    [ObservableProperty]
    public partial string VersionText { get; set; }

    [ObservableProperty]
    public partial bool DeveloperUnlocked { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingUpdates { get; set; }

    [ObservableProperty]
    public partial string? UpdateResultText { get; set; }

    /// <summary>Release page when a newer version exists.</summary>
    [ObservableProperty]
    public partial string? UpdateUrl { get; set; }

    public bool CanCheckUpdates => _services.CheckForUpdates is not null;

    /// <summary>Startup check: only if enabled and the last one was more than 24 h ago.</summary>
    public async Task CheckOnStartupAsync()
    {
        var s = _runner.Engine.Settings;
        if (!s.CheckForUpdates || _services.CheckForUpdates is null)
        {
            return;
        }

        if (s.LastUpdateCheck is { } last && _services.Now() - last < UpdateInterval)
        {
            return;
        }

        await CheckUpdatesAsync();
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (_services.CheckForUpdates is null || IsCheckingUpdates)
        {
            return;
        }

        IsCheckingUpdates = true;
        UpdateResultText = "Checking…";
        UpdateUrl = null;
        try
        {
            var result = await _services.CheckForUpdates(CancellationToken.None);
            UpdateResultText = result.Message;
            UpdateUrl = result.UpdateAvailable ? result.Url ?? UpdateChecker.ReleasesPage : null;
            var now = _services.Now();
            _runner.Post(e => e.UpdateSettings(x => x.LastUpdateCheck = now));
            if (result.UpdateAvailable)
            {
                UpdateFound?.Invoke(this, result);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"soundsys update check: {ex}");
            UpdateResultText = "Couldn't check for updates. Are you online?";
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (UpdateUrl is not null)
        {
            _services.OpenUri(UpdateUrl);
        }
    }

    /// <summary>Five clicks on the version label unlock the Developer section.</summary>
    [RelayCommand]
    private void VersionClicked()
    {
        if (DeveloperUnlocked)
        {
            return;
        }

        if (++_versionClicks >= UnlockClicks)
        {
            DeveloperUnlocked = true;
            _runner.Post(e => e.UpdateSettings(x => x.DeveloperOptionsUnlocked = true));
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        var registration = _services.Startup;
        _ = SetStartupAsync(registration, value);
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (!_loading)
        {
            _runner.Post(e => e.UpdateSettings(x => x.AutoStartWhenSpeakersOn = value));
        }
    }

    partial void OnKeepAwakeChanged(bool value)
    {
        if (!_loading)
        {
            _runner.Post(e => e.UpdateSettings(x => x.KeepAwake = value));
        }
    }

    partial void OnCheckAutomaticallyChanged(bool value)
    {
        if (!_loading)
        {
            _runner.Post(e => e.UpdateSettings(x => x.CheckForUpdates = value));
        }
    }

    private async Task SetStartupAsync(Engine.Abstractions.IStartupRegistration registration, bool value)
    {
        var ok = await _runner.RunAsync(e =>
        {
            registration.SetEnabled(value);
            e.UpdateSettings(x => x.StartWithWindows = value);
        });
        if (!ok)
        {
            _main.ReportError("Couldn't change the Windows startup setting.");
        }

        _loading = true;
        try
        {
            StartWithWindows = SafeIsEnabled();
        }
        finally
        {
            _loading = false;
        }
    }

    private bool SafeIsEnabled()
    {
        try
        {
            return _services.Startup.IsEnabled;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"soundsys startup registration: {ex}");
            return false;
        }
    }
}
