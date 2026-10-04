using RCN1Bridge.Core.Input;

namespace RCN1Bridge.Core.Output;

// Called from the reader and poll threads; implementations lock internally
public interface IGamepadOutput
{
    void Submit(in ProcessedInput input);
    void SubmitNeutral();
}

public sealed class NullGamepadOutput : IGamepadOutput
{
    public static NullGamepadOutput Instance { get; } = new();

    public void Submit(in ProcessedInput input) { }
    public void SubmitNeutral() { }
}
