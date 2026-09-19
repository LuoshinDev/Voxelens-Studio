using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using ZhuJieJing.Renderer.Camera;
using ZhuJieJing.Renderer.Meshing;

namespace ZhuJieJing.Renderer.Controls;

public partial class VoxelViewport
{
    private GpuGeometry? _observerBodyShadowGeometry;
    private readonly VoxelVertex[] _observerBodyShadowVertices = new VoxelVertex[ObserverBodyShadowGeometry.VertexCount];
    private bool _observerBodyShadowActive;
    private Vector3 _observerBodyShadowEye;
    private float _observerBodyShadowYaw;
    private GpuShadowMap? _observerBodyShadowMap;
    private ID3D11Buffer? _observerBodyShadowBuffer;
    private bool _observerBodyShadowDirty;
    private Vector3 _observerBodySunDirection;
    private ObserverBodyShadowConstants _observerBodyShadowConstants;

    private void UpdateObserverBodyShadow(ID3D11DeviceContext context)
    {
        if(_observerBodyShadowBuffer is null)
        {
            _observerBodyShadowBuffer = _device!.CreateBuffer((uint)Marshal.SizeOf<ObserverBodyShadowConstants>(), BindFlags.ConstantBuffer);
            context.UpdateSubresource(in _observerBodyShadowConstants, _observerBodyShadowBuffer);
        }
        bool enabled = _enhancedLightingEnabled && _camera.NavigationMode == CameraNavigationMode.Observer;
        if(!enabled)
        {
            if(_observerBodyShadowActive)
            {
                _observerBodyShadowActive = false;
                _observerBodyShadowConstants.Options.X = 0f;
                context.UpdateSubresource(in _observerBodyShadowConstants, _observerBodyShadowBuffer);
            }
            return;
        }

        Vector3 eye = _camera.EyePosition;
        float yaw = _camera.Yaw;
        if(_observerBodyShadowActive && eye == _observerBodyShadowEye && yaw == _observerBodyShadowYaw) return;
        ObserverBodyShadowGeometry.FillVertices(_observerBodyShadowVertices, eye, yaw);
        if(_observerBodyShadowGeometry is null)
            _observerBodyShadowGeometry = GpuGeometry.Create(_device!, _observerBodyShadowVertices, ObserverBodyShadowGeometry.Indices);
        else
            context.UpdateSubresource(_observerBodyShadowVertices.AsSpan(), _observerBodyShadowGeometry.VertexBuffer);
        _observerBodyShadowEye = eye;
        _observerBodyShadowYaw = yaw;
        _observerBodyShadowActive = true;
        _observerBodyShadowDirty = true;
    }

    private void RenderObserverBodyShadow(ID3D11DeviceContext context, ViewportDrawEventArgs drawEvent, CameraConstants mainCamera, bool sunlightEnabled)
    {
        if(!_observerBodyShadowActive || _observerBodyShadowGeometry is null) return;
        if(!sunlightEnabled)
        {
            if(_observerBodyShadowConstants.Options.X != 0f)
            {
                _observerBodyShadowConstants.Options.X = 0f;
                context.UpdateSubresource(in _observerBodyShadowConstants, _observerBodyShadowBuffer!);
            }
            return;
        }
        Vector3 sunlight = new(mainCamera.SunDirectionAndStrength.X, mainCamera.SunDirectionAndStrength.Y, mainCamera.SunDirectionAndStrength.Z);
        if(_observerBodyShadowMap is not null && !_observerBodyShadowDirty && sunlight == _observerBodySunDirection && _observerBodyShadowConstants.Options.X > 0f) return;
        _observerBodyShadowMap ??= GpuShadowMap.Create(_device!, 2048);
        Matrix4x4 projection = ObserverBodyShadowGeometry.CreateSunProjection(_observerBodyShadowEye, sunlight);
        _observerBodyShadowConstants = new ObserverBodyShadowConstants
        {
            ViewProjection = projection,
            Options = new Vector4(1f, 1f / 2048f, 0.000006f, 0f),
        };
        CameraConstants shadowCamera = mainCamera;
        shadowCamera.SunViewProjection = projection;
        context.PSSetShaderResource(12, null!);
        try
        {
            context.UpdateSubresource(in shadowCamera, _cameraBuffer!);
            context.UpdateSubresource(in _observerBodyShadowConstants, _observerBodyShadowBuffer!);
            context.OMSetRenderTargets([], _observerBodyShadowMap.DepthView);
            context.RSSetViewport(new Viewport(0f, 0f, 2048f, 2048f, 0f, 1f));
            context.RSSetState(_shadowRasterizerState);
            context.ClearDepthStencilView(_observerBodyShadowMap.DepthView, DepthStencilClearFlags.Depth, 1f, 0);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            context.IASetInputLayout(_inputLayout);
            context.VSSetShader(_shadowVertexShader);
            context.VSSetConstantBuffer(0, _cameraBuffer!);
            context.PSSetShader(null!);
            context.OMSetBlendState(_opaqueBlendState);
            context.OMSetDepthStencilState(_depthWriteState, 0);
            DrawGeometry(context, _observerBodyShadowGeometry.VertexBuffer, _observerBodyShadowGeometry.IndexBuffer,
                _observerBodyShadowGeometry.IndexCount, 0);
        }
        finally
        {
            context.UpdateSubresource(in mainCamera, _cameraBuffer!);
            context.RSSetState(_voxelRasterizerState);
            BindSceneRenderTarget(context, drawEvent);
        }
        _observerBodySunDirection = sunlight;
        _observerBodyShadowDirty = false;
    }

