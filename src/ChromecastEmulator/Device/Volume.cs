using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChromecastEmulator.Device;

public sealed class Volume
{
    public double Level { get; set; } = 1.0;
    public bool Muted { get; set; }
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
