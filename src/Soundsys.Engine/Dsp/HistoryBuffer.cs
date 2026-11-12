namespace Soundsys.Engine.Dsp;

/// <summary>Snapshot of the writer side of a <see cref="HistoryBuffer"/>.</summary>
/// <param name="Position">Total frames written since the last reset (absolute frame index of the next frame).</param>
/// <param name="EndTimestamp">Clock time (seconds) at which frame <see cref="Position"/> is/would be captured.</param>
/// <param name="SampleRate">Source sample rate; 0 when no source has been configured.</param>
/// <param name="Epoch">Incremented by every <see cref="HistoryBuffer.Reset"/>; readers re-prime when it changes.</param>
public readonly record struct WriterState(long Position, double EndTimestamp, int SampleRate, int Epoch);

/// <summary>
/// Single-writer, multi-reader circular history of stereo float audio with absolute 64-bit frame positions.
/// Each speaker keeps its own read position into this one buffer; the distance between the writer and a
/// reader is that speaker's delay, so this buffer doubles as every speaker's delay buffer (zero-copy fan-out).
/// The writer never blocks; readers are responsible for staying within <see cref="CapacityFrames"/> of the writer.
/// </summary>
public sealed class HistoryBuffer
{
    public const int Channels = 2;

    private readonly float[] _left;
    private readonly float[] _right;
    private readonly int _mask;

    // Seqlock-protected writer state.
    private int _sequence;
    private long _position;
    private double _endTimestamp;
    private int _sampleRate;
    private int _epoch;

    public HistoryBuffer(int capacityFrames)
    {
        if (capacityFrames < 16 || (capacityFrames & (capacityFrames - 1)) != 0)
        {
            throw new ArgumentException("Capacity must be a power of two ≥ 16.", nameof(capacityFrames));
        }

        CapacityFrames = capacityFrames;
        _mask = capacityFrames - 1;
        _left = new float[capacityFrames];
        _right = new float[capacityFrames];
    }

    public int CapacityFrames { get; }

    /// <summary>Starts a new timeline at <paramref name="sampleRate"/>. Call only while no other thread is writing.</summary>
    public void Reset(int sampleRate)
    {
        BeginWrite();
        _position = 0;
        _endTimestamp = 0;
        _sampleRate = sampleRate;
        _epoch++;
        EndWrite();
    }

    /// <summary>Appends interleaved stereo frames captured starting at <paramref name="timestampSeconds"/>.</summary>
    public void Write(ReadOnlySpan<float> stereoInterleaved, double timestampSeconds)
    {
        var frames = stereoInterleaved.Length / Channels;
        var pos = _position;
        var skip = Math.Max(0, frames - CapacityFrames);
        for (var i = skip; i < frames; i++)
        {
            var idx = (int)((pos + i) & _mask);
            _left[idx] = stereoInterleaved[2 * i];
            _right[idx] = stereoInterleaved[(2 * i) + 1];
        }

        Publish(pos + frames, timestampSeconds + (frames / (double)Math.Max(1, _sampleRate)));
    }

    /// <summary>Appends <paramref name="frames"/> frames of silence starting at <paramref name="timestampSeconds"/> (gap fill).</summary>
    public void WriteSilence(int frames, double timestampSeconds)
    {
        var pos = _position;
        var n = Math.Min(frames, CapacityFrames);
        for (var i = frames - n; i < frames; i++)
        {
            var idx = (int)((pos + i) & _mask);
            _left[idx] = 0f;
            _right[idx] = 0f;
        }

        Publish(pos + frames, timestampSeconds + (frames / (double)Math.Max(1, _sampleRate)));
    }

    /// <summary>
    /// Lock-free consistent read of the writer state (safe from any thread). Never blocks: if the writer is preempted
    /// mid-publish for longer than a few spins, the individually-atomic fields are returned as they are, so a real-time
    /// reader can't be stalled by a descheduled capture thread (at worst one callback sees a momentarily torn pair).
    /// </summary>
    public WriterState ReadState()
    {
        long deadline = 0;
        while (true)
        {
            var s1 = Volatile.Read(ref _sequence);
            if ((s1 & 1) == 0)
            {
                var pos = Volatile.Read(ref _position);
                var ts = Volatile.Read(ref _endTimestamp);
                var rate = Volatile.Read(ref _sampleRate);
                var epoch = Volatile.Read(ref _epoch);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _sequence) == s1)
                {
                    return new WriterState(pos, ts, rate, epoch);
                }
            }

            var nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (deadline == 0)
            {
                deadline = nowTicks + MaxReadWaitTicks;
            }
            else if (nowTicks > deadline)
            {
                break;
            }

            Thread.SpinWait(8);
        }

        return new WriterState(Volatile.Read(ref _position), Volatile.Read(ref _endTimestamp), Volatile.Read(ref _sampleRate), Volatile.Read(ref _epoch));
    }

    private static readonly long MaxReadWaitTicks = System.Diagnostics.Stopwatch.Frequency / 1000; // 1 ms

    /// <summary>Copies frames [<paramref name="startFrame"/>, startFrame + left.Length) into separate channel arrays.</summary>
    public void ReadDeinterleaved(long startFrame, Span<float> left, Span<float> right)
    {
        var count = left.Length;
        var start = (int)(startFrame & _mask);
        var first = Math.Min(count, CapacityFrames - start);
        _left.AsSpan(start, first).CopyTo(left);
        _right.AsSpan(start, first).CopyTo(right);
        if (first < count)
        {
            _left.AsSpan(0, count - first).CopyTo(left[first..]);
            _right.AsSpan(0, count - first).CopyTo(right[first..]);
        }
    }

    private void Publish(long position, double endTimestamp)
    {
        BeginWrite();
        Volatile.Write(ref _position, position);
        Volatile.Write(ref _endTimestamp, endTimestamp);
        EndWrite();
    }

    private void BeginWrite()
    {
        Volatile.Write(ref _sequence, _sequence + 1);
        Interlocked.MemoryBarrier();
    }

    private void EndWrite()
    {
        Interlocked.MemoryBarrier();
        Volatile.Write(ref _sequence, _sequence + 1);
    }
}
