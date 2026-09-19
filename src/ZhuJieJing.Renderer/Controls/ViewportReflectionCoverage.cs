using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    internal bool ReflectionCoverageEnabledForValidation { get; set; } = true;
    private ID3D11RasterizerState? _reflectionRasterizerState;
    private TransparentTriangle[]? _reflectionCoverageTriangles;
    private Matrix4x4 _reflectionCoverageCamera;
    private long _reflectionCoverageGeneration;
    private readonly Dictionary<(float Height, int Width, int HeightPixels), ReflectionCoverageEntry> _reflectionCoverage = [];
    private ReflectionCoverageEntry? _activeReflectionCoverageEntry;
    private ID3D11Buffer? _reflectionCoverageBuffer;
    private Vector4 _reflectionCoverageOptions;

    private RefractionCopyBounds GetReflectionCoverage(CameraConstants camera, CameraConstants reflected, float planeY)
    {
        int width = _postProcessTarget!.ReflectionWidth, height = _postProcessTarget.ReflectionHeight;
        _activeReflectionCoverageEntry = null;
        if(!ReflectionCoverageEnabledForValidation || _content != ViewportContent.Sections) return RefractionCopyBounds.Full(width, height);
        PrepareTransparentGeometry();
        if(!ReferenceEquals(_reflectionCoverageTriangles, _transparentTriangles) || _reflectionCoverageCamera != camera.ViewProjection)
        {
            _reflectionCoverageTriangles = _transparentTriangles;
            _reflectionCoverageCamera = camera.ViewProjection;
            _reflectionCoverageGeneration++;
        }
        var key = (planeY, width, height);
        if(_reflectionCoverage.TryGetValue(key, out var entry) && entry.Generation == _reflectionCoverageGeneration)
        {
            _activeReflectionCoverageEntry = entry;
            return entry.Mask.PixelBounds;
        }
        // Proportional resizes can preserve the same projection; do not retain every historic size.
        if(entry is null)
        {
            if(_reflectionCoverage.Count >= 8) ClearReflectionCoverageEntries();
            entry = new ReflectionCoverageEntry(new ReflectionCoverageTileMask(width, height));
            _reflectionCoverage.Add(key, entry);
        }
        // Camera changes update mask contents; they do not require allocating a new GPU texture.
        entry.BeginUpdate(_reflectionCoverageGeneration);
        ReflectionCoverageTileMask mask = entry.Mask;
        foreach(TransparentTriangle triangle in _transparentTriangles)
        {
            if(!triangle.WaterSurface) continue;
            float minimum = MathF.Min(triangle.A.Y, MathF.Min(triangle.B.Y, triangle.C.Y));
            float maximum = MathF.Max(triangle.A.Y, MathF.Max(triangle.B.Y, triangle.C.Y));
            if(maximum < planeY - 0.10f || minimum > planeY + 0.10f) continue;
            mask.Include(ReflectionCoverageMath.ForTriangle(
                triangle.A, triangle.B, triangle.C, camera.ViewProjection, reflected.ViewProjection, width, height));
        }
        _activeReflectionCoverageEntry = entry;
        return mask.PixelBounds;
    }

    private void SetReflectionCoverage(ID3D11DeviceContext context, RefractionCopyBounds coverage)
    {
        context.PSSetShaderResource(10, null!);
        _reflectionCoverageBuffer ??= _device!.CreateBuffer((uint)Marshal.SizeOf<Vector4>(), BindFlags.ConstantBuffer);
        ReflectionCoverageEntry? entry = _activeReflectionCoverageEntry;
        if(entry is not null && entry.Mask.HasUncoveredTiles)
        {
            entry.EnsureGpu(_device!, context);
            _reflectionCoverageOptions = new Vector4(1f, ReflectionCoverageTileMask.TileSize, entry.Mask.Columns, entry.Mask.Rows);
        }
        else _reflectionCoverageOptions = Vector4.Zero;
        context.UpdateSubresource(in _reflectionCoverageOptions, _reflectionCoverageBuffer);
        if(_reflectionRasterizerState is null)
        {
            RasterizerDescription description = ViewportRasterization.CreateVoxelDescription();
            description.ScissorEnable = true;
            _reflectionRasterizerState = _device!.CreateRasterizerState(description);
        }
        context.RSSetState(_reflectionRasterizerState);
        context.RSSetScissorRect(coverage.Left, coverage.Top, coverage.Right, coverage.Bottom);
    }

    private void BindReflectionCoverageMask(ID3D11DeviceContext context)
    {
        context.PSSetConstantBuffer(3, _reflectionCoverageBuffer);
        context.PSSetShaderResource(10, (_renderingReflection ? _activeReflectionCoverageEntry?.View : null)!);
    }

    private void ClearReflectionCoverageEntries()
    {
        foreach(ReflectionCoverageEntry entry in _reflectionCoverage.Values) entry.Dispose();
        _reflectionCoverage.Clear();
        _activeReflectionCoverageEntry = null;
    }

    private void DisposeReflectionCoverage()
    {
        _reflectionRasterizerState?.Dispose(); _reflectionRasterizerState = null;
        ClearReflectionCoverageEntries(); _reflectionCoverageTriangles = null;
        _reflectionCoverageBuffer?.Dispose(); _reflectionCoverageBuffer = null;
        _reflectionCoverageOptions = default;
    }

    private sealed class ReflectionCoverageEntry(ReflectionCoverageTileMask mask) : IDisposable
    {
        internal ReflectionCoverageTileMask Mask { get; } = mask;
        internal long Generation { get; private set; } = -1;
        private bool _dirty = true;
        private ID3D11Texture2D? _texture;
        internal ID3D11ShaderResourceView? View { get; private set; }

        internal void EnsureGpu(ID3D11Device device, ID3D11DeviceContext context)
        {
            if(_texture is not null)
            {
                if(_dirty) context.UpdateSubresource(Mask.Pixels.AsSpan(), _texture, 0, (uint)Mask.Columns, 0);
                _dirty = false;
                return;
            }
            ID3D11Texture2D texture = device.CreateTexture2D(new Texture2DDescription(Format.R8_UNorm,
                (uint)Mask.Columns, (uint)Mask.Rows, 1, 1, BindFlags.ShaderResource));
            try
            {
                context.UpdateSubresource(Mask.Pixels.AsSpan(), texture, 0, (uint)Mask.Columns, 0);
                View = device.CreateShaderResourceView(texture);
                _texture = texture;
                _dirty = false;
            }
            catch { texture.Dispose(); throw; }
        }

        internal void BeginUpdate(long generation)
        {
            Generation = generation;
            Mask.Clear();
            _dirty = true;
        }

        public void Dispose()
        {
            View?.Dispose(); View = null;
            _texture?.Dispose(); _texture = null;
        }
    }
}
