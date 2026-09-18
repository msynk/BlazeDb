namespace BlazeDb.Wal;

/// <summary>CRC-32 (IEEE 802.3, reflected polynomial 0xEDB88320) used to detect torn WAL writes.</summary>
internal static class BlazeDbCrc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Seed, data));

    /// <summary>A checksum over two runs of bytes that are not contiguous in memory.</summary>
    public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second) =>
        Finish(Update(Update(Seed, first), second));

    /// <summary>Starting value for an incremental checksum; feed it through <see cref="Update"/>.</summary>
    public const uint Seed = 0xFFFFFFFFu;

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }
        return crc;
    }

    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;
}
