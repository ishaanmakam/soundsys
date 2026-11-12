using Soundsys.Engine.Abstractions;
using Soundsys.Windows.Interop;

namespace Soundsys.Windows;

public enum CaptureMode
{
    /// <summary>Capture from a capture endpoint (microphone, line-in, a phone's capture endpoint).</summary>
    Endpoint,

    /// <summary>WASAPI loopback of a render endpoint: what Windows is playing there.</summary>
    Loopback,
}

/// <summary>
/// WASAPI capture of a capture endpoint (event-driven, optional raw mode) or loopback of a render endpoint
/// (polled every 5 ms because loopback event callbacks are unreliable on some builds).
/// Timestamps come from GetBuffer's QPC position.
/// </summary>
public sealed class WasapiCaptureSource : WasapiCaptureBase
{
    private readonly string? _endpointId;
    private readonly CaptureMode _mode;
    private readonly bool _requestRawMode;

    /// <param name="endpointId">Endpoint ID; null = the default capture endpoint (or default render endpoint for loopback), resolved at start.</param>
    /// <param name="displayName">Plain-English name used in messages.</param>
    /// <param name="mode">Endpoint capture or loopback.</param>
    /// <param name="requestRawMode">Ask for raw mode (bypasses voice processing on microphones). Best effort.</param>
    public WasapiCaptureSource(string? endpointId, string displayName, CaptureMode mode, bool requestRawMode = false)
        : base(SourceId(endpointId, mode), displayName)
    {
        _endpointId = endpointId;
        _mode = mode;
        _requestRawMode = requestRawMode && mode == CaptureMode.Endpoint;
    }

    public CaptureMode Mode => _mode;

    /// <summary>The endpoint that was actually opened (resolves the default endpoint), after start.</summary>
    public string? ResolvedEndpointId { get; private set; }

    private static string SourceId(string? endpointId, CaptureMode mode)
        => (mode == CaptureMode.Loopback ? "loopback:" : "capture:") + (endpointId ?? "default");

    private protected override int OpenStream(out WasapiClient? client, out bool eventDriven)
    {
        eventDriven = _mode == CaptureMode.Endpoint;
        var flow = _mode == CaptureMode.Loopback ? EDataFlow.Render : EDataFlow.Capture;
        var flags = _mode == CaptureMode.Loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.EventCallback;
        var hr = WasapiClient.TryOpenEndpoint(_endpointId, flow, flags, CaptureBufferHns, _requestRawMode, out client, out var resolved);
        ResolvedEndpointId = resolved;
        return hr;
    }

    protected override AudioDeviceException CreateStartException(int hr)
    {
        if (_mode == CaptureMode.Loopback && hr == AudioErrors.ENotFound && _endpointId is null)
            return new AudioDeviceException("Windows has no output device selected, so there's nothing to capture.", new System.ComponentModel.Win32Exception(hr, AudioErrors.Describe(hr)));
        return base.CreateStartException(hr);
    }
}
