using Soundsys.Engine.Abstractions;

namespace Soundsys.Windows.Tests;

public class EndpointClassifierTests
{
    private static readonly Guid BeatsContainer = new("287a0343-c739-58a9-bcaf-c0f6d9535b34");

    // Values recorded from this Snapdragon laptop (docs/WINDOWS_PROBE.md).
    private static RawEndpoint QualcommA2dp(int state = EndpointClassifier.MmStateUnplugged) => new(
        "{0.0.0.00000000}.{e0989faf-f399-43f5-8d78-b6cec2862c94}",
        "Headphones (Ishaan’s Beats Studio Pro)",
        "Headphones",
        "Ishaan’s Beats Studio Pro",
        "QCASD",
        @"{1}.QCASD\A2DPSPEAKERHEADSETSPEAKER-3003329455\6&1463F6D0&0&SPEAKER-3003329455",
        state,
        BeatsContainer,
        true,
        "Ishaan’s Beats Studio Pro");

    private static RawEndpoint MicrosoftStackA2dp(int state = EndpointClassifier.MmStateUnplugged) => new(
        "{0.0.0.00000000}.{1c92e70f-0758-4baa-b97d-5a1e58b474f1}",
        "Headphones (Ishaan’s Beats Studio Pro)",
        "Headphones",
        "Ishaan’s Beats Studio Pro",
        "BTHENUM",
        @"{1}.BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2017\5&1A58F167&0&F873DFD5B103_C00000000",
        state,
        BeatsContainer,
        true,
        "Ishaan’s Beats Studio Pro");

    private static RawEndpoint QualcommHandsFree() => new(
        "{0.0.0.00000000}.{4e9dca99-6ef6-4b32-841a-7dfe3215e770}",
        "Headset (Ishaan’s Beats Studio Pro Hands-Free)",
        "Headset",
        "Qualcomm(R) Aqstic(TM) ACX Bluetooth Hands Free Speaker Audio Device",
        "QCASD",
        @"{1}.QCASD\BLUETOOTHSPEAKERHANDSFREEPROFILESPEAKER-140045827\6&1463F6D0&0&SPEAKER-140045827",
        EndpointClassifier.MmStateUnplugged,
        BeatsContainer,
        true,
        "Ishaan’s Beats Studio Pro");

    private static RawEndpoint BuiltInSpeakers() => new(
        "{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}",
        "Speakers (2- Qualcomm(R) Aqstic(TM) ACX Static Endpoints Audio Device)",
        "Speakers",
        "2- Qualcomm(R) Aqstic(TM) ACX Static Endpoints Audio Device",
        "QCASD",
        @"{1}.QCASD\STATICENDPOINTSDEVICE0\6&1463F6D0&0&0",
        EndpointClassifier.MmStateActive,
        EndpointClassifier.LocalMachineContainer,
        null,
        null);

    private static RawEndpoint Jbl(string id, Guid container, int state = EndpointClassifier.MmStateActive) => new(
        id,
        "Speakers (JBL Flip 6)",
        "Speakers",
        "JBL Flip 6",
        "BTHENUM",
        @"{1}.BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&000A0057_PID&0001\5&1&0&AABBCCDDEEFF_C00000000",
        state,
        container,
        true,
        "JBL Flip 6");

    [Fact]
    public void MicrosoftStackEndpointIsBluetoothByEnumerator()
        => Assert.True(EndpointClassifier.IsBluetooth(MicrosoftStackA2dp() with { ContainerIsBluetooth = null }));

    [Fact]
    public void QualcommOffloadEndpointIsBluetoothWithoutContainerLookup()
        => Assert.True(EndpointClassifier.IsBluetooth(QualcommA2dp() with { ContainerIsBluetooth = null, InterfaceName = "Qualcomm(R) Aqstic(TM) ACX A2dp Speaker Headset" }));

    [Fact]
    public void ContainerLookupDecidesWhenPropertiesAreGeneric()
    {
        var generic = new RawEndpoint("id", "Speakers (USB Audio)", "Speakers", "USB Audio", "VENDORX", @"{1}.VENDORX\DEVICE0\1", 1, Guid.NewGuid(), true, "Some speaker");
        Assert.True(EndpointClassifier.IsBluetooth(generic));
        Assert.False(EndpointClassifier.IsBluetooth(generic with { ContainerIsBluetooth = false }));
    }

    [Fact]
    public void BuiltInSpeakersAreNotBluetooth() => Assert.False(EndpointClassifier.IsBluetooth(BuiltInSpeakers()));

    [Fact]
    public void LocalMachineContainerIsNeverBluetoothByContainer()
        => Assert.False(EndpointClassifier.IsBluetooth(BuiltInSpeakers() with { ContainerIsBluetooth = true }));

    [Theory]
    [InlineData(@"BTHENUM\Dev_F873DFD5B103\5&1a58f167&0&BluetoothDevice_F873DFD5B103", true)]
    [InlineData(@"{1}.BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C", true)]
    [InlineData(@"BTHLE\Dev_aabbccddeeff\8&1", true)]
    [InlineData(@"BTHLEDevice\{0000180f-0000-1000-8000-00805f9b34fb}_Dev_VID&02", true)]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC", false)]
    [InlineData(@"{1}.QCASD\STATICENDPOINTSDEVICE0\6&1463F6D0&0&0", false)]
    [InlineData(null, false)]
    public void BluetoothInstanceIds(string? id, bool expected) => Assert.Equal(expected, EndpointClassifier.IsBluetoothInstanceId(id));

