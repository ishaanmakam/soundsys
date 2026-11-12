using System.Text.Json;
using System.Text.Json.Serialization;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Engine.Settings;

public enum SourceMode
{
    /// <summary>A phone streams to the laptop over Bluetooth (AudioPlaybackConnection).</summary>
    Phone,

    /// <summary>Whatever the laptop itself plays (Spotify, Apple Music…).</summary>
    Laptop,
}

/// <summary>Saved sync result for one pair of speakers.</summary>
public sealed class PairCalibration
{
    /// <summary>Delay per endpoint ID, milliseconds.</summary>
    public Dictionary<string, double> OffsetsMs { get; set; } = [];

    /// <summary>"auto" or "manual".</summary>
    public string Method { get; set; } = "manual";

    public DateTimeOffset When { get; set; }

    /// <summary>Measured emit→mic latency per endpoint ID (auto calibration only).</summary>
    public Dictionary<string, double> MeasuredLatencyMs { get; set; } = [];
}

/// <summary>Everything soundsys remembers. Plain JSON in %LOCALAPPDATA%\soundsys\settings.json.</summary>
public sealed class AppSettings
{
    public string? SpeakerAId { get; set; }
    public string? SpeakerBId { get; set; }

    /// <summary>Physical-device container per selected endpoint ID, so a speaker whose endpoint ID changes is still recognised.</summary>
    public Dictionary<string, string> SpeakerContainers { get; set; } = [];

    /// <summary>User-chosen names per endpoint ID ("Desk", "Bed").</summary>
    public Dictionary<string, string> SpeakerNames { get; set; } = [];

    public double MasterVolume { get; set; } = 0.8;
    public Dictionary<string, double> SpeakerVolumes { get; set; } = [];
    public SourceMode Mode { get; set; } = SourceMode.Phone;

    /// <summary>Phones the user switched off as a source (everything else paired is enabled).</summary>
    public List<string> DisabledPhoneIds { get; set; } = [];

    /// <summary>Per speaker pair (see <see cref="PairKey"/>).</summary>
    public Dictionary<string, PairCalibration> Calibrations { get; set; } = [];

    public bool StartWithWindows { get; set; }
    public bool AutoStartWhenSpeakersOn { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool CheckForUpdates { get; set; } = true;
    public bool UseSimulatedDevices { get; set; }
    public bool DeveloperOptionsUnlocked { get; set; }

    public PlaybackSuppression PhoneSuppression { get; set; } = PlaybackSuppression.None;
    public PlaybackSuppression LaptopSuppression { get; set; } = PlaybackSuppression.None;
    public double BaseLatencyMs { get; set; } = 50;
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>Moves everything remembered about <paramref name="oldId"/> to <paramref name="newId"/> (same physical speaker).</summary>
    public void MigrateEndpoint(string oldId, string newId)
    {
        static void Move<T>(Dictionary<string, T> d, string from, string to)
        {
            if (d.Remove(from, out var v))
            {
                d[to] = v;
            }
        }

        Move(SpeakerNames, oldId, newId);
        Move(SpeakerVolumes, oldId, newId);
        Move(SpeakerContainers, oldId, newId);
        foreach (var key in Calibrations.Keys.ToList())
        {
            var parts = key.Split('|');
            if (parts.Length != 2 || (parts[0] != oldId && parts[1] != oldId))
            {
                continue;
            }

            var cal = Calibrations[key];
            Calibrations.Remove(key);
            Move(cal.OffsetsMs, oldId, newId);
            Move(cal.MeasuredLatencyMs, oldId, newId);
            Calibrations[PairKey(parts[0] == oldId ? newId : parts[0], parts[1] == oldId ? newId : parts[1])] = cal;
        }

        if (SpeakerAId == oldId)
        {
            SpeakerAId = newId;
        }

        if (SpeakerBId == oldId)
        {
            SpeakerBId = newId;
        }
    }

    /// <summary>Order-independent key for a speaker pair.</summary>
    public static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";

    public string NameFor(string endpointId, string fallback) =>
        SpeakerNames.TryGetValue(endpointId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : fallback;

    public double VolumeFor(string endpointId) => SpeakerVolumes.TryGetValue(endpointId, out var v) ? v : 1.0;
}

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>For tests and simulation.</summary>
public sealed class InMemorySettingsStore(AppSettings? initial = null) : ISettingsStore
{
    private string _json = JsonSerializer.Serialize(initial ?? new AppSettings(), JsonSettingsStore.Options);

    public int Saves { get; private set; }

    public AppSettings Load() => JsonSerializer.Deserialize<AppSettings>(_json, JsonSettingsStore.Options) ?? new AppSettings();

    public void Save(AppSettings settings)
    {
        _json = JsonSerializer.Serialize(settings, JsonSettingsStore.Options);
        Saves++;
    }
}

/// <summary>JSON file store with atomic replace (write temp, then move over), so a crash can't corrupt settings.</summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "soundsys", "settings.json");

    public string FilePath => path;

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A corrupt or unreadable file falls back to defaults; keep a copy for diagnosis.
                TryBackup();
            }

            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private void TryBackup()
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
