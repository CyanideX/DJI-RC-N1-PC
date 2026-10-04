namespace RCN1Bridge.Core.Input;

public readonly record struct AxisCalibration(ushort Min, ushort Center, ushort Max)
{
    public static AxisCalibration Factory { get; } = new(364, 1024, 1684);

    public bool IsValid => Min < Center && Center < Max;

    // Each half scales on its own so an off-centre stick still reaches full travel both ways
    public float Normalize(ushort raw)
    {
        if (!IsValid)
            return Factory.Normalize(raw);

        float value = raw >= Center
            ? (float)(raw - Center) / (Max - Center)
            : (float)(raw - Center) / (Center - Min);
        return Math.Clamp(value, -1f, 1f);
    }
}
