using System.Numerics;

namespace ZhuJieJing.Renderer.Camera;

/// <summary>Camera state and screen-space navigation math shared by the native viewport.</summary>
public sealed class OrbitCamera
{
    public const float FieldOfView = 0.82f;
    public const float MinimumFieldOfView = 0.35f;
    public const float MaximumFieldOfView = 1.75f;
    public const float MinimumDistance = 3f;
    public const float MaximumDistance = 512f;
    private const float RotationRadiansPerDip = 0.009f;
    private const float MinimumPitch = -1.48f;
    private const float MaximumPitch = 1.48f;
    private const float KeyboardAcceleration = 10f;
    private const float KeyboardDamping = 12f;
    private const float RotationVelocityResponse = 36f;
    // Matches the web preview's OrbitControls dampingFactor=0.065 at 60 Hz while remaining frame-rate independent.
    private const float OrbitPointerRotationResponse = 4.032525f;
    private const float RotationInertiaDamping = 7f;
    private const float PanDamping = 20f;
    private const float ZoomDamping = 18f;
    private const float MaximumRotationVelocity = 10f;
    private const float MinimumRotationReleaseSpeed = 0.4f;
    private const float RotationStopSpeed = 0.025f;
    private const float MaximumRotationReleaseDelay = 0.09f;
    private const float KeyboardStopSpeedFraction = 0.005f;
    private const float MinimumKeyboardStopSpeed = 0.01f;
    private const float PanStopDistance = 0.0005f;
    private const float RotationStopRadians = 0.0001f;
    private const float MaximumQueuedPanDistanceFactor = 2f;
    private const float ZoomStopDistance = 0.0005f;
    private Vector3 _keyboardVelocity;
    private Vector2 _rotationVelocity;
    private Vector2 _pendingRotation;
    private Vector3 _pendingPan;
    private float _zoomTargetDistance;
    private float? _lastKeyboardMaximumSpeed;
    private bool _rotationGestureActive;
    private bool _hasRotationSample;
    private bool _hasQueuedRotationSample;
    private float _verticalFieldOfView = FieldOfView;

    public OrbitCamera()
    {
        Focus = new Vector3(0f, 1.5f, 0f);
        Yaw = -0.78f;
        Pitch = -0.58f;
        Distance = 27f;
        _zoomTargetDistance = Distance;
    }

    public Vector3 Focus { get; private set; }

    public float Yaw { get; private set; }

    public float Pitch { get; private set; }

    public float Distance { get; private set; }

    public float ZoomTargetDistance => _zoomTargetDistance;

    public float VerticalFieldOfView => _verticalFieldOfView;

    public OrbitCameraPose CapturePose() => new(Focus, Yaw, Pitch, Distance, _verticalFieldOfView, NavigationMode);

    public void ApplyPose(OrbitCameraPose pose)
    {
        ValidateFinite(pose.Focus, nameof(pose));
        ValidateDistance(pose.Distance, nameof(pose));
        if(!float.IsFinite(pose.Yaw) || !float.IsFinite(pose.Pitch) || pose.Pitch is < MinimumPitch or > MaximumPitch ||
           !float.IsFinite(pose.VerticalFieldOfView) || pose.VerticalFieldOfView is < MinimumFieldOfView or > MaximumFieldOfView ||
           !Enum.IsDefined(pose.NavigationMode)) throw new ArgumentOutOfRangeException(nameof(pose));
        Focus = pose.Focus;
        Yaw = pose.Yaw;
        Pitch = pose.Pitch;
        Distance = pose.Distance;
        _verticalFieldOfView = pose.VerticalFieldOfView;
        NavigationMode = pose.NavigationMode;
        StopMotion();
    }

    public CameraNavigationMode NavigationMode { get; private set; }

    public Vector3 ViewDirection
    {
        get
        {
            float horizontal = MathF.Cos(Pitch);
            return Vector3.Normalize(new Vector3(
                horizontal * MathF.Cos(Yaw),
                MathF.Sin(Pitch),
                horizontal * MathF.Sin(Yaw)));
        }
    }

    public Vector3 EyePosition => Focus - ViewDirection * Distance;

