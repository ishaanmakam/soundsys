using Soundsys.Engine.Abstractions;
using Soundsys.Engine.Sync;

namespace Soundsys.Engine.Tests.Sync;

public class SpeakerConnectionTests
{
    [Fact]
    public void Starts_idle_and_requests_open_on_start()
    {
        var c = new SpeakerConnection();
        Assert.Equal(SpeakerLinkState.Idle, c.State);
        Assert.True(c.Start(0));
        Assert.Equal(SpeakerLinkState.Connecting, c.State);
        c.OnOpenSucceeded(0.1);
        Assert.Equal(SpeakerLinkState.Connected, c.State);
    }

    [Fact]
    public void Failed_open_retries_every_interval()
    {
        var c = new SpeakerConnection(retryIntervalSeconds: 1.0);
        c.Start(0);
        c.OnOpenFailed(0.05);
        Assert.Equal(SpeakerLinkState.Lost, c.State);
        Assert.False(c.OnTick(0.5));
        Assert.True(c.OnTick(1.06));
        Assert.Equal(SpeakerLinkState.Connecting, c.State);
        c.OnOpenFailed(1.1);
        Assert.False(c.OnTick(1.5));
        Assert.True(c.OnTick(2.2));
    }

    [Fact]
    public void Fault_while_connected_goes_lost_and_device_arrival_retries_immediately()
    {
        var c = new SpeakerConnection();
        c.Start(0);
        c.OnOpenSucceeded(0);
        c.OnFaulted(10);
        Assert.Equal(SpeakerLinkState.Lost, c.State);
        Assert.Equal(10, c.LostSince);
        Assert.True(c.OnDevicesChanged(10.2));
        c.OnOpenSucceeded(10.3);
        Assert.Equal(SpeakerLinkState.Connected, c.State);
        Assert.Equal(0.3, c.LastOutageSeconds!.Value, 6);
    }

    [Fact]
    public void Device_change_while_connected_does_nothing()
    {
        var c = new SpeakerConnection();
        c.Start(0);
        c.OnOpenSucceeded(0);
        Assert.False(c.OnDevicesChanged(1));
        Assert.False(c.OnTick(5));
    }

    [Fact]
    public void Stop_returns_to_idle_and_ignores_ticks()
    {
        var c = new SpeakerConnection();
        c.Start(0);
        c.OnOpenFailed(0);
        c.Stop();
        Assert.Equal(SpeakerLinkState.Idle, c.State);
        Assert.False(c.OnTick(100));
        Assert.False(c.OnDevicesChanged(100));
    }

    [Fact]
    public void Problem_is_reported_only_after_a_grace_period()
    {
        var c = new SpeakerConnection();
        c.Start(0);
        c.OnOpenSucceeded(0);
        c.OnFaulted(10);
        Assert.False(c.IsProblem(11));
        Assert.True(c.IsProblem(13.1));
    }
}

public class PhoneHandoffTests
{
    [Fact]
    public void First_phone_to_connect_becomes_active()
    {
        var h = new PhoneHandoff();
        var d = h.OnActivity("iphone", PhoneLinkState.Connected);
        Assert.Equal("iphone", h.ActivePhoneId);
        Assert.True(d.ActiveChanged);
        Assert.Empty(d.Release);
    }

    [Fact]
    public void Most_recent_phone_wins_and_the_previous_one_is_released()
    {
        var h = new PhoneHandoff();
        h.OnActivity("iphone", PhoneLinkState.Connected);
        h.OnActivity("iphone", PhoneLinkState.Streaming);
        var d = h.OnActivity("pixel", PhoneLinkState.Connected);
        Assert.Equal("pixel", h.ActivePhoneId);
        Assert.True(d.ActiveChanged);
        Assert.Equal(["iphone"], d.Release);
    }

    [Fact]
    public void Streaming_on_the_already_active_phone_changes_nothing()
    {
        var h = new PhoneHandoff();
        h.OnActivity("iphone", PhoneLinkState.Connected);
        var d = h.OnActivity("iphone", PhoneLinkState.Streaming);
        Assert.False(d.ActiveChanged);
        Assert.Empty(d.Release);
    }

    [Fact]
    public void Older_connected_phone_starting_to_stream_takes_over()
    {
        var h = new PhoneHandoff();
        h.OnActivity("iphone", PhoneLinkState.Connected);
        h.OnActivity("pixel", PhoneLinkState.Connected); // iphone released by the engine, but suppose it stays connected
        var d = h.OnActivity("iphone", PhoneLinkState.Streaming);
        Assert.Equal("iphone", h.ActivePhoneId);
        Assert.Equal(["pixel"], d.Release);
    }

    [Fact]
    public void Active_phone_leaving_falls_back_to_the_most_recent_remaining_connected_phone()
    {
        var h = new PhoneHandoff();
        h.OnActivity("iphone", PhoneLinkState.Connected);
        h.OnActivity("pixel", PhoneLinkState.Connected);
        var d = h.OnActivity("pixel", PhoneLinkState.Waiting);
        Assert.Equal("iphone", h.ActivePhoneId);
        Assert.True(d.ActiveChanged);

        h.OnActivity("iphone", PhoneLinkState.Disabled);
        Assert.Null(h.ActivePhoneId);
    }
}
