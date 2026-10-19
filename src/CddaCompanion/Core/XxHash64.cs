using System.Buffers.Binary;
using System.Numerics;

namespace CddaCompanion.Core;

/// <summary>XXH64, used by CDDA's zzip archives (seed 0x1337C0DE) to checksum entries and footers.</summary>
public static class XxHash64
{
    const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL,
                P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;

    static ulong Round(ulong acc, ulong input) => BitOperations.RotateLeft(acc + input * P2, 31) * P1;
    static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed)
    {
        int len = data.Length, i = 0;
        ulong h;
        if (len >= 32)
        {
            ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            int limit = len - 32;
            while (i <= limit)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i))); i += 8;
                v2 = Round(v2, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i))); i += 8;
                v3 = Round(v3, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i))); i += 8;
                v4 = Round(v4, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i))); i += 8;
            }
            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) +
                BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1); h = Merge(h, v2); h = Merge(h, v3); h = Merge(h, v4);
        }
        else h = seed + P5;
        h += (ulong)len;
        while (i + 8 <= len)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i)));
            h = BitOperations.RotateLeft(h, 27) * P1 + P4; i += 8;
        }
        if (i + 4 <= len)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i)) * P1;
            h = BitOperations.RotateLeft(h, 23) * P2 + P3; i += 4;
        }
        while (i < len)
        {
            h ^= data[i] * P5;
            h = BitOperations.RotateLeft(h, 11) * P1; i++;
        }
        h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
        return h;
    }
}
