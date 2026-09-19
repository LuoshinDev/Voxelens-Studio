namespace ZhuJieJing.Assets;

public sealed record MinecraftResourceOverlay
{
    private MinecraftResourceOverlay(string path, MinecraftResourceLayerRole role, string? name)
    {
        Path = path;
        Role = role;
        Name = name;
    }

    public string Path { get; }

    public MinecraftResourceLayerRole Role { get; }

    public string? Name { get; }

    public static MinecraftResourceOverlay ResourcePack(string path, string? name = null) =>
        new(path, MinecraftResourceLayerRole.ResourcePack, name);

    public static MinecraftResourceOverlay ModJar(string path, string? name = null) =>
        new(path, MinecraftResourceLayerRole.Mod, name);
}

public sealed record MinecraftResourceOrigin(
    int LayerIndex,
    string LayerName,
    MinecraftResourceLayerRole Role,
    MinecraftResourceContainerKind ContainerKind,
    string SourcePath);

public sealed record MinecraftResourceEntry(
    MinecraftResourceKey Key,
    long Length,
    MinecraftResourceOrigin Origin);

/// <summary>
/// Ordered, read-only Minecraft Java resource view. Layer zero is always the base client JAR; later user
/// overlays have progressively higher priority.
/// </summary>
public sealed class MinecraftResourceStack : IDisposable
{
    private readonly IReadOnlyList<IMinecraftResourceLayer> _layers;
    private readonly Dictionary<MinecraftResourceKey, ResolvedResource> _resources;
    private readonly Lazy<string> _revision;
    private bool _disposed;

    private MinecraftResourceStack(IReadOnlyList<IMinecraftResourceLayer> layers)
    {
        _layers = layers;
        Layers = layers.Select(CreateOrigin).ToArray();
        _resources = BuildEffectiveResources(layers, Layers);
        _revision = new Lazy<string>(ComputeRevision, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Revision
    {
        get
        {
            ThrowIfDisposed();
            return _revision.Value;
        }
    }

    public IReadOnlyList<MinecraftResourceOrigin> Layers { get; }

    public int Count
    {
        get
        {
            ThrowIfDisposed();
            return _resources.Count;
        }
    }

    public static MinecraftResourceStack Open(
        string baseClientJarPath,
        IEnumerable<MinecraftResourceOverlay>? overlays = null,
        string? baseLayerName = null)
    {
        ValidateArchiveExtension(baseClientJarPath, ".jar", "base client");
        var opened = new List<IMinecraftResourceLayer>();
        try
        {
            opened.Add(MinecraftResourceLayer.OpenArchive(
                baseClientJarPath,
                MinecraftResourceLayerRole.BaseClient,
                baseLayerName));
            if(overlays is not null)
            {
                foreach(var overlay in overlays)
                {
                    ArgumentNullException.ThrowIfNull(overlay);
                    opened.Add(OpenOverlay(overlay));
                }
            }

            return new MinecraftResourceStack(opened.ToArray());
        }
        catch
        {
            for(var index = opened.Count - 1; index >= 0; index--) opened[index].Dispose();
            throw;
        }
    }

    public bool Exists(string key) => Exists(MinecraftResourceKey.Parse(key));

    public bool Exists(MinecraftResourceKey key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        return _resources.ContainsKey(key);
    }

    public Stream OpenRead(string key) => OpenRead(MinecraftResourceKey.Parse(key));

    public Stream OpenRead(MinecraftResourceKey key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        if(!_resources.TryGetValue(key, out var resource)) throw new FileNotFoundException($"资源栈中不存在 {key}。", key.Value);
        return resource.Layer.OpenRead(key);
    }

    public bool TryOpenRead(MinecraftResourceKey key, out Stream? stream)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        if(!_resources.TryGetValue(key, out var resource))
        {
            stream = null;
            return false;
        }

        stream = resource.Layer.OpenRead(key);
        return true;
    }

    public MinecraftResourceOrigin GetSource(string key) => GetSource(MinecraftResourceKey.Parse(key));

    public MinecraftResourceOrigin GetSource(MinecraftResourceKey key)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        if(!_resources.TryGetValue(key, out var resource)) throw new FileNotFoundException($"资源栈中不存在 {key}。", key.Value);
        return resource.Entry.Origin;
    }

