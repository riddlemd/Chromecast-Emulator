using ChromecastEmulator.Device;

namespace ChromecastEmulator.UnitTests.Device;

public class VolumeTests
{
    [Fact]
    public void Level_Default_IsOne()
    {
        var volume = new Volume();

        Assert.Equal(1.0, volume.Level);
    }

    [Fact]
    public void Muted_Default_IsFalse()
    {
        var volume = new Volume();

        Assert.False(volume.Muted);
    }

    [Fact]
    public void StepInterval_Default_IsPointZeroFive()
    {
        var volume = new Volume();

        Assert.Equal(0.05, volume.StepInterval);
    }

    [Fact]
    public void ToJson_Default_EmitsExpectedFields()
    {
        var volume = new Volume();

        var json = volume.ToJson();

        Assert.Equal("attenuation", json["controlType"]!.GetValue<string>());
        Assert.Equal(1.0, json["level"]!.GetValue<double>());
        Assert.False(json["muted"]!.GetValue<bool>());
        Assert.Equal(0.05, json["stepInterval"]!.GetValue<double>());
    }

    [Fact]
    public void ToJson_CustomValues_ReflectsThem()
    {
        var volume = new Volume { Level = 0.5, Muted = true, StepInterval = 0.1 };

        var json = volume.ToJson();

        Assert.Equal(0.5, json["level"]!.GetValue<double>());
        Assert.True(json["muted"]!.GetValue<bool>());
        Assert.Equal(0.1, json["stepInterval"]!.GetValue<double>());
    }

    [Fact]
    public void ToJson_Level_RoundsToFourDecimalPlaces()
    {
        var volume = new Volume { Level = 0.123456789 };

        var json = volume.ToJson();

        Assert.Equal(0.1235, json["level"]!.GetValue<double>());
    }
}
