namespace RCN1Bridge.Core.Input;

// One-pole low-pass. Time based, so the feel doesn't change with the RC's update rate.
public struct AxisSmoother
{
    private float _value;
    private bool _primed;

    public float Next(float input, float smoothingMs, float elapsedSeconds)
    {
        if (smoothingMs <= 0f || !_primed || elapsedSeconds <= 0f)
        {
            _value = input;
            _primed = true;
            return input;
        }

        float alpha = 1f - MathF.Exp(-elapsedSeconds * 1000f / smoothingMs);
        _value += (input - _value) * alpha;
        return _value;
    }

    public void Reset() => _primed = false;
}