    /// <summary>The coordinate controlled by navigation: the orbit target normally, and the camera eye in observer mode.</summary>
    public Vector3 NavigationPosition => NavigationMode == CameraNavigationMode.Observer ? EyePosition : Focus;

    public bool SetNavigationMode(CameraNavigationMode mode)
    {
        if(!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if(NavigationMode == mode) return false;
        StopMotion();
        NavigationMode = mode;
        return true;
    }

    public bool SetFieldOfView(float radians)
    {
        if(!float.IsFinite(radians) || radians is < MinimumFieldOfView or > MaximumFieldOfView)
            throw new ArgumentOutOfRangeException(nameof(radians));
        if(MathF.Abs(_verticalFieldOfView - radians) < 0.000001f) return false;
        _verticalFieldOfView = radians;
        return true;
    }

    public void SetTarget(Vector3 worldPosition, float? distance = null)
    {
        ValidateFinite(worldPosition, nameof(worldPosition));
        if(distance is float framedDistance)
        {
            ValidateDistance(framedDistance, nameof(distance));
            Distance = framedDistance;
        }

        Focus = worldPosition;
        StopMotion();
    }

    /// <summary>Moves the coordinate controlled by the current navigation mode without changing the viewing direction.</summary>
    public void SetNavigationPosition(Vector3 worldPosition, float? distance = null)
    {
        ValidateFinite(worldPosition, nameof(worldPosition));
        if(distance is float framedDistance)
        {
            ValidateDistance(framedDistance, nameof(distance));
            Distance = framedDistance;
        }

        Focus = NavigationMode == CameraNavigationMode.Observer
            ? worldPosition + ViewDirection * Distance
            : worldPosition;
        StopMotion();
    }

    public void Rotate(Vector2 pointerDelta)
    {
        ValidateFinite(pointerDelta, nameof(pointerDelta));
        CancelRotationGesture();
        ApplyRotation(new Vector2(
            pointerDelta.X * RotationRadiansPerDip,
            pointerDelta.Y * RotationRadiansPerDip));
    }

    /// <summary>Rotates the view around the eye instead of orbiting around the target.</summary>
    public void RotateFirstPerson(Vector2 pointerDelta)
    {
        ValidateFinite(pointerDelta, nameof(pointerDelta));
        CancelRotationGesture();
        ApplyRotationPreservingEye(new Vector2(
            pointerDelta.X * RotationRadiansPerDip,
            -pointerDelta.Y * RotationRadiansPerDip));
    }

    /// <summary>Starts pointer rotation without discarding any damped rotation already in flight.</summary>
    public void BeginRotationGesture()
    {
        _rotationGestureActive = true;
        _hasRotationSample = false;
        _hasQueuedRotationSample = false;
        _rotationVelocity = Vector2.Zero;
    }

    /// <summary>Applies one drag sample immediately and records its angular velocity for release inertia.</summary>
    public void RotateGesture(Vector2 pointerDelta, float elapsedSeconds)
    {
        ValidateFinite(pointerDelta, nameof(pointerDelta));
        if(!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if(!_rotationGestureActive)
            throw new InvalidOperationException("A rotation gesture must be started before samples are recorded.");

        Vector2 applied = ApplyRotation(new Vector2(
            pointerDelta.X * RotationRadiansPerDip,
            pointerDelta.Y * RotationRadiansPerDip));
        RecordRotationVelocity(applied, elapsedSeconds);
    }

    /// <summary>
    /// Coalesces pointer samples for one render-frame update. The same residual is damped while dragging, while the
    /// pointer is held still, and after release, matching the continuous motion of the web preview's OrbitControls.
    /// </summary>
    public void QueueRotationGesture(Vector2 pointerDelta, float elapsedSeconds)
    {
        ValidateFinite(pointerDelta, nameof(pointerDelta));
        if(!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if(!_rotationGestureActive)
            throw new InvalidOperationException("A rotation gesture must be started before samples are recorded.");

        Vector2 angularDelta = new(
            pointerDelta.X * RotationRadiansPerDip,
            pointerDelta.Y * RotationRadiansPerDip);
        _pendingRotation += angularDelta;
        RecordRotationVelocity(angularDelta, elapsedSeconds);
        _hasQueuedRotationSample = true;
    }

    /// <summary>Queues Minecraft-style mouse look; the camera eye remains fixed when the frame consumes it.</summary>
    public void QueueFirstPersonLook(Vector2 pointerDelta)
    {
        ValidateFinite(pointerDelta, nameof(pointerDelta));
        if(NavigationMode != CameraNavigationMode.Observer)
            throw new InvalidOperationException("First-person look requires observer navigation mode.");

        Vector2 angularDelta = new(
            pointerDelta.X * RotationRadiansPerDip,
            -pointerDelta.Y * RotationRadiansPerDip);
        // Observer input is relative mouse motion, so every sample must survive until the next render frame.
        // Replacing or magnitude-clamping a pending sample makes a fast sweep depend on render timing and lets
        // tiny cursor-warp corrections suppress motion in the opposite direction.
        _pendingRotation += angularDelta;
    }

    /// <summary>Ends pointer rotation. Queued drag residual keeps damping continuously; direct samples keep only a recent flick.</summary>
    public void EndRotationGesture(float secondsSinceLastSample)
    {
        if(!float.IsFinite(secondsSinceLastSample) || secondsSinceLastSample < 0f)
            throw new ArgumentOutOfRangeException(nameof(secondsSinceLastSample));
        if(!_rotationGestureActive)
        {
            _rotationVelocity = Vector2.Zero;
            _hasRotationSample = false;
            return;
        }

        _rotationGestureActive = false;
        bool carriesRelease = _hasRotationSample &&
                              secondsSinceLastSample <= MaximumRotationReleaseDelay &&
                              _rotationVelocity.LengthSquared() >= MinimumRotationReleaseSpeed * MinimumRotationReleaseSpeed;
        if(_hasQueuedRotationSample)
        {
            // OrbitControls-style dragging stores the unconsumed pointer delta itself. Releasing the button must
            // never flush that residual synchronously: doing so makes long presses and release events outside the
            // viewport stop dead. A pointer held still has already allowed this residual to settle frame by frame.
            _rotationVelocity = Vector2.Zero;
        }
        else if(carriesRelease)
        {
            _rotationVelocity *= MathF.Exp(-RotationInertiaDamping * secondsSinceLastSample);
        }
        else _rotationVelocity = Vector2.Zero;
        _hasRotationSample = false;
        _hasQueuedRotationSample = false;
    }

    /// <summary>Cancels both an active rotation gesture and any pending rotational inertia.</summary>
    public void CancelRotationGesture()
    {
        _rotationGestureActive = false;
        _hasRotationSample = false;
        _hasQueuedRotationSample = false;
        _rotationVelocity = Vector2.Zero;
        _pendingRotation = Vector2.Zero;
    }

    /// <summary>
    /// Pans the orbit target in the camera's screen plane. Pointer movement grabs the scene: dragging right
    /// moves the target toward screen-left, while dragging down moves it toward screen-up.
    /// </summary>
    public void Pan(Vector2 pointerDelta, float viewportHeight)
    {
        Vector3 displacement = CalculatePanDisplacement(pointerDelta, viewportHeight);
        CancelRotationGesture();
        Focus += displacement;
    }

    /// <summary>Queues a screen-space pan that is consumed smoothly over subsequent motion updates.</summary>
    public void QueuePan(Vector2 pointerDelta, float viewportHeight)
    {
        Vector3 displacement = CalculatePanDisplacement(pointerDelta, viewportHeight);
        CancelRotationGesture();
        float maximumDistance = MathF.Max(Distance * MaximumQueuedPanDistanceFactor, MinimumDistance);
        if(Vector3.Dot(_pendingPan, displacement) < 0f) _pendingPan = Vector3.Zero;
        _pendingPan = ClampMagnitude(_pendingPan + displacement, maximumDistance);
    }

    public bool MoveRelative(float forwardAxis, float rightAxis, float verticalAxis, float distance)
    {
        if(!float.IsFinite(forwardAxis) || !float.IsFinite(rightAxis) || !float.IsFinite(verticalAxis))
        {
            throw new ArgumentOutOfRangeException(nameof(forwardAxis));
        }
        if(!float.IsFinite(distance) || distance < 0f) throw new ArgumentOutOfRangeException(nameof(distance));

        var forward = Vector3.Normalize(new Vector3(MathF.Cos(Yaw), 0f, MathF.Sin(Yaw)));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 direction = forward * forwardAxis + right * rightAxis + Vector3.UnitY * verticalAxis;
        if(direction.LengthSquared() < 0.000001f || distance == 0f) return false;
        Focus += Vector3.Normalize(direction) * distance;
        return true;
    }

    public void Zoom(int wheelDelta)
    {
        if(wheelDelta == 0 || NavigationMode == CameraNavigationMode.Observer) return;
        float scale = wheelDelta > 0 ? 0.86f : 1.16f;
        Distance = Math.Clamp(Distance * scale, MinimumDistance, MaximumDistance);
        _zoomTargetDistance = Distance;
    }

    /// <summary>Queues a wheel zoom target so distance changes ease instead of stepping immediately.</summary>
    public void QueueZoom(int wheelDelta)
    {
        if(wheelDelta == 0 || NavigationMode == CameraNavigationMode.Observer) return;
        float wheelSteps = MathF.Abs(wheelDelta / 120f);
        float scale = MathF.Pow(wheelDelta > 0 ? 0.86f : 1.16f, wheelSteps);
        _zoomTargetDistance = Math.Clamp(_zoomTargetDistance * scale, MinimumDistance, MaximumDistance);
    }

    /// <summary>Changes the smoothly approached orbit distance. Observer mode intentionally ignores zoom.</summary>
    public bool SetZoomTargetDistance(float distance)
    {
        ValidateDistance(distance, nameof(distance));
        if(NavigationMode == CameraNavigationMode.Observer || _zoomTargetDistance == distance) return false;
        _zoomTargetDistance = distance;
        return true;
    }

    /// <summary>Advances keyboard motion, rotation inertia, queued pointer pan and wheel zoom damping.</summary>
    public OrbitCameraMotion UpdateMotion(float forwardAxis, float rightAxis, float verticalAxis, float maximumSpeed, float elapsedSeconds)
    {
        if(!float.IsFinite(forwardAxis) || !float.IsFinite(rightAxis) || !float.IsFinite(verticalAxis))
        {
            throw new ArgumentOutOfRangeException(nameof(forwardAxis));
        }
        if(!float.IsFinite(maximumSpeed) || maximumSpeed < 0f) throw new ArgumentOutOfRangeException(nameof(maximumSpeed));
        if(!float.IsFinite(elapsedSeconds) || elapsedSeconds < 0f) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        AdaptKeyboardVelocity(maximumSpeed);
        Vector3 focusBefore = Focus;
        float distanceBefore = Distance;
        ApplyQueuedRotation(elapsedSeconds);
        Vector3 focusAfterRotation = Focus;
        if(elapsedSeconds > 0f && !_rotationGestureActive && _pendingRotation == Vector2.Zero && _rotationVelocity != Vector2.Zero)
        {
            float decay = MathF.Exp(-RotationInertiaDamping * elapsedSeconds);
            Vector2 requestedRotation = _rotationVelocity * ((1f - decay) / RotationInertiaDamping);
            Vector2 appliedRotation = NavigationMode == CameraNavigationMode.Observer
                ? ApplyRotationPreservingEye(requestedRotation)
                : ApplyRotation(requestedRotation);
            _rotationVelocity *= decay;
            if(MathF.Abs(appliedRotation.Y - requestedRotation.Y) > 0.000001f)
                _rotationVelocity.Y = 0f;
            if(_rotationVelocity.LengthSquared() <= RotationStopSpeed * RotationStopSpeed)
                _rotationVelocity = Vector2.Zero;
        }

        var forward = NavigationMode == CameraNavigationMode.Observer
            ? ViewDirection
            : Vector3.Normalize(new Vector3(MathF.Cos(Yaw), 0f, MathF.Sin(Yaw)));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 desiredDirection = forward * forwardAxis + right * rightAxis + Vector3.UnitY * verticalAxis;
        bool hasKeyboardInput = desiredDirection.LengthSquared() >= 0.000001f && maximumSpeed > 0f;
        Vector3 desiredVelocity = hasKeyboardInput ? Vector3.Normalize(desiredDirection) * maximumSpeed : Vector3.Zero;
        float keyboardResponse = hasKeyboardInput ? KeyboardAcceleration : KeyboardDamping;
        Focus += IntegrateVelocity(ref _keyboardVelocity, desiredVelocity, keyboardResponse, elapsedSeconds);

        if(!hasKeyboardInput)
        {
            float stopSpeed = MathF.Max(MinimumKeyboardStopSpeed, maximumSpeed * KeyboardStopSpeedFraction);
            if(_keyboardVelocity.LengthSquared() <= stopSpeed * stopSpeed) _keyboardVelocity = Vector3.Zero;
        }

        if(elapsedSeconds > 0f && _pendingPan != Vector3.Zero)
        {
            float blend = 1f - MathF.Exp(-PanDamping * elapsedSeconds);
            Vector3 panDelta = _pendingPan * blend;
            _pendingPan -= panDelta;
            if(_pendingPan.LengthSquared() <= PanStopDistance * PanStopDistance)
            {
                panDelta += _pendingPan;
                _pendingPan = Vector3.Zero;
            }
            Focus += panDelta;
        }

        if(elapsedSeconds > 0f && Distance != _zoomTargetDistance)
        {
            float blend = 1f - MathF.Exp(-ZoomDamping * elapsedSeconds);
            Distance += (_zoomTargetDistance - Distance) * blend;
            if(MathF.Abs(_zoomTargetDistance - Distance) <= ZoomStopDistance) Distance = _zoomTargetDistance;
        }

        bool translationChanged = Focus != focusAfterRotation;
        bool translationActive = hasKeyboardInput || _keyboardVelocity != Vector3.Zero || _pendingPan != Vector3.Zero;
        bool zoomActive = Distance != _zoomTargetDistance;
        bool rotationActive = _pendingRotation != Vector2.Zero || (!_rotationGestureActive && _rotationVelocity != Vector2.Zero);
        return new OrbitCameraMotion(
            Focus != focusBefore,
            Distance != distanceBefore,
            translationActive,
            translationActive || zoomActive || rotationActive)
        {
            TranslationChanged = translationChanged,
        };
    }

    public void StopMotion()
    {
        _keyboardVelocity = Vector3.Zero;
        CancelRotationGesture();
        _pendingPan = Vector3.Zero;
        _zoomTargetDistance = Distance;
    }

    /// <summary>Stops mouse orbit and pan damping without changing keyboard movement or the zoom target.</summary>
    public void StopPointerMotion()
    {
        CancelRotationGesture();
        _pendingPan = Vector3.Zero;
    }

    public Matrix4x4 CreateViewProjection(float viewportWidth, float viewportHeight)
    {
        if(!float.IsFinite(viewportWidth) || viewportWidth <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        }
        if(!float.IsFinite(viewportHeight) || viewportHeight <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        }

        Matrix4x4 view = Matrix4x4.CreateLookAt(EyePosition, Focus, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            _verticalFieldOfView,
            viewportWidth / viewportHeight,
            0.1f,
            2048f);
        return view * projection;
    }

    private static void ValidateDistance(float distance, string parameterName)
    {
        if(!float.IsFinite(distance) || distance is < MinimumDistance or > MaximumDistance)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private Vector3 CalculatePanDisplacement(Vector2 pointerDelta, float viewportHeight)
    {
        ValidateFinite(pointerDelta, nameof(pointerDelta));
        if(!float.IsFinite(viewportHeight) || viewportHeight <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight));
        }

        Vector3 forward = ViewDirection;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        float worldUnitsPerDip = 2f * Distance * MathF.Tan(_verticalFieldOfView * 0.5f) / viewportHeight;
        return (-right * pointerDelta.X + up * pointerDelta.Y) * worldUnitsPerDip;
    }

    private static Vector3 IntegrateVelocity(ref Vector3 velocity, Vector3 targetVelocity, float response, float elapsedSeconds)
    {
        if(elapsedSeconds == 0f) return Vector3.Zero;
        float decay = MathF.Exp(-response * elapsedSeconds);
        Vector3 difference = velocity - targetVelocity;
        Vector3 displacement = targetVelocity * elapsedSeconds + difference * ((1f - decay) / response);
        velocity = targetVelocity + difference * decay;
        return displacement;
    }

    private void AdaptKeyboardVelocity(float maximumSpeed)
    {
        if(_lastKeyboardMaximumSpeed is float previousSpeed && previousSpeed != maximumSpeed)
        {
            _keyboardVelocity = previousSpeed > 0f && maximumSpeed > 0f
                ? _keyboardVelocity * (maximumSpeed / previousSpeed)
                : Vector3.Zero;
        }
        _lastKeyboardMaximumSpeed = maximumSpeed;
    }

    private Vector2 ApplyRotation(Vector2 angularDelta)
    {
        float previousPitch = Pitch;
        Yaw += angularDelta.X;
        Pitch = Math.Clamp(Pitch + angularDelta.Y, MinimumPitch, MaximumPitch);
        return new Vector2(angularDelta.X, Pitch - previousPitch);
    }

    private Vector2 ApplyRotationPreservingEye(Vector2 angularDelta)
    {
        Vector3 eye = EyePosition;
        Vector2 applied = ApplyRotation(angularDelta);
        Focus = eye + ViewDirection * Distance;
        return applied;
    }

    private void ApplyQueuedRotation(float elapsedSeconds)
    {
        if(elapsedSeconds <= 0f || _pendingRotation == Vector2.Zero) return;
        // Minecraft-style mouse look consumes the complete relative input for this frame. Orbit dragging keeps
        // its deliberately soft web-preview damping, but applying that damping to observer look feels resistant.
        float blend = NavigationMode == CameraNavigationMode.Observer
            ? 1f
            : 1f - MathF.Exp(-OrbitPointerRotationResponse * elapsedSeconds);
        Vector2 requested = _pendingRotation * blend;
        Vector2 applied = NavigationMode == CameraNavigationMode.Observer
            ? ApplyRotationPreservingEye(requested)
            : ApplyRotation(requested);
        _pendingRotation.X -= requested.X;
        _pendingRotation.Y -= applied.Y;
        if(MathF.Abs(applied.Y - requested.Y) > 0.000001f) _pendingRotation.Y = 0f;
        if(_pendingRotation.LengthSquared() <= RotationStopRadians * RotationStopRadians)
            _pendingRotation = Vector2.Zero;
    }

    private void RecordRotationVelocity(Vector2 appliedRotation, float elapsedSeconds)
    {
        if(elapsedSeconds <= 0f) return;
        Vector2 sampleVelocity = ClampMagnitude(appliedRotation / elapsedSeconds, MaximumRotationVelocity);
        float blend = _hasRotationSample
            ? 1f - MathF.Exp(-RotationVelocityResponse * elapsedSeconds)
            : 1f;
        _rotationVelocity = Vector2.Lerp(_rotationVelocity, sampleVelocity, blend);
        _hasRotationSample = true;
    }

    private static Vector2 ClampMagnitude(Vector2 value, float maximumLength)
    {
        float lengthSquared = value.LengthSquared();
        float maximumSquared = maximumLength * maximumLength;
        return lengthSquared > maximumSquared
            ? value * (maximumLength / MathF.Sqrt(lengthSquared))
            : value;
    }

    private static Vector3 ClampMagnitude(Vector3 value, float maximumLength)
    {
        float lengthSquared = value.LengthSquared();
        float maximumSquared = maximumLength * maximumLength;
        return lengthSquared > maximumSquared
            ? value * (maximumLength / MathF.Sqrt(lengthSquared))
            : value;
    }

    private static void ValidateFinite(Vector2 value, string parameterName)
    {
        if(!float.IsFinite(value.X) || !float.IsFinite(value.Y))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateFinite(Vector3 value, string parameterName)
    {
        if(!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

public enum CameraNavigationMode
{
    Orbit,
    Observer,
}

public readonly record struct OrbitCameraMotion(
    bool TargetChanged,
    bool DistanceChanged,
    bool TranslationActive,
    bool IsActive)
{
    public bool TranslationChanged { get; init; }
}