    [Fact]
    public void HandsFreeDetection()
    {
        Assert.True(EndpointClassifier.IsHandsFree(QualcommHandsFree()));
        Assert.False(EndpointClassifier.IsHandsFree(QualcommA2dp()));
        Assert.True(EndpointClassifier.IsHandsFree(MicrosoftStackA2dp() with { HardwareInstanceId = @"{1}.BTHHFENUM\BthHFPAudio\8&1" }));
    }

    [Fact]
    public void BluetoothDisplayNameIsTheDeviceName()
        => Assert.Equal("Ishaan’s Beats Studio Pro", EndpointClassifier.DisplayName(QualcommA2dp(), true));

    [Fact]
    public void BluetoothDisplayNameAvoidsDriverNamesWithoutContainer()
    {
        var e = QualcommA2dp() with { ContainerName = null, InterfaceName = "Qualcomm(R) Aqstic(TM) ACX A2dp Speaker Headset" };
        Assert.Equal("Headphones (Ishaan’s Beats Studio Pro)", EndpointClassifier.DisplayName(e, true));
    }

    [Theory]
    [InlineData("Speakers (2- Qualcomm(R) Aqstic(TM) ACX Static Endpoints Audio Device)", "Speakers")]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers")]
    [InlineData("Headset Earphone on SoundWire Device (3- SoundWire Audio)", "Headset Earphone on SoundWire Device")]
    [InlineData("Speakers (JBL Flip 6)", "Speakers (JBL Flip 6)")]
    [InlineData("DELLSE2726HGS", "DELLSE2726HGS")]
    public void StripsDriverSuffix(string input, string expected) => Assert.Equal(expected, EndpointClassifier.StripDriverSuffix(input));

    [Fact]
    public void SpeakersKeepOneUnavailableEndpointPerBluetoothDeviceAndDropHandsFree()
    {
        var list = EndpointClassifier.SelectSpeakers([BuiltInSpeakers(), QualcommA2dp(), MicrosoftStackA2dp(), QualcommHandsFree()]);
        Assert.Equal(2, list.Count);
        Assert.Equal("Speakers", list[0].Name);
        Assert.Equal(EndpointState.Active, list[0].State);
        var beats = list[1];
        Assert.Equal(QualcommA2dp().Id, beats.Id); // the current (vendor) stack wins over the stale BTHENUM endpoint
        Assert.True(beats.IsBluetooth);
        Assert.Equal(EndpointState.Unavailable, beats.State);
        Assert.Equal(BeatsContainer.ToString("B"), beats.ContainerId);
    }

    [Fact]
    public void InactiveDuplicatesHiddenWhenDeviceIsActive()
    {
        var list = EndpointClassifier.SelectSpeakers([QualcommA2dp(EndpointClassifier.MmStateActive), MicrosoftStackA2dp()]);
        var only = Assert.Single(list);
        Assert.Equal(EndpointState.Active, only.State);
    }

    [Fact]
    public void NonBluetoothUnpluggedEndpointsAreNotListed()
    {
        var unpluggedJack = BuiltInSpeakers() with { Id = "jack", MmState = EndpointClassifier.MmStateUnplugged };
        Assert.DoesNotContain(EndpointClassifier.SelectSpeakers([unpluggedJack]), e => e.Id == "jack");
    }

    [Fact]
    public void TwoIdenticalSpeakersGetDistinctNames()
    {
        var a = Jbl("{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-000000000000}", Guid.NewGuid());
        var b = Jbl("{0.0.0.00000000}.{bbbbbbbb-0000-0000-0000-000000000000}", Guid.NewGuid());
        var list = EndpointClassifier.SelectSpeakers([b, a]);
        Assert.Equal(["JBL Flip 6", "JBL Flip 6 (2)"], list.Select(e => e.Name));
        Assert.Equal(a.Id, list[0].Id); // stable order by ID
    }

    [Fact]
    public void MicrophonesAreActiveOnly()
    {
        var mic = BuiltInSpeakers() with { Id = "mic", FriendlyName = "Microphone Array (Qualcomm(R) Aqstic(TM) ACX Static Endpoints Audio Device)" };
        var list = EndpointClassifier.SelectMicrophones([mic, QualcommA2dp()]);
        Assert.Equal("Microphone Array", Assert.Single(list).Name);
    }

    [Theory]
    [InlineData(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", "{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}")]
    [InlineData("{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}", "{0.0.0.00000000}.{0936b7db-c788-4189-94aa-d5b55d64b018}")]
    public void NormalizesEndpointIds(string input, string expected) => Assert.Equal(expected, EndpointClassifier.NormalizeEndpointId(input));

    [Theory]
    [InlineData(1, EndpointState.Active)]
    [InlineData(2, EndpointState.Disabled)]
    [InlineData(4, EndpointState.Unavailable)]
    [InlineData(8, EndpointState.Unavailable)]
    public void MapsStates(int mm, EndpointState expected) => Assert.Equal(expected, EndpointClassifier.ToEndpointState(mm));
}
