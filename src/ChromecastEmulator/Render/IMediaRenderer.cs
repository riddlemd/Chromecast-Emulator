using ChromecastEmulator.Device;

namespace ChromecastEmulator.Render;

/// What the media namespace needs from a render window. Handlers hold this rather than a
/// window so they stay identical whether or not --render is on.
public interface IMediaRenderer
{
    /// New item loaded: start decoding it and show it.
    void Load(MediaSession media);

    /// Playback state moved (play/pause/seek/rate). Implementations must be idempotent —
    /// this fires after every transport command, including ones that changed nothing.
    void Sync(MediaSession media);

    void SetVolume(Volume volume);

    /// Media or app torn down: stop decoding and return to the idle screen.
    void Clear();
}

/// The window as the controller uses it. Keeping this narrow is what lets the controller
/// be tested without a webview — see the note on RenderWindow about the main thread.
public interface IRenderSurface
{
    /// The page loaded and holds no state of its own; subscribers re-send what it should show.
    event Action? Ready;

    /// Delivers a command to the page. Safe from any thread.
    void Post(string json);
}

/// Used when --render is off. Handlers never null-check.
public sealed class NullMediaRenderer : IMediaRenderer
{
    public static readonly NullMediaRenderer Instance = new();

    private NullMediaRenderer() { }

    public void Load(MediaSession media) { }
    public void Sync(MediaSession media) { }
    public void SetVolume(Volume volume) { }
    public void Clear() { }
}
