namespace RCN1Bridge.Core.Input;

// Hysteresis stops a dial resting right on the threshold from chattering
public struct ThresholdButton
{
    public const float DefaultPress = 32000f / 32767f;
    public const float DefaultReleaseGap = 0.03f;

    public bool IsPressed { get; private set; }

    public bool Update(float value, float pressAt = DefaultPress, float releaseGap = DefaultReleaseGap)
    {
        IsPressed = IsPressed ? value > pressAt - releaseGap : value >= pressAt;
        return IsPressed;
    }

    public void Reset() => IsPressed = false;
}
