using ChromecastEmulator;
using ChromecastEmulator.Device;

namespace ChromecastEmulator.UnitTests.Device;

public class VirtualDeviceTests
{
    private static VirtualDevice CreateDevice(bool allowAnyApp = true, params string[] extraNamespaces)
    {
        var options = new EmulatorOptions { AllowAnyApp = allowAnyApp };
        options.ExtraNamespaces.AddRange(extraNamespaces);
        return new VirtualDevice(options, "deviceid");
    }

    [Theory]
    [InlineData("CC1AD845")]
    [InlineData("SOME_UNKNOWN_ID")]
    public void CanLaunch_AllowAnyApp_IsAlwaysTrue(string appId)
    {
        var device = CreateDevice(allowAnyApp: true);

        Assert.True(device.CanLaunch(appId));
    }

    [Theory]
    [InlineData("CC1AD845")]
    [InlineData("cc1ad845")]
    [InlineData("E8C28D3C")]
    [InlineData("233637DE")]
    [InlineData("B3419EF5")]
    [InlineData("9AC194DC")]
    [InlineData("A9BCCB7C")]
    public void CanLaunch_StrictApps_KnownIdCaseInsensitive_IsTrue(string appId)
    {
        var device = CreateDevice(allowAnyApp: false);

        Assert.True(device.CanLaunch(appId));
    }

    [Fact]
    public void CanLaunch_StrictApps_UnknownId_IsFalse()
    {
        var device = CreateDevice(allowAnyApp: false);

        Assert.False(device.CanLaunch("DEADBEEF"));
    }

    [Fact]
    public void Launch_CanLaunchFalse_ReturnsNull()
    {
        var device = CreateDevice(allowAnyApp: false);

        var session = device.Launch("DEADBEEF");

        Assert.Null(session);
    }

    [Fact]
    public void Launch_AssignsGuidSessionIdAndWebTransportId()
    {
        var device = CreateDevice();

        var session = device.Launch("CC1AD845");

        Assert.NotNull(session);
        Assert.True(Guid.TryParse(session!.SessionId, out _));
        Assert.Equal("web-1", session.TransportId);
    }

    [Fact]
    public void Launch_Namespaces_IncludeMediaBroadcastExtraAndAppSpecific_NoDuplicates()
    {
        var device = CreateDevice(allowAnyApp: true, "urn:x-cast:custom.extra");

        var session = device.Launch("CC1AD845");

        Assert.NotNull(session);
        Assert.Contains(CastNamespaces.Media, session!.Namespaces);
        Assert.Contains(CastNamespaces.Broadcast, session.Namespaces);
        Assert.Contains("urn:x-cast:custom.extra", session.Namespaces);
        Assert.Contains("urn:x-cast:cc1ad845", session.Namespaces);
        Assert.Equal(session.Namespaces.Count, session.Namespaces.Distinct().Count());
    }

    [Fact]
    public void Launch_SameAppIdTwice_ReturnsExistingSession()
    {
        var device = CreateDevice();

        var first = device.Launch("CC1AD845");
        var second = device.Launch("CC1AD845");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(first!.SessionId, second!.SessionId);
    }

    [Fact]
    public void Launch_DifferentAppId_ReplacesFirstSession()
    {
        var device = CreateDevice();

        var first = device.Launch("CC1AD845");
        var second = device.Launch("233637DE");

        Assert.Single(device.Sessions);
        Assert.Same(second, device.Sessions[0]);
        Assert.DoesNotContain(device.Sessions, s => s.SessionId == first!.SessionId);
    }

    [Fact]
    public void Stop_LiveSession_ReturnsTrueAndBecomesIdle()
    {
        var device = CreateDevice();
        var session = device.Launch("CC1AD845");

        var result = device.Stop(session!.SessionId);

        Assert.True(result);
        Assert.True(device.IsIdle);
    }

    [Fact]
    public void Stop_UnknownId_ReturnsFalse()
    {
        var device = CreateDevice();

        var result = device.Stop(Guid.NewGuid().ToString());

        Assert.False(result);
    }

