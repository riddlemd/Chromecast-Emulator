using ChromecastEmulator.Transport;

namespace ChromecastEmulator.TestSupport;

public sealed class TempState : IDisposable
{
    public TempState()
    {
        Directory = Path.Combine(Path.GetTempPath(), "chromecast-emulator-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public EmulatorOptions Options(Action<EmulatorOptions>? configure = null)
    {
        var options = new EmulatorOptions { StateDirectory = Directory };
        configure?.Invoke(options);
        return options;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// Generating an RSA key per test costs ~100ms, so every test that only needs
/// *a* certificate shares this one.
public static class SharedIdentity
{
    private static readonly Lazy<DeviceIdentity> Instance = new(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), "chromecast-emulator-tests", "shared-identity");
        Directory.CreateDirectory(directory);
        return DeviceIdentity.Load(new EmulatorOptions { StateDirectory = directory });
    });

    public static DeviceIdentity Value => Instance.Value;
}
