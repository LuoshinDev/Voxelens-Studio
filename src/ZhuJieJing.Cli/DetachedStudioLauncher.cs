using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZhuJieJing.Cli;

internal static class DetachedStudioLauncher
{
    private const string CommandLineEnvironmentVariable = "ZJJ_DETACHED_STUDIO_COMMAND_LINE";
    private const string WorkingDirectoryEnvironmentVariable = "ZJJ_DETACHED_STUDIO_WORKING_DIRECTORY";
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(15);
    private const string LaunchScript = """
        $ErrorActionPreference = 'Stop'
        $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
            CommandLine = $env:ZJJ_DETACHED_STUDIO_COMMAND_LINE
            CurrentDirectory = $env:ZJJ_DETACHED_STUDIO_WORKING_DIRECTORY
        }
        if ([int]$result.ReturnValue -ne 0) {
            throw "Win32_Process.Create returned $($result.ReturnValue)."
        }
        [Console]::Out.Write([string]$result.ProcessId)
        """;

    public static async Task<int> StartAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if(!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("筑界镜 Studio 只能在 Windows 桌面环境中启动。");

        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string powershellPath = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if(!File.Exists(powershellPath))
            throw new FileNotFoundException("找不到 Windows PowerShell，无法独立启动筑界镜 Studio。", powershellPath);

        ProcessStartInfo startInfo = new(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(LaunchScript);
        startInfo.Environment[CommandLineEnvironmentVariable] =
            BuildCommandLine(executablePath, arguments);
        startInfo.Environment[WorkingDirectoryEnvironmentVariable] = workingDirectory;

        using Process launcher = Process.Start(startInfo) ??
                                 throw new InvalidOperationException("无法启动 Windows WMI 进程创建器。");
        Task<string> outputTask = launcher.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = launcher.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(LaunchTimeout);
        try
        {
            await launcher.WaitForExitAsync(timeout.Token);
        }
        catch(OperationCanceledException)
        {
            if(!launcher.HasExited) launcher.Kill(entireProcessTree: true);
            throw new InvalidOperationException("筑界镜 Studio 独立启动超时。");
        }
        string output = (await outputTask).Trim();
        string error = (await errorTask).Trim();
        if(launcher.ExitCode != 0 || !int.TryParse(output, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
            throw new InvalidOperationException("筑界镜 Studio 独立启动失败。" +
                                                (string.IsNullOrWhiteSpace(error) ? string.Empty : $" {error}"));
        return processId;
    }

    private static string BuildCommandLine(string executablePath, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executablePath }.Concat(arguments).Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if(argument.Length > 0 && argument.All(static character =>
               !char.IsWhiteSpace(character) && character != '"')) return argument;

        var result = new StringBuilder(argument.Length + 2);
        result.Append('"');
        int backslashCount = 0;
        foreach(char character in argument)
        {
            if(character == '\\')
            {
                backslashCount++;
                continue;
            }
            if(character == '"')
            {
                result.Append('\\', backslashCount * 2 + 1);
                result.Append('"');
                backslashCount = 0;
                continue;
            }
            result.Append('\\', backslashCount);
            backslashCount = 0;
            result.Append(character);
        }
        result.Append('\\', backslashCount * 2);
        result.Append('"');
        return result.ToString();
    }
}
