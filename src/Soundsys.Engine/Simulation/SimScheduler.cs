using System.Diagnostics;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Engine.Simulation;

/// <summary>Virtual clock advanced by <see cref="SimScheduler"/>.</summary>
public sealed class VirtualClock : IClock
{
    private double _now;

    public double NowSeconds => Volatile.Read(ref _now);

    internal void Set(double now) => Volatile.Write(ref _now, now);
}

/// <summary>
/// Discrete-event scheduler on a virtual clock. All simulated devices run their callbacks as events here, in
/// time order, on whichever thread calls <see cref="RunUntil"/>. Tests step it as fast as the CPU allows;
/// <see cref="StartRealTime"/> drives it from the wall clock for the UI's simulated mode.
/// Scheduling is thread-safe; events execute outside the lock.
/// </summary>
public sealed class SimScheduler : ITimerScheduler, IDisposable
{
    private readonly PriorityQueue<Action<double>, (double Time, long Seq)> _queue = new();
    private readonly Lock _lock = new();
    private readonly VirtualClock _clock = new();
    private long _seq;
    private Thread? _realTimeThread;
    private volatile bool _stopRealTime;

    public IClock Clock => _clock;

    public double Now => _clock.NowSeconds;

    /// <summary>Exceptions thrown by events while running in real time (surfaced for diagnostics).</summary>
    public event EventHandler<Exception>? EventFailed;

    public void Schedule(double time, Action<double> action)
    {
        lock (_lock)
        {
            _queue.Enqueue(action, (Math.Max(time, _clock.NowSeconds), _seq++));
        }
    }

    public void ScheduleAfter(double delaySeconds, Action<double> action) => Schedule(Now + delaySeconds, action);

    /// <summary>Runs all events up to and including <paramref name="time"/>, then sets the clock to it.</summary>
    public void RunUntil(double time)
    {
        while (true)
        {
            Action<double> action;
            double eventTime;
            lock (_lock)
            {
                if (!_queue.TryPeek(out _, out var key) || key.Time > time)
                {
                    break;
                }

                action = _queue.Dequeue();
                eventTime = key.Time;
            }

            _clock.Set(eventTime);
            action(eventTime);
        }

        if (time > _clock.NowSeconds)
        {
            _clock.Set(time);
        }
    }

    public void RunFor(double seconds) => RunUntil(Now + seconds);

    public IDisposable SchedulePeriodic(double periodSeconds, Action<double> tick)
    {
        var handle = new PeriodicHandle();
        void Fire(double now)
        {
            if (handle.Disposed)
            {
                return;
            }

            tick(now);
            ScheduleAfter(periodSeconds, Fire);
        }

        ScheduleAfter(periodSeconds, Fire);
        return handle;
    }

    /// <summary>Drives the virtual clock from the wall clock on a background thread (simulated mode in the app).</summary>
    public void StartRealTime()
    {
        if (_realTimeThread is not null)
        {
            return;
        }

        _stopRealTime = false;
        var origin = Now;
        var sw = Stopwatch.StartNew();
        _realTimeThread = new Thread(() =>
        {
            while (!_stopRealTime)
            {
                try
                {
                    RunUntil(origin + sw.Elapsed.TotalSeconds);
                }
                catch (Exception ex)
                {
                    EventFailed?.Invoke(this, ex);
                }

                Thread.Sleep(2);
            }
        })
        {
            IsBackground = true,
            Name = "soundsys-sim",
            Priority = ThreadPriority.AboveNormal,
        };
        _realTimeThread.Start();
    }

    public void Dispose()
    {
        _stopRealTime = true;
        _realTimeThread?.Join(1000);
        _realTimeThread = null;
    }

    private sealed class PeriodicHandle : IDisposable
    {
        public volatile bool Disposed;

        public void Dispose() => Disposed = true;
    }
}
