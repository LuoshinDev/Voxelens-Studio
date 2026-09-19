using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    internal bool PlanarRefractionEnabledForValidation { get; set; } = true;
    internal int PlanarRefractionPlaneCountForValidation => _planarRefractionAvailable;
    internal string? PlanarRefractionFallbackReasonForValidation { get; private set; }
    internal string? VisibleWaterPlanesForValidation { get; private set; }
    private bool _renderingRefractionBackground;
    private GpuRefractionLayerSet? _planarRefractionTargets;
    private ID3D11Buffer? _planarRefractionBuffer;
    private float[] _planarRefractionHeights = [];
    private int _planarRefractionAvailable;
    private float _planarRefractionBuildingHeight;
    private long _planarRefractionGeneration;
    private float[] _planarRefractionSignatureHeights = [];
    private TransparentTriangle[]? _planarRefractionSelectionTriangles;
    private Matrix4x4 _planarRefractionSelectionProjection;
    private Vector4 _planarRefractionSelectionCandidates;
    private int _planarRefractionSelectionCandidateCount;
    private (int Width, int Height) _planarRefractionSelectionSize;
    private float _planarRefractionSelectionEyeY;
    private float[] _planarRefractionSelectionHeights = [];
    private string? _planarRefractionSelectionFallback;
    private string? _planarRefractionSelectionVisiblePlanes;

    /// <summary>
    /// Build immutable backgrounds from low water surfaces to high ones. The caller has already rendered
    /// opaque/cutout geometry and supplied the main camera's global back-to-front transparent index order.
    /// </summary>
    private void PreparePlanarRefraction(ID3D11DeviceContext context, ViewportDrawEventArgs drawEvent, uint[] sortedIndices)
    {
        if(_renderingRefractionBackground) return;
        _planarRefractionAvailable = 0;
        _planarRefractionHeights = [];
        PlanarRefractionFallbackReasonForValidation = null;
        VisibleWaterPlanesForValidation = null;
        string? unavailable = !PlanarRefractionEnabledForValidation ? "validation-disabled" :
            !_enhancedLightingEnabled ? "enhanced-lighting-disabled" :
            _renderingReflection ? "reflection-pass" :
            _content != ViewportContent.Sections ? "non-section-content" :
            _postProcessTarget is null ? "no-main-render-target" :
            _transparentGeometry is null || sortedIndices.Length == 0 ? "no-transparent-geometry" : null;
        if(unavailable is not null)
        {
            PlanarRefractionFallbackReasonForValidation = unavailable;
            UnbindPlanarRefractionViews(context);
            ReleasePlanarRefractionTargets();
            BindPlanarRefraction(context);
            return;
        }

        int width = drawEvent.Surface.TextureWidth, height = drawEvent.Surface.TextureHeight;
        float[] heights = SelectPlanarRefractionHeights(width, height);
        if(heights.Length == 0)
        {
            UnbindPlanarRefractionViews(context);
            ReleasePlanarRefractionTargets();
            BindPlanarRefraction(context);
            return;
        }
        int retainedLayers = _planarRefractionTargets is not null &&
            _planarRefractionTargets.Width == width && _planarRefractionTargets.Height == height
            ? Math.Max(heights.Length, _planarRefractionTargets.LayerCount)
            : heights.Length;
        // RGBA16F requires eight bytes per full-resolution pixel. Reject the extra background targets
        // as a group when they exceed one GiB; the established sorted snapshots remain the fallback.
        // Divide the limit first so even a malformed oversized viewport cannot overflow this check.
        if(!GpuRefractionLayerSet.CanAllocate(width, height, retainedLayers))
        {
            PlanarRefractionFallbackReasonForValidation = $"target-budget-exceeded:{width}x{height}x{retainedLayers}:limit-16-layers-1GiB";
            UnbindPlanarRefractionViews(context);
            ReleasePlanarRefractionTargets();
            BindPlanarRefraction(context);
            return;
        }

        bool complete = false;
        try
        {
            UnbindPlanarRefractionViews(context);
            EnsurePlanarRefractionTargets(width, height, heights.Length);
            _planarRefractionHeights = heights;
            context.PSSetShaderResource(7, null!);
            context.PSSetShaderResource(8, null!);
            context.OMSetRenderTargets([], null);
            // Depth is immutable until the final transparent metadata pass. Each background starts from
            // the same opaque color, so no above-water glass can leak into a lower water's refraction.
            context.CopyResource(_postProcessTarget!.OpaqueTexture, _postProcessTarget.Texture);
            Surface.Profiler?.Copy();
            context.CopyResource(_postProcessTarget.OpaqueDepthTexture, _postProcessTarget.DepthTexture);
            Surface.Profiler?.Copy();
            _hasOpaqueSceneCopy = true;
            if(!ReferenceEquals(_uploadedTransparentIndices, sortedIndices))
            {
                context.UpdateSubresource(sortedIndices.AsSpan(), _transparentGeometry!.IndexBuffer);
                _uploadedTransparentIndices = sortedIndices;
            }

            for(int index = 0; index < heights.Length; index++)
            {
                _renderingRefractionBackground = true;
                _planarRefractionBuildingHeight = heights[index];
                // Bind only completed lower layers. In particular, this frame's current RTV can never
                // still be bound as a previous frame's SRV while it is initialized or drawn into.
                BindPlanarRefraction(context);
                context.OMSetRenderTargets([], null);
                // One mip per array slice: slice N is D3D11 subresource N. The single opaque source
                // cannot be CopyResource'd into a resource with a different array size.
                context.CopySubresourceRegion(_planarRefractionTargets!.Texture, (uint)index, 0, 0, 0,
                    _postProcessTarget.OpaqueTexture, 0, null);
                Surface.Profiler?.Copy();
                context.OMSetRenderTargets(_planarRefractionTargets.GetRenderView(index), _postProcessTarget.DepthView);
                SetMainViewport(context, drawEvent);
                context.RSSetState(_voxelRasterizerState);
                BindVoxelPipeline(context);
                BindPlanarRefraction(context);
                SetMaterialAlphaCutoff(context, 0.001f);
                context.OMSetBlendState(_transparentColorBlend);
                context.OMSetDepthStencilState(_depthReadState, 0);
                DrawGeometry(context, _transparentGeometry!.VertexBuffer, _transparentGeometry.IndexBuffer,
                    sortedIndices.Length, 0);
                context.OMSetRenderTargets([], null);
                _planarRefractionAvailable = index + 1;
            }
            RecordPlanarRefractionSelection(heights);
            complete = true;
        }
        finally
        {
            _renderingRefractionBackground = false;
            _planarRefractionBuildingHeight = 0;
            if(!complete)
            {
                _planarRefractionAvailable = 0;
                RecordPlanarRefractionSelection([]);
                PlanarRefractionFallbackReasonForValidation = "background-generation-failed";
            }
            context.RSSetState(_voxelRasterizerState);
            BindSceneRenderTarget(context, drawEvent);
            BindVoxelPipeline(context);
            BindPlanarRefraction(context);
            SetMaterialAlphaCutoff(context, 0.001f);
            context.OMSetBlendState(_transparentColorBlend);
            context.OMSetDepthStencilState(_depthReadState, 0);
        }
    }

    private float[] SelectPlanarRefractionHeights(int width, int height)
    {
        int count = Math.Min(_reflectionPlaneHeights.Length, 4);
        float Candidate(int index) => index < count ? _reflectionPlaneHeights[index] : 0;
        var candidates = new Vector4(Candidate(0), Candidate(1), Candidate(2), Candidate(3));
        if(ReferenceEquals(_planarRefractionSelectionTriangles, _transparentTriangles) &&
           _planarRefractionSelectionProjection == _currentViewProjection &&
           _planarRefractionSelectionCandidates == candidates && _planarRefractionSelectionCandidateCount == count &&
           _planarRefractionSelectionSize == (width, height) && _planarRefractionSelectionEyeY == _currentEye.Y)
        {
            PlanarRefractionFallbackReasonForValidation = _planarRefractionSelectionFallback;
            VisibleWaterPlanesForValidation = _planarRefractionSelectionVisiblePlanes;
            return _planarRefractionSelectionHeights;
        }
        float[] selected = ComputePlanarRefractionHeights(width, height);
        _planarRefractionSelectionTriangles = _transparentTriangles;
        _planarRefractionSelectionProjection = _currentViewProjection;
        _planarRefractionSelectionCandidates = candidates;
        _planarRefractionSelectionCandidateCount = count;
        _planarRefractionSelectionSize = (width, height);
        _planarRefractionSelectionEyeY = _currentEye.Y;
        _planarRefractionSelectionHeights = selected;
        _planarRefractionSelectionFallback = PlanarRefractionFallbackReasonForValidation;
        _planarRefractionSelectionVisiblePlanes = VisibleWaterPlanesForValidation;
        return selected;
    }

    private float[] ComputePlanarRefractionHeights(int width, int height)
    {
        float eyeY = _currentEye.Y;
        var heights = new SortedDictionary<float, int>();
        int slopedTriangles = 0;
        foreach(TransparentTriangle triangle in _transparentWaterTriangles)
        {
            bool flat = float.IsFinite(triangle.A.Y) && triangle.A.Y == triangle.B.Y && triangle.A.Y == triangle.C.Y;
            // Canonical water tops are front-facing only from above. Do not enable new underwater
            // visibility or a two-sided water shader as part of this background optimization.
            if(flat && eyeY <= triangle.A.Y) continue;
            if(RefractionCopyBounds.ForTriangle(triangle.A, triangle.B, triangle.C,
                   _currentViewProjection, width, height, uvPadding: 0).IsEmpty) continue;
            if(!flat)
            {
                slopedTriangles++;
                continue;
            }
            heights[triangle.A.Y] = heights.GetValueOrDefault(triangle.A.Y) + 1;
        }
        // Refraction depends on every visible water surface, independently of which four reflection
        // planes were selected by area. Finish the entire census before deciding whether to fall back.
        string heightCounts = string.Join(",", heights.Select(pair =>
            pair.Key.ToString("R", CultureInfo.InvariantCulture) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture)));
        VisibleWaterPlanesForValidation = $"flat-height:triangle-count=[{heightCounts}];sloped-triangles={slopedTriangles}";
        if(slopedTriangles > 0)
        {
            PlanarRefractionFallbackReasonForValidation = "visible-sloped-water-top;" + VisibleWaterPlanesForValidation;
            return [];
        }
        if(heights.Count > GpuRefractionLayerSet.MaximumLayers)
        {
            PlanarRefractionFallbackReasonForValidation = "visible-water-plane-limit-exceeded:limit-16;" + VisibleWaterPlanesForValidation;
            return [];
        }
        float[] selected = heights.Keys.ToArray();
        if(selected.Length == 0) PlanarRefractionFallbackReasonForValidation = "no-eligible-visible-water-top";
        return selected;
    }

    private bool HasPlanarRefraction(float y)
    {
        if(!PlanarRefractionEnabledForValidation || !_enhancedLightingEnabled || _renderingReflection ||
           _content != ViewportContent.Sections) return false;
        for(int index = 0; index < _planarRefractionAvailable; index++)
            if(MathF.Abs(y - _planarRefractionHeights[index]) < 0.002f) return true;
        return false;
    }

    private void BindPlanarRefraction(ID3D11DeviceContext context)
    {
        bool enabled = PlanarRefractionEnabledForValidation && _enhancedLightingEnabled && !_renderingReflection &&
            _content == ViewportContent.Sections;
        int available = enabled ? _planarRefractionAvailable : 0;
        if(_device is not null)
        {
            _planarRefractionBuffer ??= _device.CreateBuffer((uint)Marshal.SizeOf<PlanarRefractionConstants>(), BindFlags.ConstantBuffer);
            float HeightAt(int index) => index < _planarRefractionHeights.Length ? _planarRefractionHeights[index] : 0;
            var constants = new PlanarRefractionConstants
            {
                Heights0 = new Vector4(HeightAt(0), HeightAt(1), HeightAt(2), HeightAt(3)),
                Heights1 = new Vector4(HeightAt(4), HeightAt(5), HeightAt(6), HeightAt(7)),
                Heights2 = new Vector4(HeightAt(8), HeightAt(9), HeightAt(10), HeightAt(11)),
                Heights3 = new Vector4(HeightAt(12), HeightAt(13), HeightAt(14), HeightAt(15)),
                Options = new Vector4(available, _planarRefractionBuildingHeight,
                    enabled && _renderingRefractionBackground ? 1f : 0f, 0f),
            };
            context.UpdateSubresource(in constants, _planarRefractionBuffer);
        }
        context.PSSetConstantBuffer(4, _planarRefractionBuffer);
        context.PSSetShaderResource(13, (available > 0 ? _planarRefractionTargets!.GetCompletedView(available) : null)!);
    }

    private void EnsurePlanarRefractionTargets(int width, int height, int count)
    {
        if(_planarRefractionTargets is not null && _planarRefractionTargets.Width == width &&
           _planarRefractionTargets.Height == height && _planarRefractionTargets.LayerCount >= count) return;
        ReleasePlanarRefractionTargets();
        _planarRefractionTargets = GpuRefractionLayerSet.Create(_device!, width, height, count);
    }

    private static void UnbindPlanarRefractionViews(ID3D11DeviceContext context)
    {
        context.PSSetShaderResource(13, null!);
    }

    private void ReleasePlanarRefractionTargets()
    {
        RecordPlanarRefractionSelection([]);
        _planarRefractionAvailable = 0;
        _planarRefractionHeights = [];
        _planarRefractionTargets?.Dispose();
        _planarRefractionTargets = null;
    }

    private void DisposePlanarRefraction()
    {
        ReleasePlanarRefractionTargets();
        _planarRefractionBuffer?.Dispose();
        _planarRefractionBuffer = null;
        _renderingRefractionBackground = false;
        _planarRefractionBuildingHeight = 0;
        PlanarRefractionFallbackReasonForValidation = null;
        VisibleWaterPlanesForValidation = null;
        _planarRefractionSelectionTriangles = null;
        _planarRefractionSelectionHeights = [];
        _planarRefractionSelectionFallback = null;
        _planarRefractionSelectionVisiblePlanes = null;
    }

    private void RecordPlanarRefractionSelection(float[] heights)
    {
        if(_planarRefractionSignatureHeights.AsSpan().SequenceEqual(heights)) return;
        _planarRefractionSignatureHeights = heights;
        _planarRefractionGeneration++;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlanarRefractionConstants
    {
        public Vector4 Heights0;
        public Vector4 Heights1;
        public Vector4 Heights2;
        public Vector4 Heights3;
        public Vector4 Options;
    }
}
