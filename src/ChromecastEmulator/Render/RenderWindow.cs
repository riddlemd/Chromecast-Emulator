using System.Drawing;
using Microsoft.Extensions.Logging;
using Photino.NET;

namespace ChromecastEmulator.Render;

/// The Photino window hosting the player page.
///
/// AppKit ties its run loop to the process main thread: constructing the window anywhere
/// else dies with "setting the main menu on a non-main thread". <see cref="Run"/> must
/// therefore be called from Main, and everything else marshals in through Photino's
/// Invoke.
public sealed class RenderWindow : IRenderSurface
{
    private readonly EmulatorOptions _options;
    private readonly ILogger<RenderWindow> _logger;

    private PhotinoWindow? _window;

    public RenderWindow(EmulatorOptions options, ILogger<RenderWindow> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// Raised when the page has loaded and can accept commands. Also fires again if the
    /// webview reloads, so subscribers should re-send the current state rather than
    /// assume a single delivery.
    public event Action? Ready;

    /// Raised when the user closes the window.
    public event Action? Closed;

    /// Runs the native message loop. Blocks until the window closes.
    public void Run(string url)
    {
        var window = new PhotinoWindow()
            .SetTitle(_options.FriendlyName)
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(_options.RenderWidth, _options.RenderHeight))
            .Center()
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler(OnWebMessage)
            .Load(new Uri($"{url}/index.html"));

        _window = window;
        _logger.LogInformation("render window open at {Width}x{Height}", _options.RenderWidth, _options.RenderHeight);

        window.WaitForClose();

        _window = null;
        _logger.LogInformation("render window closed");
        Closed?.Invoke();
    }

    /// Safe from any thread; a command sent before the window exists is dropped, and the
    /// Ready event is what recovers the state.
    public void Post(string json)
    {
        var window = _window;
        if (window is null) return;

        try
        {
            window.SendWebMessage(json);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("render command dropped: {Reason}", ex.Message);
        }
    }

    public void Close()
    {
        var window = _window;
        if (window is null) return;

        try
        {
            window.Invoke(window.Close);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("could not close render window: {Reason}", ex.Message);
        }
    }

    private void OnWebMessage(object? sender, string message)
    {
        if (message == "ready")
        {
            Ready?.Invoke();
            return;
        }

        // Anything else is the page reporting what the video element actually did, which
        // is the only view we get of real playback.
        _logger.LogDebug("render page: {Message}", message);
    }
}
