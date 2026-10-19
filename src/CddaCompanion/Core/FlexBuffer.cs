using System.Text;

namespace CddaCompanion.Core;

/// <summary>
/// Minimal read-only FlexBuffers decoder (the format of the zzip footer).
/// Only what the footer uses: maps, vectors, keys, strings and integers.
/// </summary>
public readonly struct FlexRef
{
    readonly byte[] _buf;
    readonly int _pos, _parentWidth, _byteWidth;
    public readonly int Type;

    // FlexBuffers type ids
    public const int Null = 0, Int = 1, UInt = 2, Float = 3, Key = 4, String = 5, IndirectInt = 6,
                     IndirectUInt = 7, IndirectFloat = 8, Map = 9, Vector = 10, VectorInt = 11,
                     VectorUInt = 12, VectorFloat = 13, VectorKey = 14, Bool = 26, VectorBool = 36;

    FlexRef(byte[] buf, int pos, int parentWidth, byte packedType)
    {
        _buf = buf; _pos = pos; _parentWidth = parentWidth;
        _byteWidth = 1 << (packedType & 3); Type = packedType >> 2;
    }

    FlexRef(byte[] buf, int pos, int parentWidth, int byteWidth, int type)
    {
        _buf = buf; _pos = pos; _parentWidth = parentWidth; _byteWidth = byteWidth; Type = type;
    }

    public static FlexRef Root(byte[] buf)
    {
        if (buf.Length < 3) throw new InvalidDataException("FlexBuffer too short");
        int rootWidth = buf[^1];
        byte packed = buf[^2];
        int pos = buf.Length - 2 - rootWidth;
        if (pos < 0) throw new InvalidDataException("Bad FlexBuffer root");
        return new FlexRef(buf, pos, rootWidth, packed);
    }

    static ulong ReadU(byte[] b, int pos, int width)
    {
        if (pos < 0 || pos + width > b.Length) throw new InvalidDataException("FlexBuffer read out of range");
        return width switch
        {
            1 => b[pos],
            2 => BitConverter.ToUInt16(b, pos),
            4 => BitConverter.ToUInt32(b, pos),
            8 => BitConverter.ToUInt64(b, pos),
            _ => throw new InvalidDataException("Bad FlexBuffer width")
        };
    }

    static long ReadI(byte[] b, int pos, int width) => width switch
    {
        1 => (sbyte)b[pos],
        2 => BitConverter.ToInt16(b, pos),
        4 => BitConverter.ToInt32(b, pos),
        8 => BitConverter.ToInt64(b, pos),
        _ => throw new InvalidDataException("Bad FlexBuffer width")
    };

    int Indirect => checked(_pos - (int)ReadU(_buf, _pos, _parentWidth));

    public long AsLong => Type switch
    {
        Int => ReadI(_buf, _pos, _parentWidth),
        UInt => (long)ReadU(_buf, _pos, _parentWidth),
        Bool => (long)ReadU(_buf, _pos, _parentWidth),
        IndirectInt => ReadI(_buf, Indirect, _byteWidth),
        IndirectUInt => (long)ReadU(_buf, Indirect, _byteWidth),
        Float => (long)(_parentWidth == 8 ? BitConverter.ToDouble(_buf, _pos) : BitConverter.ToSingle(_buf, _pos)),
        _ => throw new InvalidDataException($"FlexBuffer value of type {Type} is not a number")
    };

    public string AsString
    {
        get
        {
            if (Type == String)
            {
                int start = Indirect;
                int len = (int)ReadU(_buf, start - _byteWidth, _byteWidth);
                return Encoding.UTF8.GetString(_buf, start, len);
            }
            if (Type == Key)
            {
                int start = Indirect, end = start;
                while (end < _buf.Length && _buf[end] != 0) end++;
                return Encoding.UTF8.GetString(_buf, start, end - start);
            }
            throw new InvalidDataException($"FlexBuffer value of type {Type} is not a string");
        }
    }

    bool IsTypedVector => Type >= VectorInt && Type <= 15 || Type == VectorBool;

    public int Count
    {
        get
        {
            if (Type != Map && Type != Vector && !IsTypedVector) throw new InvalidDataException($"FlexBuffer value of type {Type} is not a vector");
            int vec = Indirect;
            return (int)ReadU(_buf, vec - _byteWidth, _byteWidth);
        }
    }

    public FlexRef this[int i]
    {
        get
        {
            int vec = Indirect, n = Count;
            if ((uint)i >= (uint)n) throw new IndexOutOfRangeException();
            if (Type == Map || Type == Vector)
            {
                byte packed = _buf[vec + n * _byteWidth + i];
                return new FlexRef(_buf, vec + i * _byteWidth, _byteWidth, packed);
            }
            int elemType = Type == VectorBool ? Bool : Type == VectorKey ? Key : Type - VectorInt + Int;
            return new FlexRef(_buf, vec + i * _byteWidth, _byteWidth, 1, elemType);
        }
    }

    /// <summary>Keys of a map, in stored (sorted) order.</summary>
    public string[] Keys
    {
        get
        {
            if (Type != Map) throw new InvalidDataException("Not a map");
            int vec = Indirect;
            int keysOffsetPos = vec - 3 * _byteWidth;
            int keysVec = checked(keysOffsetPos - (int)ReadU(_buf, keysOffsetPos, _byteWidth));
            int keyWidth = (int)ReadU(_buf, vec - 2 * _byteWidth, _byteWidth);
            int n = Count;
            var keys = new string[n];
            for (int i = 0; i < n; i++)
            {
                int kp = keysVec + i * keyWidth;
                int start = checked(kp - (int)ReadU(_buf, kp, keyWidth)), end = start;
                while (end < _buf.Length && _buf[end] != 0) end++;
                keys[i] = Encoding.UTF8.GetString(_buf, start, end - start);
            }
            return keys;
        }
    }

    public bool TryGet(string key, out FlexRef value)
    {
        var keys = Keys;
        for (int i = 0; i < keys.Length; i++)
            if (keys[i] == key) { value = this[i]; return true; }
        value = default;
        return false;
    }
}
