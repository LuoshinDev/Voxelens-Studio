using System.Numerics;

namespace ZhuJieJing.Renderer.Camera;

public readonly record struct OrbitCameraPose(Vector3 Focus, float Yaw, float Pitch, float Distance, float VerticalFieldOfView, CameraNavigationMode NavigationMode);
