using System.IO.Compression;

namespace ZhuJieJing.Assets;

public enum MinecraftResourceLayerRole
{
    BaseClient,
    ResourcePack,
    Mod,
}

public enum MinecraftResourceContainerKind
{
    Archive,
    Directory,
}

public sealed record MinecraftResourceLayerEntry(MinecraftResourceKey Key, long Length);

/// <summary>Common read-only interface for directory, ZIP resource-pack, client JAR, and Mod JAR layers.</summary>
public interface IMinecraftResourceLayer : IDisposable
{
    string Name { get; }

    string SourcePath { get; }

    MinecraftResourceLayerRole Role { get; }

    MinecraftResourceContainerKind ContainerKind { get; }

    IReadOnlyList<MinecraftResourceLayerEntry> Entries { get; }

    string Revision { get; }

    bool Contains(MinecraftResourceKey key);

    Stream OpenRead(MinecraftResourceKey key);
}

public static class MinecraftResourceLayer
{
    public static IMinecraftResourceLayer OpenArchive(
        string path,
        MinecraftResourceLayerRole role,
        string? name = null)
    {
        ValidateRole(role);
        return new ArchiveResourceLayer(path, role, name);
    }

    public static IMinecraftResourceLayer OpenDirectory(
        string path,
        MinecraftResourceLayerRole role = MinecraftResourceLayerRole.ResourcePack,
        string? name = null)
    {
        ValidateRole(role);
        if(role == MinecraftResourceLayerRole.BaseClient) throw new ArgumentException("base client 必须使用 JAR 归档层。", nameof(role));
        return new DirectoryResourceLayer(path, role, name);
    }

    private sealed class ArchiveResourceLayer : IMinecraftResourceLayer
    {
        private readonly FileStream _file;
        private readonly ZipArchive _archive;
        private readonly Dictionary<MinecraftResourceKey, ZipArchiveEntry> _resources;
        private readonly Lazy<string> _revision;
        private bool _disposed;

        public ArchiveResourceLayer(string path, MinecraftResourceLayerRole role, string? name)
        {
            SourcePath = ValidateArchivePath(path);
            Name = ResolveName(name, SourcePath);
            Role = role;
            ContainerKind = MinecraftResourceContainerKind.Archive;
            _file = new FileStream(SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            try
            {
                _archive = new ZipArchive(_file, ZipArchiveMode.Read, leaveOpen: false);
                _resources = IndexArchive(_archive, SourcePath);
                Entries = _resources
                    .Select(pair => new MinecraftResourceLayerEntry(pair.Key, pair.Value.Length))
                    .OrderBy(entry => entry.Key)
                    .ToArray();
                _revision = new Lazy<string>(ComputeRevision, LazyThreadSafetyMode.ExecutionAndPublication);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        public string Name { get; }

        public string SourcePath { get; }

        public MinecraftResourceLayerRole Role { get; }

        public MinecraftResourceContainerKind ContainerKind { get; }

        public IReadOnlyList<MinecraftResourceLayerEntry> Entries { get; }

        public string Revision
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _revision.Value;
            }
        }

        public bool Contains(MinecraftResourceKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(key);
            return _resources.ContainsKey(key);
        }

        public Stream OpenRead(MinecraftResourceKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(key);
            return OpenReadCore(key);
        }

        public void Dispose()
        {
            if(_disposed) return;
            _disposed = true;
            _archive.Dispose();
        }

        private Stream OpenReadCore(MinecraftResourceKey key)
        {
            if(!_resources.TryGetValue(key, out var entry)) throw new FileNotFoundException($"资源层 {Name} 中不存在 {key}。", key.Value);
            return entry.Open();
        }

        private string ComputeRevision()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ResourceRevision.ComputeLayer(Entries, OpenReadCore);
        }
    }

    private sealed class DirectoryResourceLayer : IMinecraftResourceLayer
    {
        private readonly Dictionary<MinecraftResourceKey, DirectoryResource> _resources;
        private readonly Lazy<string> _revision;
        private bool _disposed;

        public DirectoryResourceLayer(string path, MinecraftResourceLayerRole role, string? name)
        {
            SourcePath = ValidateDirectoryPath(path);
            Name = ResolveName(name, SourcePath);
            Role = role;
            ContainerKind = MinecraftResourceContainerKind.Directory;
            _resources = IndexDirectory(SourcePath);
            Entries = _resources
                .Select(pair => new MinecraftResourceLayerEntry(pair.Key, pair.Value.Length))
                .OrderBy(entry => entry.Key)
                .ToArray();
            _revision = new Lazy<string>(ComputeRevision, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public string Name { get; }

        public string SourcePath { get; }

        public MinecraftResourceLayerRole Role { get; }

        public MinecraftResourceContainerKind ContainerKind { get; }

        public IReadOnlyList<MinecraftResourceLayerEntry> Entries { get; }

        public string Revision
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _revision.Value;
            }
        }

