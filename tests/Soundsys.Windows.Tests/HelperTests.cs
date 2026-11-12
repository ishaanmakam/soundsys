using System.Diagnostics;
using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows.Tests;

public class AudioErrorsTests
{
    [Fact]
    public void DeviceInvalidatedIsAPlainEnglishDisconnect()
    {
        Assert.Equal("Desk speaker is off or out of range.", AudioErrors.ToUserMessage(AudioErrors.AudclntEDeviceInvalidated, "Desk speaker"));
        Assert.Equal(DeviceFaultKind.Disconnected, AudioErrors.ToFaultKind(AudioErrors.AudclntEDeviceInvalidated));
        Assert.Equal(DeviceFaultKind.Disconnected, AudioErrors.ToFaultKind(AudioErrors.AudclntEResourcesInvalidated));
    }

    [Fact]
    public void OtherFailuresAreFailed() => Assert.Equal(DeviceFaultKind.Failed, AudioErrors.ToFaultKind(AudioErrors.AudclntEDeviceInUse));

    [Fact]
    public void MessagesNeverContainHexCodes()
    {
        int[] codes = [AudioErrors.AudclntEDeviceInUse, AudioErrors.AudclntEUnsupportedFormat, AudioErrors.EAccessDenied, AudioErrors.AudclntEServiceNotRunning, unchecked((int)0x80001234)];
        foreach (var hr in codes)
        {
            var message = AudioErrors.ToUserMessage(hr, "Desk", isCapture: true);
            Assert.DoesNotContain("0x", message, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MissingNameUsesGenericSubject()
    {
        Assert.StartsWith("The speaker ", AudioErrors.ToUserMessage(AudioErrors.AudclntEDeviceInvalidated, null), StringComparison.Ordinal);
        Assert.StartsWith("The audio source ", AudioErrors.ToUserMessage(AudioErrors.AudclntEDeviceInvalidated, " ", isCapture: true), StringComparison.Ordinal);
    }

    [Fact]
    public void MicrophonePrivacyMessagePointsToSettings()
        => Assert.Contains("Privacy", AudioErrors.ToUserMessage(AudioErrors.EAccessDenied, "Microphone", isCapture: true), StringComparison.Ordinal);

    [Fact]
    public void DescribeNamesKnownCodes()
    {
        Assert.Equal("AUDCLNT_E_DEVICE_INVALIDATED (0x88890004)", AudioErrors.Describe(AudioErrors.AudclntEDeviceInvalidated));
        Assert.Equal("0x80001234", AudioErrors.Describe(unchecked((int)0x80001234)));
    }
}

public class FormatNegotiationTests
{
    [Fact]
    public void PreferredIsFloatStereoAtMixRate()
    {
        Assert.Equal(new StreamFormat(44100, 2, SampleKind.Float32), FormatNegotiation.Preferred(44100));
        Assert.Equal(48000, FormatNegotiation.Preferred(0).SampleRate);
    }

    [Fact]
    public void ParsesExtensibleFloat()
        => Assert.Equal(new StreamFormat(48000, 2, SampleKind.Float32), FormatNegotiation.Parse(0xFFFE, 2, 48000, 32, new Guid("00000003-0000-0010-8000-00aa00389b71")));

    [Fact]
    public void ParsesPlainPcm16AndUnsupported24()
    {
        Assert.Equal(SampleKind.Int16, FormatNegotiation.Parse(1, 1, 16000, 16, null).Kind);
        Assert.Equal(SampleKind.Unsupported, FormatNegotiation.Parse(1, 2, 48000, 24, null).Kind);
        Assert.Equal(SampleKind.Int32, FormatNegotiation.Parse(0xFFFE, 2, 48000, 32, new Guid("00000001-0000-0010-8000-00aa00389b71")).Kind);
    }

    [Fact]
    public void BlockAlignFollowsKind()
    {
        Assert.Equal(8, new StreamFormat(48000, 2, SampleKind.Float32).BlockAlign);
        Assert.Equal(4, new StreamFormat(48000, 2, SampleKind.Int16).BlockAlign);
    }

    [Theory]
    [InlineData(1, 0x4u)]
    [InlineData(2, 0x3u)]
    [InlineData(6, 0x3Fu)]
    [InlineData(3, 0u)]
    public void ChannelMasks(int channels, uint mask) => Assert.Equal(mask, FormatNegotiation.DefaultChannelMask(channels));

    [Fact]
    public unsafe void ConvertsInt16ToFloat()
    {
        short[] src = [0, 16384, -32768, 32767];
        var dst = new float[4];
        fixed (short* p = src)
            FormatNegotiation.ToFloat(SampleKind.Int16, (byte*)p, dst);
        Assert.Equal(0f, dst[0]);
        Assert.Equal(0.5f, dst[1]);
        Assert.Equal(-1f, dst[2]);
        Assert.InRange(dst[3], 0.9999f, 1f);
    }

    [Fact]
    public unsafe void CopiesFloatAndConversionDoesNotAllocate()
    {
        float[] src = [0.25f, -0.5f];
        var dst = new float[2];
        fixed (float* p = src)
        {
            FormatNegotiation.ToFloat(SampleKind.Float32, (byte*)p, dst);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                FormatNegotiation.ToFloat(SampleKind.Float32, (byte*)p, dst);
            Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        }

        Assert.Equal(src, dst);
    }
}

public class PhoneLinkLogicTests
{
    [Fact]
    public void RetryDelaysDoubleAndFitTheBudget()
    {
        var delays = PhoneLinkLogic.OpenRetryDelays(TimeSpan.FromSeconds(10));
        Assert.Equal([0.5, 1, 2, 4], delays.Select(d => d.TotalSeconds));
        Assert.True(delays.Sum(d => d.TotalSeconds) <= 10);
    }

    [Fact]
    public void RetryDelaysCapAtFourSeconds()
        => Assert.All(PhoneLinkLogic.OpenRetryDelays(TimeSpan.FromSeconds(30)), d => Assert.True(d <= TimeSpan.FromSeconds(4)));

    [Fact]
    public void FindsPhoneCaptureEndpointByContainerFirst()
    {
        var container = Guid.NewGuid();
        AudioEndpointInfo[] mics =
        [
            new("mic", "Microphone Array", false, EndpointState.Active, EndpointClassifier.LocalMachineContainer.ToString("B")),
            new("byname", "Ishaan's iPhone audio", true, EndpointState.Active, Guid.NewGuid().ToString("B")),
            new("bycontainer", "Bluetooth input", true, EndpointState.Active, container.ToString("B")),
        ];
        Assert.Equal("bycontainer", PhoneLinkLogic.FindPhoneCaptureEndpoint(mics, container.ToString(), "Ishaan's iPhone")?.Id);
        Assert.Equal("byname", PhoneLinkLogic.FindPhoneCaptureEndpoint(mics, null, "Ishaan's iPhone")?.Id);
        Assert.Null(PhoneLinkLogic.FindPhoneCaptureEndpoint(mics, null, "Pixel 9"));
    }

    [Fact]
    public void IgnoresUnavailablePhoneEndpoints()
    {
        AudioEndpointInfo[] mics = [new("off", "Pixel 9", true, EndpointState.Unavailable)];
        Assert.Null(PhoneLinkLogic.FindPhoneCaptureEndpoint(mics, null, "Pixel 9"));
    }

    [Theory]
    [InlineData(@"\\?\BTHENUM#{0000110a-0000-1000-8000-00805f9b34fb}_VID&0001004c_PID&2017#5&1a58f167&0&F873DFD5B103_C00000000#{6994ad04-93ef-11d0-a3cc-00a0c9223196}", "F873DFD5B103")]
    [InlineData(@"BTHENUM\Dev_a1b2c3d4e5f6\5&1a58f167&0&BluetoothDevice_A1B2C3D4E5F6", "A1B2C3D4E5F6")]
    [InlineData("not-a-bluetooth-id", null)]
    [InlineData(null, null)]
    public void ExtractsBluetoothAddress(string? id, string? expected) => Assert.Equal(expected, PhoneLinkLogic.TryGetBluetoothAddress(id));
}

public class MiscTests
{
    [Fact]
    public void StartupCommandQuotesExeAndAddsTray()
        => Assert.Equal("\"C:\\Apps\\soundsys.exe\" --tray", StartupRegistration.BuildCommand(@"C:\Apps\soundsys.exe"));

    [Theory]
    [InlineData("\"C:\\Apps\\soundsys.exe\" --tray", @"C:\Apps\soundsys.exe", true)]
    [InlineData("\"c:\\apps\\SOUNDSYS.EXE\" --tray", @"C:\Apps\soundsys.exe", true)]
    [InlineData(@"C:\Apps\soundsys.exe --tray", @"C:\Apps\soundsys.exe", true)]
    [InlineData("\"C:\\Old\\soundsys.exe\" --tray", @"C:\Apps\soundsys.exe", false)]
    public void RecognisesOwnRunValue(string command, string exe, bool expected) => Assert.Equal(expected, StartupRegistration.IsOurs(command, exe));

    [Theory]
    [InlineData((byte)50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, null)]
    [InlineData(-1, null)]
    [InlineData("50", null)]
    [InlineData(null, null)]
    public void BatteryPercent(object? value, int? expected) => Assert.Equal(expected, BatteryReader.ToPercent(value));

    [Fact]
    public void SessionMatcherByProcessOrToken()
    {
        var m = new SessionMatcher(["Spotify"], ["F873DFD5B103"]);
        Assert.True(m.Matches("spotify", null, null));
        Assert.True(m.Matches("svchost", null, "{0.0.0.00000000}|#%b{F873DFD5B103}"));
        Assert.False(m.Matches("chrome", "Chrome", "chrome-session"));
    }

    [Fact]
    public void QpcClockMatchesWasapiTimeBase()
    {
        // WASAPI reports QPC in 100 ns units; converting the current QPC that way must equal QpcClock.Now.
        var ticks = Stopwatch.GetTimestamp();
        var qpc100ns = (ulong)(ticks * (10_000_000.0 / Stopwatch.Frequency));
        var now = QpcClock.Now;
        Assert.InRange(now - QpcClock.FromWasapiQpc(qpc100ns), -0.001, 0.05);
    }

    [Fact]
    public void FriendlyAppNames()
    {
        Assert.Equal("Apple Music", WindowsAudioBackend.FriendlyAppName("AppleMusic"));
        Assert.Equal("Spotify", WindowsAudioBackend.FriendlyAppName("Spotify"));
    }
}
