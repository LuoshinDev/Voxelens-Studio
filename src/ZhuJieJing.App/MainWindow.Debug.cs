using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private static readonly string DebugReportsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private DispatcherTimer? debugRecordingTimer;
    private string? debugRecordingPath;
    private bool debugRecordingStopping;
    private bool closingAfterDebugRecording;
    private Task? debugStopTask;
    private readonly Stopwatch debugRecordingClock = new();
    private readonly List<object> debugTimeline = [];

    private void OpenDebugLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DebugReportsDirectory);
            // Pass a shell item identity rather than a command-line path to Explorer.
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SHParseDisplayName(
                Path.GetFullPath(DebugReportsDirectory), IntPtr.Zero, out IntPtr folder, 0, out _));
            try
            {
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(folder, 0, IntPtr.Zero, 0));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(folder); }
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            UiText.Bind(StatusText, "Text", UiText.Message($"无法打开日志文件夹：{exception.Message}"));
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr item, uint requestedAttributes, out uint attributes);

    [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr items, uint flags);

    protected override void OnDeactivated(EventArgs e)
    {
        Viewport?.SuspendNavigationInput();
        base.OnDeactivated(e);
    }

    private async void DebugRecordingToggle_Click(object sender, RoutedEventArgs e)
    {
        if(DebugRecordingToggle.IsChecked != true)
        {
            await FinishDebugRecordingAsync();
            return;
        }
        try
        {
            string directory = Path.Combine(DebugReportsDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            debugRecordingPath = Viewport.StartDebugRecording(directory);
            debugTimeline.Clear();
            debugRecordingClock.Restart();
            debugRecordingTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, DebugRecordingTick, Dispatcher);
            debugRecordingTimer.Start();
            DebugRecordingStatus.Visibility = Visibility.Collapsed;
            DebugRecordingToggle.Content = "Debug · 30s";
            UiText.Bind(StatusText, "Text", UiText.Text("Debug 已开启，请正常移动、转动视角；30 秒后自动保存。"));
        }
        catch(Exception exception)
        {
            DebugRecordingToggle.IsChecked = false;
            DebugRecordingToggle.Content = "Debug";
            DebugRecordingStatus.Visibility = Visibility.Visible;
            UiText.Bind(DebugRecordingStatus, "Text", UiText.Message($"无法开始记录：{exception.Message}"));
        }
    }

    private async void DebugRecordingTick(object? sender, EventArgs e)
    {
        int remaining = Math.Max(0, 30 - (int)debugRecordingClock.Elapsed.TotalSeconds);
        DebugRecordingToggle.Content = $"Debug · {remaining}s";
        debugTimeline.Add(new
        {
            utc = DateTime.UtcNow, elapsedMs = debugRecordingClock.Elapsed.TotalMilliseconds,
            windowActive = IsActive,
            worldLoading = activeWorldPreviewTask is { IsCompleted: false },
            navigationLoading = activeNavigationPreviewTask is { IsCompleted: false },
            conversionLoading = activeConversionPreviewTask is { IsCompleted: false },
            displayedSections = displayedWorldSections.Count,
            loadedChunks = displayedWorldChunkStatistics.Count,
            managedBytes = GC.GetTotalMemory(false),
            gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2),
        });
        if(remaining == 0) await FinishDebugRecordingAsync();
    }

    private Task FinishDebugRecordingAsync() => debugStopTask is { IsCompleted: false } ? debugStopTask : debugStopTask = StopDebugRecordingCoreAsync();

    private async Task StopDebugRecordingCoreAsync()
    {
        if(debugRecordingPath is not { } path) return;
        debugRecordingStopping = true;
        debugRecordingTimer?.Stop();
        DebugRecordingToggle.IsChecked = false;
        DebugRecordingToggle.IsEnabled = false;
        UiText.Bind(DebugRecordingToggle, "Content", UiText.Text("Debug · 保存中"));
        try
        {
            await Viewport.StopDebugRecording();
            object[] timeline = debugTimeline.ToArray();
            await Task.Run(() =>
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "加载与内存时间线.json"), System.Text.Json.JsonSerializer.Serialize(timeline));
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "运行环境.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                    os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                    logicalProcessors = Environment.ProcessorCount,
                    sampleIntervalMs = 100, rendererFormat = 2,
                }));
                WriteDebugSummary(path);
                WriteDetailedDebugSummary(path);
            });
            UiText.Bind(DebugRecordingStatus, "Text", UiText.Text("已保存"));
            DebugRecordingStatus.Visibility = Visibility.Visible;
            UiText.Bind(StatusText, "Text", UiText.Message($"性能报告已保存：{Path.GetDirectoryName(path)}"));
        }
        catch(Exception exception)
        {
            UiText.Bind(DebugRecordingStatus, "Text", UiText.Message($"报告保存失败：{exception.Message}"));
            DebugRecordingStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            debugRecordingPath = null;
            debugRecordingStopping = false;
            DebugRecordingToggle.IsEnabled = true;
            DebugRecordingToggle.Content = "Debug";
            debugTimeline.Clear();
        }
    }

    private async void FinishDebugRecordingAndClose()
    {
        if(closingAfterDebugRecording) return;
        closingAfterDebugRecording = true;
        await FinishDebugRecordingAsync();
        Close();
    }

    private static void WriteDebugSummary(string path)
    {
        var intervals = new List<double>();
        var stages = new Dictionary<string, (List<double> Cpu, List<double> Gpu)>();
        foreach(string line in File.ReadLines(path).Skip(1))
        {
            string[] cells = line.Split(',');
            if(cells.Length < 18) continue;
            if(cells[2] == "counter") continue;
            double Number(int index) => double.TryParse(cells[index], CultureInfo.InvariantCulture, out double value) ? value : double.NaN;
            if(cells[2] == "present" && cells[3] == "image_unlock" && Number(17) > 0) intervals.Add(Number(17));
            string name = $"{cells[2]}/{cells[3]}";
            if(!stages.TryGetValue(name, out var values)) stages[name] = values = ([], []);
            if(double.IsFinite(Number(4))) values.Cpu.Add(Number(4));
            if(double.IsFinite(Number(5))) values.Gpu.Add(Number(5));
        }
        static double Percentile(List<double> values, double percentile) => values.Count == 0 ? double.NaN : values.Order().ElementAt((int)Math.Ceiling((values.Count - 1) * percentile));
        var report = new StringBuilder("# Studio 性能记录\n\n");
        report.AppendLine("scheduling 行的 CPU 列是提交到发现 GPU 完成的墙钟时间，包含 GPU 执行与调度等待，不是 CPU 忙碌时间；CSV 的 counter 行记录未完成查询次数，单位为次。\n");
        report.AppendLine($"完成帧间隔样本：{intervals.Count}；平均 FPS：{(intervals.Count == 0 ? double.NaN : 1000 / intervals.Average()):F1}");
        report.AppendLine($"帧间隔 P50 / P95 / P99：{Percentile(intervals, .5):F2} / {Percentile(intervals, .95):F2} / {Percentile(intervals, .99):F2} ms。144 FPS 对应约 6.94 ms。\n");
        report.AppendLine("| 阶段 | CPU 平均 ms | CPU P95 ms | GPU 平均 ms | GPU P95 ms | 样本数 CPU/GPU |\n|---|---:|---:|---:|---:|---:|");
        foreach(var (name, values) in stages)
            report.AppendLine($"| {name} | {(values.Cpu.Count == 0 ? double.NaN : values.Cpu.Average()):F3} | {Percentile(values.Cpu, .95):F3} | {(values.Gpu.Count == 0 ? double.NaN : values.Gpu.Average()):F3} | {Percentile(values.Gpu, .95):F3} | {values.Cpu.Count}/{values.Gpu.Count} |");
        report.AppendLine("\nCPU 与 GPU 分开计时；cpu 子阶段可能包含在 stage 中，不能重复相加。NaN 表示无有效样本。帧间隔来自应用提交完成，并非显示器扫描时间。GPU 使用非阻塞有限采样，繁忙时可能跳过，停止时尾部查询不等待。记录本身有少量开销；请保持地图、窗口尺寸和画质相同进行对比。CSV 含逐帧分辨率、画质、绘制次数及反射平面数，不含地图内容。\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "性能摘要.md"), report.ToString(), Encoding.UTF8);
    }
}
