namespace RCN1Bridge.Core.Output;

[Flags]
public enum PadHold
{
    None = 0,
    Paused = 1,
    Reader = 2,
}

// Called from the reader and poll threads; implementations lock internally
public interface IGamepadOutput
{
    void Submit(in PadReport report);
    void SubmitNeutral();

    // While any hold is on, Submit sends neutral. Decided under the output's own lock, so a report
    // already on its way from the reader thread can't land after the neutral.
    void SetHold(PadHold hold, bool on);
}

public sealed class NullGamepadOutput : IGamepadOutput
{
    public static NullGamepadOutput Instance { get; } = new();

    public void Submit(in PadReport report) { }
    public void SubmitNeutral() { }
    public void SetHold(PadHold hold, bool on) { }
}
