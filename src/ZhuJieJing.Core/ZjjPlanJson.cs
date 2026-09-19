using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZhuJieJing.Core;

public static class ZjjPlanJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static readonly JsonSerializerOptions StrictOptions = CreateStrictOptions();

    public static string Serialize(ZjjPlan plan, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var options = new JsonSerializerOptions(Options) { WriteIndented = indented };
        return JsonSerializer.Serialize(plan, options);
    }

    public static async ValueTask SerializeAsync(
        Stream destination,
        ZjjPlan plan,
        bool indented = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(plan);
        if(!destination.CanWrite) throw new ArgumentException("目标流不可写。", nameof(destination));
        var options = new JsonSerializerOptions(Options) { WriteIndented = indented };
        await JsonSerializer.SerializeAsync(destination, plan, options, cancellationToken).ConfigureAwait(false);
    }

    public static ZjjPlan Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ArgumentException("蓝图 JSON 不能为空。", nameof(json));
        return JsonSerializer.Deserialize<ZjjPlan>(json, Options) ?? throw new JsonException("无法读取筑界蓝图文档。");
    }

    public static ZjjPlan DeserializeStrict(ReadOnlySpan<byte> utf8Json)
    {
        if(utf8Json.IsEmpty) throw new ArgumentException("蓝图 JSON 不能为空。", nameof(utf8Json));
        if(utf8Json.Length >= 3 && utf8Json[0] == 0xef && utf8Json[1] == 0xbb && utf8Json[2] == 0xbf)
            throw new JsonException("蓝图必须使用无 BOM 的 UTF-8。");
        return JsonSerializer.Deserialize<ZjjPlan>(utf8Json, StrictOptions) ?? throw new JsonException("无法读取筑界蓝图文档。");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonSerializerOptions CreateStrictOptions()
    {
        JsonSerializerOptions options = CreateOptions();
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.AllowTrailingCommas = false;
        options.Converters.Clear();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
