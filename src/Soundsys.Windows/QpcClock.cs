using System.Diagnostics;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>
/// QueryPerformanceCounter in seconds. WASAPI's u64QPCPosition is the same counter expressed in
/// 100 ns units, so <see cref="FromWasapiQpc"/> maps capture timestamps onto this time base.
/// </summary>
public sealed class QpcClock : IClock
{
    private static readonly double SecondsPerTick = 1.0 / Stopwatch.Frequency;

    public static QpcClock Instance { get; } = new();

    public double NowSeconds => Now;

    /// <summary>Allocation-free static accessor for real-time loops.</summary>
    public static double Now => Stopwatch.GetTimestamp() * SecondsPerTick;

    /// <summary>Converts a WASAPI QPC position (100 ns units) to <see cref="IClock"/> seconds.</summary>
    public static double FromWasapiQpc(ulong qpc100ns) => qpc100ns * 1e-7;
}
