using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;

namespace IconBuilder;

/// <summary>WOFF 1.0 → TrueType/OpenType (WPF reads TTF/OTF but not WOFF). Tables are zlib-compressed individually.</summary>
internal static class Woff
{
    public static byte[] ToSfnt(byte[] woff)
    {
        if (BinaryPrimitives.ReadUInt32BigEndian(woff) != 0x774F4646) // 'wOFF'
        {
            throw new FormatException("Not a WOFF 1.0 file.");
        }

        var flavor = BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(4));
        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(12));
        var tables = new List<(uint Tag, uint Checksum, byte[] Data)>(tableCount);
        for (var i = 0; i < tableCount; i++)
        {
            var entry = woff.AsSpan(44 + (i * 20), 20);
            var tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var compressedLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var originalLength = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            var checksum = BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);

            byte[] data;
            if (compressedLength < originalLength)
            {
                using var input = new ZLibStream(new MemoryStream(woff, offset, compressedLength), CompressionMode.Decompress);
                data = new byte[originalLength];
                input.ReadExactly(data);
            }
            else
            {
                data = woff.AsSpan(offset, originalLength).ToArray();
            }

            tables.Add((tag, checksum, data));
        }

        // sfnt header + table records + 4-byte aligned table data, tables sorted by tag.
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        var searchRange = 1;
        var entrySelector = 0;
        while (searchRange * 2 <= tableCount)
        {
            searchRange *= 2;
            entrySelector++;
        }

        searchRange *= 16;
        var headerLength = 12 + (16 * tableCount);
        var output = new MemoryStream();
        var header = new byte[headerLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, flavor);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), tableCount);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)((tableCount * 16) - searchRange));

        var position = headerLength;
        for (var i = 0; i < tables.Count; i++)
        {
            var record = header.AsSpan(12 + (i * 16), 16);
            BinaryPrimitives.WriteUInt32BigEndian(record, tables[i].Tag);
            BinaryPrimitives.WriteUInt32BigEndian(record[4..], tables[i].Checksum);
            BinaryPrimitives.WriteUInt32BigEndian(record[8..], (uint)position);
            BinaryPrimitives.WriteUInt32BigEndian(record[12..], (uint)tables[i].Data.Length);
            position += (tables[i].Data.Length + 3) & ~3;
        }

        output.Write(header);
        foreach (var (_, _, data) in tables)
        {
            output.Write(data);
            output.Write(new byte[((data.Length + 3) & ~3) - data.Length]);
        }

        return output.ToArray();
    }
}
