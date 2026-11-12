using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

public enum SampleKind
{
    Float32,
    Int16,
    Int32,
    Unsupported,
}

/// <summary>The negotiated shared-mode stream format (pure data, unit-testable).</summary>
public readonly record struct StreamFormat(int SampleRate, int Channels, SampleKind Kind)
{
    public int BytesPerSample => Kind switch
    {
        SampleKind.Float32 or SampleKind.Int32 => 4,
        SampleKind.Int16 => 2,
        _ => 0,
    };

    public int BlockAlign => BytesPerSample * Channels;

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, {Kind}";
}

/// <summary>Format negotiation helpers (pure; see tests).</summary>
public static class FormatNegotiation
{
    /// <summary>
    /// What soundsys asks WASAPI for: 32-bit float stereo at the device's mix rate (no Windows resampling;
    /// AUTOCONVERTPCM handles the channel conversion).
    /// </summary>
    public static StreamFormat Preferred(int mixSampleRate) => new(mixSampleRate > 0 ? mixSampleRate : 48000, 2, SampleKind.Float32);

    internal static WaveFormatExtensible ToWaveFormat(StreamFormat format)
    {
        var bits = (ushort)(format.BytesPerSample * 8);
        var block = (ushort)format.BlockAlign;
        return new WaveFormatExtensible
        {
            FormatTag = WaveFormatExtensible.TagExtensible,
            Channels = (ushort)format.Channels,
            SamplesPerSec = (uint)format.SampleRate,
            AvgBytesPerSec = (uint)(format.SampleRate * block),
            BlockAlign = block,
            BitsPerSample = bits,
            CbSize = 22,
            ValidBitsPerSample = bits,
            ChannelMask = DefaultChannelMask(format.Channels),
            SubFormat = format.Kind == SampleKind.Float32 ? WaveFormatExtensible.SubtypeIeeeFloat : WaveFormatExtensible.SubtypePcm,
        };
    }

    /// <summary>Parses a WAVEFORMATEX/WAVEFORMATEXTENSIBLE header into a <see cref="StreamFormat"/>.</summary>
    public static StreamFormat Parse(ushort formatTag, ushort channels, uint sampleRate, ushort bitsPerSample, Guid? subFormat)
    {
        var kind = SampleKind.Unsupported;
        var isFloat = formatTag == WaveFormatExtensible.TagIeeeFloat
            || (formatTag == WaveFormatExtensible.TagExtensible && subFormat == WaveFormatExtensible.SubtypeIeeeFloat);
        var isPcm = formatTag == WaveFormatExtensible.TagPcm
            || (formatTag == WaveFormatExtensible.TagExtensible && subFormat == WaveFormatExtensible.SubtypePcm);
        if (isFloat && bitsPerSample == 32)
            kind = SampleKind.Float32;
        else if (isPcm && bitsPerSample == 16)
            kind = SampleKind.Int16;
        else if (isPcm && bitsPerSample == 32)
            kind = SampleKind.Int32;
        return new StreamFormat((int)sampleRate, channels, kind);
    }

    internal static unsafe StreamFormat Parse(WaveFormatExtensible* format)
    {
        Guid? sub = format->FormatTag == WaveFormatExtensible.TagExtensible && format->CbSize >= 22 ? format->SubFormat : null;
        return Parse(format->FormatTag, format->Channels, format->SamplesPerSec, format->BitsPerSample, sub);
    }

    public static uint DefaultChannelMask(int channels) => channels switch
    {
        1 => 0x4, // FRONT_CENTER
        2 => WaveFormatExtensible.SpeakerFrontLeft | WaveFormatExtensible.SpeakerFrontRight,
        4 => 0x33, // FL FR BL BR
        6 => 0x3F, // 5.1
        8 => 0x63F, // 7.1
        _ => 0,
    };

    /// <summary>Converts interleaved device samples to float (no allocation).</summary>
    public static unsafe void ToFloat(SampleKind kind, byte* source, Span<float> destination)
    {
        switch (kind)
        {
            case SampleKind.Float32:
                new ReadOnlySpan<float>(source, destination.Length).CopyTo(destination);
                break;
            case SampleKind.Int16:
                {
                    var s = (short*)source;
                    for (var i = 0; i < destination.Length; i++)
                        destination[i] = s[i] * (1f / 32768f);
                    break;
                }
            case SampleKind.Int32:
                {
                    var s = (int*)source;
                    for (var i = 0; i < destination.Length; i++)
                        destination[i] = s[i] * (1f / 2147483648f);
                    break;
                }
            default:
                destination.Clear();
                break;
        }
    }
}
