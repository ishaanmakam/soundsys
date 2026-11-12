using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Simulation;

namespace Soundsys.App.ViewModels;

/// <summary>
/// Hidden Developer section: switch to simulated devices (takes effect after a restart) and, when simulated, drive the
/// simulated world: speakers on/off, phones play/pause/leave, laptop playing, and a sample error.
/// </summary>
public sealed partial class DeveloperViewModel : ObservableObject
{
    public const string SampleError = "Couldn't receive audio from the phone. Try turning Bluetooth off and on on the phone.";

    private readonly EngineRunner _runner;
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly SimulatedBackend? _sim;
    private bool _loading;

    public DeveloperViewModel(EngineRunner runner, AppServices services, MainViewModel main)
    {
        _runner = runner;
        _services = services;
        _main = main;
        _sim = services.Simulation;
        _loading = true;
        UseSimulated = services.UseSimulatedPreference || runner.Engine.IsSimulated;
        LaptopPlaying = _sim?.LaptopPlaying ?? false;
        _loading = false;
        if (_sim is not null)
        {
            foreach (var phone in _sim.Phones.GetPairedPhones())
            {
                SimPhones.Add(new SimPhoneViewModel(phone, _sim.Phones));
            }
        }

        Refresh();
    }

    public bool IsSimulated => _sim is not null;

    public ObservableCollection<SimSpeakerViewModel> SimSpeakers { get; } = [];

    public ObservableCollection<SimPhoneViewModel> SimPhones { get; } = [];

    [ObservableProperty]
    public partial bool UseSimulated { get; set; }

    /// <summary>The preference differs from what is running: offer "Restart soundsys".</summary>
    [ObservableProperty]
    public partial bool RestartNeeded { get; set; }

    [ObservableProperty]
    public partial bool LaptopPlaying { get; set; }

    /// <summary>Re-reads the simulated speakers (UI thread).</summary>
    public void Refresh()
    {
        if (_sim is null)
        {
            return;
        }

        foreach (var s in _sim.GetSpeakers())
        {
            var row = SimSpeakers.FirstOrDefault(r => r.Id == s.Id);
            if (row is null)
            {
                row = new SimSpeakerViewModel(s.Id, _sim);
                SimSpeakers.Add(row);
            }

            row.Apply(s.Name, s.State == EndpointState.Active);
        }

        foreach (var p in SimPhones)
        {
            p.Refresh();
        }
    }

    [RelayCommand]
    private void Restart() => _services.Restart(UseSimulated);

    [RelayCommand]
    private void ShowSampleError() => _main.ReportError(SampleError);

    /// <summary>Clears both speaker choices (the empty state).</summary>
    [RelayCommand]
    private async Task ForgetSpeakersAsync()
    {
        await _runner.RunAsync(e =>
        {
            e.Stop();
            e.UpdateSettings(x =>
            {
                x.SpeakerAId = null;
                x.SpeakerBId = null;
            });
        });
        _main.SpeakersPage.Reload();
        _main.Refresh();
    }

    partial void OnUseSimulatedChanged(bool value)
    {
        RestartNeeded = value != _runner.Engine.IsSimulated;
        if (_loading)
        {
            return;
        }

        try
        {
            _services.SaveUseSimulated(value);
        }
        catch (Exception ex)
        {
            _main.ReportError(EngineRunner.Describe(ex));
        }
    }

    partial void OnLaptopPlayingChanged(bool value)
    {
        if (!_loading && _sim is not null)
        {
            _sim.LaptopPlaying = value;
        }
    }
}

public sealed partial class SimSpeakerViewModel(string id, SimulatedBackend sim) : ObservableObject
{
    private bool _applying;

    public string Id => id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    internal void Apply(string name, bool on)
    {
        _applying = true;
        Name = name;
        IsOn = on;
        _applying = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_applying)
        {
            _ = Task.Run(() => sim.SetSpeakerAvailable(id, value));
        }
    }
}

public sealed partial class SimPhoneViewModel(PhoneInfo phone, SimulatedPhoneLink link) : ObservableObject
{
    public string Name => phone.Name;

    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    internal void Refresh() => StateText = link.State(phone.Id) switch
    {
        PhoneLinkState.Streaming => "playing",
        PhoneLinkState.Connected => "connected",
        PhoneLinkState.Waiting => "not connected",
        PhoneLinkState.Disabled => "not listening",
        _ => "error",
    };

    [RelayCommand]
    private void Play() => Do(() => link.SimulatePlay(phone.Id));

    [RelayCommand]
    private void Pause() => Do(() => link.SimulatePause(phone.Id));

    [RelayCommand]
    private void Leave() => Do(() => link.SimulateLeave(phone.Id));

    private void Do(Action action)
    {
        action();
        Refresh();
    }
}
