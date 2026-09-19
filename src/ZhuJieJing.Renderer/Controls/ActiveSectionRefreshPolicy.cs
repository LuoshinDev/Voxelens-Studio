using System.Numerics;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Rate-limits expensive active-Section reselection while preserving an immediate final refresh.</summary>
internal sealed class ActiveSectionRefreshPolicy
{
    internal const float MovementThreshold = 16f;
    internal const double MinimumRefreshIntervalSeconds = 1d / 12d;

    private Vector3 _anchor;
    private bool _hasAnchor;
    private bool _thresholdRefreshDeferred;
    private bool _finalRefreshNeeded;
    private double _lastRefreshSeconds = double.NegativeInfinity;

    public bool RequestForMovement(Vector3 position, double nowSeconds)
    {
        Validate(position, nowSeconds);
        float distanceSquared = _hasAnchor ? Vector3.DistanceSquared(position, _anchor) : float.PositiveInfinity;
        _finalRefreshNeeded = distanceSquared > 0.000001f;
        if(distanceSquared < MovementThreshold * MovementThreshold) return false;
        if(nowSeconds - _lastRefreshSeconds >= MinimumRefreshIntervalSeconds) return true;
        _thresholdRefreshDeferred = true;
        return false;
    }

    public bool ReleaseDeferred(double nowSeconds, bool translationActive)
    {
        if(!double.IsFinite(nowSeconds)) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
        if(translationActive)
        {
            if(!_thresholdRefreshDeferred || nowSeconds - _lastRefreshSeconds < MinimumRefreshIntervalSeconds) return false;
        }
        else if(!_finalRefreshNeeded && !_thresholdRefreshDeferred)
        {
            return false;
        }
        _thresholdRefreshDeferred = false;
        _finalRefreshNeeded = false;
        return true;
    }

    public void MarkRefreshed(Vector3 position, double nowSeconds)
    {
        Validate(position, nowSeconds);
        _anchor = position;
        _hasAnchor = true;
        _thresholdRefreshDeferred = false;
        _finalRefreshNeeded = false;
        _lastRefreshSeconds = nowSeconds;
    }

    public void Reset()
    {
        _hasAnchor = false;
        _thresholdRefreshDeferred = false;
        _finalRefreshNeeded = false;
        _lastRefreshSeconds = double.NegativeInfinity;
    }

    private static void Validate(Vector3 position, double nowSeconds)
    {
        if(!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
            throw new ArgumentOutOfRangeException(nameof(position));
        if(!double.IsFinite(nowSeconds)) throw new ArgumentOutOfRangeException(nameof(nowSeconds));
    }
}
