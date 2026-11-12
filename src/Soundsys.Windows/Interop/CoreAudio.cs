using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Soundsys.Windows.Interop;

// Precise CoreAudio declarations for the streaming path. Every method uses [PreserveSig] so the
// real-time loops see raw HRESULTs (no exception allocation on the hot path). Enumeration,
// property reads, endpoint volume and sessions use NAudio's wrappers instead.

internal static class AudioClientStreamFlags
{
    public const uint Loopback = 0x00020000;
    public const uint EventCallback = 0x00040000;
    public const uint NoPersist = 0x00080000;
    public const uint SrcDefaultQuality = 0x08000000;
    public const uint AutoConvertPcm = 0x80000000;
}

internal static class AudioClientBufferFlags
{
    public const uint DataDiscontinuity = 0x1;
    public const uint Silent = 0x2;
    public const uint TimestampError = 0x4;
}

internal static class AudioShareMode
{
    public const int Shared = 0;
}

internal static class AudioClientStreamOptions
{
    public const int None = 0;
    public const int Raw = 0x1;
}

internal static class EDataFlow
{
    public const int Render = 0;
    public const int Capture = 1;
}

internal static class ERole
{
    public const int Console = 0;
    public const int Multimedia = 1;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCoClass;

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumeratorNative
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceNative? endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDeviceNative? device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceNative
{
    [PreserveSig]
    int Activate(in Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);

    [PreserveSig]
    int OpenPropertyStore(uint access, out nint properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IAudioClientNative
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, WaveFormatExtensible* format, Guid* audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long hnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, WaveFormatExtensible* format, WaveFormatExtensible** closestMatch);

    [PreserveSig]
    int GetMixFormat(WaveFormatExtensible** deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long hnsDefaultPeriod, out long hnsMinimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

/// <summary>IAudioClient2 (Windows 8+): only used for SetClientProperties (raw mode).</summary>
[ComImport]
[Guid("726778CD-F60A-4eda-82DE-E47610CD78AA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IAudioClient2Native
{
    [PreserveSig]
    int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, WaveFormatExtensible* format, Guid* audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferFrames);

    [PreserveSig]
    int GetStreamLatency(out long hnsLatency);

    [PreserveSig]
    int GetCurrentPadding(out uint paddingFrames);

    [PreserveSig]
    int IsFormatSupported(int shareMode, WaveFormatExtensible* format, WaveFormatExtensible** closestMatch);

    [PreserveSig]
    int GetMixFormat(WaveFormatExtensible** deviceFormat);

    [PreserveSig]
    int GetDevicePeriod(out long hnsDefaultPeriod, out long hnsMinimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(nint eventHandle);

    [PreserveSig]
    int GetService(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);

    [PreserveSig]
    int IsOffloadCapable(int category, out int offloadCapable);

    [PreserveSig]
    int SetClientProperties(AudioClientProperties* properties);

    [PreserveSig]
    int GetBufferSizeLimits(WaveFormatExtensible* format, int eventDriven, out long hnsMinBufferDuration, out long hnsMaxBufferDuration);
}

[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IAudioRenderClientNative
{
    [PreserveSig]
    int GetBuffer(uint framesRequested, out byte* data);

    [PreserveSig]
    int ReleaseBuffer(uint framesWritten, uint flags);
}

[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe interface IAudioCaptureClientNative
{
    [PreserveSig]
    int GetBuffer(out byte* data, out uint framesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);

    [PreserveSig]
    int ReleaseBuffer(uint framesRead);

    [PreserveSig]
    int GetNextPacketSize(out uint framesInNextPacket);
}

// Process-loopback activation uses source-generated COM: the completion handler is an internal managed
// class exposed to COM through ComWrappers ([GeneratedComClass]), which works for non-public types and
// is free-threaded. The async operation is called through its vtable directly (see ProcessLoopbackSource).

[GeneratedComInterface]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
internal partial interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(nint activateOperation);
}

/// <summary>Marker interface: tells COM the object can be called from any apartment without marshaling.</summary>
[GeneratedComInterface]
[Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
internal partial interface IAgileObject;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public const ushort TagPcm = 1;
    public const ushort TagIeeeFloat = 3;
    public const ushort TagExtensible = 0xFFFE;
    public const uint SpeakerFrontLeft = 0x1;
    public const uint SpeakerFrontRight = 0x2;

    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort CbSize;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProperties
{
    public uint CbSize;
    public int IsOffload;
    public int Category;
    public int Options;
}

internal static class AudioClientActivationType
{
    public const int Default = 0;
    public const int ProcessLoopback = 1;
}

internal static class ProcessLoopbackMode
{
    public const int IncludeTargetProcessTree = 0;
    public const int ExcludeTargetProcessTree = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    public int ActivationType;
    public uint TargetProcessId;
    public int ProcessLoopbackMode;
}

/// <summary>A PROPVARIANT holding a VT_BLOB (sequential layout matches the native union on 32- and 64-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariantBlob
{
    public const ushort VtBlob = 65;

    public ushort Vt;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public uint BlobSize;
    public nint BlobData;
}

internal static class CoreAudioIids
{
    public static readonly Guid AudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid AudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid AudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid ActivateCompletionHandler = new("41D949AB-9862-444A-80F6-C261334DA5EB");
    public const uint ClsCtxAll = 0x17;
}
