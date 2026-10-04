namespace RCN1Bridge.Core.Protocol;

// DUML uses reflected CRC-8 (poly 0x31) and CRC-16/KERMIT (poly 0x1021) tables with non-standard seeds
public static class Crc
{
    public const byte Crc8Seed = 0x77;
    public const ushort Crc16Seed = 0x3692;

    internal static readonly byte[] Crc8Table = BuildCrc8Table();
    internal static readonly ushort[] Crc16Table = BuildCrc16Table();

    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        byte crc = Crc8Seed;
        foreach (byte b in data)
            crc = Crc8Table[b ^ crc];
        return crc;
    }

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = Crc16Seed;
        foreach (byte b in data)
            crc = (ushort)((crc >> 8) ^ Crc16Table[(b ^ crc) & 0xFF]);
        return crc;
    }

    private static byte[] BuildCrc8Table()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8C : crc >> 1;
            table[i] = (byte)crc;
        }
        return table;
    }

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
            table[i] = (ushort)crc;
        }
        return table;
    }
}
