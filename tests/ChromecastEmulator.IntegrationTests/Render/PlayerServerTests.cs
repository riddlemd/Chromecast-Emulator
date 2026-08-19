using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ChromecastEmulator.Render;
using ChromecastEmulator.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChromecastEmulator.IntegrationTests.Render;

public class PlayerServerTests
{
    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/index.html", "text/html")]
    [InlineData("/player.js", "text/javascript")]
    [InlineData("/hls.js", "text/javascript")]
    public async Task Get_EmbeddedAsset_Returns200WithExpectedContentTypeAndNonEmptyBody(string path, string expectedMediaType)
    {
        using var temp = new TempState();
        using var server = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);
        server.Start();
        using var client = new HttpClient();

        var response = await client.GetAsync(server.BaseUrl + path);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedMediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(body);
    }

    [Fact]
    public async Task Get_UnknownPath_Returns404()
    {
        using var temp = new TempState();
        using var server = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);
        server.Start();
        using var client = new HttpClient();

        var response = await client.GetAsync(server.BaseUrl + "/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_MediaPlaylist_ReturnsHlsContentType()
    {
        using var temp = new TempState();
        File.WriteAllText(Path.Combine(temp.Directory, "index.m3u8"), "#EXTM3U\n#EXTINF:2.0,\nseg00000.ts\n");
        using var server = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);
        server.Start();
        using var client = new HttpClient();

        var response = await client.GetAsync(server.BaseUrl + "/media/index.m3u8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.apple.mpegurl", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_MediaSegment_ReturnsMp2tContentType()
    {
        using var temp = new TempState();
        File.WriteAllBytes(Path.Combine(temp.Directory, "seg0.ts"), [1, 2, 3, 4]);
        using var server = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);
        server.Start();
        using var client = new HttpClient();

        var response = await client.GetAsync(server.BaseUrl + "/media/seg0.ts");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video/mp2t", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_MediaFileWithRangeHeader_Returns206WithExactRequestedBytes()
    {
        using var temp = new TempState();
        var content = Encoding.ASCII.GetBytes(new string('a', 50) + new string('b', 50));
        File.WriteAllBytes(Path.Combine(temp.Directory, "seg0.ts"), content);
        using var server = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);
        server.Start();
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Get, server.BaseUrl + "/media/seg0.ts");
        request.Headers.Range = new RangeHeaderValue(10, 19);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 10-19/100", response.Content.Headers.ContentRange?.ToString());
        Assert.Equal(content[10..20], body);
    }

    [Fact]
    public async Task Get_MediaPathWithTraversalSequence_DoesNotServeFileOutsideMediaDirectory()
    {
        using var temp = new TempState();
        var mediaDirectory = Path.Combine(temp.Directory, "media");
        Directory.CreateDirectory(mediaDirectory);
        File.WriteAllText(Path.Combine(temp.Directory, "secret.txt"), "top secret");
        using var server = new PlayerServer(0, mediaDirectory, NullLogger<PlayerServer>.Instance);
        server.Start();
        using var client = new HttpClient();

        var response = await client.GetAsync(server.BaseUrl + "/media/..%2f..%2fsecret.txt");
        var body = await response.Content.ReadAsStringAsync();

        // End-to-end backstop only. HttpListener normalises `..` away before a request can
        // reach the resolver, so this cannot exercise the check itself — that is what
        // ResolveMediaFile_PathEscapingTheMediaDirectory_ResolvesToNothing is for.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("top secret", body);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../secret.txt")]
    [InlineData("/etc/passwd")]
    public void ResolveMediaFile_PathEscapingTheMediaDirectory_ResolvesToNothing(string requested)
    {
        using var temp = new TempState();
        var mediaDirectory = Path.Combine(temp.Directory, "media");
        Directory.CreateDirectory(mediaDirectory);

        var resolved = PlayerServer.ResolveMediaFile(mediaDirectory, requested);

        Assert.True(
            resolved is null || resolved.StartsWith(mediaDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            $"escaped the media directory: {resolved}");
    }

    [Fact]
    public void ResolveMediaFile_OrdinarySegmentName_ResolvesInsideTheMediaDirectory()
    {
        using var temp = new TempState();

        var resolved = PlayerServer.ResolveMediaFile(temp.Directory, "seg00001.ts");

        Assert.Equal(Path.Combine(temp.Directory, "seg00001.ts"), resolved);
    }

    [Fact]
    public void Port_ZeroExplicit_PicksAFreePortAndBindsIt()
    {
        using var temp = new TempState();
        using var first = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);
        using var second = new PlayerServer(0, temp.Directory, NullLogger<PlayerServer>.Instance);

        // A hardcoded "free" port would satisfy Port > 0; two instances colliding would
        // not, and Start proves the port is actually bindable.
        Assert.NotEqual(first.Port, second.Port);
        first.Start();
        second.Start();
    }

    [Fact]
    public void Port_ExplicitValue_IsUsedAsIs()
    {
        using var temp = new TempState();
        int freePort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            freePort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var server = new PlayerServer(freePort, temp.Directory, NullLogger<PlayerServer>.Instance);

        Assert.Equal(freePort, server.Port);
    }
}