    public bool TryGetSource(MinecraftResourceKey key, out MinecraftResourceOrigin? origin)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(key);
        if(!_resources.TryGetValue(key, out var resource))
        {
            origin = null;
            return false;
        }

        origin = resource.Entry.Origin;
        return true;
    }

    public IReadOnlyList<MinecraftResourceEntry> Enumerate()
    {
        ThrowIfDisposed();
        return _resources.Values.Select(resource => resource.Entry).OrderBy(entry => entry.Key).ToArray();
    }

    public IReadOnlyList<MinecraftResourceEntry> EnumerateNamespace(string resourceNamespace)
    {
        ThrowIfDisposed();
        MinecraftResourceKey.ValidateNamespace(resourceNamespace);
        return _resources.Values
            .Select(resource => resource.Entry)
            .Where(entry => string.Equals(entry.Key.Namespace, resourceNamespace, StringComparison.Ordinal))
            .OrderBy(entry => entry.Key)
            .ToArray();
    }

    public void Dispose()
    {
        if(_disposed) return;
        _disposed = true;
        for(var index = _layers.Count - 1; index >= 0; index--) _layers[index].Dispose();
    }

    private static IMinecraftResourceLayer OpenOverlay(MinecraftResourceOverlay overlay)
    {
        if(overlay.Role == MinecraftResourceLayerRole.Mod)
        {
            ValidateArchiveExtension(overlay.Path, ".jar", "Mod");
            return MinecraftResourceLayer.OpenArchive(overlay.Path, overlay.Role, overlay.Name);
        }
        if(overlay.Role != MinecraftResourceLayerRole.ResourcePack)
        {
            throw new ArgumentException($"不支持的叠加层角色：{overlay.Role}", nameof(overlay));
        }
        if(Directory.Exists(overlay.Path))
        {
            return MinecraftResourceLayer.OpenDirectory(overlay.Path, overlay.Role, overlay.Name);
        }

        ValidateArchiveExtension(overlay.Path, ".zip", "资源包");
        return MinecraftResourceLayer.OpenArchive(overlay.Path, overlay.Role, overlay.Name);
    }

    private static Dictionary<MinecraftResourceKey, ResolvedResource> BuildEffectiveResources(
        IReadOnlyList<IMinecraftResourceLayer> layers,
        IReadOnlyList<MinecraftResourceOrigin> origins)
    {
        var resources = new Dictionary<MinecraftResourceKey, ResolvedResource>();
        for(var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var origin = origins[index];
            foreach(var layerEntry in layer.Entries)
            {
                var entry = new MinecraftResourceEntry(layerEntry.Key, layerEntry.Length, origin);
                resources[layerEntry.Key] = new ResolvedResource(layer, entry);
            }
        }

        return resources;
    }

    private static MinecraftResourceOrigin CreateOrigin(IMinecraftResourceLayer layer, int index) => new(
        index,
        layer.Name,
        layer.Role,
        layer.ContainerKind,
        layer.SourcePath);

    private static void ValidateArchiveExtension(string path, string extension, string description)
    {
        if(string.IsNullOrWhiteSpace(path)) throw new ArgumentException($"{description} 路径不能为空。", nameof(path));
        if(!string.Equals(System.IO.Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{description} 必须是 {extension} 文件：{path}", nameof(path));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private string ComputeRevision()
    {
        ThrowIfDisposed();
        return ResourceRevision.ComputeStack(_layers);
    }

    private sealed record ResolvedResource(IMinecraftResourceLayer Layer, MinecraftResourceEntry Entry);
}
