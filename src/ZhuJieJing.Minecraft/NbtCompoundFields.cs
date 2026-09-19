using System.Buffers.Binary;
using System.Text;

namespace ZhuJieJing.Minecraft;

/// <summary>Bounded, lossless fields for semantic codecs. Unchanged payloads remain slices of owned source bytes.</summary>
internal sealed record NbtField(byte Type, ReadOnlyMemory<byte> Payload)
{
    internal string StringValue()
    {
        if(Type != 8) throw new InvalidDataException("NBT 字段应为字符串。");
        int position = 0;
        string value = NbtCompoundFields.ReadString(Payload.Span, ref position);
        if(position != Payload.Length) throw new InvalidDataException("NBT 字符串后有多余数据。");
        return value;
    }

    internal int IntegerValue() => Type switch
    {
        1 when Payload.Length == 1 => unchecked((sbyte)Payload.Span[0]),
        2 when Payload.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(Payload.Span),
        3 when Payload.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(Payload.Span),
        _ => throw new InvalidDataException("NBT 字段应为 byte/short/int 整数。"),
    };

    internal Dictionary<string, NbtField> Compound() => Type == 10
        ? NbtCompoundFields.Read(Payload) : throw new InvalidDataException("NBT 字段应为 compound。");
}

internal static class NbtCompoundFields
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumElements = 1_048_576;

    internal static Dictionary<string, NbtField> Read(ReadOnlyMemory<byte> payload)
    {
        if(payload.Length is 0 or > MaximumBytes) throw new InvalidDataException("方块实体 compound 超出安全大小限制。");
        Dictionary<string, NbtField> fields = new(StringComparer.Ordinal);
        int position = 0;
        while(true)
        {
            byte type = ReadByte(payload.Span, ref position);
            if(type == 0) break;
            string name = ReadString(payload.Span, ref position);
            int start = position;
            Skip(payload.Span, ref position, type, 0);
            if(!fields.TryAdd(name, new NbtField(type, payload[start..position])))
                throw new InvalidDataException($"NBT compound 含重复字段：{name}。");
            if(fields.Count > MaximumElements) throw new InvalidDataException("NBT compound 字段过多。");
        }
        if(position != payload.Length) throw new InvalidDataException("NBT compound 后有多余数据。");
        return fields;
    }

    internal static IReadOnlyList<NbtField> List(NbtField field)
    {
        if(field.Type != 9) throw new InvalidDataException("NBT 字段应为 list。");
        int position = 0;
        byte type = ReadByte(field.Payload.Span, ref position);
        int count = Count(field.Payload.Span, ref position);
        if(type == 0 && count != 0) throw new InvalidDataException("非空 NBT list 不能使用 TAG_End。");
        List<NbtField> values = new(count);
        for(int i = 0; i < count; i++)
        {
            int start = position;
            Skip(field.Payload.Span, ref position, type, 0);
            values.Add(new NbtField(type, field.Payload[start..position]));
        }
        if(position != field.Payload.Length) throw new InvalidDataException("NBT list 后有多余数据。");
        return values;
    }

    internal static byte[] Write(IReadOnlyDictionary<string, NbtField> fields)
    {
        using MemoryStream output = new();
        LegacyNbtWriter writer = new(output);
        foreach((string name, NbtField field) in fields)
        {
            output.WriteByte(field.Type);
            writer.WriteStringPayload(name);
            output.Write(field.Payload.Span);
        }
        output.WriteByte(0);
        if(output.Length > MaximumBytes) throw new InvalidDataException("转码后的 NBT compound 超出安全大小限制。");
        return output.ToArray();
    }

    internal static NbtField String(string value)
    {
        using MemoryStream stream = new();
        new LegacyNbtWriter(stream).WriteStringPayload(value);
        return new NbtField(8, stream.ToArray());
    }

    internal static NbtField Integer(int value, byte type = 3)
    {
        byte[] payload = new byte[type == 1 ? 1 : type == 2 ? 2 : 4];
        if(type == 1) payload[0] = unchecked((byte)value);
        else if(type == 2) BinaryPrimitives.WriteInt16BigEndian(payload, checked((short)value));
        else if(type == 3) BinaryPrimitives.WriteInt32BigEndian(payload, value);
        else throw new ArgumentOutOfRangeException(nameof(type));
        return new NbtField(type, payload);
    }

    internal static NbtField Compound(IReadOnlyDictionary<string, NbtField> fields) => new(10, Write(fields));

    internal static NbtField List(byte type, IReadOnlyList<NbtField> fields)
    {
        using MemoryStream stream = new();
        stream.WriteByte(type);
        new LegacyNbtWriter(stream).WriteIntPayload(fields.Count);
        foreach(NbtField field in fields)
        {
            if(field.Type != type) throw new InvalidDataException("NBT list 元素类型不一致。");
            stream.Write(field.Payload.Span);
        }
        return new NbtField(9, stream.ToArray());
    }

    private static void Skip(ReadOnlySpan<byte> source, ref int position, byte type, int depth)
    {
        if(depth > 64) throw new InvalidDataException("NBT 嵌套层数超过 64。");
        switch(type)
        {
            case 1: Advance(source, ref position, 1); break;
            case 2: Advance(source, ref position, 2); break;
            case 3: case 5: Advance(source, ref position, 4); break;
            case 4: case 6: Advance(source, ref position, 8); break;
            case 7: case 11: case 12:
                int count = Count(source, ref position);
                Advance(source, ref position, checked(count * (type == 7 ? 1 : type == 11 ? 4 : 8)));
                break;
            case 8: _ = ReadString(source, ref position); break;
            case 9:
                byte itemType = ReadByte(source, ref position);
                int items = Count(source, ref position);
                if(itemType == 0 && items != 0) throw new InvalidDataException("非空 NBT list 不能使用 TAG_End。");
                for(int i = 0; i < items; i++) Skip(source, ref position, itemType, depth + 1);
                break;
            case 10:
                int tags = 0;
                while(true)
                {
                    byte child = ReadByte(source, ref position);
                    if(child == 0) break;
                    if(++tags > MaximumElements) throw new InvalidDataException("NBT compound 字段过多。");
                    _ = ReadString(source, ref position);
                    Skip(source, ref position, child, depth + 1);
                }
                break;
            default: throw new InvalidDataException($"未知 NBT 标签类型：{type}。");
        }
    }

    private static int Count(ReadOnlySpan<byte> source, ref int position)
    {
        int start = position;
        Advance(source, ref position, 4);
        int value = BinaryPrimitives.ReadInt32BigEndian(source[start..position]);
        if(value < 0 || value > MaximumElements) throw new InvalidDataException("NBT 集合长度超过安全限制。");
        return value;
    }

    private static byte ReadByte(ReadOnlySpan<byte> source, ref int position)
    {
        if((uint)position >= (uint)source.Length) throw new InvalidDataException("NBT 数据被截断。");
        return source[position++];
    }

    private static void Advance(ReadOnlySpan<byte> source, ref int position, int count)
    {
        if(count < 0 || position > source.Length - count) throw new InvalidDataException("NBT 数据被截断。");
        position += count;
    }

    internal static string ReadString(ReadOnlySpan<byte> source, ref int position)
    {
        int start = position;
        Advance(source, ref position, 2);
        int length = BinaryPrimitives.ReadUInt16BigEndian(source[start..position]);
        int end = checked(position + length);
        if(end > source.Length) throw new InvalidDataException("NBT 字符串被截断。");
        StringBuilder value = new(length);
        while(position < end)
        {
            int b = source[position++];
            if(b is >= 1 and <= 0x7f) value.Append((char)b);
            else if((b & 0xe0) == 0xc0)
            {
                if(position >= end || (source[position] & 0xc0) != 0x80) throw new InvalidDataException("无效 Modified UTF-8。");
                int character = (b & 31) << 6 | source[position++] & 63;
                if(character < 0x80 && character != 0) throw new InvalidDataException("Modified UTF-8 含过长编码。");
                value.Append((char)character);
            }
            else if((b & 0xf0) == 0xe0)
            {
                if(position + 1 >= end || (source[position] & 0xc0) != 0x80 || (source[position + 1] & 0xc0) != 0x80)
                    throw new InvalidDataException("无效 Modified UTF-8。");
                int character = (b & 15) << 12 | (source[position++] & 63) << 6 | source[position++] & 63;
                if(character < 0x800) throw new InvalidDataException("Modified UTF-8 含过长编码。");
                value.Append((char)character);
            }
            else throw new InvalidDataException("无效 Modified UTF-8。");
        }
        return value.ToString();
    }

}