    private void BindObserverBodyShadow(ID3D11DeviceContext context)
    {
        context.PSSetConstantBuffer(2, _observerBodyShadowBuffer);
        context.PSSetShaderResource(12, (_observerBodyShadowActive ? _observerBodyShadowMap?.View : null)!);
    }

    private void DisposeObserverBodyShadow()
    {
        _observerBodyShadowGeometry?.Dispose();
        _observerBodyShadowGeometry = null;
        _observerBodyShadowActive = false;
        _observerBodyShadowMap?.Dispose();
        _observerBodyShadowMap = null;
        _observerBodyShadowBuffer?.Dispose();
        _observerBodyShadowBuffer = null;
        _observerBodyShadowConstants = default;
        _observerBodyShadowDirty = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObserverBodyShadowConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 Options;
    }
}

/// <summary>A small upright Minecraft-shaped shadow caster; its feet follow the actual eye, never the orbit target.</summary>
internal static class ObserverBodyShadowGeometry
{
    internal const int VertexCount = 48;
    internal const float Height = 1.8f;
    private static readonly Vector3[] LocalVertices = CreateLocalVertices();
    internal static readonly uint[] Indices = CreateIndices();

    internal static Matrix4x4 CreateSunProjection(Vector3 eye, Vector3 sunlightDirection)
    {
        float directionLengthSquared = sunlightDirection.LengthSquared();
        if(!float.IsFinite(sunlightDirection.X) || !float.IsFinite(sunlightDirection.Y) || !float.IsFinite(sunlightDirection.Z) ||
           !float.IsFinite(directionLengthSquared) || directionLengthSquared < 0.000001f)
            throw new ArgumentOutOfRangeException(nameof(sunlightDirection));
        Vector3 direction = Vector3.Normalize(sunlightDirection);
        Vector3 center = SunOpticsMath.ObserverBodyAnchor(eye) + Vector3.UnitY * (Height * 0.5f);
        Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.94f ? Vector3.UnitZ : Vector3.UnitY;
        // A tight light-space footprint stays sharp even with a very large world shadow map.
        // The long depth interval still accepts distant receivers below a flying observer.
        Matrix4x4 view = Matrix4x4.CreateLookAt(center - direction * 512f, center, up);
        return view * Matrix4x4.CreateOrthographic(5f, 5f, 0.01f, 1024f);
    }

    internal static void FillVertices(Span<VoxelVertex> destination, Vector3 eye, float yaw)
    {
        if(destination.Length != VertexCount) throw new ArgumentException("Observer shadow vertex buffer has the wrong size.", nameof(destination));
        if(!float.IsFinite(yaw)) throw new ArgumentOutOfRangeException(nameof(yaw));
        Vector3 feet = SunOpticsMath.ObserverBodyAnchor(eye);
        (float sine, float cosine) = MathF.SinCos(yaw);
        Vector3 forward = new(cosine, 0f, sine);
        Vector3 right = new(-sine, 0f, cosine);
        for(int index = 0; index < LocalVertices.Length; index++)
        {
            Vector3 local = LocalVertices[index];
            Vector3 position = feet + right * local.X + Vector3.UnitY * local.Y + forward * local.Z;
            destination[index] = new VoxelVertex(position, Vector3.UnitY, Vector4.One, Vector2.Zero, Vector4.Zero);
        }
    }

    private static Vector3[] CreateLocalVertices()
    {
        var vertices = new Vector3[VertexCount];
        AddBox(0, new(-0.24f, 0f, -0.13f), new(-0.02f, 0.65f, 0.13f));
        AddBox(1, new(0.02f, 0f, -0.13f), new(0.24f, 0.65f, 0.13f));
        AddBox(2, new(-0.25f, 0.65f, -0.13f), new(0.25f, 1.3f, 0.13f));
        AddBox(3, new(-0.47f, 0.7f, -0.13f), new(-0.25f, 1.3f, 0.13f));
        AddBox(4, new(0.25f, 0.7f, -0.13f), new(0.47f, 1.3f, 0.13f));
        AddBox(5, new(-0.25f, 1.3f, -0.25f), new(0.25f, Height, 0.25f));
        return vertices;

        void AddBox(int box, Vector3 minimum, Vector3 maximum)
        {
            for(int corner = 0; corner < 8; corner++)
                vertices[box * 8 + corner] = new Vector3(
                    (corner & 1) == 0 ? minimum.X : maximum.X,
                    (corner & 2) == 0 ? minimum.Y : maximum.Y,
                    (corner & 4) == 0 ? minimum.Z : maximum.Z);
        }
    }

    private static uint[] CreateIndices()
    {
        ReadOnlySpan<uint> boxIndices = [0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6,
            0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
            0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7];
        var indices = new uint[6 * boxIndices.Length];
        for(int box = 0; box < 6; box++)
            for(int index = 0; index < boxIndices.Length; index++)
                indices[box * boxIndices.Length + index] = (uint)(box * 8) + boxIndices[index];
        return indices;
    }
}
