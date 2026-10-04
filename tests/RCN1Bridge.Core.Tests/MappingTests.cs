using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Mapping;
using RCN1Bridge.Core.Output;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

public class MappingTests
{
    private static readonly ProcessedInput Centre = default;

    [Fact]
    public void DefaultMatchesStraightThrough()
    {
        var input = new ProcessedInput(0.5f, -0.25f, 1f, -1f, 0.8f);
        Assert.Equal(PadReport.FromSticks(input), new InputMapper(MappingProfile.Default).Map(input, null, 0));
    }

    [Fact]
    public void DialDoesNothingByDefault()
    {
        var report = new InputMapper(MappingProfile.Default).Map(Centre with { Dial = 1f }, null, 0);
        Assert.Equal(PadReport.Neutral, report);
    }

    [Fact]
    public void SwapAndInvert()
    {
        var mapper = new InputMapper(MappingProfile.Default with
        {
            LeftX = new AxisBinding { Target = AxisTarget.RightX, Invert = true },
            RightX = new AxisBinding { Target = AxisTarget.LeftX },
        });
        var report = mapper.Map(Centre with { LeftX = 1f, RightX = 0.5f }, null, 0);
        Assert.Equal(-32767, report.RightX);
        Assert.Equal(16384, report.LeftX);
    }

    [Fact]
    public void SplitTriggers()
    {
        var mapper = new InputMapper(MappingProfile.Default with { Dial = new AxisBinding { Target = AxisTarget.SplitTriggers } });
        Assert.Equal((byte)255, mapper.Map(Centre with { Dial = 1f }, null, 0).RightTrigger);
        var down = mapper.Map(Centre with { Dial = -0.5f }, null, 0);
        Assert.Equal((byte)128, down.LeftTrigger);
        Assert.Equal((byte)0, down.RightTrigger);
    }

    [Fact]
    public void OneWayTriggerIgnoresOtherHalf()
    {
        var mapper = new InputMapper(MappingProfile.Default with { Dial = new AxisBinding { Target = AxisTarget.LeftTrigger } });
        Assert.Equal((byte)0, mapper.Map(Centre with { Dial = -1f }, null, 0).LeftTrigger);
        Assert.Equal((byte)255, mapper.Map(Centre with { Dial = 1f }, null, 0).LeftTrigger);
    }

    [Fact]
    public void DialEndsPressButtonsWithHysteresis()
    {
        var mapper = new InputMapper(MappingProfile.Default with
        {
            Dial = new AxisBinding { Target = AxisTarget.Buttons, Positive = PadButton.DPadUp, Negative = PadButton.DPadDown },
        });
        Assert.Equal(PadButton.None, mapper.Map(Centre with { Dial = 0.9f }, null, 0).Buttons);
        Assert.Equal(PadButton.DPadUp, mapper.Map(Centre with { Dial = 1f }, null, 0).Buttons);
        Assert.Equal(PadButton.DPadUp, mapper.Map(Centre with { Dial = 0.96f }, null, 0).Buttons);
        Assert.Equal(PadButton.None, mapper.Map(Centre with { Dial = 0.9f }, null, 0).Buttons);
        Assert.Equal(PadButton.DPadDown, mapper.Map(Centre with { Dial = -1f }, null, 0).Buttons);
    }

    [Fact]
    public void ButtonsAreHeldWhilePressed()
    {
        var mapper = new InputMapper(MappingProfile.Default with { Fn = PadButton.A, Capture = PadButton.RightShoulder });
        var held = ButtonDecoder.Decode(0x1000 | 0x0002 | 0x0020);
        Assert.Equal(PadButton.A | PadButton.RightShoulder, mapper.Map(Centre, held, 0).Buttons);
        Assert.Equal(PadButton.None, mapper.Map(Centre, ButtonDecoder.Decode(0x1000), 0).Buttons);
    }

    [Fact]
    public void SwitchPulsesOnConnectAndOnChange()
    {
        var mapper = new InputMapper(MappingProfile.Default with { ModeC = PadButton.DPadLeft, ModeN = PadButton.DPadDown });
        long t = 1000;
        long pulse = (long)(InputMapper.PulseLength.TotalSeconds * Stopwatch.Frequency);
        var normal = ButtonDecoder.Decode(0x1000);
        var cine = ButtonDecoder.Decode(0x2000);

        Assert.Equal(PadButton.DPadDown, mapper.Map(Centre, normal, t).Buttons);
        Assert.Equal(PadButton.DPadDown, mapper.Map(Centre, normal, t + pulse - 1).Buttons);
        Assert.Equal(PadButton.None, mapper.Map(Centre, normal, t + pulse).Buttons);

        t += pulse * 10;
        Assert.Equal(PadButton.DPadLeft, mapper.Map(Centre, cine, t).Buttons);
        Assert.Equal(PadButton.None, mapper.Map(Centre, cine, t + pulse).Buttons);
    }

    [Fact]
    public void EditingBindingsDoesNotRepulse()
    {
        var profile = MappingProfile.Default with { ModeN = PadButton.DPadDown };
        var first = new InputMapper(profile);
        var normal = ButtonDecoder.Decode(0x1000);
        first.Map(Centre, normal, 0);

        var edited = new InputMapper(profile with { Fn = PadButton.A }, first);
        Assert.Equal(PadButton.None, edited.Map(Centre, normal, 0).Buttons);
    }

    [Fact]
    public void ResetPulsesAgain()
    {
        var mapper = new InputMapper(MappingProfile.Default with { ModeS = PadButton.DPadRight });
        var sport = ButtonDecoder.Decode(0x0000);
        mapper.Map(Centre, sport, 0);
        mapper.Reset();
        Assert.Equal(PadButton.DPadRight, mapper.Map(Centre, sport, 1_000_000_000).Buttons);
    }

    [Fact]
    public void UsesButtonsOnlyWhenBound()
    {
        Assert.False(new InputMapper(MappingProfile.Default).UsesButtons);
        Assert.True(new InputMapper(MappingProfile.Default with { ModeS = PadButton.B }).UsesButtons);
    }

    [Fact]
    public void ProfilesRoundTripThroughJson()
    {
        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        var mapping = MappingProfile.Default with
        {
            Dial = new AxisBinding { Target = AxisTarget.Buttons, Positive = PadButton.DPadUp, Invert = true },
            ModeC = PadButton.DPadLeft,
        };
        var tuning = TuningProfile.Default with
        {
            Left = new StickShaping { Deadzone = 0.05f, Expo = 0.3f },
            Calibration = new StickCalibration { RightV = new AxisCalibration(370, 1020, 1680) },
        };

        Assert.Equal(mapping, JsonSerializer.Deserialize<MappingProfile>(JsonSerializer.Serialize(mapping, json), json));
        Assert.Equal(tuning, JsonSerializer.Deserialize<TuningProfile>(JsonSerializer.Serialize(tuning, json), json));
    }
}
