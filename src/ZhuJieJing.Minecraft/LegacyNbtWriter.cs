using System.Buffers.Binary;

namespace ZhuJieJing.Minecraft;

internal sealed class LegacyNbtWriter
{
    private readonly Stream stream;

    public LegacyNbtWriter(Stream stream)
    {
        this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public void WriteRootCompoundStart(string name = "")
    {
        stream.WriteByte(10);
        WriteStringPayload(name);
    }

    public void WriteCompoundStart(string name)
    {
        WriteTagHeader(10, name);
    }

    public void WriteCompoundEnd() => stream.WriteByte(0);

    public void WriteByte(string name, byte value)
    {
        WriteTagHeader(1, name);
        stream.WriteByte(value);
    }

    public void WriteShort(string name, short value)
    {
        WriteTagHeader(2, name);
        Span<byte> bytes = stackalloc byte[sizeof(short)];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteInt(string name, int value)
    {
        WriteTagHeader(3, name);
        WriteIntPayload(value);
    }

    public void WriteLong(string name, long value)
    {
        WriteTagHeader(4, name);
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteString(string name, string value)
    {
        WriteTagHeader(8, name);
        WriteStringPayload(value);
    }

    public void WriteByteArray(string name, ReadOnlySpan<byte> values)
    {
        WriteTagHeader(7, name);
        WriteIntPayload(values.Length);
        stream.Write(values);
    }

    public void WriteIntArray(string name, ReadOnlySpan<int> values)
    {
        WriteTagHeader(11, name);
        WriteIntPayload(values.Length);
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        foreach(int value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }
    }

    public void WriteListStart(string name, byte elementType, int count)
    {
        if(count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if(count > 0 && elementType == 0)
            throw new ArgumentException("A non-empty NBT list cannot use TAG_End elements.", nameof(elementType));
        WriteTagHeader(9, name);
        stream.WriteByte(elementType);
        WriteIntPayload(count);
    }

    public void WriteRawCompoundListElement(ReadOnlySpan<byte> compoundPayload)
    {
        if(compoundPayload.IsEmpty || compoundPayload[^1] != 0)
            throw new InvalidDataException("An encoded compound-list element must end with TAG_End.");
        stream.Write(compoundPayload);
    }

    public void WriteIntPayload(int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    public void WriteStringPayload(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] encoded = EncodeModifiedUtf8(value);
        if(encoded.Length > ushort.MaxValue)
            throw new InvalidDataException("NBT string exceeds the 65,535-byte Modified UTF-8 limit.");
        Span<byte> length = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)encoded.Length);
        stream.Write(length);
        stream.Write(encoded);
    }

    private void WriteTagHeader(byte type, string name)
    {
        stream.WriteByte(type);
        WriteStringPayload(name);
    }

    private static byte[] EncodeModifiedUtf8(string value)
    {
        using MemoryStream encoded = new(value.Length * 3);
        foreach(char character in value)
        {
            if(character is >= '\u0001' and <= '\u007f')
            {
                encoded.WriteByte((byte)character);
            }
            else if(character <= '\u07ff')
            {
                encoded.WriteByte((byte)(0xc0 | character >> 6 & 0x1f));
                encoded.WriteByte((byte)(0x80 | character & 0x3f));
            }
            else
            {
                encoded.WriteByte((byte)(0xe0 | character >> 12 & 0x0f));
                encoded.WriteByte((byte)(0x80 | character >> 6 & 0x3f));
                encoded.WriteByte((byte)(0x80 | character & 0x3f));
            }
        }
        return encoded.ToArray();
    }
}
