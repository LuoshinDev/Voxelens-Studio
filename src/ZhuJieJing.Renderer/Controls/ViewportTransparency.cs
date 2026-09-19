using System.Numerics;
using System.Diagnostics;
using ZhuJieJing.Renderer.Meshing;
using Vortice.Direct3D11;
using DrawEventArgs = ZhuJieJing.Renderer.Controls.ViewportDrawEventArgs;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    internal bool RefractionOptimizationEnabledForValidation { get; set; } = true;
    private GpuGeometry? _transparentGeometry;
    private TransparentTriangle[] _transparentTriangles = [];
    private TransparentTriangle[] _transparentWaterTriangles = [];
    private RefractionSnapshotTracker? _refractionSnapshotTracker;
    private long _transparentGeneration = -1;
    private HashSet<SectionRenderGeometry> _transparentSources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Vector3, TransparentTriangleOrder> _transparentOrders = [];
    private readonly Queue<Vector3> _transparentOrderKeys = [];
    private TransparentTriangleSorter? _transparentSorter;
    private uint[]? _uploadedTransparentIndices;
    private ID3D11BlendState? _transparentColorBlend;
    private ID3D11BlendState? _transparentSurfaceBlend;
    private ID3D11BlendState? _skyBlendState;
    private Matrix4x4 _currentViewProjection;
    private Vector3 _currentEye, _currentForward;
    private uint[]? _refractionPlanIndices;
    private Matrix4x4 _refractionPlanProjection;
    private (int Width, int Height) _refractionPlanSize;
    private (int Start, RefractionCopyBounds Bounds)[] _refractionCopies = [];
    private bool _refractionPlanOptimized;
    private bool _refractionPlanUsesPlanar;
    private long _refractionPlanPlaneGeneration = -1;

    private void CreateTransparencyStates(ID3D11Device device)
    {
        // Late sky draws fill only background color; they must not touch the geometry metadata.
        BlendDescription sky = BlendDescription.Opaque;
        sky.IndependentBlendEnable = true;
        sky.RenderTarget[1].RenderTargetWriteMask = ColorWriteEnable.None;
        sky.RenderTarget[2].RenderTargetWriteMask = ColorWriteEnable.None;
        _skyBlendState = device.CreateBlendState(sky);
        BlendDescription color = BlendDescription.NonPremultiplied;
        color.IndependentBlendEnable = true;
        color.RenderTarget[1].RenderTargetWriteMask = ColorWriteEnable.None;
        color.RenderTarget[2].RenderTargetWriteMask = ColorWriteEnable.None;
        _transparentColorBlend = device.CreateBlendState(color);
        BlendDescription surface = BlendDescription.Opaque;
        surface.IndependentBlendEnable = true;
        surface.RenderTarget[0].RenderTargetWriteMask = ColorWriteEnable.None;
        _transparentSurfaceBlend = device.CreateBlendState(surface);
    }

    private static BlendDescription PreserveSceneOpacity(BlendDescription description)
    {
        // The viewport already composites foliage, glass and rain over an opaque sky. WPF interprets
        // the shared BGRA surface as premultiplied: exporting a cutout texel's fractional alpha here
        // makes the window background leak through as white fringes. Keep clear/sky alpha at one;
        // source alpha still controls RGB blending. Only standard single-target scene draws use
        // this state; HDR, metadata and final presentation retain their original alpha writes.
        description.RenderTarget[0].RenderTargetWriteMask &= ~ColorWriteEnable.Alpha;
        return description;
    }

    private void PrepareTransparentGeometry()
    {
        if(_transparentGeneration != _sectionGpuGeneration && !_transparentSources.SetEquals(_activeSections))
        {
            _transparentSources = new HashSet<SectionRenderGeometry>(_activeSections, ReferenceEqualityComparer.Instance);
            _transparentOrders.Clear();
            _transparentOrderKeys.Clear();
            _transparentGeometry?.Dispose();
            _transparentGeometry = null;
            _uploadedTransparentIndices = null;
            var vertices = new List<VoxelVertex>();
            var triangles = new List<TransparentTriangle>();
            foreach(SectionRenderGeometry section in _activeSections)
            {
                foreach(SectionDrawRange range in section.EffectiveDrawRanges)
                {
                    if(range.Layer != BlockRenderLayer.Translucent) continue;
                    for(int index = range.StartIndex; index < range.StartIndex + range.IndexCount; index += 3)
                    {
                        VoxelVertex a = section.Vertices[section.Indices[index]], b = section.Vertices[section.Indices[index + 1]], c = section.Vertices[section.Indices[index + 2]];
                        int first = vertices.Count;
                        vertices.Add(a); vertices.Add(b); vertices.Add(c);
                        triangles.Add(new TransparentTriangle(first, (a.Position + b.Position + c.Position) / 3f,
                            a.MaterialFlags > 0.5f && a.Normal.Y > 0.55f, a.Position, b.Position, c.Position,
                            a.Normal == Vector3.UnitY && b.Normal == Vector3.UnitY && c.Normal == Vector3.UnitY ? 0.0003f : 0.01f));
                    }
                }
            }
            _transparentTriangles = triangles.ToArray();
            _transparentWaterTriangles = _transparentTriangles.Where(triangle => triangle.WaterSurface).ToArray();
            _transparentSorter = new TransparentTriangleSorter(_transparentTriangles);
            if(vertices.Count != 0) _transparentGeometry = GpuGeometry.Create(_device!, vertices.ToArray(), Enumerable.Range(0, vertices.Count).Select(index => (uint)index).ToArray());
        }
        _transparentGeneration = _sectionGpuGeneration;
    }

    private void DrawSortedTransparency(ID3D11DeviceContext context, DrawEventArgs drawEvent)
    {
        long preparationStarted = Surface.Profiler is not null ? Stopwatch.GetTimestamp() : 0;
        PrepareTransparentGeometry();
        Surface.Profiler?.Cpu("transparent_geometry_prepare", preparationStarted);
        if(_transparentGeometry is null)
        {
            if(!_renderingReflection) ReleasePlanarRefractionTargets();
            return;
        }
        var key = _currentForward;
        long sortStarted = Surface.Profiler is not null ? Stopwatch.GetTimestamp() : 0;
        if(!_transparentOrders.TryGetValue(key, out var order))
        {
            // Translation subtracts the same depth from every triangle; only direction changes order.
            // The four horizontal reflection planes therefore also share a single sorted buffer.
            // Keep the cache bounded while navigating instead of retaining every historical viewpoint.
            TransparentTriangleOrder? reuse = null;
            if(_transparentOrders.Count >= 5)
            {
                _transparentOrders.Remove(_transparentOrderKeys.Dequeue(), out reuse);
                // Refilled arrays have the same identity but new contents: force their GPU upload and copy plan.
                if(ReferenceEquals(_uploadedTransparentIndices, reuse!.Indices)) _uploadedTransparentIndices = null;
                if(ReferenceEquals(_refractionPlanIndices, reuse.Indices)) _refractionPlanIndices = null;
            }
            order = _transparentSorter!.Sort(_currentForward, reuse);
            _transparentOrders.Add(key, order);
            _transparentOrderKeys.Enqueue(key);
        }
        TransparentTriangle[] sorted = order.Triangles;
        Surface.Profiler?.Cpu("transparent_sort", sortStarted);
        uint[] indices = order.Indices;
        long indexUploadStarted = Surface.Profiler is not null ? Stopwatch.GetTimestamp() : 0;
        if(!ReferenceEquals(_uploadedTransparentIndices, indices))
        {
            context.UpdateSubresource(indices.AsSpan(), _transparentGeometry.IndexBuffer);
            _uploadedTransparentIndices = indices;
        }
        Surface.Profiler?.Cpu("transparent_index_upload", indexUploadStarted);
        SetMaterialAlphaCutoff(context, 0.001f);
        context.OMSetBlendState(_transparentColorBlend);
        context.OMSetDepthStencilState(_depthReadState, 0);
        if(_renderingReflection || !_enhancedLightingEnabled)
        {
            Surface.Profiler?.Cpu("transparent_prepare", preparationStarted);
            // These passes never refract the current framebuffer. Every sorted triangle has exactly
            // the same pipeline state, so retain the index order in one draw instead of thousands.
            DrawGeometry(context, _transparentGeometry.VertexBuffer, _transparentGeometry.IndexBuffer, indices.Length, 0);
            return;
        }
        long planarStarted = Surface.Profiler is not null ? Stopwatch.GetTimestamp() : 0;
        PreparePlanarRefraction(context, drawEvent, indices);
        Surface.Profiler?.Cpu("transparent_planar_prepare", planarStarted);
        long snapshotsStarted = Surface.Profiler is not null ? Stopwatch.GetTimestamp() : 0;
        SetMaterialAlphaCutoff(context, 0.001f);
        context.OMSetBlendState(_transparentColorBlend);
        context.OMSetDepthStencilState(_depthReadState, 0);
        var size = (drawEvent.Surface.TextureWidth, drawEvent.Surface.TextureHeight);
        if(!ReferenceEquals(_refractionPlanIndices, indices) || _refractionPlanProjection != _currentViewProjection || _refractionPlanSize != size ||
           _refractionPlanOptimized != RefractionOptimizationEnabledForValidation || _refractionPlanUsesPlanar != PlanarRefractionEnabledForValidation ||
           _refractionPlanPlaneGeneration != _planarRefractionGeneration)
        {
            var copies = new List<(int Start, RefractionCopyBounds Bounds)>();
            // Planar backgrounds already handle their water surfaces. With no remaining water
            // reads, transparent writes cannot invalidate any snapshot, so no coverage plan is needed.
            bool needsSnapshots = _transparentWaterTriangles.Any(triangle => !HasPlanarRefraction(triangle.Center.Y));
            if(needsSnapshots)
            {
                if(_refractionSnapshotTracker is null || _refractionSnapshotTracker.Width != size.Item1 ||
                   _refractionSnapshotTracker.Height != size.Item2)
                    _refractionSnapshotTracker = new RefractionSnapshotTracker(size.Item1, size.Item2);
                else _refractionSnapshotTracker.Reset();
                var snapshots = _refractionSnapshotTracker;
                int lastSnapshotReader = sorted.Length - 1;
                while(lastSnapshotReader >= 0 && (!sorted[lastSnapshotReader].WaterSurface || HasPlanarRefraction(sorted[lastSnapshotReader].Center.Y))) lastSnapshotReader--;
                for(int start = 0; start < sorted.Length;)
                {
                    bool water = sorted[start].WaterSurface;
                    int end = start + 1;
                    while(end < sorted.Length && sorted[end].WaterSurface == water) end++;
                    if(water)
                    {
                        RefractionCopyBounds bounds = default;
                        for(int index = start; index < end; index++)
                        {
                            TransparentTriangle triangle = sorted[index];
                            if(HasPlanarRefraction(triangle.Center.Y)) continue;
                            bounds = RefractionCopyBounds.Union(bounds, RefractionCopyBounds.ForTriangle(
                                triangle.A, triangle.B, triangle.C, _currentViewProjection, size.Item1, size.Item2, triangle.RefractionUvPadding));
                        }
                        if(!RefractionOptimizationEnabledForValidation) bounds = RefractionCopyBounds.Full(size.Item1, size.Item2);
                        if(!bounds.IsEmpty && (!RefractionOptimizationEnabledForValidation || snapshots.RequiresCopy(bounds)))
                        {
                            bounds = snapshots.AlignCopyBounds(bounds);
                            copies.Add((start, bounds));
                            snapshots.MarkCopied(bounds);
                        }
                    }
                    // Only pixels possibly written since the last snapshot can invalidate a later water
                    // sample. Distant glass/side faces retain order without forcing redundant GPU barriers.
                    // Initially every tile is dirty, and after the last reader no later copy can
                    // depend on writes. Only track the interval between actual copies and readers.
                    for(int index = start; copies.Count != 0 && index < end && index < lastSnapshotReader; index++)
                    {
                        TransparentTriangle triangle = sorted[index];
                        snapshots.MarkDrawn(RefractionCopyBounds.ForTriangle(triangle.A, triangle.B, triangle.C,
                            _currentViewProjection, size.Item1, size.Item2, 0f));
                    }
                    start = end;
                }
                }
            _refractionCopies = copies.ToArray();
            _refractionPlanIndices = indices;
            _refractionPlanProjection = _currentViewProjection;
            _refractionPlanSize = size;
            _refractionPlanOptimized = RefractionOptimizationEnabledForValidation;
            _refractionPlanUsesPlanar = PlanarRefractionEnabledForValidation;
            _refractionPlanPlaneGeneration = _planarRefractionGeneration;
        }
        Surface.Profiler?.Cpu("transparent_snapshot_plan", snapshotsStarted);
        Surface.Profiler?.Cpu("transparent_prepare", preparationStarted);
        // Preserve every triangle's order and every necessary snapshot. Between snapshots the state is
        // identical, including side faces and offscreen water, so submit that interval in one draw.
        int pendingStart = 0;
        foreach(var copy in _refractionCopies)
        {
            if(copy.Start > pendingStart)
                DrawGeometry(context, _transparentGeometry.VertexBuffer, _transparentGeometry.IndexBuffer,
                    (copy.Start - pendingStart) * 3, pendingStart * 3);
            bool hadCopy = _hasOpaqueSceneCopy;
            CaptureOpaqueScene(context, drawEvent, copy.Bounds);
            if(!hadCopy) SetMaterialAlphaCutoff(context, 0.001f);
            pendingStart = copy.Start;
        }
        if(pendingStart < sorted.Length)
            DrawGeometry(context, _transparentGeometry.VertexBuffer, _transparentGeometry.IndexBuffer,
                (sorted.Length - pendingStart) * 3, pendingStart * 3);
        if(_enhancedLightingEnabled && !_renderingReflection)
        {
            // Record the nearest actual transparent surface for SSAO/SSR without blending normals or
            // borrowing the material alpha as a blend factor, which corrupts glass and water metadata.
            context.OMSetBlendState(_transparentSurfaceBlend);
            context.OMSetDepthStencilState(_depthWriteState, 0);
            DrawGeometry(context, _transparentGeometry.VertexBuffer, _transparentGeometry.IndexBuffer, indices.Length, 0);
        }
    }

    private void DisposeTransparency()
    {
        _transparentGeometry?.Dispose(); _transparentGeometry = null;
        _transparentTriangles = [];
        _transparentWaterTriangles = [];
        _refractionSnapshotTracker = null;
        _uploadedTransparentIndices = null;
        _transparentSources.Clear();
        _transparentOrders.Clear();
        _transparentOrderKeys.Clear();
        _transparentSorter = null;
        _refractionPlanIndices = null;
        _refractionCopies = [];
        _transparentGeneration = -1;
        _transparentColorBlend?.Dispose(); _transparentColorBlend = null;
        _transparentSurfaceBlend?.Dispose(); _transparentSurfaceBlend = null;
        _skyBlendState?.Dispose(); _skyBlendState = null;
    }
}

internal readonly record struct TransparentTriangle(int FirstVertex, Vector3 Center, bool WaterSurface,
    Vector3 A = default, Vector3 B = default, Vector3 C = default, float RefractionUvPadding = 0.01f)
{
    internal static TransparentTriangle[] Sort(TransparentTriangle[] triangles, Vector3 eye, Vector3 forward) =>
        new TransparentTriangleSorter(triangles).Sort(forward).Triangles;
}
