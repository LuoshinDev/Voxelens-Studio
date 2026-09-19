using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ZhuJieJing.Assets;

internal static class ResourceRevision
{
    public static string ComputeLayer(
        IEnumerable<MinecraftResourceLayerEntry> entries,
        Func<MinecraftResourceKey, Stream> openRead)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "zhujiejing.minecraft-resource-layer/1");
        var ordered = entries.OrderBy(entry => entry.Key).ToArray();
        Append(hash, ordered.Length);
        foreach(var entry in ordered)
        {
            Append(hash, entry.Key.Value);
            using var stream = openRead(entry.Key);
            Append(hash, SHA256.HashData(stream));
        }

        return Finish(hash);
    }

    public static string ComputeStack(IReadOnlyList<IMinecraftResourceLayer> layers)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "zhujiejing.minecraft-resource-stack/1");
        Append(hash, layers.Count);
        foreach(var layer in layers)
        {
            Append(hash, layer.Revision);
        }

        return Finish(hash);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Append(hash, value.Length);
        hash.AppendData(value);
    }

    private static string Finish(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}
