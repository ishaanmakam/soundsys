using System.Diagnostics;
using NAudio.CoreAudioApi;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>Which audio sessions <see cref="PlaybackSuppressor"/> mutes for <see cref="PlaybackSuppression.MuteSourceSession"/>.</summary>
/// <param name="ProcessNames">Process names without extension (case-insensitive), e.g. "Spotify".</param>
/// <param name="IdentifierTokens">Substrings matched against the session's display name and session identifiers (e.g. a phone's name or Bluetooth address).</param>
public sealed record SessionMatcher(IReadOnlyList<string> ProcessNames, IReadOnlyList<string> IdentifierTokens)
{
    public bool Matches(string? processName, string? displayName, string? sessionIdentifier)
    {
        if (processName is not null && ProcessNames.Any(p => string.Equals(p, processName, StringComparison.OrdinalIgnoreCase)))
            return true;
        foreach (var token in IdentifierTokens)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;
            if ((displayName?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)
                || (sessionIdentifier?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Stops Windows from also playing the captured audio (double audio). <see cref="Apply"/> saves state and mutes;
/// <see cref="Restore"/> puts back exactly what it changed (never unmutes something the user had muted).
/// Restores on Dispose and on process exit, and leaves a marker file so a crash can be undone at next start
/// (<see cref="RestoreAfterCrash"/>). Never touches volume levels, only mute.
/// </summary>
public sealed class PlaybackSuppressor : IDisposable
{
    private readonly PlaybackSuppression _mode;
    private readonly string? _endpointId;
    private readonly SessionMatcher? _matcher;
    private readonly object _gate = new();
    private readonly object _sessionGate = new();
    private readonly List<AudioSessionControl> _mutedSessions = [];
    private readonly HashSet<string> _mutedSessionIds = new(StringComparer.Ordinal);
    private string? _mutedEndpointId;
    private Timer? _rescan;
    private bool _applied;
    private bool _disposed;

    private PlaybackSuppressor(PlaybackSuppression mode, string? endpointId, SessionMatcher? matcher)
    {
        _mode = mode;
        _endpointId = endpointId;
        _matcher = matcher;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public static PlaybackSuppressor None() => new(PlaybackSuppression.None, null, null);

    /// <summary>Mute a render endpoint (null = the default render endpoint at Apply time) while loopback-capturing it.</summary>
    public static PlaybackSuppressor ForEndpointMute(string? renderEndpointId = null) => new(PlaybackSuppression.MuteEndpointDuringLoopback, renderEndpointId, null);

    /// <summary>Mute matching sessions on the default render endpoint (re-scanned every 2 s while applied, for late starters).</summary>
    public static PlaybackSuppressor ForSessions(SessionMatcher matcher) => new(PlaybackSuppression.MuteSourceSession, null, matcher);

    public static PlaybackSuppressor Create(PlaybackSuppression mode, string? renderEndpointId, SessionMatcher? matcher) => mode switch
    {
        PlaybackSuppression.MuteEndpointDuringLoopback => ForEndpointMute(renderEndpointId),
        PlaybackSuppression.MuteSourceSession when matcher is not null => ForSessions(matcher),
        _ => None(),
    };

    public PlaybackSuppression Mode => _mode;

    /// <summary>Marker file recording an endpoint we muted, so a crash can be undone at next start.</summary>
    public static string MarkerPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "soundsys", "suppression-restore.txt");

    public void Apply()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_applied || _mode == PlaybackSuppression.None)
                return;
            _applied = true;
        }

        // Core Audio objects are created and used on the (MTA) thread pool, never on the caller's UI thread.
        RunMta(() =>
        {
            if (_mode == PlaybackSuppression.MuteEndpointDuringLoopback)
            {
                MuteEndpoint();
                return;
            }

            lock (_sessionGate)
                MuteSessions();
        });

        if (_mode == PlaybackSuppression.MuteSourceSession)
        {
            lock (_gate)
            {
                _rescan ??= new Timer(static s => ((PlaybackSuppressor)s!).Rescan(), this, 2000, 2000);
            }
        }
    }

    public void Restore()
    {
        lock (_gate)
        {
            if (!_applied)
                return;
            _applied = false;
            _rescan?.Dispose();
            _rescan = null;
        }

        RunMta(() =>
        {
            RestoreEndpoint();
            lock (_sessionGate)
                RestoreSessions();
        });
    }

    /// <summary>Undo an endpoint mute left behind by a crash (call once at startup).</summary>
    public static void RestoreAfterCrash()
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return;
            var id = File.ReadAllText(MarkerPath).Trim();
            var restored = id.Length == 0;
            RunMta(() =>
            {
                if (id.Length > 0)
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = enumerator.GetDevice(id);
                    if (device.State == DeviceState.Active)
                    {
                        device.AudioEndpointVolume.Mute = false;
                        restored = true;
                    }
                }
            });

            // Keep the marker until the unmute really happened (the device may be off right now; try again next start).
            if (restored)
                File.Delete(MarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            // Best effort.
        }
    }

    private void MuteEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = _endpointId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console)
            : enumerator.GetDevice(_endpointId);
        var volume = device.AudioEndpointVolume;
        if (volume.Mute)
            return; // Already muted by the user: leave it alone and don't "restore" it later.
        WriteMarker(device.ID);
        volume.Mute = true;
        _mutedEndpointId = device.ID;
    }

    private void RestoreEndpoint()
    {
        var id = _mutedEndpointId;
        if (id is null)
            return;
        _mutedEndpointId = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active)
                device.AudioEndpointVolume.Mute = false;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Endpoint gone; nothing to restore.
        }

        DeleteMarker();
    }

    private void MuteSessions()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            string? instance = null;
            try
            {
                instance = session.GetSessionInstanceIdentifier;
                if (instance is null || _mutedSessionIds.Contains(instance))
                    continue;
                if (!_matcher!.Matches(ProcessName((int)session.GetProcessID), session.DisplayName, instance))
                    continue;
                var simple = session.SimpleAudioVolume;
                if (simple.Mute)
                    continue; // Muted by the user already.
                simple.Mute = true;
                _mutedSessions.Add(session);
                _mutedSessionIds.Add(instance);
                session = null;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Session expired while we looked at it.
            }
            finally
            {
                session?.Dispose();
            }
        }
    }

    private void RestoreSessions()
    {
        foreach (var session in _mutedSessions)
        {
            try
            {
                session.SimpleAudioVolume.Mute = false;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Session ended.
            }

            session.Dispose();
        }

        _mutedSessions.Clear();
        _mutedSessionIds.Clear();
    }

    private void Rescan()
    {
        lock (_gate)
        {
            if (!_applied)
                return;
        }

        try
        {
            lock (_sessionGate)
            {
                lock (_gate)
                {
                    if (!_applied)
                        return;
                }

                MuteSessions();
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Default device changing; try again next tick.
        }
    }

    private static string? ProcessName(int pid)
    {
        if (pid <= 0)
            return null;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void WriteMarker(string endpointId)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, endpointId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteMarker()
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void RunMta(Action action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            action();
        else
            Task.Run(action).GetAwaiter().GetResult();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Restore();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        try
        {
            Restore();
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        }
    }
}
