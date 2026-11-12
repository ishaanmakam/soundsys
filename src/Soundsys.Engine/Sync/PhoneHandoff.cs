using Soundsys.Engine.Abstractions;

namespace Soundsys.Engine.Sync;

/// <param name="ActiveChanged">The active phone changed (engine must switch capture source).</param>
/// <param name="Release">Phones the engine should disconnect so only the active phone's audio is captured.</param>
public sealed record HandoffDecision(bool ActiveChanged, IReadOnlyList<string> Release);

/// <summary>
/// "Most recent wins" between paired phones. A phone that connects or starts streaming becomes the active source and every
/// other connected phone is released (they stay paired and can take over again later). If the active phone leaves, the most
/// recently active phone that is still connected takes over.
/// </summary>
public sealed class PhoneHandoff
{
    private readonly Dictionary<string, PhoneLinkState> _states = [];
    private readonly List<string> _recency = []; // most recent last
    private static readonly IReadOnlyList<string> None = [];

    public string? ActivePhoneId { get; private set; }

    public HandoffDecision OnActivity(string phoneId, PhoneLinkState state)
    {
        _states.TryGetValue(phoneId, out var previous);
        _states[phoneId] = state;
        var before = ActivePhoneId;

        if (IsLive(state))
        {
            var promoted = !IsLive(previous) || (state == PhoneLinkState.Streaming && previous != PhoneLinkState.Streaming);
            if (promoted || ActivePhoneId is null)
            {
                _recency.Remove(phoneId);
                _recency.Add(phoneId);
                ActivePhoneId = phoneId;
            }
        }
        else if (ActivePhoneId == phoneId)
        {
            ActivePhoneId = null;
            for (var i = _recency.Count - 1; i >= 0; i--)
            {
                if (_recency[i] != phoneId && _states.TryGetValue(_recency[i], out var s) && IsLive(s))
                {
                    ActivePhoneId = _recency[i];
                    break;
                }
            }
        }

        var changed = before != ActivePhoneId;
        if (!changed || ActivePhoneId is null || ActivePhoneId != phoneId)
        {
            return new HandoffDecision(changed, None);
        }

        var release = new List<string>();
        foreach (var (id, s) in _states)
        {
            if (id != ActivePhoneId && IsLive(s))
            {
                release.Add(id);
            }
        }

        return new HandoffDecision(true, release);
    }

    public void Reset()
    {
        _states.Clear();
        _recency.Clear();
        ActivePhoneId = null;
    }

    private static bool IsLive(PhoneLinkState s) => s is PhoneLinkState.Connected or PhoneLinkState.Streaming;
}
