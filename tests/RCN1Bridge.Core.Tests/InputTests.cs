using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Protocol;

namespace RCN1Bridge.Core.Tests;

public class CalibrationTests
{
    [Theory]
    [InlineData(364, -1f)]
    [InlineData(694, -0.5f)]
    [InlineData(1024, 0f)]
    [InlineData(1354, 0.5f)]
    [InlineData(1684, 1f)]
    [InlineData(0, -1f)]
    [InlineData(2000, 1f)]
    public void FactoryRange(int raw, float expected) =>
        Assert.Equal(expected, AxisCalibration.Factory.Normalize((ushort)raw), 4);

    [Theory]
    [InlineData(400, -1f)]
    [InlineData(700, -0.5f)]
    [InlineData(1000, 0f)]
    [InlineData(1350, 0.5f)]
    [InlineData(1700, 1f)]
    public void AsymmetricHalvesScaleSeparately(int raw, float expected) =>
        Assert.Equal(expected, new AxisCalibration(400, 1000, 1700).Normalize((ushort)raw), 4);

    [Fact]
    public void InvalidCalibrationFallsBackToFactory() =>
        Assert.Equal(1f, new AxisCalibration(1000, 1000, 1000).Normalize(1684), 4);
}

public class ShapingTests
{
    [Fact]
    public void InsideDeadzoneIsZero() =>
        Assert.Equal((0f, 0f), StickShaping.ApplyRadialDeadzone(0.02f, 0.02f, 0.05f));

    [Fact]
    public void DeadzoneEdgeStartsFromZero()
    {
        var (x, _) = StickShaping.ApplyRadialDeadzone(0.051f, 0f, 0.05f);
        Assert.InRange(x, 0f, 0.002f);
    }

    [Fact]
    public void FullDeflectionStaysFull()
    {
        Assert.Equal(1f, StickShaping.ApplyRadialDeadzone(1f, 0f, 0.1f).X, 4);
        var (cx, cy) = StickShaping.ApplyRadialDeadzone(1f, 1f, 0.1f);
        Assert.Equal(1f, cx, 4);
        Assert.Equal(1f, cy, 4);
    }

    [Fact]
    public void DeadzoneKeepsDirection()
    {
        var (x, y) = StickShaping.ApplyRadialDeadzone(0.3f, -0.4f, 0.1f);
        Assert.Equal(-0.4f / 0.3f, y / x, 4);
    }

    [Theory]
    [InlineData(0f, 0.5f, 0.5f)]
    [InlineData(1f, 0.5f, 0.125f)]
    [InlineData(0.5f, 0.5f, 0.3125f)]
    [InlineData(1f, 1f, 1f)]
    [InlineData(0.7f, -1f, -1f)]
    public void Expo(float expo, float input, float expected) =>
        Assert.Equal(expected, new StickShaping { Expo = expo }.Curve(input), 4);

    [Fact]
    public void RateScales() =>
        Assert.Equal(-0.5f, new StickShaping { Rate = 0.5f }.Curve(-1f), 4);

    [Fact]
    public void AxisDeadzone()
    {
        Assert.Equal(0f, StickShaping.ApplyDeadzone(-0.05f, 0.1f));
        Assert.Equal(-1f, StickShaping.ApplyDeadzone(-1f, 0.1f), 4);
        Assert.Equal(0.5f, StickShaping.ApplyDeadzone(0.55f, 0.1f), 4);
    }
}

public class SmootherTests
{
    [Fact]
    public void OffPassesThrough()
    {
        var s = new AxisSmoother();
        s.Next(0f, 0f, 0.01f);
        Assert.Equal(1f, s.Next(1f, 0f, 0.01f));
    }

    [Fact]
    public void OneTimeConstantReaches63Percent()
    {
        var s = new AxisSmoother();
        s.Next(0f, 20f, 0.01f);
        Assert.Equal(1f - MathF.Exp(-1f), s.Next(1f, 20f, 0.02f), 4);
    }
}

public class ThresholdButtonTests
{
    [Fact]
    public void PressesAtThresholdAndReleasesBelowGap()
    {
        var b = new ThresholdButton();
        Assert.False(b.Update(0.9f, 0.95f, 0.05f));
        Assert.True(b.Update(0.95f, 0.95f, 0.05f));
        Assert.True(b.Update(0.91f, 0.95f, 0.05f));
        Assert.False(b.Update(0.9f, 0.95f, 0.05f));
    }
}

public class StickProcessorTests
{
    [Fact]
    public void MapsChannelsToSticks()
    {
        var p = new StickProcessor { Left = new StickShaping { Deadzone = 0 }, Right = new StickShaping { Deadzone = 0 } };

        var result = p.Process(new RawSticks(RightH: 1684, RightV: 364, LeftH: 1354, LeftV: 694, Dial: 1024, HasDial: true), 0.007f);

        Assert.Equal(1f, result.RightX, 4);
        Assert.Equal(-1f, result.RightY, 4);
        Assert.Equal(0.5f, result.LeftX, 4);
        Assert.Equal(-0.5f, result.LeftY, 4);
        Assert.Equal(0f, result.Dial);
    }

    [Fact]
    public void DialEndsPressButtons()
    {
        var p = new StickProcessor();

        var up = p.Process(new RawSticks(1024, 1024, 1024, 1024, 1684, true), 0.007f);
        Assert.True(up.DialUp);
        Assert.False(up.DialDown);

        var down = p.Process(new RawSticks(1024, 1024, 1024, 1024, 364, true), 0.007f);
        Assert.False(down.DialUp);
        Assert.True(down.DialDown);
    }

    [Fact]
    public void CompactFrameLeavesDialNeutral()
    {
        var result = new StickProcessor().Process(new RawSticks(1024, 1024, 1024, 1024, 0, false), 0.007f);
        Assert.Equal(0f, result.Dial);
        Assert.False(result.DialDown);
    }

    [Theory]
    [InlineData(1f, 32767)]
    [InlineData(-1f, -32767)]
    [InlineData(2f, 32767)]
    [InlineData(-2f, -32768)]
    [InlineData(0f, 0)]
    public void ToAxis(float value, short expected) => Assert.Equal(expected, StickProcessor.ToAxis(value));
}

public class HotPathTests
{
    [Fact]
    public void ParseDecodeProcessAllocatesNothing()
    {
        var processor = new StickProcessor { Left = new StickShaping { Expo = 0.3f, SmoothingMs = 10 } };
        float sink = 0;
        var parser = new DumlParser(frame =>
        {
            if (StickDecoder.TryDecode(frame, out var raw))
                sink += processor.Process(raw, 0.007f).LeftX;
        });

        var stream = Enumerable.Range(0, 64)
            .SelectMany(i => TestFrames.Extended(1024, 1024, 1024, (ushort)(400 + i * 10), 1024).Concat(new byte[] { 0x55, 0x00 }))
            .ToArray();

        parser.Feed(stream);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            parser.Feed(stream);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(64 * 101, parser.FrameCount);
        Assert.NotEqual(0, sink);
    }
}
