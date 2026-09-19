using System.Globalization;
using System.IO;
using System.Text;

namespace ZhuJieJing.App;

public partial class MainWindow
{
    private static void WriteDetailedDebugSummary(string path)
    {
        var frames = new Dictionary<string, DebugFrameDetails>();
        foreach(string row in File.ReadLines(path).Skip(1))
        {
            string[] cells = row.Split(',');
            if(cells.Length < 31) continue;
            if(!frames.TryGetValue(cells[1], out var frame)) frames[cells[1]] = frame = new();
            static double Number(string value) => double.TryParse(value, CultureInfo.InvariantCulture, out double parsed) ? parsed : double.NaN;
            if(cells[2] == "present" && cells[3] == "image_unlock")
            {
                frame.Interval = Number(cells[17]);
                frame.Utc = cells[0];
            }
            if(cells[2] != "stage") continue;
            frame.Action = cells[18] == "0" ? "后台" : cells[19] == "1" && cells[20] == "1" ? "移动并转向" : cells[19] == "1" ? "移动" : cells[20] == "1" ? "转向" : cells[21] == "1" ? "缩放" : "静止";
            frame.Pending = cells[30];
            frame.Size = cells[6] + "×" + cells[7];
            double gpu = Number(cells[5]);
            if(double.IsFinite(gpu)) frame.Gpu += gpu;
            if(gpu > frame.WorstGpu) { frame.WorstGpu = gpu; frame.WorstStage = cells[3]; }
            frame.Cpu += Number(cells[4]);
        }
        var report = new StringBuilder("# 操作与慢帧分析\n\n");
        report.AppendLine("动作根据相邻采样帧的相机变化判定，包含惯性，不等同于按键状态。GPU 忙碌时可能跳过采样；加载任务、GC 和内存请按 UTC 对照同目录的时间线，不能仅凭相关性判断原因。\n");
        report.AppendLine("| 操作 | 帧数 | 平均 FPS | 帧间隔 P95 ms |\n|---|---:|---:|---:|");
        foreach(var group in frames.Values.Where(f => f.Interval > 0).GroupBy(f => f.Action))
        {
            double[] intervals = group.Select(f => f.Interval).Order().ToArray();
            report.AppendLine($"| {group.Key} | {intervals.Length} | {1000 / intervals.Average():F1} | {intervals[(int)Math.Ceiling((intervals.Length - 1) * .95)]:F2} |");
        }
        report.AppendLine("\n| 慢帧 ID | 提交 UTC | 动作 | 帧间隔 ms | CPU 阶段合计 ms | GPU 阶段合计 ms | 最慢 GPU 阶段 | 该阶段 ms | 待提交候选 Section | 内部分辨率 |\n|---|---|---|---:|---:|---:|---|---:|---:|---|");
        foreach(var (id, frame) in frames.OrderByDescending(f => f.Value.Interval).Take(30))
            report.AppendLine($"| {id} | {frame.Utc} | {frame.Action} | {frame.Interval:F2} | {frame.Cpu:F2} | {frame.Gpu:F2} | {frame.WorstStage} | {frame.WorstGpu:F2} | {frame.Pending} | {frame.Size} |");
        report.AppendLine("\nCPU 阶段合计不含呈现、WPF 调度或未埋点的后台任务。GPU 合计为已采样阶段，不是端到端延迟。候选 Section 包含已缓存项，并非实际上传数。时间线是 UI 线程尽力每 100 ms 采样，UI 卡顿会拉长间隔。\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "操作与慢帧分析.md"), report.ToString(), Encoding.UTF8);
    }

    private sealed class DebugFrameDetails
    {
        public string Action = "未采样", Utc = "", Pending = "", Size = "", WorstStage = "";
        public double Interval, Cpu, Gpu, WorstGpu;
    }
}
