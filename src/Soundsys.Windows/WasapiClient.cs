using System.Runtime.InteropServices;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

/// <summary>An initialized shared-mode IAudioClient plus its negotiated format. Must be created and used on an MTA thread.</summary>
internal sealed class WasapiClient : IDisposable
{
    private WasapiClient(IAudioClientNative client, StreamFormat format, uint bufferFrames, bool rawMode)
    {
        Client = client;
        Format = format;
        BufferFrames = bufferFrames;
        RawMode = rawMode;
    }

    public IAudioClientNative Client { get; }

    public StreamFormat Format { get; }

    public uint BufferFrames { get; }

    public bool RawMode { get; }

    public void Dispose()
    {
        if (Marshal.IsComObject(Client))
            Marshal.FinalReleaseComObject(Client);
    }

    /// <summary>Activates IAudioClient on an endpoint (null = default endpoint of the given flow).</summary>
    public static int TryActivate(string? endpointId, int dataFlow, out IAudioClientNative? client, out string? resolvedId)
    {
        client = null;
        resolvedId = null;
        IMMDeviceEnumeratorNative? enumerator = null;
        IMMDeviceNative? device = null;
        try
        {
            enumerator = (IMMDeviceEnumeratorNative)new MMDeviceEnumeratorCoClass();
            var hr = endpointId is null
                ? enumerator.GetDefaultAudioEndpoint(dataFlow, ERole.Console, out device)
                : enumerator.GetDevice(endpointId, out device);
            if (hr < 0 || device is null)
                return hr < 0 ? hr : AudioErrors.ENotFound;
            device.GetId(out resolvedId);
            hr = device.GetState(out var state);
            if (hr >= 0 && state != 1 /* DEVICE_STATE_ACTIVE */)
                return AudioErrors.AudclntEDeviceInvalidated;
            hr = device.Activate(CoreAudioIids.AudioClient, CoreAudioIids.ClsCtxAll, 0, out var instance);
            if (hr < 0)
                return hr;
            client = (IAudioClientNative)instance!;
            return 0;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        catch (InvalidCastException)
        {
            return AudioErrors.ENoInterface;
        }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    public static unsafe int TryGetMixFormat(IAudioClientNative client, out StreamFormat mix)
    {
        mix = default;
        WaveFormatExtensible* p = null;
        var hr = client.GetMixFormat(&p);
        if (hr < 0 || p is null)
            return hr < 0 ? hr : AudioErrors.EUnexpected;
        try
        {
            mix = FormatNegotiation.Parse(p);
            return 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)p);
        }
    }

    /// <summary>
    /// Opens an endpoint stream: float32 stereo at the mix rate with AUTOCONVERTPCM, falling back to the exact
    /// mix format if the driver refuses. Optionally requests raw mode (best effort, silently dropped if refused).
    /// </summary>
    /// <returns>HRESULT; on success <paramref name="opened"/> is set.</returns>
    public static int TryOpenEndpoint(string? endpointId, int dataFlow, uint extraFlags, long bufferHns, bool tryRawMode, out WasapiClient? opened, out string? resolvedId)
    {
        opened = null;
        resolvedId = null;
        var lastHr = AudioErrors.EUnexpected;
        // Attempts: (raw?, preferred format?) — each attempt needs a fresh IAudioClient because Initialize is one-shot.
        ReadOnlySpan<(bool Raw, bool Preferred)> attempts = tryRawMode
            ? [(true, true), (false, true), (false, false)]
            : [(false, true), (false, false)];
        foreach (var (raw, preferred) in attempts)
        {
            var hr = TryActivate(endpointId, dataFlow, out var client, out resolvedId);
            if (hr < 0)
                return hr;
            hr = TryGetMixFormat(client!, out var mix);
            if (hr < 0)
            {
                Release(client);
                return hr;
            }

            StreamFormat format;
            if (preferred)
                format = FormatNegotiation.Preferred(mix.SampleRate);
            else if (mix.Kind != SampleKind.Unsupported)
                format = mix;
            else
            {
                Release(client);
                return lastHr;
            }

            var rawApplied = raw && TrySetRaw(client!);
            if (raw && !rawApplied)
            {
                Release(client);
                continue;
            }

            var flags = extraFlags | (preferred ? AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality : 0);
            hr = TryInitialize(client!, format, flags, bufferHns, out var bufferFrames);
            if (hr >= 0)
            {
                opened = new WasapiClient(client!, format, bufferFrames, rawApplied);
                return 0;
            }

            Release(client);
            lastHr = hr;
            if (AudioErrors.IsDisconnect(hr) || hr is AudioErrors.AudclntEDeviceInUse or AudioErrors.EAccessDenied or AudioErrors.AudclntEServiceNotRunning)
                return hr;
        }

        return lastHr;
    }

    /// <summary>Wraps an already-activated client (process loopback) with an explicit format.</summary>
    public static int TryOpenActivated(IAudioClientNative client, StreamFormat format, uint flags, long bufferHns, out WasapiClient? opened)
    {
        opened = null;
        var hr = TryInitialize(client, format, flags, bufferHns, out var bufferFrames);
        if (hr < 0)
            return hr;
        opened = new WasapiClient(client, format, bufferFrames, false);
        return 0;
    }

    private static unsafe int TryInitialize(IAudioClientNative client, StreamFormat format, uint flags, long bufferHns, out uint bufferFrames)
    {
        bufferFrames = 0;
        var wf = FormatNegotiation.ToWaveFormat(format);
        var hr = client.Initialize(AudioShareMode.Shared, flags, bufferHns, 0, &wf, null);
        if (hr < 0)
            return hr;
        return client.GetBufferSize(out bufferFrames);
    }

    private static unsafe bool TrySetRaw(IAudioClientNative client)
    {
        if (client is not IAudioClient2Native client2)
            return false;
        var props = new AudioClientProperties
        {
            CbSize = (uint)sizeof(AudioClientProperties),
            IsOffload = 0,
            Category = 0, // AudioCategory_Other
            Options = AudioClientStreamOptions.Raw,
        };
        return client2.SetClientProperties(&props) >= 0;
    }

    public T GetService<T>(Guid iid)
        where T : class
    {
        var hr = Client.GetService(iid, out var service);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return (T)service!;
    }

    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }
}
