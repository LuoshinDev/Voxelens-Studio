using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZhuJieJing.Renderer.Minecraft;

public sealed record MinecraftTextureAnimationUpdate(int X, int Y, int Size, byte[] BgraPixels);

internal sealed class TextureAnimation(int size, byte[][] frames, int[] sequence, int[] durations, bool interpolate)
{
    private long _lastTick = -1;
    private int _lastSequenceStep = -1;
    internal int Size => size;

    internal byte[]? FrameAt(double seconds)
    {
        long tick = (long)Math.Floor(Math.Max(0, seconds) * 20d);
        if(tick == _lastTick) return null;
        _lastTick = tick;
        long cursor = tick % durations.Sum();
        int step = 0;
        while(cursor >= durations[step]) { cursor -= durations[step]; step++; }
        if(!interpolate && step == _lastSequenceStep) return null;
        _lastSequenceStep = step;
        byte[] current = frames[sequence[step]];
        if(!interpolate || cursor == 0) return current;
        byte[] next = frames[sequence[(step + 1) % sequence.Length]];
        float blend = cursor / (float)durations[step];
        byte[] result = new byte[current.Length];
        for(int index = 0; index < result.Length; index++) result[index] = (byte)Math.Clamp((int)MathF.Round(current[index] + (next[index] - current[index]) * blend), 0, 255);
        return result;
    }

    internal static TextureAnimation? Load(Stream png, Func<Stream>? openMetadata)
    {
        if(openMetadata is null) return null;
        try
        {
            using Stream metadata = openMetadata();
            using JsonDocument document = JsonDocument.Parse(metadata);
            if(!document.RootElement.TryGetProperty("animation", out JsonElement animation)) return null;
            using var buffered = new MemoryStream(); png.CopyTo(buffered); buffered.Position = 0;
            BitmapSource source = BitmapDecoder.Create(buffered, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if(source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int size = source.PixelWidth;
            int frameCount = source.PixelHeight / size;
            if(frameCount <= 1) return null;
            int stride = size * 4;
            byte[] allPixels = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(allPixels, stride, 0);
            byte[][] frames = Enumerable.Range(0, frameCount).Select(index => allPixels.AsSpan(index * size * stride, size * stride).ToArray()).ToArray();
            int defaultDuration = animation.TryGetProperty("frametime", out JsonElement time) && time.TryGetInt32(out int specified) ? Math.Clamp(specified, 1, 12000) : 1;
            var sequence = new List<int>(); var durations = new List<int>();
            if(animation.TryGetProperty("frames", out JsonElement declaredFrames) && declaredFrames.ValueKind == JsonValueKind.Array)
            {
                foreach(JsonElement frame in declaredFrames.EnumerateArray())
                {
                    int index, duration = defaultDuration;
                    if(frame.ValueKind == JsonValueKind.Number && frame.TryGetInt32(out index)) { }
                    else if(frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("index", out JsonElement position) && position.TryGetInt32(out index))
                    {
                        if(frame.TryGetProperty("time", out JsonElement frameTime) && frameTime.TryGetInt32(out int ownTime)) duration = Math.Clamp(ownTime, 1, 12000);
                    }
                    else continue;
                    if(index < 0 || index >= frameCount) continue;
                    sequence.Add(index); durations.Add(duration);
                }
            }
            if(sequence.Count == 0) { sequence.AddRange(Enumerable.Range(0, frameCount)); durations.AddRange(Enumerable.Repeat(defaultDuration, frameCount)); }
            bool interpolate = animation.TryGetProperty("interpolate", out JsonElement interpolation) && interpolation.ValueKind == JsonValueKind.True;
            return new TextureAnimation(size, frames, sequence.ToArray(), durations.ToArray(), interpolate);
        }
        catch(Exception exception) when(exception is IOException or JsonException or NotSupportedException or FormatException)
        {
            return null;
        }
    }
}
