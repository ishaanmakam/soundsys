using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Soundsys.Engine.Abstractions;

namespace Soundsys.App.ViewModels;

/// <summary>The Phones tab: paired phones, each with an on/off toggle and its live state.</summary>
public sealed partial class PhonesViewModel(EngineRunner runner) : ObservableObject
{
    public const string Explainer = "On your phone, open Bluetooth and choose this laptop.";

    public ObservableCollection<PhoneRowViewModel> Phones { get; } = [];

    [ObservableProperty]
    public partial bool HasPhones { get; set; }

    public void Reload() => Refresh();

    /// <summary>Updates the list in place (UI thread).</summary>
    public void Refresh()
    {
        IReadOnlyList<(PhoneStatus Phone, bool Enabled)> phones;
        try
        {
            phones = runner.Engine.GetPhones();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"soundsys phones: {ex}");
            return;
        }

        for (var i = Phones.Count - 1; i >= 0; i--)
        {
            if (!phones.Any(p => p.Phone.Id == Phones[i].Id))
            {
                Phones.RemoveAt(i);
            }
        }

        foreach (var (phone, enabled) in phones)
        {
            var row = Phones.FirstOrDefault(r => r.Id == phone.Id);
            if (row is null)
            {
                row = new PhoneRowViewModel(phone.Id, runner);
                Phones.Add(row);
            }

            row.Apply(phone, enabled);
        }

        HasPhones = Phones.Count > 0;
    }

    internal static string StateText(PhoneStatus phone, bool enabled) => !enabled ? "Off" : phone.State switch
    {
        PhoneLinkState.Streaming => "Playing",
        PhoneLinkState.Connected => "Connected",
        PhoneLinkState.Error => string.IsNullOrWhiteSpace(phone.Detail) ? "Couldn't connect" : phone.Detail!,
        _ => "Not connected",
    };
}

public sealed partial class PhoneRowViewModel(string id, EngineRunner runner) : ObservableObject
{
    private readonly EditHold _hold = new();
    private bool _applying;

    public string Id => id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string StateText { get; set; } = "";

    /// <summary>Streaming or connected (the dot lights up).</summary>
    [ObservableProperty]
    public partial bool IsLive { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    internal void Apply(PhoneStatus phone, bool enabled)
    {
        _applying = true;
        try
        {
            Name = phone.Name;
            if (!_hold.Active)
            {
                Enabled = enabled;
            }

            StateText = PhonesViewModel.StateText(phone, Enabled);
            IsPlaying = Enabled && phone.State == PhoneLinkState.Streaming;
            IsLive = Enabled && phone.State is PhoneLinkState.Streaming or PhoneLinkState.Connected;
        }
        finally
        {
            _applying = false;
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        _hold.Touch();
        if (!value)
        {
            StateText = "Off";
            IsLive = IsPlaying = false;
        }

        runner.Post(e => e.SetPhoneEnabled(id, value));
    }
}
