namespace ZhuJieJing.Assets;

/// <summary>A canonical Minecraft Java resource path in the form assets/&lt;namespace&gt;/&lt;path&gt;.</summary>
public sealed record MinecraftResourceKey : IComparable<MinecraftResourceKey>
{
    private const string AssetsPrefix = "assets/";

    private MinecraftResourceKey(string value, string resourceNamespace, string path)
    {
        Value = value;
        Namespace = resourceNamespace;
        Path = path;
    }

    public string Value { get; }

    public string Namespace { get; }

    public string Path { get; }

    public static MinecraftResourceKey Parse(string value)
    {
        if(!TryParse(value, out var key, out var error)) throw new FormatException(error);
        return key!;
    }

    public static bool TryParse(string? value, out MinecraftResourceKey? key) => TryParse(value, out key, out _);

    public int CompareTo(MinecraftResourceKey? other) =>
        other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => Value;

    internal static bool TryParse(string? value, out MinecraftResourceKey? key, out string error)
    {
        key = null;
        if(string.IsNullOrWhiteSpace(value))
        {
            error = "资源键不能为空。";
            return false;
        }
        if(value.Contains('\\'))
        {
            error = $"资源键必须使用正斜杠：{value}";
            return false;
        }
        if(value[0] == '/' || System.IO.Path.IsPathRooted(value))
        {
            error = $"资源键不能是绝对路径：{value}";
            return false;
        }
        if(!value.StartsWith(AssetsPrefix, StringComparison.Ordinal))
        {
            error = $"资源键必须以 assets/ 开头：{value}";
            return false;
        }

        var segments = value.Split('/');
        if(segments.Length < 3 || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            error = $"资源键包含空段或路径穿越：{value}";
            return false;
        }

        var resourceNamespace = segments[1];
        if(!resourceNamespace.All(IsNamespaceCharacter))
        {
            error = $"资源命名空间包含非法字符：{resourceNamespace}";
            return false;
        }

        var resourcePath = string.Join('/', segments, 2, segments.Length - 2);
        if(!resourcePath.All(IsPathCharacter))
        {
            error = $"资源路径包含非法字符：{resourcePath}";
            return false;
        }

        key = new MinecraftResourceKey(value, resourceNamespace, resourcePath);
        error = string.Empty;
        return true;
    }

    internal static void ValidateNamespace(string resourceNamespace)
    {
        if(string.IsNullOrWhiteSpace(resourceNamespace) ||
           resourceNamespace is "." or ".." ||
           !resourceNamespace.All(IsNamespaceCharacter))
        {
            throw new ArgumentException($"资源命名空间非法：{resourceNamespace}", nameof(resourceNamespace));
        }
    }

    private static bool IsNamespaceCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.';

    private static bool IsPathCharacter(char value) => IsNamespaceCharacter(value) || value == '/';
}
