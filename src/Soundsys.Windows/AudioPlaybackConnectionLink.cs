using Soundsys.Engine.Abstractions;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Soundsys.Windows;

/// <summary>
/// Makes the laptop a Bluetooth speaker (A2DP sink) for paired phones via Windows.Media.Audio.AudioPlaybackConnection.
///
/// Lifecycle per enabled phone: TryCreateFromId → StartAsync (the laptop is now available to that phone) →
/// optionally OpenAsync (the laptop actively connects; Windows then plays the phone's audio on the default output,
/// which soundsys captures). With <see cref="AutoOpen"/> on, enabling a phone makes one ~10 s OpenAsync attempt with
/// backoff; afterwards the phone is expected to reconnect from its side (to verify on hardware). There is deliberately
/// no periodic background paging: paging an absent phone occupies the radio and would disturb the speaker streams.
/// </summary>
public sealed class AudioPlaybackConnectionLink : IPhoneLink
{
    private static readonly string[] PhoneProperties = ["System.Devices.ContainerId"];

    private readonly EndpointCatalog _catalog;
    private readonly object _gate = new();
    private readonly Dictionary<string, PhoneEntry> _phones = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public AudioPlaybackConnectionLink(EndpointCatalog catalog) => _catalog = catalog;

    /// <summary>When true, enabling a phone also tries OpenAsync once (≈10 s with backoff). Default true.</summary>
    public bool AutoOpen { get; set; } = true;

    /// <summary>Budget for one <see cref="OpenWithRetryAsync"/> call.</summary>
    public TimeSpan OpenBudget { get; set; } = TimeSpan.FromSeconds(10);

    public event EventHandler<PhoneActivityEventArgs>? ActivityChanged;

    private sealed class PhoneEntry(string id, string name, string? containerId)
    {
        public string Id { get; } = id;
        public string Name { get; set; } = name;
        public string? ContainerId { get; set; } = containerId;
        public AudioPlaybackConnection? Connection { get; set; }
        public bool Enabled { get; set; }
        public PhoneLinkState State { get; set; } = PhoneLinkState.Disabled;
        public string? Detail { get; set; }
        public CancellationTokenSource? OpenCts { get; set; }
    }

    /// <summary>The AQS selector for paired devices that can stream A2DP to this PC.</summary>
    public static string DeviceSelector => AudioPlaybackConnection.GetDeviceSelector();

    public IReadOnlyList<PhoneInfo> GetPairedPhones()
    {
        DeviceInformationCollection devices;
        try
        {
            devices = Run(async () => await DeviceInformation.FindAllAsync(DeviceSelector, PhoneProperties));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return [];
        }

        var list = new List<PhoneInfo>(devices.Count);
        lock (_gate)
        {
            foreach (var d in devices)
            {
                var container = d.Properties.TryGetValue("System.Devices.ContainerId", out var c) ? c?.ToString() : null;
                if (_phones.TryGetValue(d.Id, out var entry))
                {
                    entry.Name = d.Name;
                    entry.ContainerId = container;
                }
                else
                {
                    _phones[d.Id] = new PhoneEntry(d.Id, d.Name, container);
                }

                list.Add(new PhoneInfo(d.Id, d.Name));
            }
        }

        return list;
    }

    public void SetEnabledPhones(IReadOnlyCollection<string> phoneIds)
    {
        ArgumentNullException.ThrowIfNull(phoneIds);
        var enabled = new HashSet<string>(phoneIds, StringComparer.OrdinalIgnoreCase);
        List<PhoneEntry> toClose, toOpen;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var id in enabled)
            {
                if (!_phones.ContainsKey(id))
                    _phones[id] = new PhoneEntry(id, id, null);
            }

