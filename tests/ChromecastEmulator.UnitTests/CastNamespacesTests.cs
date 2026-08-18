namespace ChromecastEmulator.UnitTests;

public class CastNamespacesTests
{
    [Theory]
    [InlineData(CastNamespaces.Connection, "tp.connection")]
    [InlineData(CastNamespaces.Heartbeat, "tp.heartbeat")]
    [InlineData(CastNamespaces.DeviceAuth, "tp.deviceauth")]
    [InlineData(CastNamespaces.Receiver, "receiver")]
    [InlineData(CastNamespaces.Media, "media")]
    [InlineData(CastNamespaces.Broadcast, "broadcast")]
    public void Shorten_GoogleCastNamespace_StripsGooglePrefix(string ns, string expected)
    {
        Assert.Equal(expected, CastNamespaces.Shorten(ns));
    }

    [Fact]
    public void Shorten_NonGoogleCastUrn_FallsBackToStrippingCastPrefix()
    {
        Assert.Equal("com.example.foo", CastNamespaces.Shorten("urn:x-cast:com.example.foo"));
    }

    [Theory]
    [InlineData("not-a-urn")]
    [InlineData("")]
    [InlineData("urn:x-other:com.google.cast.media")]
    public void Shorten_UnknownInput_ReturnedUnchanged(string ns)
    {
        Assert.Equal(ns, CastNamespaces.Shorten(ns));
    }

    [Fact]
    public void Shorten_IsOrdinalCaseSensitive_DifferentCasingLeftUnchanged()
    {
        var upper = "URN:X-CAST:COM.GOOGLE.CAST.MEDIA";

        Assert.Equal(upper, CastNamespaces.Shorten(upper));
    }

    [Fact]
    public void IsHeartbeat_ExactHeartbeatUrn_ReturnsTrue()
    {
        Assert.True(CastNamespaces.IsHeartbeat(CastNamespaces.Heartbeat));
    }

    [Theory]
    [InlineData(CastNamespaces.Connection)]
    [InlineData(CastNamespaces.Receiver)]
    [InlineData("tp.heartbeat")]
    [InlineData("urn:x-cast:com.google.cast.tp.HEARTBEAT")]
    [InlineData("")]
    public void IsHeartbeat_AnythingElse_ReturnsFalse(string ns)
    {
        Assert.False(CastNamespaces.IsHeartbeat(ns));
    }
}
