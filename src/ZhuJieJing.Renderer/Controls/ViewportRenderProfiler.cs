using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using Vortice.Direct3D11;

namespace ZhuJieJing.Renderer.Controls;

/// <summary>Opt-in, bounded GPU timestamp sampling. No query wait, disk I/O or Flush is added to the UI thread.</summary>
internal sealed class ViewportRenderProfiler : IDisposable
{
    private const int MarkerCount = 32;
    private readonly Queue<Frame> _free = new(), _pending = new();
    private readonly Channel<string> _rows = Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private long _droppedRows, _skippedGpuFrames;
    private int _unfinishedGpuFrames;
    private Frame? _current;
    private long _callbacks, _distinctTimes, _completed, _previousPresent;
    private TimeSpan _lastRenderingTime = TimeSpan.MinValue;
    private ZhuJieJing.Renderer.Camera.OrbitCameraPose? _previousPose;

    internal Task Completion { get; }
    internal string OutputPath { get; }

    internal ViewportRenderProfiler(ID3D11Device device, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"renderer-{Environment.ProcessId}-{Guid.NewGuid():N}.csv");
        OutputPath = path;
        var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        writer.WriteLine("utc,frame,kind,stage,cpu_ms,gpu_ms,width,height,sections,reflection_planes,enhanced,draw_calls,triangles,opaque_copies,callback_count,distinct_render_times,completed_count,frame_interval_ms,window_active,moving,turning,zooming,focus_x,focus_y,focus_z,yaw,pitch,distance,fov_radians,navigation_mode,pending_section_candidates");
        writer.Flush();
        Completion = Task.Run(async () =>
        {
            using(writer)
            {
                try
                {
                    int count = 0;
                    await foreach(string row in _rows.Reader.ReadAllAsync())
                    {
                        await writer.WriteLineAsync(row);
                        if(++count % 16 == 0) await writer.FlushAsync();
                    }
                }
                catch(IOException exception) { Debug.WriteLine($"Renderer profiling stopped: {exception.Message}"); throw; }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "采样完整性.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                droppedRows = _droppedRows, skippedGpuFrames = _skippedGpuFrames,
                unfinishedGpuFramesAtStop = _unfinishedGpuFrames,
            }));
        });
        for(int index = 0; index < 4; index++) _free.Enqueue(new Frame(device));
    }

    internal static ViewportRenderProfiler? Create(ID3D11Device device)
    {
        string? directory = Environment.GetEnvironmentVariable("ZJJ_RENDER_PROFILE_DIRECTORY");
        if(string.IsNullOrWhiteSpace(directory)) return null;
        try { return new ViewportRenderProfiler(device, directory); }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or ArgumentException)
        { Debug.WriteLine($"Cannot create renderer profile: {exception.Message}"); return null; }
    }

    internal void Callback(TimeSpan renderingTime)
    {
        _callbacks++;
        if(_lastRenderingTime != renderingTime) { _distinctTimes++; _lastRenderingTime = renderingTime; }
    }

    internal void BeginFrame(ID3D11DeviceContext context, long frame, int width, int height)
    {
        Collect(context);
        if(!_free.TryDequeue(out _current)) { _skippedGpuFrames++; return; }
        _current.Reset(frame, width, height);
        context.Begin(_current.Disjoint);
        context.End(_current.Timestamps[0]);
    }

    internal void Scene(int sections, int reflectionPlanes, bool enhanced)
    {
        if(_current is null) return;
        _current.Sections = sections; _current.Planes = reflectionPlanes; _current.Enhanced = enhanced;
    }

    internal void Draw(int indexCount)
    {
        if(_current is null) return;
        _current.DrawCalls++; _current.Triangles += indexCount / 3;
    }

    internal void CameraContext(ZhuJieJing.Renderer.Camera.OrbitCameraPose pose, bool active, int pendingSections)
    {
        bool moving = _previousPose is { } old && System.Numerics.Vector3.DistanceSquared(old.Focus, pose.Focus) > 0.000001f;
        bool turning = _previousPose is { } previous && (Math.Abs(previous.Yaw - pose.Yaw) > 0.00001f || Math.Abs(previous.Pitch - pose.Pitch) > 0.00001f);
        bool zooming = _previousPose is { } last && Math.Abs(last.Distance - pose.Distance) > 0.0001f;
        _previousPose = pose;
        if(_current is null) return;
        _current.Context = string.Create(CultureInfo.InvariantCulture,
            $"{(active ? 1 : 0)},{(moving ? 1 : 0)},{(turning ? 1 : 0)},{(zooming ? 1 : 0)},{pose.Focus.X:F4},{pose.Focus.Y:F4},{pose.Focus.Z:F4},{pose.Yaw:F5},{pose.Pitch:F5},{pose.Distance:F4},{pose.VerticalFieldOfView:F5},{pose.NavigationMode},{pendingSections}");
    }

    internal void Copy() { if(_current is not null) _current.Copies++; }

    internal void Mark(ID3D11DeviceContext context, string stage)
    {
        if(_current is null || _current.Count >= MarkerCount - 1) return;
        long now = Stopwatch.GetTimestamp();
        int index = ++_current.Count;
        context.End(_current.Timestamps[index]);
        _current.Stages[index] = stage;
        _current.CpuMs[index] = Stopwatch.GetElapsedTime(_current.PreviousCpu, now).TotalMilliseconds;
        _current.PreviousCpu = now;
    }

    internal void Cpu(string stage, long started)
    {
        if(_current is not null) Write(_current, "cpu", stage, Stopwatch.GetElapsedTime(started).TotalMilliseconds, double.NaN, 0);
    }

    internal void EndFrame(ID3D11DeviceContext context)
    {
        if(_current is null) return;
        Mark(context, "finalize");
        context.End(_current.Disjoint);
        _pending.Enqueue(_current); _current = null;
    }

    internal void Presented(long frame, int width, int height, double lockMs, double copyMs, double unlockMs)
    {
        _completed++;
        long now = Stopwatch.GetTimestamp();
        double interval = _previousPresent == 0 ? 0 : Stopwatch.GetElapsedTime(_previousPresent, now).TotalMilliseconds;
        _previousPresent = now;
        var metadata = new Frame(frame, width, height);
        Write(metadata, "present", "image_lock", lockMs, double.NaN, interval);
        Write(metadata, "present", "copy_and_fence", copyMs, double.NaN, interval);
        Write(metadata, "present", "image_unlock", unlockMs, double.NaN, interval);
    }

    internal void SubmissionWait(long frame, int width, int height, double elapsedMs, int misses)
    {
        var metadata = new Frame(frame, width, height);
        Write(metadata, "scheduling", "submit_to_ready_observed_ms", elapsedMs, double.NaN, 0);
        Write(metadata, "counter", "gpu_poll_not_ready_count", misses, double.NaN, 0);
    }

    private void Collect(ID3D11DeviceContext context)
    {
        while(_pending.TryPeek(out Frame? frame))
        {
            if(!context.GetData(frame.Disjoint, AsyncGetDataFlags.DoNotFlush, out QueryDataTimestampDisjoint clock) ||
               !context.GetData(frame.Timestamps[frame.Count], AsyncGetDataFlags.DoNotFlush, out ulong _)) return;
            _pending.Dequeue();
            ulong previous = 0;
            context.GetData(frame.Timestamps[0], AsyncGetDataFlags.DoNotFlush, out previous);
            for(int index = 1; index <= frame.Count; index++)
            {
                context.GetData(frame.Timestamps[index], AsyncGetDataFlags.DoNotFlush, out ulong next);
                double milliseconds = !clock.Disjoint && clock.Frequency != 0 ? (next - previous) * 1000d / clock.Frequency : double.NaN;
                Write(frame, "stage", frame.Stages[index], frame.CpuMs[index], milliseconds, 0);
                previous = next;
            }
            _free.Enqueue(frame);
        }
    }

    private void Write(Frame frame, string kind, string stage, double cpu, double gpu, double interval)
    {
        // TryWrite never blocks the render thread; report losses instead of silently dropping them.
        if(!_rows.Writer.TryWrite(string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O},{frame.Id},{kind},{stage},{cpu:F4},{gpu:F4},{frame.Width},{frame.Height},{frame.Sections},{frame.Planes},{(frame.Enhanced ? 1 : 0)},{frame.DrawCalls},{frame.Triangles},{frame.Copies},{_callbacks},{_distinctTimes},{_completed},{interval:F4},{frame.Context}"))) _droppedRows++;
    }

    public void Dispose()
    {
        _unfinishedGpuFrames = _pending.Count + (_current is null ? 0 : 1);
        _current?.Dispose(); _current = null;
        foreach(Frame frame in _free) frame.Dispose(); _free.Clear();
        foreach(Frame frame in _pending) frame.Dispose(); _pending.Clear();
        _rows.Writer.TryComplete();
    }

    private sealed class Frame : IDisposable
    {
        internal readonly ID3D11Query Disjoint = null!;
        internal readonly ID3D11Query[] Timestamps = [];
        internal readonly string[] Stages = new string[MarkerCount];
        internal readonly double[] CpuMs = new double[MarkerCount];
        internal long Id, PreviousCpu, Triangles;
        internal int Width, Height, Count, Sections, Planes, DrawCalls, Copies;
        internal bool Enhanced;
        internal string Context = ",,,,,,,,,,,,";
        internal Frame(ID3D11Device device)
        {
            Disjoint = device.CreateQuery(QueryType.TimestampDisjoint);
            Timestamps = Enumerable.Range(0, MarkerCount).Select(_ => device.CreateQuery(QueryType.Timestamp)).ToArray();
        }
        internal Frame(long id, int width, int height) => Reset(id, width, height);
        internal void Reset(long id, int width, int height)
        {
            Id = id; Width = width; Height = height; Count = Sections = Planes = DrawCalls = Copies = 0; Triangles = 0;
            Enhanced = false; PreviousCpu = Stopwatch.GetTimestamp();
            Context = ",,,,,,,,,,,,";
        }
        public void Dispose() { Disjoint.Dispose(); foreach(ID3D11Query query in Timestamps) query.Dispose(); }
    }
}
