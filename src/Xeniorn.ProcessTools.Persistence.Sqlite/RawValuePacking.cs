using System.Buffers.Binary;

namespace Xeniorn.ProcessTools.Persistence.Sqlite;

/// <summary>
/// Stores a series of samples as a compact blob: 8 bytes per sample, little-endian IEEE-754 double, no header.
/// The number of samples is the blob length / 8. Parsable by anything (SQL <c>substr</c> + a bit of decoding, Python
/// <c>struct.unpack('&lt;%dd')</c>, R <c>readBin</c>, ...) without this library.
/// </summary>
public static class RawValuePacking
{
    /// <summary>Packs <paramref name="values"/>.</summary>
    public static byte[] Pack(ReadOnlySpan<double> values)
    {
        var bytes = new byte[values.Length * sizeof(double)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * sizeof(double)), values[i]);
        return bytes;
    }

    /// <summary>Unpacks a blob written by <see cref="Pack"/>.</summary>
    public static double[] Unpack(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(double) != 0)
            throw new ArgumentException($"A packed series has a multiple of {sizeof(double)} bytes, got {bytes.Length}.", nameof(bytes));
        var values = new double[bytes.Length / sizeof(double)];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes[(i * sizeof(double))..]);
        return values;
    }
}
