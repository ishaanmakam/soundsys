using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Soundsys.Engine;
using Soundsys.Engine.Abstractions;

namespace Soundsys.App.ViewModels;

/// <summary>One endpoint in the speaker pickers. Unavailable ones are labelled "off".</summary>
public sealed record SpeakerChoice(string Id, string Name, bool Available, bool IsBluetooth)
{
    public string? Note => Available ? null : "off";

    public string AutomationName => Available ? Name : $"{Name}, off";
}

/// <summary>The Speakers tab: which two endpoints are Speaker 1 / Speaker 2, and what to call them.</summary>
public sealed partial class SpeakersViewModel : ObservableObject
{
    private readonly EngineRunner _runner;

    public SpeakersViewModel(EngineRunner runner, MainViewModel main)
    {
        _runner = runner;
        SlotA = new SpeakerSlotViewModel(SpeakerSlot.A, "Speaker 1", runner, this);
        SlotB = new SpeakerSlotViewModel(SpeakerSlot.B, "Speaker 2", runner, this);
        Main = main;
    }

    public ObservableCollection<SpeakerChoice> Choices { get; } = [];

    public SpeakerSlotViewModel SlotA { get; }

    public SpeakerSlotViewModel SlotB { get; }

    internal MainViewModel Main { get; }

    [ObservableProperty]
    public partial bool HasChoices { get; set; }

    /// <summary>Re-reads the endpoint list and the current selection (UI thread).</summary>
    public void Reload()
    {
        IReadOnlyList<AudioEndpointInfo> endpoints;
        try
        {
            endpoints = _runner.Engine.GetSpeakerChoices();
        }
        catch (Exception ex)
        {
            Main.ReportError(EngineRunner.Describe(ex));
            return;
        }

        var fresh = endpoints.Select(e => new SpeakerChoice(e.Id, e.Name, e.State == EndpointState.Active, e.IsBluetooth)).ToList();
        if (!fresh.SequenceEqual(Choices))
        {
            Choices.Clear();
            foreach (var c in fresh)
            {
                Choices.Add(c);
            }
        }

        HasChoices = Choices.Count > 0;
        var settings = _runner.Engine.Settings;
        SlotA.Load(settings.SpeakerAId);
        SlotB.Load(settings.SpeakerBId);
    }
}

/// <summary>"Speaker 1" or "Speaker 2": a picker and a name box.</summary>
public sealed partial class SpeakerSlotViewModel(SpeakerSlot slot, string title, EngineRunner runner, SpeakersViewModel owner) : ObservableObject
{
    private readonly EditHold _nameHold = new(3000);
    private bool _loading;

    public SpeakerSlot Slot => slot;

    public string Title => title;

    /// <summary>Endpoint ID of the chosen speaker (the ComboBox's SelectedValue).</summary>
    [ObservableProperty]
    public partial string? SelectedId { get; set; }

    [ObservableProperty]
    public partial string NameText { get; set; } = "";

    /// <summary>Placeholder for the name box (the device name).</summary>
    [ObservableProperty]
    public partial string NamePlaceholder { get; set; } = "";

    internal void Load(string? endpointId)
    {
        _loading = true;
        try
        {
            SelectedId = endpointId;
            OnPropertyChanged(nameof(SelectedId)); // re-select after the list was rebuilt
            var device = owner.Choices.FirstOrDefault(c => c.Id == endpointId)?.Name ?? "";
            NamePlaceholder = device;
            if (!_nameHold.Active)
            {
                NameText = endpointId is null ? "" : runner.Engine.DisplayName(endpointId);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnSelectedIdChanged(string? value)
    {
        // A rebuilt list briefly pushes null back through the binding; only real picks count.
        if (_loading || value is null)
        {
            return;
        }

        _ = Select(value);
    }

    partial void OnNameTextChanged(string value)
    {
        if (_loading || SelectedId is null)
        {
            return;
        }

        _nameHold.Touch();
        var id = SelectedId;
        runner.Post(e => e.RenameSpeaker(id, value));
    }

    private async Task Select(string endpointId)
    {
        if (await runner.RunAsync(e => e.SelectSpeaker(slot, endpointId)))
        {
            owner.Reload();
            owner.Main.Refresh();
        }
    }
}
