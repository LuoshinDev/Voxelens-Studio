using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZhuJieJing.Core.Collaboration;

public static class ZhujieProjectJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize(ZhujieProjectManifest value) => SerializeValidated(value, ZhujieProjectValidator.EnsureValid);

    public static byte[] Serialize(ZhujieBatchRecord value) => SerializeValidated(value, ZhujieProjectValidator.EnsureValid);

    public static byte[] Serialize(ZhujieInstruction value) => SerializeValidated(value, ZhujieProjectValidator.EnsureValid);

    public static byte[] Serialize(ZhujieArchitectStatus value) => SerializeValidated(value, ZhujieProjectValidator.EnsureValid);

    public static byte[] Serialize(ZhujieInstructionAcknowledgement value) => SerializeValidated(value, ZhujieProjectValidator.EnsureValid);

    public static byte[] Serialize(ZhujiePreviewReceipt value) => SerializeValidated(value, ZhujieProjectValidator.EnsureValid);

    public static ZhujieProjectManifest DeserializeManifest(ReadOnlySpan<byte> json) =>
        DeserializeValidated<ZhujieProjectManifest>(json, ZhujieProjectValidator.EnsureValid);

    public static ZhujieBatchRecord DeserializeBatch(ReadOnlySpan<byte> json) =>
        DeserializeValidated<ZhujieBatchRecord>(json, ZhujieProjectValidator.EnsureValid);

    public static ZhujieInstruction DeserializeInstruction(ReadOnlySpan<byte> json) =>
        DeserializeValidated<ZhujieInstruction>(json, ZhujieProjectValidator.EnsureValid);

    public static ZhujieArchitectStatus DeserializeArchitectStatus(ReadOnlySpan<byte> json) =>
        DeserializeValidated<ZhujieArchitectStatus>(json, ZhujieProjectValidator.EnsureValid);

    public static ZhujieInstructionAcknowledgement DeserializeInstructionAcknowledgement(ReadOnlySpan<byte> json) =>
        DeserializeValidated<ZhujieInstructionAcknowledgement>(json, ZhujieProjectValidator.EnsureValid);

    public static ZhujiePreviewReceipt DeserializePreviewReceipt(ReadOnlySpan<byte> json) =>
        DeserializeValidated<ZhujiePreviewReceipt>(json, ZhujieProjectValidator.EnsureValid);

    private static byte[] SerializeValidated<T>(T value, Action<T> validate)
    {
        ArgumentNullException.ThrowIfNull(value);
        validate(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, Options);
    }

    private static T DeserializeValidated<T>(ReadOnlySpan<byte> json, Action<T> validate)
    {
        if(json.IsEmpty) throw new JsonException("协议 JSON 不能为空。");
        if(json.Length >= 3 && json[0] == 0xef && json[1] == 0xbb && json[2] == 0xbf)
            throw new JsonException("协议 JSON 必须使用无 BOM 的 UTF-8。 ");
        T value = JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("协议 JSON 不能为空对象。");
        validate(value);
        return value;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
