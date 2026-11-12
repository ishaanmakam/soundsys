using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Soundsys.Engine.Abstractions;
using Soundsys.Windows.Interop;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace Soundsys.Windows;

/// <summary>
/// Silent environment probe. Never renders audio, never opens a microphone, never changes mute/volume and never calls
/// AudioPlaybackConnection.OpenAsync. It enumerates, reads properties, activates clients without starting render
/// streams, captures 1 s of loopback (silent) and 0.3 s of process loopback on its own (silent) process.
/// </summary>
public static partial class WindowsProbe
{
    /// <param name="tryPhoneStart">Run TryCreateFromId + StartAsync (then Dispose; never OpenAsync) on at most one paired phone.</param>
    public static async Task<string> RunAsync(bool tryPhoneStart = true)
    {
        var md = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine("# Windows probe").AppendLine();
        md.AppendLine(inv, $"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `soundsys-diag probe` (silent).").AppendLine();

        // --- Environment
        md.AppendLine("## Environment").AppendLine();
        md.AppendLine(inv, $"- OS: {RuntimeInformation.OSDescription} ({ReadDisplayVersion()})");
        md.AppendLine(inv, $"- OS architecture: {RuntimeInformation.OSArchitecture}; process architecture: {RuntimeInformation.ProcessArchitecture}");
        md.AppendLine(inv, $"- .NET: {RuntimeInformation.FrameworkDescription}");
        md.AppendLine(inv, $"- Package identity: {PackageIdentity()}");
        md.AppendLine(inv, $"- QPC frequency: {Stopwatch.Frequency} Hz").AppendLine();

        // --- Endpoints
        using var catalog = new EndpointCatalog();
        AppendEndpoints(md, catalog, DataFlow.Render, "Render endpoints");
        AppendEndpoints(md, catalog, DataFlow.Capture, "Capture endpoints");

        md.AppendLine("## What soundsys lists").AppendLine();
        md.AppendLine("Speakers:");
        foreach (var e in catalog.GetSpeakers())
            md.AppendLine(inv, $"- {e.Name} — {e.State}, Bluetooth={e.IsBluetooth}, battery={Battery(e)} (`{e.Id}`)");
        md.AppendLine().AppendLine("Microphones:");
        foreach (var e in catalog.GetMicrophones())
            md.AppendLine(inv, $"- {e.Name} — {e.State} (`{e.Id}`)");
        var def = catalog.GetDefault(render: true);
        md.AppendLine().AppendLine(inv, $"Default render endpoint: {(def is null ? "none" : $"{def.Name} (`{def.Id}`)")}").AppendLine();

        // --- AudioPlaybackConnection
        md.AppendLine("## AudioPlaybackConnection").AppendLine();
        await AppendPhoneLinkAsync(md, tryPhoneStart);

        // --- Process loopback
        md.AppendLine("## Process loopback (own process, 0.3 s)").AppendLine();
        md.AppendLine(CaptureTest(new ProcessLoopbackSource(Environment.ProcessId, "soundsys-diag"), 300)).AppendLine();

        // --- Endpoint loopback
        md.AppendLine("## Default-endpoint loopback capture (1 s)").AppendLine();
        md.AppendLine(CaptureTest(new WasapiCaptureSource(null, "This laptop", CaptureMode.Loopback), 1000)).AppendLine();
        return md.ToString();
    }

    private static string Battery(AudioEndpointInfo e)
        => e.IsBluetooth && Guid.TryParse(e.ContainerId, out var c) && BatteryReader.GetPercent(c) is { } p ? $"{p}%" : "n/a";

