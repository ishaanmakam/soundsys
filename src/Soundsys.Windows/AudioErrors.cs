using System.Globalization;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows;

/// <summary>HRESULTs seen from WASAPI / MMDevice and their plain-English meaning.</summary>
public static class AudioErrors
{
    public const int SOk = 0;
    public const int AudclntSBufferEmpty = 0x08890001;
    public const int AudclntENotInitialized = unchecked((int)0x88890001);
    public const int AudclntEAlreadyInitialized = unchecked((int)0x88890002);
    public const int AudclntEWrongEndpointType = unchecked((int)0x88890003);
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    public const int AudclntENotStopped = unchecked((int)0x88890005);
    public const int AudclntEBufferTooLarge = unchecked((int)0x88890006);
    public const int AudclntEOutOfOrder = unchecked((int)0x88890007);
    public const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);
    public const int AudclntEInvalidSize = unchecked((int)0x88890009);
    public const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntEBufferOperationPending = unchecked((int)0x8889000B);
    public const int AudclntEThreadNotRegistered = unchecked((int)0x8889000C);
    public const int AudclntEExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int AudclntEEndpointCreateFailed = unchecked((int)0x8889000F);
    public const int AudclntEServiceNotRunning = unchecked((int)0x88890010);
    public const int AudclntEEventHandleNotExpected = unchecked((int)0x88890011);
    public const int AudclntEBufferSizeError = unchecked((int)0x88890016);
    public const int AudclntECpuUsageExceeded = unchecked((int)0x88890017);
    public const int AudclntEBufferError = unchecked((int)0x88890018);
    public const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890019);
    public const int AudclntEInvalidDevicePeriod = unchecked((int)0x88890020);
    public const int AudclntEResourcesInvalidated = unchecked((int)0x88890026);
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int ENotFound = unchecked((int)0x80070490);
    public const int EFileNotFound = unchecked((int)0x80070002);
    public const int ENotImpl = unchecked((int)0x80004001);
    public const int ENoInterface = unchecked((int)0x80004002);
    public const int EInvalidArg = unchecked((int)0x80070057);
    public const int EOutOfMemory = unchecked((int)0x8007000E);
    public const int EUnexpected = unchecked((int)0x8000FFFF);

    public static bool Succeeded(int hr) => hr >= 0;

    public static bool Failed(int hr) => hr < 0;

    /// <summary>True when the HRESULT means the device went away (turned off, out of range, unplugged).</summary>
    public static bool IsDisconnect(int hr) => hr is AudclntEDeviceInvalidated or AudclntEResourcesInvalidated or ENotFound or EFileNotFound;

    public static DeviceFaultKind ToFaultKind(int hr) => IsDisconnect(hr) ? DeviceFaultKind.Disconnected : DeviceFaultKind.Failed;

    /// <summary>A sentence safe to show the user, e.g. "Desk speaker is off or out of range."</summary>
    /// <param name="hr">The failing HRESULT.</param>
    /// <param name="deviceName">Friendly device name; a generic phrase is used when null/empty.</param>
    /// <param name="isCapture">True for microphones and capture sources.</param>
    public static string ToUserMessage(int hr, string? deviceName, bool isCapture = false)
    {
        var name = string.IsNullOrWhiteSpace(deviceName) ? (isCapture ? "The audio source" : "The speaker") : deviceName.Trim();
        return hr switch
        {
            AudclntEDeviceInvalidated or AudclntEResourcesInvalidated or ENotFound or EFileNotFound
                => $"{name} is off or out of range.",
            AudclntEDeviceInUse or AudclntEExclusiveModeNotAllowed
                => $"{name} is being used exclusively by another app. Close that app and try again.",
            AudclntEUnsupportedFormat
                => $"{name} doesn't accept the audio format soundsys needs.",
            AudclntEServiceNotRunning
                => "The Windows Audio service isn't running. Restart the laptop and try again.",
            EAccessDenied when isCapture
                => $"Windows blocked access to {name}. Allow microphone access for desktop apps in Settings > Privacy & security > Microphone.",
            EAccessDenied
                => $"Windows blocked access to {name}.",
            AudclntEEndpointCreateFailed
                => $"{name} couldn't be opened. Try turning it off and on again.",
            AudclntECpuUsageExceeded
                => $"{name} stopped because the laptop is too busy. Close some apps and try again.",
            _ => $"{name} couldn't start. Try turning it off and on again.",
        };
    }

    /// <summary>Technical description for logs: "AUDCLNT_E_DEVICE_INVALIDATED (0x88890004)".</summary>
    public static string Describe(int hr)
    {
        var symbol = hr switch
        {
            SOk => "S_OK",
            AudclntSBufferEmpty => "AUDCLNT_S_BUFFER_EMPTY",
            AudclntENotInitialized => "AUDCLNT_E_NOT_INITIALIZED",
            AudclntEAlreadyInitialized => "AUDCLNT_E_ALREADY_INITIALIZED",
            AudclntEWrongEndpointType => "AUDCLNT_E_WRONG_ENDPOINT_TYPE",
            AudclntEDeviceInvalidated => "AUDCLNT_E_DEVICE_INVALIDATED",
            AudclntENotStopped => "AUDCLNT_E_NOT_STOPPED",
            AudclntEBufferTooLarge => "AUDCLNT_E_BUFFER_TOO_LARGE",
            AudclntEOutOfOrder => "AUDCLNT_E_OUT_OF_ORDER",
            AudclntEUnsupportedFormat => "AUDCLNT_E_UNSUPPORTED_FORMAT",
            AudclntEInvalidSize => "AUDCLNT_E_INVALID_SIZE",
            AudclntEDeviceInUse => "AUDCLNT_E_DEVICE_IN_USE",
            AudclntEBufferOperationPending => "AUDCLNT_E_BUFFER_OPERATION_PENDING",
            AudclntEThreadNotRegistered => "AUDCLNT_E_THREAD_NOT_REGISTERED",
            AudclntEExclusiveModeNotAllowed => "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED",
            AudclntEEndpointCreateFailed => "AUDCLNT_E_ENDPOINT_CREATE_FAILED",
            AudclntEServiceNotRunning => "AUDCLNT_E_SERVICE_NOT_RUNNING",
            AudclntEEventHandleNotExpected => "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED",
            AudclntEBufferSizeError => "AUDCLNT_E_BUFFER_SIZE_ERROR",
            AudclntECpuUsageExceeded => "AUDCLNT_E_CPUUSAGE_EXCEEDED",
            AudclntEBufferError => "AUDCLNT_E_BUFFER_ERROR",
            AudclntEBufferSizeNotAligned => "AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED",
            AudclntEInvalidDevicePeriod => "AUDCLNT_E_INVALID_DEVICE_PERIOD",
            AudclntEResourcesInvalidated => "AUDCLNT_E_RESOURCES_INVALIDATED",
            EAccessDenied => "E_ACCESSDENIED",
            ENotFound => "E_NOTFOUND",
            EFileNotFound => "ERROR_FILE_NOT_FOUND",
            ENotImpl => "E_NOTIMPL",
            ENoInterface => "E_NOINTERFACE",
            EInvalidArg => "E_INVALIDARG",
            EOutOfMemory => "E_OUTOFMEMORY",
            EUnexpected => "E_UNEXPECTED",
            _ => null,
        };
        var hex = "0x" + hr.ToString("X8", CultureInfo.InvariantCulture);
        return symbol is null ? hex : $"{symbol} ({hex})";
    }

    internal static AudioDeviceException ToException(int hr, string? deviceName, string operation, bool isCapture = false)
        => new(ToUserMessage(hr, deviceName, isCapture), new System.ComponentModel.Win32Exception(hr, $"{operation} failed: {Describe(hr)}"));
}
