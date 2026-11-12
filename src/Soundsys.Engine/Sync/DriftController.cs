namespace Soundsys.Engine.Sync;

/// <summary>
/// PI controller that turns a speaker's latency error (frames) into a relative resampling correction.
/// Plant: the error grows at (drift − correction) seconds per second. With proportional time constant
/// τp and integral time constant τi = 4·τp the closed loop is critically damped, so a step in clock drift
/// is absorbed without overshoot. The input is low-pass filtered (EMA) to reject callback jitter.
/// Allocation-free; safe to call from the real-time thread.
/// </summary>
public sealed class DriftController
{
    /// <summary>Largest correction applied (±1000 ppm ≈ 1 ms of slew per second, inaudible).</summary>
    public const double MaxCorrection = 1000e-6;

    private const double MaxDt = 0.1;

    private readonly double _sampleRate;
    private readonly double _filterTau;
    private readonly double _tauP;
    private readonly double _tauI;

    private bool _hasFiltered;
    private double _filtered;
    private double _integral;
    private double _last;

    public DriftController(double sampleRate, double filterTau = 0.5, double tauP = 5.0, double tauI = 20.0)
    {
        _sampleRate = sampleRate;
        _filterTau = filterTau;
        _tauP = tauP;
        _tauI = tauI;
    }

    /// <summary>The filtered latency error in frames.</summary>
    public double FilteredError => _filtered;

    /// <summary>The most recent correction.</summary>
    public double Correction => _last;

    /// <param name="errorFrames">Measured latency minus desired latency, in source frames. Positive = too much latency.</param>
    /// <param name="dtSeconds">Time since the previous update.</param>
    /// <returns>Relative correction c: consume input at nominal·(1 + c).</returns>
    public double Update(double errorFrames, double dtSeconds)
    {
        if (!(dtSeconds > 0))
        {
            return _last;
        }

        var dt = Math.Min(dtSeconds, MaxDt);
        if (!_hasFiltered)
        {
            _filtered = errorFrames;
            _hasFiltered = true;
        }
        else
        {
            _filtered += (errorFrames - _filtered) * (1 - Math.Exp(-dt / _filterTau));
        }

        var errorSeconds = _filtered / _sampleRate;
        _integral = Math.Clamp(_integral + (errorSeconds * dt / (_tauP * _tauI)), -MaxCorrection, MaxCorrection);
        _last = Math.Clamp((errorSeconds / _tauP) + _integral, -MaxCorrection, MaxCorrection);
        return _last;
    }

    public void Reset()
    {
        _hasFiltered = false;
        _filtered = 0;
        _integral = 0;
        _last = 0;
    }

    /// <summary>Clears the error filter but keeps the learned drift (used after a read-pointer jump).</summary>
    public void ResetError()
    {
        _hasFiltered = false;
        _filtered = 0;
        _last = _integral;
    }
}
