using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChromecastEmulator.Device;

public sealed class Volume
{
    private double _level = 1.0;
    private bool _muted;

    /// Raised on every change. The receiver namespace, the media namespace and the
    /// console all set volume, so subscribers hook this rather than each call site.
    /// Unrelated to `VirtualDevice.StatusChanged` — volume is absent from the mDNS TXT
    /// records and must not trigger a republish.
    public event Action<Volume>? Changed;

    public double Level
    {
        get => _level;
        set
        {
            if (_level.Equals(value)) return;
            _level = value;
            Changed?.Invoke(this);
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value) return;
            _muted = value;
            Changed?.Invoke(this);
        }
    }

    public double StepInterval { get; set; } = 0.05;

    /// Single owner of the wire-shape parsing so the receiver and media namespaces
    /// cannot drift in how they clamp or validate.
    public void Apply(JsonObject? volume)
    {
        if (volume is null) return;

        if (volume["level"] is { } level && level.GetValueKind() == JsonValueKind.Number)
            Level = Math.Clamp(level.GetValue<double>(), 0, 1);

        if (volume["muted"] is { } muted && muted.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
            Muted = muted.GetValue<bool>();
    }

    public JsonObject ToJson() => new()
    {
        ["controlType"] = "attenuation",
        ["level"] = Math.Round(Level, 4),
        ["muted"] = Muted,
        ["stepInterval"] = StepInterval,
    };
}
