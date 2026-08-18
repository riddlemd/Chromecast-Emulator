using ChromecastEmulator.Device;
using System.Text.Json.Nodes;

namespace ChromecastEmulator.UnitTests.Device;

public class AppSessionTests
{
    private static AppSession CreateSession(IReadOnlyList<string>? namespaces = null, bool isIdleScreen = false) => new()
    {
        AppId = "CC1AD845",
        DisplayName = "Default Media Receiver",
        SessionId = "session-1",
        TransportId = "web-1",
        Namespaces = namespaces ?? ["urn:x-cast:com.google.cast.media"],
        IsIdleScreen = isIdleScreen,
    };

    [Fact]
    public void ToJson_EmitsAllExpectedFields()
    {
        using var session = CreateSession();

        var json = session.ToJson();

        Assert.Equal("CC1AD845", json["appId"]!.GetValue<string>());
        Assert.Equal("WEB", json["appType"]!.GetValue<string>());
        Assert.Equal("Default Media Receiver", json["displayName"]!.GetValue<string>());
        Assert.Equal("", json["iconUrl"]!.GetValue<string>());
        Assert.False(json["isIdleScreen"]!.GetValue<bool>());
        Assert.False(json["launchedFromCloud"]!.GetValue<bool>());
        Assert.Equal("session-1", json["sessionId"]!.GetValue<string>());
        Assert.Equal("Ready To Cast", json["statusText"]!.GetValue<string>());
        Assert.Equal("web-1", json["transportId"]!.GetValue<string>());
    }

    [Fact]
    public void ToJson_UniversalAppId_MirrorsAppId()
    {
        using var session = CreateSession();

        var json = session.ToJson();

        Assert.Equal(session.AppId, json["universalAppId"]!.GetValue<string>());
    }

    [Fact]
    public void ToJson_Namespaces_BecomesArrayOfNameObjects()
    {
        using var session = CreateSession(["urn:x-cast:com.google.cast.media", "urn:x-cast:com.google.cast.broadcast"]);

        var json = session.ToJson();
        var namespaces = json["namespaces"]!.AsArray();

        Assert.Equal(2, namespaces.Count);
        Assert.Equal("urn:x-cast:com.google.cast.media", namespaces[0]!["name"]!.GetValue<string>());
        Assert.Equal("urn:x-cast:com.google.cast.broadcast", namespaces[1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void ToJson_IsIdleScreen_ReflectsInitValue()
    {
        using var session = CreateSession(isIdleScreen: true);

        var json = session.ToJson();

        Assert.True(json["isIdleScreen"]!.GetValue<bool>());
    }

    [Fact]
    public void StartMedia_ReturnsSessionWithMediaSessionIdOne()
    {
        using var session = CreateSession();

        using var media = session.StartMedia([], 0, autoplay: false, null);

        Assert.Equal(1, media.MediaSessionId);
        Assert.Same(media, session.Media);
    }

    [Fact]
    public void StartMedia_CalledTwice_IncrementsMediaSessionId()
    {
        using var session = CreateSession();

        session.StartMedia([], 0, autoplay: false, null);
        using var second = session.StartMedia([], 0, autoplay: false, null);

        Assert.Equal(2, second.MediaSessionId);
        Assert.Same(second, session.Media);
    }

    [Fact]
    public async Task StartMedia_CalledTwice_DisposesPreviousMedia()
    {
        using var session = CreateSession();

        var first = session.StartMedia(new JsonObject { ["duration"] = 0.1 }, 0, autoplay: true, null);
        var fired = false;
        first.Finished += _ => fired = true;

        using var second = session.StartMedia([], 0, autoplay: false, null);

        // The completion timer polls every 500ms; if disposal failed to stop it,
        // Finished would still fire for the replaced session.
        await Task.Delay(700);

        Assert.False(fired);
    }

    [Fact]
    public void Dispose_MediaIsNull_DoesNotThrow()
    {
        var session = CreateSession();

        var exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
    }
}
