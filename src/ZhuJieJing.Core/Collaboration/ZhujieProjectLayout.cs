namespace ZhuJieJing.Core.Collaboration;

public sealed class ZhujieProjectLayout
{
    public static string DefaultProjectsRoot
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("ZJJ_PROJECTS_ROOT");
            if(!string.IsNullOrWhiteSpace(configured))
            {
                if(!Path.IsPathFullyQualified(configured))
                    throw new ArgumentException("ZJJ_PROJECTS_ROOT must be an absolute directory path.");
                return Path.GetFullPath(configured);
            }

            // Keep existing installations on their original workspace; new users do not need a D: drive.
            const string legacyRoot = @"D:\筑界镜工程";
            if(OperatingSystem.IsWindows() && Directory.Exists(legacyRoot)) return legacyRoot;
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if(string.IsNullOrWhiteSpace(documents))
                documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
            return Path.Combine(documents, "Voxelens Studio", "Projects");
        }
    }
    public const string ManifestRelativePath = "project.json";
    public const string ProjectBriefRelativePath = "PROJECT.md";
    public const string AgentsRelativePath = "agents.md";
    public const string SceneStoreRelativePath = "live/scene.zjjscene";
    public const string ArchitectStatusRelativePath = "live/architect-status.json";
    public const string InstructionsRelativePath = "instructions";
    public const string AcknowledgementsRelativePath = "acknowledgements";
    public const string BatchesRelativePath = "batches";
    public const string ModulesRelativePath = "modules";
    public const string ReceiptsRelativePath = ".zhujie/receipts";
    public const string StagingRelativePath = ".zhujie/staging";

    /// <summary>
    /// Reserved for optional desktop-private state. It is not a required or interoperable MVP document.
    /// </summary>
    public const string AppStateRelativePath = ".zhujie/app-state.json";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private ZhujieProjectLayout(string projectsRoot, string projectDirectory)
    {
        ProjectsRoot = projectsRoot;
        ProjectDirectory = projectDirectory;
    }

    public string ProjectsRoot { get; }

    public string ProjectDirectory { get; }

    public string ManifestPath => ResolveProjectPath(ManifestRelativePath);

    public string ProjectBriefPath => ResolveProjectPath(ProjectBriefRelativePath);

    public string AgentsPath => ResolveProjectPath(AgentsRelativePath);

    public string SceneStorePath => ResolveProjectPath(SceneStoreRelativePath);

    public string ArchitectStatusPath => ResolveProjectPath(ArchitectStatusRelativePath);

    public string InstructionsDirectory => ResolveProjectPath(InstructionsRelativePath);

    public string AcknowledgementsDirectory => ResolveProjectPath(AcknowledgementsRelativePath);

    public string BatchesDirectory => ResolveProjectPath(BatchesRelativePath);

    public string ModulesDirectory => ResolveProjectPath(ModulesRelativePath);

    public string ReceiptsDirectory => ResolveProjectPath(ReceiptsRelativePath);

    public string StagingDirectory => ResolveProjectPath(StagingRelativePath);

    /// <summary>
    /// Gets the reserved location for optional desktop-private state. AI architects must not depend on it.
    /// </summary>
    public string AppStatePath => ResolveProjectPath(AppStateRelativePath);

    public static ZhujieProjectLayout Resolve(string projectPath, string? projectsRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        string root = Path.GetFullPath(string.IsNullOrWhiteSpace(projectsRoot) ? DefaultProjectsRoot : projectsRoot);
        string projectDirectory = Path.GetFullPath(Path.IsPathRooted(projectPath)
            ? projectPath
            : Path.Combine(root, projectPath));
        string relative = Path.GetRelativePath(root, projectDirectory);
        if(relative is "." or "" || Path.IsPathRooted(relative) ||
           relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) ||
           relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison) ||
           string.Equals(relative, "..", PathComparison) ||
           relative.Contains(Path.DirectorySeparatorChar) ||
           relative.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException($"筑界镜工程必须是 {root} 下的一级目录。", nameof(projectPath));
        EnsureNoReparsePoint(root, allowMissingLeaf: true);
        EnsureNoReparsePoint(projectDirectory, allowMissingLeaf: true);
        return new ZhujieProjectLayout(root, projectDirectory);
    }

    public string ResolveProjectPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if(Path.IsPathRooted(relativePath))
            throw new ArgumentException("工程协议路径必须是相对路径。", nameof(relativePath));
        if(relativePath.Contains('\\'))
            throw new ArgumentException("工程协议路径必须使用正斜杠。", nameof(relativePath));
        string resolved = Path.GetFullPath(Path.Combine(ProjectDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContained(resolved, nameof(relativePath));
        EnsureNoReparsePoint(resolved, allowMissingLeaf: true);
        return resolved;
    }

    public string ResolveContainedInputPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectDirectory, path));
        EnsureContained(resolved, nameof(path));
        EnsureNoReparsePoint(resolved, allowMissingLeaf: false);
        return resolved;
    }

    public void CreateDirectories()
    {
        Directory.CreateDirectory(ProjectsRoot);
        Directory.CreateDirectory(ProjectDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(SceneStorePath)!);
        Directory.CreateDirectory(InstructionsDirectory);
        Directory.CreateDirectory(AcknowledgementsDirectory);
        Directory.CreateDirectory(BatchesDirectory);
        Directory.CreateDirectory(ModulesDirectory);
        Directory.CreateDirectory(ReceiptsDirectory);
        Directory.CreateDirectory(StagingDirectory);
    }

    public void EnsureDirectoryStructure()
    {
        if(!Directory.Exists(ProjectDirectory)) throw new DirectoryNotFoundException($"工程目录不存在：{ProjectDirectory}");
        if(!File.Exists(ManifestPath)) throw new FileNotFoundException("工程缺少 project.json。", ManifestPath);
        if(!File.Exists(ProjectBriefPath)) throw new FileNotFoundException("工程缺少 PROJECT.md。", ProjectBriefPath);
        if(!File.Exists(AgentsPath)) throw new FileNotFoundException("工程缺少 agents.md。", AgentsPath);
        foreach(string directory in new[]
                {
                    Path.GetDirectoryName(SceneStorePath)!,
                    InstructionsDirectory,
                    AcknowledgementsDirectory,
                    BatchesDirectory,
                    ModulesDirectory,
                    ReceiptsDirectory,
                    StagingDirectory,
                })
        {
            if(!Directory.Exists(directory)) throw new DirectoryNotFoundException($"工程协议目录不存在：{directory}");
        }
    }

    private void EnsureContained(string resolvedPath, string parameterName)
    {
        string relative = Path.GetRelativePath(ProjectDirectory, resolvedPath);
        if(relative is "." or "") return;
        if(Path.IsPathRooted(relative) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison) ||
           relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison) ||
           string.Equals(relative, "..", PathComparison))
            throw new ArgumentException("路径不能逃逸筑界镜工程目录。", parameterName);
    }

    private static void EnsureNoReparsePoint(string path, bool allowMissingLeaf)
    {
        string fullPath = Path.GetFullPath(path);
        string? current = fullPath;
        bool first = true;
        while(!string.IsNullOrEmpty(current))
        {
            if(File.Exists(current) || Directory.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"工程协议不允许经过重解析点：{current}");
            }
            else if(first && !allowMissingLeaf)
            {
                throw new FileNotFoundException("工程输入文件不存在。", fullPath);
            }
            first = false;
            current = Path.GetDirectoryName(current);
        }
    }
}