        public bool Contains(MinecraftResourceKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(key);
            return _resources.ContainsKey(key);
        }

        public Stream OpenRead(MinecraftResourceKey key)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(key);
            return OpenReadCore(key);
        }

        public void Dispose() => _disposed = true;

        private Stream OpenReadCore(MinecraftResourceKey key)
        {
            if(!_resources.TryGetValue(key, out var resource)) throw new FileNotFoundException($"资源层 {Name} 中不存在 {key}。", key.Value);
            return new FileStream(resource.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        private string ComputeRevision()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ResourceRevision.ComputeLayer(Entries, OpenReadCore);
        }
    }

    private static Dictionary<MinecraftResourceKey, ZipArchiveEntry> IndexArchive(ZipArchive archive, string sourcePath)
    {
        var resources = new Dictionary<MinecraftResourceKey, ZipArchiveEntry>();
        foreach(var entry in archive.Entries)
        {
            ValidateContainerEntryPath(entry.FullName, sourcePath);
            if(entry.Name.Length == 0) continue;
            if(!IsAssetsPath(entry.FullName)) continue;
            if(IsAssetsRootMarker(entry.FullName)) continue;
            if(!MinecraftResourceKey.TryParse(entry.FullName, out var key, out var error))
            {
                throw new InvalidDataException($"归档 {sourcePath} 包含非法资源路径：{error}");
            }
            if(!resources.TryAdd(key!, entry))
            {
                throw new InvalidDataException($"归档 {sourcePath} 对资源 {key} 包含重复且歧义的条目。");
            }
        }

        return resources;
    }

    private static Dictionary<MinecraftResourceKey, DirectoryResource> IndexDirectory(string rootPath)
    {
        var resources = new Dictionary<MinecraftResourceKey, DirectoryResource>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach(var filePath in Directory.EnumerateFiles(rootPath, "*", options))
        {
            var fullPath = Path.GetFullPath(filePath);
            EnsureDescendant(rootPath, fullPath);
            var relativePath = Path.GetRelativePath(rootPath, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if(!IsAssetsPath(relativePath)) continue;
            if(IsAssetsRootMarker(relativePath)) continue;
            if(!MinecraftResourceKey.TryParse(relativePath, out var key, out var error))
            {
                throw new InvalidDataException($"目录 {rootPath} 包含非法资源路径：{error}");
            }
            var file = new FileInfo(fullPath);
            if(!resources.TryAdd(key!, new DirectoryResource(fullPath, file.Length)))
            {
                throw new InvalidDataException($"目录 {rootPath} 对资源 {key} 包含重复且歧义的文件。");
            }
        }

        return resources;
    }

    private static bool IsAssetsPath(string path) =>
        path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase);

    private static bool IsAssetsRootMarker(string path) =>
        string.Equals(path, "assets/.mcassetsroot", StringComparison.Ordinal);

    private static void ValidateContainerEntryPath(string path, string sourcePath)
    {
        if(string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains('\0') || path[0] == '/' || Path.IsPathRooted(path))
        {
            throw new InvalidDataException($"归档 {sourcePath} 包含绝对路径或非规范路径：{path}");
        }
        if(path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            throw new InvalidDataException($"归档 {sourcePath} 包含驱动器路径：{path}");
        }

        var value = path[^1] == '/' ? path[..^1] : path;
        if(value.Length == 0 || value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"归档 {sourcePath} 包含路径穿越或空路径段：{path}");
        }
    }

    private static string ValidateArchivePath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("归档路径不能为空。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if(!File.Exists(fullPath)) throw new FileNotFoundException("资源归档不存在。", fullPath);
        return fullPath;
    }

    private static string ValidateDirectoryPath(string path)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException("资源目录路径不能为空。", nameof(path));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if(!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"资源目录不存在：{fullPath}");
        if((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"资源目录根不能是符号链接或重解析点：{fullPath}");
        }
        return fullPath;
    }

    private static void EnsureDescendant(string rootPath, string candidate)
    {
        var root = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if(!candidate.StartsWith(root, comparison)) throw new InvalidDataException($"资源文件逃逸目录根：{candidate}");
    }

    private static string ResolveName(string? name, string sourcePath)
    {
        if(name is not null && string.IsNullOrWhiteSpace(name)) throw new ArgumentException("资源层名称不能为空。", nameof(name));
        return name ?? Path.GetFileName(sourcePath);
    }

    private static void ValidateRole(MinecraftResourceLayerRole role)
    {
        if(!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
    }

    private sealed record DirectoryResource(string FullPath, long Length);
}
