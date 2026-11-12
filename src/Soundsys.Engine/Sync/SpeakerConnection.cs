namespace Soundsys.Engine.Sync;

public enum SpeakerLinkState
{
    /// <summary>The engine is stopped.</summary>
    Idle,

    /// <summary>An open attempt is in progress.</summary>
    Connecting,

    Connected,

    /// <summary>Not available (off / out of range / dropped); retrying.</summary>
    Lost,
}

/// <summary>
/// Pure reconnect state machine for one speaker. The engine feeds it events and performs an open attempt
/// whenever a method returns true. Lost speakers are retried every <c>retryInterval</c> and immediately when
/// Windows reports a device change, so a speaker that comes back rejoins within about a second.
/// </summary>
public sealed class SpeakerConnection(double retryIntervalSeconds = 1.0, double problemGraceSeconds = 3.0)
{
    private double _lastAttempt = double.NegativeInfinity;

    public SpeakerLinkState State { get; private set; } = SpeakerLinkState.Idle;

    /// <summary>When the speaker was lost (or first failed to open).</summary>
    public double? LostSince { get; private set; }

    /// <summary>Duration of the most recent outage (lost → connected again).</summary>
    public double? LastOutageSeconds { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>Starts the connection; returns true (open now).</summary>
    public bool Start(double now)
    {
        LostSince = null;
        return Attempt(now);
    }

    public void Stop()
    {
        State = SpeakerLinkState.Idle;
        LostSince = null;
    }

    public void OnOpenSucceeded(double now)
    {
        if (State == SpeakerLinkState.Idle)
        {
            return;
        }

        if (LostSince is { } since)
        {
            LastOutageSeconds = now - since;
        }

        LostSince = null;
        State = SpeakerLinkState.Connected;
    }

    public void OnOpenFailed(double now)
    {
        if (State == SpeakerLinkState.Idle)
        {
            return;
        }

        LostSince ??= now;
        State = SpeakerLinkState.Lost;
    }

    public void OnFaulted(double now)
    {
        if (State is SpeakerLinkState.Idle or SpeakerLinkState.Lost)
        {
            return;
        }

        LostSince = now;
        State = SpeakerLinkState.Lost;
        _lastAttempt = now;
    }

    /// <summary>Periodic tick; returns true if a retry is due.</summary>
    public bool OnTick(double now)
        => State == SpeakerLinkState.Lost && now - _lastAttempt >= retryIntervalSeconds && Attempt(now);

    /// <summary>Windows reported endpoint changes; returns true if a lost speaker should be retried now.</summary>
    public bool OnDevicesChanged(double now) => State == SpeakerLinkState.Lost && Attempt(now);

    /// <summary>True once the speaker has been unavailable long enough to tell the user.</summary>
    public bool IsProblem(double now) => State == SpeakerLinkState.Lost && LostSince is { } since && now - since >= problemGraceSeconds;

    private bool Attempt(double now)
    {
        _lastAttempt = now;
        Attempts++;
        State = SpeakerLinkState.Connecting;
        return true;
    }
}