    private static void AppendEndpoints(StringBuilder md, EndpointCatalog catalog, DataFlow flow, string title)
    {
        var inv = CultureInfo.InvariantCulture;
        md.AppendLine(inv, $"## {title} (all states)").AppendLine();
        md.AppendLine("| Name | State | Bluetooth | Hands-free | Enumerator | Container | Mix format | ID |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var e in catalog.ReadRaw(flow, DeviceState.All))
        {
            var bt = EndpointClassifier.IsBluetooth(e);
            var mix = e.MmState == EndpointClassifier.MmStateActive ? MixFormat(e.Id, flow == DataFlow.Render ? EDataFlow.Render : EDataFlow.Capture) : "—";
            md.AppendLine(inv, $"| {Esc(EndpointClassifier.DisplayName(e, bt))} | {MmStateName(e.MmState)} | {bt} | {EndpointClassifier.IsHandsFree(e)} | {e.EnumeratorName} | {e.ContainerId?.ToString("B")} {(e.ContainerName is null ? "" : $"({Esc(e.ContainerName)})")} | {mix} | `{e.Id}` |");
        }

        md.AppendLine();
    }

    /// <summary>Activates IAudioClient and reads the mix format. No Initialize, no Start.</summary>
    private static string MixFormat(string id, int flow)
    {
        var hr = WasapiClient.TryActivate(id, flow, out var client, out _);
        if (hr < 0)
            return AudioErrors.Describe(hr);
        try
        {
            hr = WasapiClient.TryGetMixFormat(client!, out var mix);
            return hr < 0 ? AudioErrors.Describe(hr) : mix.ToString();
        }
        finally
        {
            WasapiClient.Release(client);
        }
    }

    private static async Task AppendPhoneLinkAsync(StringBuilder md, bool tryPhoneStart)
    {
        var inv = CultureInfo.InvariantCulture;
        string selector;
        try
        {
            selector = AudioPlaybackConnection.GetDeviceSelector();
            md.AppendLine(inv, $"- GetDeviceSelector: OK — `{selector}`");
        }
        catch (Exception ex)
        {
            md.AppendLine(inv, $"- GetDeviceSelector FAILED: {ExceptionText(ex)}").AppendLine();
            return;
        }

        DeviceInformationCollection devices;
        try
        {
            devices = await DeviceInformation.FindAllAsync(selector, ["System.Devices.ContainerId"]);
            md.AppendLine(inv, $"- Paired devices that can stream to this PC (A2DP source): {devices.Count}");
            foreach (var d in devices)
                md.AppendLine(inv, $"  - {d.Name} (enabled={d.IsEnabled}, container={(d.Properties.TryGetValue("System.Devices.ContainerId", out var c) ? c : null)}) `{d.Id}`");
        }
        catch (Exception ex)
        {
            md.AppendLine(inv, $"- FindAllAsync FAILED: {ExceptionText(ex)}").AppendLine();
            return;
        }

        // Harmless identity check: a non-phone ID creates nothing. A missing-package-identity gate would show up here as
        // APPMODEL_ERROR_NO_PACKAGE (0x80073D54) or E_ACCESSDENIED instead of null / "not a valid device".
        try
        {
            var none = AudioPlaybackConnection.TryCreateFromId("soundsys-probe-not-a-device");
            md.AppendLine(inv, $"- TryCreateFromId(non-device ID): returned {(none is null ? "null" : "an object")} (no identity error)");
            none?.Dispose();
        }
        catch (Exception ex)
        {
            md.AppendLine(inv, $"- TryCreateFromId(non-device ID) threw: {ExceptionText(ex)}");
        }

        if (!tryPhoneStart)
        {
            md.AppendLine("- TryCreateFromId + StartAsync experiment: skipped (--no-phone-start).").AppendLine();
            return;
        }

        if (devices.Count == 0)
        {
            md.AppendLine("- TryCreateFromId + StartAsync experiment: not possible, no paired phone.").AppendLine();
            return;
        }

        var phone = devices[0];
        try
        {
            using var connection = AudioPlaybackConnection.TryCreateFromId(phone.Id);
            if (connection is null)
            {
                md.AppendLine(inv, $"- TryCreateFromId(\"{phone.Name}\"): returned null").AppendLine();
                return;
            }

            md.AppendLine(inv, $"- TryCreateFromId(\"{phone.Name}\"): OK, state={connection.State}");
            await connection.StartAsync();
            md.AppendLine(inv, $"- StartAsync: OK, state={connection.State} (OpenAsync deliberately not called). Disposed.").AppendLine();
        }
        catch (Exception ex)
        {
            md.AppendLine(inv, $"- TryCreateFromId/StartAsync FAILED: {ExceptionText(ex)}").AppendLine();
        }
    }

    private static string CaptureTest(WasapiCaptureBase source, int milliseconds)
    {
        var inv = CultureInfo.InvariantCulture;
        var sink = new CountingSink();
        using (source)
        {
            try
            {
                var started = QpcClock.Now;
                source.Start(sink);
                Thread.Sleep(milliseconds);
                source.Stop();
                var elapsed = QpcClock.Now - started;
                var extra = source is ProcessLoopbackSource pl ? $", target PID {pl.TargetProcessId}" : "";
                return string.Create(inv, $"- OK: format {sink.Format}, {source.PacketCount} packets / {sink.Frames} frames in {elapsed:F2} s, discontinuities {sink.Discontinuities}, peak {sink.Peak:F4}, first-packet timestamp lag {sink.FirstLagMs:F1} ms, bytes allocated in capture loop {source.LoopAllocatedBytes}{extra}");
            }
            catch (AudioDeviceException ex)
            {
                return $"- FAILED: {ex.Message} — {ExceptionText(ex.InnerException ?? ex)}";
            }
        }
    }

    private sealed class CountingSink : ISourceSink
    {
        public AudioFormat Format { get; private set; }
        public long Frames { get; private set; }
        public int Discontinuities { get; private set; }
        public float Peak { get; private set; }
        public double FirstLagMs { get; private set; } = double.NaN;

        public void OnFormat(AudioFormat format) => Format = format;

        public void Write(ReadOnlySpan<float> interleaved, double timestampSeconds, bool discontinuity)
        {
            if (double.IsNaN(FirstLagMs))
                FirstLagMs = (QpcClock.Now - timestampSeconds) * 1000;
            Frames += interleaved.Length / Math.Max(1, Format.Channels);
            if (discontinuity)
                Discontinuities++;
            foreach (var s in interleaved)
                Peak = Math.Max(Peak, Math.Abs(s));
        }
    }

    private static string MmStateName(int state) => state switch
    {
        EndpointClassifier.MmStateActive => "Active",
        EndpointClassifier.MmStateDisabled => "Disabled",
        EndpointClassifier.MmStateNotPresent => "NotPresent",
        EndpointClassifier.MmStateUnplugged => "Unplugged",
        _ => state.ToString(CultureInfo.InvariantCulture),
    };

    private static string Esc(string s) => s.Replace("|", "\\|", StringComparison.Ordinal);

    private static string ExceptionText(Exception ex) => $"{ex.GetType().Name} HRESULT {AudioErrors.Describe(ex.HResult)}: {ex.Message}";

    private static string ReadDisplayVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return $"{key?.GetValue("DisplayVersion")}, build {key?.GetValue("CurrentBuildNumber")}.{key?.GetValue("UBR")}";
    }

    private static unsafe string PackageIdentity()
    {
        uint length = 0;
        var rc = GetCurrentPackageFullName(ref length, null);
        if (rc == 15700)
            return "none (unpackaged process; APPMODEL_ERROR_NO_PACKAGE)";
        var buffer = stackalloc char[(int)Math.Max(length, 1)];
        rc = GetCurrentPackageFullName(ref length, buffer);
        return rc == 0 ? new string(buffer) : $"unknown (error {rc})";
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFullName(ref uint packageFullNameLength, char* packageFullName);
}
