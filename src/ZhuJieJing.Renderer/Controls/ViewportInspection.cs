using System.Numerics;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Camera;
using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Controls;

public readonly record struct ViewportCameraPose(OrbitCameraPose Camera);

public sealed record ViewportBlockPick(BlockPosition Position, BlockState State, Vector3 HitPoint, Vector3 FaceNormal, byte SkyLight, byte BlockLight, string Dimension)
{
    public LegacyBlockEncoding? SourceLegacyEncoding { get; init; }
    public TextureAtlasRegion TextureRegion { get; init; }
    public Vector4 TextureTint { get; init; } = Vector4.One;
}

public partial class VoxelViewport
{
    private bool _disposed;

    public void Dispose()
    {
        Dispatcher.VerifyAccess();
        if(_disposed) return;
        _disposed = true;
        Surface.AlwaysRefresh = false;
        ClearSections();
        ClearModel();
        OnDispatcherShutdown(this, EventArgs.Empty);
        Surface.Shutdown();
        Content = null;
    }

    public async Task<BitmapSource> CaptureSceneAsync(CancellationToken cancellationToken = default)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if(!Surface.IsLoaded) throw new InvalidOperationException("预览窗口尚未加载，无法截图。");
        using IDisposable frames = Surface.RequestCaptureFrames();
        await _shaderBytecodePreparation.WaitAsync(cancellationToken);
        await WaitForSectionGpuPublicationAsync(cancellationToken);
        return await Surface.CaptureSceneAsync(cancellationToken);
    }

    public ViewportCameraPose CaptureCameraPose() => new(_camera.CapturePose());

    internal IDisposable RequestProfilingFrames()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Surface.RequestCaptureFrames();
    }

    internal void ApplyProfilingCameraPose(ViewportCameraPose pose)
    {
        Dispatcher.VerifyAccess();
        Vector3 previousPosition = _camera.NavigationPosition;
        _camera.ApplyPose(pose.Camera);
        // Deterministic camera samples use the navigation refresh policy, not the public saved-pose
        // restoration path which deliberately rebuilds scene selection and shadows immediately.
        if(previousPosition != _camera.NavigationPosition) InvalidateActiveSectionsForCameraMovement();
        Surface.InvalidateVisual();
    }

    public void ApplyCameraPose(ViewportCameraPose pose)
    {
        Dispatcher.VerifyAccess();
        ReleaseObserverPointerLock(true);
        ResetCameraGesture();
        _camera.ApplyPose(pose.Camera);
        InvalidateActiveSections();
        NotifyCameraTargetChanged(true, false);
        NavigationModeChanged?.Invoke(NavigationMode);
        ZoomTargetDistanceChanged?.Invoke(_camera.ZoomTargetDistance);
        Surface.InvalidateVisual();
    }

    public bool TryPickBlock(Point viewportPoint, out ViewportBlockPick result)
    {
        Dispatcher.VerifyAccess();
        result = null!;
        if(_content != ViewportContent.Sections || _activeDimension is null || ActualWidth <= 0 || ActualHeight <= 0 ||
           viewportPoint.X < 0 || viewportPoint.Y < 0 || viewportPoint.X >= ActualWidth || viewportPoint.Y >= ActualHeight) return false;
        ViewportSkyCameraBasis basis = ViewportSkyMath.CreateCameraBasis(_camera.ViewDirection, (float)ActualWidth, (float)ActualHeight, _camera.VerticalFieldOfView);
        Vector3 ray = ViewportSkyMath.CreateWorldRay(new Vector2((float)(viewportPoint.X / ActualWidth), (float)(viewportPoint.Y / ActualHeight)), basis);
        float closest = Math.Max(_sectionDrawDistance, 512f);
        foreach(SectionRenderGeometry section in _activeSections)
        {
            if(!ViewportRayIntersection.IntersectsSection(_camera.EyePosition, ray, section.Coordinate, closest)) continue;
            for(int index = 0; index + 2 < section.Indices.Length; index += 3)
            {
                VoxelVertex a = section.Vertices[section.Indices[index]], b = section.Vertices[section.Indices[index + 1]], c = section.Vertices[section.Indices[index + 2]];
                if(!ViewportRayIntersection.IntersectTriangle(_camera.EyePosition, ray, a.Position, b.Position, c.Position, out float distance) || distance >= closest) continue;
                Vector3 hit = _camera.EyePosition + ray * distance;
                Vector3 normal = a.Normal;
                Vector3 inside = hit - normal * 0.002f;
                BlockPosition position = new((int)MathF.Floor(inside.X), (int)MathF.Floor(inside.Y), (int)MathF.Floor(inside.Z));
                if(!_sectionCache.TryGetBlock(_activeDimension, position, out BlockState state) || state.IsAir || state.Name == "minecraft:barrier") continue;
                _sectionCache.TryGetLight(_activeDimension, position, out VoxelLightSample light);
                closest = distance;
                result = new ViewportBlockPick(position, state, hit, normal, light.SkyLight, light.BlockLight, _activeDimension)
                {
                    SourceLegacyEncoding = _sectionCache.GetSourceLegacyEncoding(_activeDimension, position),
                    TextureRegion = new(a.TextureRegion.X, a.TextureRegion.Y, a.TextureRegion.Z, a.TextureRegion.W),
                    TextureTint = a.Color,
                };
            }
        }
        return result is not null;
    }

    public BitmapSource? CaptureBlockTexture(ViewportBlockPick pick)
    {
        Dispatcher.VerifyAccess();
        if(_minecraftResources is null) return null;
        var tile = _minecraftResources.Atlas.SnapshotTile(pick.TextureRegion);
        byte[] pixels = tile.BgraPixels.ToArray();
        for(int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = (byte)Math.Clamp(pixels[offset] * pick.TextureTint.Z, 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(pixels[offset + 1] * pick.TextureTint.Y, 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(pixels[offset + 2] * pick.TextureTint.X, 0, 255);
            pixels[offset + 3] = (byte)Math.Clamp(pixels[offset + 3] * pick.TextureTint.W, 0, 255);
        }
        var bitmap = BitmapSource.Create(tile.Width, tile.Height, 96, 96, PixelFormats.Bgra32, null, pixels, tile.RowPitch);
        bitmap.Freeze();
        return bitmap;
    }
}

internal static class ViewportRayIntersection
{
    internal static bool IntersectTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0;
        Vector3 edge1 = b - a, edge2 = c - a;
        Vector3 cross = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, cross);
        if(MathF.Abs(determinant) < 0.000001f) return false;
        float inverse = 1f / determinant;
        Vector3 relative = origin - a;
        float u = Vector3.Dot(relative, cross) * inverse;
        if(u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(relative, edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if(v < 0 || u + v > 1) return false;
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0;
    }

    internal static bool IntersectsSection(Vector3 origin, Vector3 direction, SectionCoordinate coordinate, float maximumDistance)
    {
        Vector3 minimum = new(coordinate.X * 16f - 1, coordinate.Y * 16f - 1, coordinate.Z * 16f - 1);
        Vector3 maximum = minimum + new Vector3(18);
        float near = 0, far = maximumDistance;
        for(int axis = 0; axis < 3; axis++)
        {
            float o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            float d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            float lo = axis == 0 ? minimum.X : axis == 1 ? minimum.Y : minimum.Z;
            float hi = axis == 0 ? maximum.X : axis == 1 ? maximum.Y : maximum.Z;
            if(MathF.Abs(d) < 0.000001f) { if(o < lo || o > hi) return false; continue; }
            float first = (lo - o) / d, second = (hi - o) / d;
            near = Math.Max(near, Math.Min(first, second)); far = Math.Min(far, Math.Max(first, second));
            if(near > far) return false;
        }
        return true;
    }
}
