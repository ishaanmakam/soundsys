namespace Soundsys.Engine.Abstractions;

/// <summary>
/// Periodic timers. Real mode uses dedicated threads; simulation uses the virtual-time scheduler so tests
/// can run an hour of audio in seconds.
/// </summary>
public interface ITimerScheduler
{
    IClock Clock { get; }

    /// <summary>Calls <paramref name="tick"/> (with the current clock time) every <paramref name="periodSeconds"/> until disposed.</summary>
    IDisposable SchedulePeriodic(double periodSeconds, Action<double> tick);
}