            toClose = _phones.Values.Where(p => p.Enabled && !enabled.Contains(p.Id)).ToList();
            // Also retry phones that are enabled but failed earlier (e.g. Bluetooth was off).
            toOpen = _phones.Values.Where(p => enabled.Contains(p.Id) && (!p.Enabled || p.State == PhoneLinkState.Error)).ToList();
            foreach (var p in toClose)
                p.Enabled = false;
            foreach (var p in toOpen)
                p.Enabled = true;
        }

        foreach (var p in toClose)
        {
            Close(p);
            SetState(p, PhoneLinkState.Disabled, null);
        }

        foreach (var p in toOpen)
        {
            Close(p);
            if (CreateAndStart(p) && AutoOpen)
                _ = OpenWithRetryAsync(p.Id);
        }
    }

    public IReadOnlyList<PhoneStatus> GetStatus()
    {
        lock (_gate)
            return _phones.Values.Select(p => new PhoneStatus(p.Id, p.Name, p.State, p.Detail)).ToList();
    }

    /// <summary>The engine reports detected audio (Streaming) or its end (back to Connected).</summary>
    public void ReportStreaming(string phoneId, bool streaming)
    {
        PhoneEntry? p;
        lock (_gate)
            _phones.TryGetValue(phoneId, out p);
        if (p is null)
            return;
        if (streaming && p.State == PhoneLinkState.Connected)
            SetState(p, PhoneLinkState.Streaming, null);
        else if (!streaming && p.State == PhoneLinkState.Streaming)
            SetState(p, PhoneLinkState.Connected, null);
    }

    /// <summary>Handoff: drop this phone's connection but stay available (re-created and started, not opened).</summary>
    public void Release(string phoneId)
    {
        PhoneEntry? p;
        lock (_gate)
            _phones.TryGetValue(phoneId, out p);
        if (p is null || !p.Enabled)
            return;
        Close(p);
        CreateAndStart(p);
    }

    /// <summary>
    /// Actively connects to the phone: OpenAsync with backoff (0.5 s, 1 s, 2 s, 4 s…) within <see cref="OpenBudget"/>.
    /// Returns true once Opened. Never called by the silent probe.
    /// </summary>
    public async Task<bool> OpenWithRetryAsync(string phoneId, CancellationToken cancellationToken = default)
    {
        PhoneEntry? p;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (!_phones.TryGetValue(phoneId, out p) || p.Connection is null)
                return false;
            p.OpenCts?.Cancel();
            p.OpenCts?.Dispose();
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            p.OpenCts = cts;
        }

        var delays = PhoneLinkLogic.OpenRetryDelays(OpenBudget);
        string? lastDetail = null;
        try
        {
            for (var attempt = 0; attempt <= delays.Count; attempt++)
            {
                var connection = p.Connection;
                if (connection is null || cts.IsCancellationRequested)
                    return false;
                if (connection.State == AudioPlaybackConnectionState.Opened)
                    return true;
                try
                {
                    var result = await connection.OpenAsync().AsTask(cts.Token).ConfigureAwait(false);
                    if (result.Status == AudioPlaybackConnectionOpenResultStatus.Success)
                    {
                        SetState(p, PhoneLinkState.Connected, null);
                        return true;
                    }

                    lastDetail = result.ExtendedError is { } e ? $"{result.Status}: 0x{e.HResult:X8} {e.Message}" : result.Status.ToString();
                    if (result.Status == AudioPlaybackConnectionOpenResultStatus.DeniedBySystem)
                        break; // Retrying won't help (e.g. Bluetooth off or policy).
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
                {
                    lastDetail = $"OpenAsync threw 0x{ex.HResult:X8} {ex.Message}";
                }

                if (attempt < delays.Count)
                    await Task.Delay(delays[attempt], cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        lock (_gate)
            p.Detail = lastDetail;
        return false;
    }

    public IAudioSource CreateSource(string phoneId, PlaybackSuppression suppression)
    {
        PhoneEntry? p;
        lock (_gate)
            _phones.TryGetValue(phoneId, out p);
        var name = p?.Name ?? "Phone";

        IAudioSource inner;
        var endpoint = PhoneLinkLogic.FindPhoneCaptureEndpoint(_catalog.GetMicrophones(), p?.ContainerId, name);
        if (endpoint is not null)
        {
            inner = new WasapiCaptureSource(endpoint.Id, name, CaptureMode.Endpoint);
            if (suppression == PlaybackSuppression.MuteEndpointDuringLoopback)
                suppression = PlaybackSuppression.None; // Not a loopback: nothing plays that we capture.
        }
        else
        {
            inner = new WasapiCaptureSource(null, name, CaptureMode.Loopback);
        }

        if (suppression == PlaybackSuppression.None)
            return inner;
        var tokens = new List<string> { name };
        if (PhoneLinkLogic.TryGetBluetoothAddress(phoneId) is { } address)
            tokens.Add(address);
        return new SuppressedSource(inner, PlaybackSuppressor.Create(suppression, null, new SessionMatcher([], tokens)));
    }

    private bool CreateAndStart(PhoneEntry p)
    {
        try
        {
            var connection = AudioPlaybackConnection.TryCreateFromId(p.Id);
            if (connection is null)
            {
                SetState(p, PhoneLinkState.Error, "This phone can't stream audio to the laptop. Pair it again in Bluetooth settings.");
                return false;
            }

            connection.StateChanged += OnConnectionStateChanged;
            try
            {
                Run(async () =>
                {
                    await connection.StartAsync();
                    return true;
                });
            }
            catch
            {
                connection.StateChanged -= OnConnectionStateChanged;
                connection.Dispose();
                throw;
            }
            lock (_gate)
                p.Connection = connection;
            SetState(p, connection.State == AudioPlaybackConnectionState.Opened ? PhoneLinkState.Connected : PhoneLinkState.Waiting, null);
            return true;
        }
        catch (Exception ex)
        {
            // Boundary: any WinRT failure (including a missing package identity) becomes a plain-English status.
            SetState(p, PhoneLinkState.Error, $"Windows wouldn't let soundsys receive audio from this phone (0x{ex.HResult:X8}).");
            return false;
        }
    }

    private void Close(PhoneEntry p)
    {
        AudioPlaybackConnection? connection;
        lock (_gate)
        {
            connection = p.Connection;
            p.Connection = null;
            p.OpenCts?.Cancel();
            p.OpenCts?.Dispose();
            p.OpenCts = null;
        }

        if (connection is null)
            return;
        connection.StateChanged -= OnConnectionStateChanged;
        try
        {
            connection.Dispose();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        PhoneEntry? p;
        lock (_gate)
            p = _phones.Values.FirstOrDefault(x => ReferenceEquals(x.Connection, sender));
        if (p is null)
            return;
        var state = sender.State == AudioPlaybackConnectionState.Opened ? PhoneLinkState.Connected : PhoneLinkState.Waiting;
        if (state == PhoneLinkState.Connected && p.State == PhoneLinkState.Streaming)
            return;
        SetState(p, state, null);
    }

    private void SetState(PhoneEntry p, PhoneLinkState state, string? detail)
    {
        bool changed;
        lock (_gate)
        {
            changed = p.State != state;
            p.State = state;
            p.Detail = detail;
        }

        if (changed)
            ActivityChanged?.Invoke(this, new PhoneActivityEventArgs(p.Id, state));
    }

    private static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    public void Dispose()
    {
        List<PhoneEntry> all;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            all = _phones.Values.ToList();
        }

        foreach (var p in all)
            Close(p);
    }
}
