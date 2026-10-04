namespace RCN1Bridge.Core.Protocol;

public static class RcCommands
{
    public const byte PcAddress = 0x0A;
    public const byte RcAddress = 0x06;
    public const byte RequestType = 0x40;
    public const byte RcCommandSet = 0x06;

    public const byte GetChannels = 0x01;
    public const byte SimulatorMode = 0x24;
    public const byte AetrPush = 0x26;
    public const byte GetButtons = 0x27;

    public static byte[] BuildPoll(ushort sequence) =>
        DumlPacket.Build(PcAddress, RcAddress, sequence, RequestType, RcCommandSet, GetChannels);

    public static byte[] BuildSimulatorEnable(ushort sequence) =>
        DumlPacket.Build(PcAddress, RcAddress, sequence, RequestType, RcCommandSet, SimulatorMode, [0x01]);
}
