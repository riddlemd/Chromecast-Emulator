using System.Text.Json.Nodes;
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
    public void Apply_NullVolume_LeavesStateAloneAndDoesNotThrow()
    {
        var volume = new Volume { Level = 0.4, Muted = true };

        volume.Apply(null);

        Assert.Equal(0.4, volume.Level);
        Assert.True(volume.Muted);
    }

    [Theory]
    [InlineData("\"loud\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Apply_LevelIsNotANumber_KeepsThePreviousLevel(string levelJson)
    {
        var volume = new Volume { Level = 0.4 };

        // Senders do send junk here; taking it would throw out of the handler or park the
        // device at a nonsense level.
        volume.Apply(new JsonObject { ["level"] = JsonNode.Parse(levelJson) });

        Assert.Equal(0.4, volume.Level);
    }

    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("null")]
    [InlineData("1")]
    public void Apply_MutedIsNotABoolean_KeepsThePreviousMuted(string mutedJson)
    {
        var volume = new Volume { Muted = true };

        volume.Apply(new JsonObject { ["muted"] = JsonNode.Parse(mutedJson) });

        Assert.True(volume.Muted);
    }

    [Theory]
    [InlineData(-2.0, 0.0)]
    [InlineData(5.0, 1.0)]
    public void Apply_LevelOutOfRange_ClampsToZeroOne(double sent, double expected)
    {
        var volume = new Volume();

        volume.Apply(new JsonObject { ["level"] = sent });

        Assert.Equal(expected, volume.Level);
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

    [Fact]
    public void Level_SetToDifferentValue_RaisesChanged()
    {
        var volume = new Volume();
        var raised = 0;
        volume.Changed += _ => raised++;

        volume.Level = 0.5;

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Level_SetToSameValue_DoesNotRaiseChanged()
    {
        var volume = new Volume();
        var raised = 0;
        volume.Changed += _ => raised++;

        volume.Level = volume.Level;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Muted_SetToDifferentValue_RaisesChanged()
    {
        var volume = new Volume();
        var raised = 0;
        volume.Changed += _ => raised++;

        volume.Muted = true;

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Muted_SetToSameValue_DoesNotRaiseChanged()
    {
        var volume = new Volume();
        var raised = 0;
        volume.Changed += _ => raised++;

        volume.Muted = false;

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Apply_NewLevel_RaisesChangedWithVolumeInstance()
    {
        var volume = new Volume();
        Volume? received = null;
        volume.Changed += v => received = v;

        volume.Apply(new JsonObject { ["level"] = 0.25 });

        Assert.Same(volume, received);
        Assert.Equal(0.25, volume.Level);
    }

    [Fact]
    public void Apply_SameLevelAndMuted_DoesNotRaiseChanged()
    {
        var volume = new Volume();
        var raised = 0;
        volume.Changed += _ => raised++;

        volume.Apply(new JsonObject { ["level"] = volume.Level, ["muted"] = volume.Muted });

        Assert.Equal(0, raised);
    }
}
