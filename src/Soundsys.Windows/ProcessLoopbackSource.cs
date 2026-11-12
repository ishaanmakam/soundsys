using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Soundsys.Engine.Abstractions;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

/// <summary>
/// Process loopback (Windows 10 2004+/build 20348+ for the include-tree mode): captures only what one app
/// (and its child processes) plays, via ActivateAudioInterfaceAsync on VAD\Process_Loopback.
/// GetMixFormat isn't supported on this virtual device, so the format is explicit: 48 kHz float stereo
/// (falls back to 16-bit PCM if float is refused).
/// </summary>
public sealed partial class ProcessLoopbackSource : WasapiCaptureBase
{
    public const string VirtualDevicePath = @"VAD\Process_Loopback";

    private const int ActivationTimeoutMs = 5000;

    private readonly string? _processName;
    private readonly int? _processId;

    /// <summary>Captures the first running process with this name (no extension, e.g. "Spotify").</summary>
    public ProcessLoopbackSource(string processName, string? displayName = null)
        : base("process:" + processName, displayName ?? processName)
    {
        ArgumentException.ThrowIfNullOrEmpty(processName);
        _processName = processName;
    }

    /// <summary>Captures a specific process tree.</summary>
    public ProcessLoopbackSource(int processId, string displayName)
        : base("pid:" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture), displayName)
    {
        _processId = processId;
    }

    /// <summary>The process ID captured (after start).</summary>
    public int? TargetProcessId { get; private set; }

    /// <summary>Returns the ID of the first running process with this name (prefers one with a main window), or null.</summary>
    public static int? FindProcessId(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            if (processes.Length == 0)
                return null;
            var withWindow = processes.FirstOrDefault(p => SafeHasWindow(p));
            return (withWindow ?? processes.OrderBy(p => p.Id).First()).Id;
        }
        finally
        {
            foreach (var p in processes)
                p.Dispose();
        }
    }

    private static bool SafeHasWindow(Process p)
    {
        try
        {
            return p.MainWindowHandle != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private protected override int OpenStream(out WasapiClient? client, out bool eventDriven)
    {
        client = null;
        eventDriven = true; // Microsoft's ApplicationLoopback sample is event-driven; the loop also tolerates silence.
        var pid = _processId ?? (_processName is null ? null : FindProcessId(_processName));
        if (pid is null)
            return AudioErrors.ENotFound;
        TargetProcessId = pid;

        var lastHr = AudioErrors.AudclntEUnsupportedFormat;
        foreach (var kind in (ReadOnlySpan<SampleKind>)[SampleKind.Float32, SampleKind.Int16])
        {
            var hr = TryActivate((uint)pid.Value, out var audioClient);
            if (hr < 0)
                return hr;
            var format = new StreamFormat(48000, 2, kind);
            hr = WasapiClient.TryOpenActivated(
                audioClient!,
                format,
                AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
                CaptureBufferHns,
                out client);
            if (hr >= 0)
                return hr;
            WasapiClient.Release(audioClient);
            lastHr = hr;
        }

        return lastHr;
    }

    protected override AudioDeviceException CreateStartException(int hr)
    {
        if (hr == AudioErrors.ENotFound)
            return new AudioDeviceException($"{DisplayName} isn't running.");
        return new AudioDeviceException(
            $"Couldn't capture {DisplayName}'s audio. soundsys will use the laptop's whole output instead.",
            new System.ComponentModel.Win32Exception(hr, "Process loopback: " + AudioErrors.Describe(hr)));
    }

    /// <summary>
    /// Activates an IAudioClient for process-loopback capture of <paramref name="processId"/> and its children.
    /// Blocks up to 5 s for the asynchronous activation. Call from an MTA thread.
    /// </summary>
    internal static unsafe int TryActivate(uint processId, out IAudioClientNative? client)
    {
        client = null;
        var parameters = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            TargetProcessId = processId,
            ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree,
        };
        var variant = new PropVariantBlob
        {
            Vt = PropVariantBlob.VtBlob,
            BlobSize = (uint)sizeof(AudioClientActivationParams),
            BlobData = (nint)(&parameters),
        };

        var handler = new ActivationCompletionHandler();
        nint unknown = 0, handlerItf = 0, operation = 0, activated = 0;
        try
        {
            unknown = ComWrappersInstance.GetOrCreateComInterfaceForObject(handler, CreateComInterfaceFlags.None);
            var hr = Marshal.QueryInterface(unknown, CoreAudioIids.ActivateCompletionHandler, out handlerItf);
            if (hr < 0)
                return hr;

            // The PROPVARIANT and its blob live on this stack frame, which outlives the activation (we block below).
            hr = NativeMethods.ActivateAudioInterfaceAsync(VirtualDevicePath, CoreAudioIids.AudioClient, (nint)(&variant), handlerItf, out operation);
            if (hr < 0)
                return hr;
            if (!handler.Completed.Wait(ActivationTimeoutMs))
                return AudioErrors.EUnexpected;

            // IActivateAudioInterfaceAsyncOperation::GetActivateResult is vtable slot 3 (after IUnknown).
            int activateHr;
            var getResult = (delegate* unmanaged[Stdcall]<nint, int*, nint*, int>)(*(*(void***)operation + 3));
            hr = getResult(operation, &activateHr, &activated);
            if (hr < 0)
                return hr;
            if (activateHr < 0)
                return activateHr;
            if (activated == 0)
                return AudioErrors.ENoInterface;
            client = (IAudioClientNative)Marshal.GetObjectForIUnknown(activated);
            return 0;
        }
        catch (InvalidCastException)
        {
            return AudioErrors.ENoInterface;
        }
        catch (COMException ex)
        {
            return ex.HResult;
        }
        catch (EntryPointNotFoundException)
        {
            return AudioErrors.ENotImpl;
        }
        finally
        {
            if (activated != 0)
                Marshal.Release(activated);
            if (operation != 0)
                Marshal.Release(operation);
            if (handlerItf != 0)
                Marshal.Release(handlerItf);
            if (unknown != 0)
                Marshal.Release(unknown);
        }
    }

    private static readonly StrategyBasedComWrappers ComWrappersInstance = new();

    /// <summary>
    /// Completion callback for ActivateAudioInterfaceAsync. Must be agile or activation fails with
    /// E_ILLEGAL_METHOD_CALL: ComWrappers CCWs are free-threaded, and IAgileObject makes that explicit to COM.
    /// </summary>
    [GeneratedComClass]
    internal sealed partial class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Completed { get; } = new();

        public int ActivateCompleted(nint activateOperation)
        {
            Completed.Set();
            return 0;
        }
    }
}