    [Fact]
    public void IsIdle_AfterLaunch_IsFalse()
    {
        var device = CreateDevice();
        device.Launch("CC1AD845");

        Assert.False(device.IsIdle);
    }

    [Fact]
    public void BySessionId_Found_ReturnsSession()
    {
        var device = CreateDevice();
        var session = device.Launch("CC1AD845");

        var found = device.BySessionId(session!.SessionId);

        Assert.Same(session, found);
    }

    [Fact]
    public void BySessionId_Missing_ReturnsNull()
    {
        var device = CreateDevice();

        Assert.Null(device.BySessionId("nonexistent"));
    }

    [Fact]
    public void ByTransportId_Found_ReturnsSession()
    {
        var device = CreateDevice();
        var session = device.Launch("CC1AD845");

        var found = device.ByTransportId(session!.TransportId);

        Assert.Same(session, found);
    }

    [Fact]
    public void ByTransportId_Missing_ReturnsNull()
    {
        var device = CreateDevice();

        Assert.Null(device.ByTransportId("web-999"));
    }

    [Fact]
    public void StatusChanged_OnLaunch_Fires()
    {
        var device = CreateDevice();
        var fired = false;
        device.StatusChanged += () => fired = true;

        device.Launch("CC1AD845");

        Assert.True(fired);
    }

    [Fact]
    public void StatusChanged_OnSuccessfulStop_Fires()
    {
        var device = CreateDevice();
        var session = device.Launch("CC1AD845");
        var fired = false;
        device.StatusChanged += () => fired = true;

        device.Stop(session!.SessionId);

        Assert.True(fired);
    }

    [Fact]
    public void StatusChanged_OnFailedStop_DoesNotFire()
    {
        var device = CreateDevice();
        var fired = false;
        device.StatusChanged += () => fired = true;

        device.Stop(Guid.NewGuid().ToString());

        Assert.False(fired);
    }

    [Fact]
    public void StatusChanged_OnNotifyStatusChanged_Fires()
    {
        var device = CreateDevice();
        var fired = false;
        device.StatusChanged += () => fired = true;

        device.NotifyStatusChanged();

        Assert.True(fired);
    }

    [Fact]
    public void StatusText_Idle_IsEmpty()
    {
        var device = CreateDevice();

        Assert.Equal("", device.StatusText);
    }

    [Fact]
    public void StatusText_Casting_ReportsDisplayName()
    {
        var device = CreateDevice();
        device.Launch("CC1AD845");

        Assert.Equal("Casting: Default Media Receiver", device.StatusText);
    }

    [Fact]
    public void BuildReceiverStatus_Idle_ReportsBackdropApp()
    {
        var device = CreateDevice();

        var status = device.BuildReceiverStatus();
        var app = status["applications"]!.AsArray()[0]!;

        Assert.Equal("E8C28D3C", app["appId"]!.GetValue<string>());
        Assert.True(app["isIdleScreen"]!.GetValue<bool>());
        Assert.Equal(Guid.Empty.ToString(), app["sessionId"]!.GetValue<string>());
        Assert.Equal("web-0", app["transportId"]!.GetValue<string>());
    }

    [Fact]
    public void BuildReceiverStatus_Casting_ReportsRealSession()
    {
        var device = CreateDevice();
        var session = device.Launch("CC1AD845");

        var status = device.BuildReceiverStatus();
        var app = status["applications"]!.AsArray()[0]!;

        Assert.Equal(session!.SessionId, app["sessionId"]!.GetValue<string>());
        Assert.False(app["isIdleScreen"]!.GetValue<bool>());
    }

    [Fact]
    public void BuildReceiverStatus_AlwaysIncludesUserEqAndVolume()
    {
        var device = CreateDevice();

        var status = device.BuildReceiverStatus();

        Assert.NotNull(status["userEq"]);
        Assert.NotNull(status["volume"]);
    }
}
