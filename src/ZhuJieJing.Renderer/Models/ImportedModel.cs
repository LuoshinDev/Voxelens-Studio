using System.Numerics;

namespace ZhuJieJing.Renderer.Models;

public enum ModelImportDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ModelImportDiagnostic(
    ModelImportDiagnosticSeverity Severity,
    string Code,
    string Message,
    int? NodeIndex = null,
    int? MeshIndex = null,
    int? PrimitiveIndex = null);

public readonly record struct ImportedModelVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TextureCoordinate);

public enum ImportedModelAlphaMode
{
    Opaque,
    Mask,
    Blend,
}

public sealed record ImportedModelMaterial(
    Vector4 BaseColorFactor,
    int? BaseColorTextureIndex,
    ImportedModelAlphaMode AlphaMode,
    float AlphaCutoff,
    bool DoubleSided)
{
    public static ImportedModelMaterial Default { get; } = new(
        Vector4.One,
        null,
        ImportedModelAlphaMode.Opaque,
        0.5f,
        false);
}

public sealed record ImportedModelPrimitive(
    ImportedModelVertex[] Vertices,
    uint[] Indices,
    ImportedModelMaterial Material,
    int NodeIndex,
    int MeshIndex,
    int PrimitiveIndex)
{
    public int TriangleCount => Indices.Length / 3;
}

public sealed class ImportedModelTexture
{
    public ImportedModelTexture(int width, int height, byte[] bgraPixels, bool hasTransparentPixels, bool hasTranslucentPixels)
    {
        if(width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if(height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        ArgumentNullException.ThrowIfNull(bgraPixels);
        if(bgraPixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("BGRA 像素长度与纹理尺寸不匹配。", nameof(bgraPixels));
        }

        Width = width;
        Height = height;
        BgraPixels = bgraPixels;
        HasTransparentPixels = hasTransparentPixels;
        HasTranslucentPixels = hasTranslucentPixels;
    }

    public int Width { get; }

    public int Height { get; }

    public int RowPitch => checked(Width * 4);

    public byte[] BgraPixels { get; }

    public bool HasTransparentPixels { get; }

    public bool HasTranslucentPixels { get; }
}

public readonly record struct ImportedModelBounds(Vector3 Minimum, Vector3 Maximum, bool IsEmpty)
{
    public static ImportedModelBounds Empty { get; } = new(Vector3.Zero, Vector3.Zero, true);

    public Vector3 Center => IsEmpty ? Vector3.Zero : (Minimum + Maximum) * 0.5f;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Maximum - Minimum;

    public float Radius => IsEmpty ? 0f : Size.Length() * 0.5f;
}

/// <summary>A renderer-ready, node-transform-flattened glTF scene with decoded embedded textures.</summary>
public sealed class ImportedModel
{
    public ImportedModel(
        IReadOnlyList<ImportedModelPrimitive> primitives,
        IReadOnlyList<ImportedModelTexture> textures,
        IReadOnlyList<ModelImportDiagnostic> diagnostics,
        ImportedModelBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(primitives);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Primitives = primitives;
        Textures = textures;
        Diagnostics = diagnostics;
        Bounds = bounds;
    }

    public IReadOnlyList<ImportedModelPrimitive> Primitives { get; }

    public IReadOnlyList<ImportedModelTexture> Textures { get; }

    public IReadOnlyList<ModelImportDiagnostic> Diagnostics { get; }

    public ImportedModelBounds Bounds { get; }

    public int TriangleCount => Primitives.Sum(primitive => primitive.TriangleCount);

    public bool HasRenderableGeometry => Primitives.Count > 0 && TriangleCount > 0;
}
