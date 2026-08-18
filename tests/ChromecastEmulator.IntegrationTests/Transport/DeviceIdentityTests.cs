using ChromecastEmulator.Transport;
using ChromecastEmulator.TestSupport;

namespace ChromecastEmulator.IntegrationTests.Transport;

public class DeviceIdentityTests
{
    [Fact]
    public void Load_CreatesStateDirectoryDeviceJsonAndDevicePfx()
    {
        using var state = new TempState();

        DeviceIdentity.Load(state.Options());

        Assert.True(Directory.Exists(state.Directory));
        Assert.True(File.Exists(Path.Combine(state.Directory, "device.json")));
        Assert.True(File.Exists(Path.Combine(state.Directory, "device.pfx")));
    }

    [Fact]
    public void Load_FreshStateDir_ReturnsThirtyTwoCharLowercaseHexIdAndUsableCertificate()
    {
        using var state = new TempState();

        var identity = DeviceIdentity.Load(state.Options());

        Assert.Matches("^[0-9a-f]{32}$", identity.DeviceId);
        Assert.NotNull(identity.Certificate);
        Assert.Contains(identity.DeviceId, identity.Certificate.Subject);
        Assert.NotNull(identity.PrivateKey);
        Assert.True(identity.PrivateKey.KeySize > 0);
    }

    [Fact]
    public void Load_CalledTwiceOnSameStateDir_ReturnsSamePersistedIdentity()
    {
        using var state = new TempState();
        var options = state.Options();

        var first = DeviceIdentity.Load(options);
        var second = DeviceIdentity.Load(state.Options());

        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.Equal(first.BaseStationId, second.BaseStationId);
        Assert.Equal(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
    }

    [Fact]
    public void Load_ExplicitDeviceIdOption_OverridesPersistedIdAndIsWrittenBack()
    {
        using var state = new TempState();
        DeviceIdentity.Load(state.Options());

        var overridden = DeviceIdentity.Load(state.Options(o => o.DeviceId = "cafef00dcafef00dcafef00dcafef00"));

        Assert.Equal("cafef00dcafef00dcafef00dcafef00", overridden.DeviceId);
        var written = File.ReadAllText(Path.Combine(state.Directory, "device.json"));
        Assert.Contains("cafef00dcafef00dcafef00dcafef00", written);
    }

    [Fact]
    public void Load_BaseStationId_IsUppercaseHexOfExpectedLength()
    {
        using var state = new TempState();

        var identity = DeviceIdentity.Load(state.Options());

        // RandomHex(6) yields 12 hex chars, upper-cased for the BaseStationId.
        Assert.Matches("^[0-9A-F]{12}$", identity.BaseStationId);
    }

    [Fact]
    public void Load_TwoDifferentStateDirs_ProduceDifferentDeviceIds()
    {
        using var stateA = new TempState();
        using var stateB = new TempState();

        var identityA = DeviceIdentity.Load(stateA.Options());
        var identityB = DeviceIdentity.Load(stateB.Options());

        Assert.NotEqual(identityA.DeviceId, identityB.DeviceId);
    }
}
