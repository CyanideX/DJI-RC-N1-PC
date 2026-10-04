namespace RCN1Bridge.Core.Input;

public sealed record StickShaping
{
    public static StickShaping Default { get; } = new();

    public float Deadzone { get; init; } = 0.03f;
    public float Expo { get; init; }
    public float Rate { get; init; } = 1f;
    public float SmoothingMs { get; init; }

    public (float X, float Y) Apply(float x, float y)
    {
        (x, y) = ApplyRadialDeadzone(x, y, Deadzone);
        return (Curve(x), Curve(y));
    }

    public float Curve(float value)
    {
        float expo = Math.Clamp(Expo, 0f, 1f);
        float shaped = expo * value * value * value + (1f - expo) * value;
        return Math.Clamp(shaped * Math.Clamp(Rate, 0f, 1f), -1f, 1f);
    }

    // Scaled radial: output starts from 0 at the zone edge instead of jumping to the deadzone value
    public static (float X, float Y) ApplyRadialDeadzone(float x, float y, float deadzone)
    {
        deadzone = Math.Clamp(deadzone, 0f, 0.95f);
        float magnitude = MathF.Sqrt(x * x + y * y);
        if (magnitude <= deadzone)
            return (0f, 0f);

        float scale = (magnitude - deadzone) / (1f - deadzone) / magnitude;
        return (Math.Clamp(x * scale, -1f, 1f), Math.Clamp(y * scale, -1f, 1f));
    }

    public static float ApplyDeadzone(float value, float deadzone)
    {
        deadzone = Math.Clamp(deadzone, 0f, 0.95f);
        float magnitude = MathF.Abs(value);
        if (magnitude <= deadzone)
            return 0f;
        return MathF.CopySign((magnitude - deadzone) / (1f - deadzone), value);
    }
}
