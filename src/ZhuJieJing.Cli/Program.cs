using System.Globalization;
using System.Text.Json;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Collaboration;
using ZhuJieJing.Minecraft;
using ZhuJieJing.SceneStore;

namespace ZhuJieJing.Cli;

internal static class Program
{
    private const int MaximumReportedFailures = 100;

    internal const string HelpText = """
        筑界镜 Studio · AI 工具入口

          zjj plan validate <蓝图.zz>
          zjj plan compile  <无基础场景的蓝图.zz>
          zjj plan format   <输入.zz> <输出.zz>
          zjj plan publish  <工程目录> <.zhujie/staging 直系草稿.zz> [--projects-root <根目录>]
          zjj batch list    <工程目录> [--projects-root <根目录>]
          zjj batch revert  <工程目录> <revision> [--projects-root <根目录>]
          zjj project init  <工程目录> [--name <名称>] [--projects-root <根目录>]
                            [--dimension <命名空间 ID>] [--version <Java 版本>]
                            [--data-version <整数>] [--storage-family <存储格式族>]
                            Minecraft Edition 固定为 Java，不支持 --edition。
          zjj project validate <工程目录> [--projects-root <根目录>]
          zjj studio open <工程目录> [--projects-root <根目录>]
          zjj architect status <工程目录> --state <状态> --phase <阶段> --message <说明>
                               --session <会话ID> --agent <AI名称> [--plan-sha <SHA-256>] [--revision <revision>]
          zjj instruction list <工程目录> [--pending]
          zjj instruction ack  <工程目录> <instructionId> --state <状态>
                               --session <会话ID> --message <说明> [--plan-sha <SHA-256>] [--revision <revision>]
          zjj world inspect <Minecraft 世界文件夹>
          zjj world audit   <Minecraft 世界文件夹>
          zjj world analyze <1.12.2 世界文件夹>

        AI 只操作 .zz 和场景命令；请勿直接编辑 Minecraft MCA/NBT。
        """;

    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or PlanValidationException or PlanCompilationException or ZhujieProtocolValidationException or ArgumentException or InvalidOperationException or NotSupportedException or SceneRevisionConflictException or SceneVoxelConflictException)
        {
            WriteJson(new { ok = false, error = exception.Message });
            return 2;
        }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if(args.Length == 0 || args[0] == "help" || args.Any(IsHelpOption))
        {
            WriteHelp();
            return 0;
        }

        _ = RequirePositionalOperand(args, 0, "命令");
        if(args.Length > 1) _ = RequirePositionalOperand(args, 1, "子命令");

        if(args.Length == 3 && string.Equals(args[0], "world", StringComparison.Ordinal) &&
           string.Equals(args[1], "inspect", StringComparison.Ordinal))
        {
            return await InspectWorldAsync(Path.GetFullPath(RequirePositionalOperand(args, 2, "Minecraft 世界文件夹")));
        }

        if(args.Length == 3 && string.Equals(args[0], "world", StringComparison.Ordinal) &&
           string.Equals(args[1], "audit", StringComparison.Ordinal))
        {
            return await AuditWorldAsync(Path.GetFullPath(RequirePositionalOperand(args, 2, "Minecraft 世界文件夹")));
        }

        if(args.Length == 3 && string.Equals(args[0], "world", StringComparison.Ordinal) &&
           string.Equals(args[1], "analyze", StringComparison.Ordinal))
        {
            return await AnalyzeWorldAsync(Path.GetFullPath(RequirePositionalOperand(args, 2, "Minecraft 世界文件夹")));
        }

        if(args.Length >= 3 && string.Equals(args[0], "project", StringComparison.Ordinal))
        {
            return args[1] switch
            {
                "init" => await InitializeProjectAsync(args),
                "validate" => await ValidateProjectAsync(args),
                _ => throw new ArgumentException($"未知命令：project {args[1]}。"),
            };
        }

        if(args.Length >= 3 && string.Equals(args[0], "studio", StringComparison.Ordinal) &&
           string.Equals(args[1], "open", StringComparison.Ordinal))
            return await OpenStudioProjectAsync(args);

        if(args.Length >= 3 && string.Equals(args[0], "architect", StringComparison.Ordinal) &&
           string.Equals(args[1], "status", StringComparison.Ordinal))
            return await WriteArchitectStatusAsync(args);

        if(args.Length >= 3 && string.Equals(args[0], "instruction", StringComparison.Ordinal))
        {
            return args[1] switch
            {
                "list" => await ListInstructionsAsync(args),
                "ack" => await AcknowledgeInstructionAsync(args),
                _ => throw new ArgumentException($"未知命令：instruction {args[1]}。"),
            };
        }

        if(args.Length >= 3 && string.Equals(args[0], "batch", StringComparison.Ordinal))
        {
            return args[1] switch
            {
                "list" => await ListBatchesAsync(args),
                "revert" => await RevertBatchAsync(args),
                _ => throw new ArgumentException($"未知命令：batch {args[1]}。"),
            };
        }

        if(args.Length >= 4 && string.Equals(args[0], "plan", StringComparison.Ordinal) &&
           string.Equals(args[1], "publish", StringComparison.Ordinal))
            return await PublishPlanAsync(args);

        if(args.Length < 3 || !string.Equals(args[0], "plan", StringComparison.Ordinal))
        {
            throw new ArgumentException("命令格式不正确。运行 `zjj help` 查看可用命令。");
        }

        string operation = args[1];
        string inputPath = Path.GetFullPath(RequirePositionalOperand(args, 2, "输入蓝图"));
        string? formatOutputPath = operation == "format" && args.Length == 4
            ? Path.GetFullPath(RequirePositionalOperand(args, 3, "输出蓝图"))
            : null;
        EnsurePlanExtension(inputPath, "输入蓝图");
        ZjjPlan plan = ZjjPlanJson.Deserialize(File.ReadAllText(inputPath));

        return operation switch
        {
            "validate" => Validate(plan, inputPath),
            "compile" => Compile(plan, inputPath),
            "format" when formatOutputPath is not null => Format(plan, formatOutputPath),
            _ => throw new ArgumentException($"未知命令：plan {operation}。")
        };
    }

    private static async Task<int> InspectWorldAsync(string worldDirectory)
    {
        ReadOnlyWorldMountRequest request = new(
            worldDirectory,
            ConcurrentWritePolicy: ConcurrentWorldWritePolicy.RejectActiveWriter,
            IncludeCustomDimensions: true,
            CaptureSourceFingerprints: false);
        await using IReadOnlyMinecraftWorld world = await new AnvilWorldMounter().MountAsync(request);
        List<object> dimensions = new();
        int totalRegions = 0;

        foreach(MinecraftDimensionDescriptor dimension in world.Descriptor.Dimensions)
        {
            int chunkCount = world.ChunkIndex.GetChunkCount(dimension.Id);
            int regionCount = world.ChunkIndex.GetRegionCount(dimension.Id);
            totalRegions += regionCount;
            dimensions.Add(new
            {
                id = dimension.Id.Value,
                regionDirectories = dimension.RegionDirectories,
                regions = regionCount,
                chunks = chunkCount,
                declaredBuildRange = dimension.DeclaredBuildRange
            });
        }

        MinecraftWorldDescriptor descriptor = world.Descriptor;
        WriteJson(new
        {
            ok = true,
            command = "world.inspect",
            inspectionLevel = "regionHeaders",
            rootPath = descriptor.RootPath,
            levelName = descriptor.LevelName,
            sourceRevision = descriptor.SourceRevision,
            sourceRevisionStrength = descriptor.SourceRevisionStrength.ToString(),
            version = new
            {
                dataVersion = descriptor.Version.DataVersion,
                name = descriptor.Version.VersionName,
                storageFamily = descriptor.Version.StorageFamily.ToString()
            },
            spawnLocation = descriptor.SpawnLocation,
            sessionLock = descriptor.SessionLockState.ToString(),
            dimensions,
            totalRegions,
            totalChunks = world.ChunkIndex.TotalChunkCount,
            diagnostics = descriptor.Diagnostics
        });
        return 0;
    }

    private static async Task<int> AuditWorldAsync(string worldDirectory)
    {
        ReadOnlyWorldMountRequest request = new(
            worldDirectory,
            ConcurrentWritePolicy: ConcurrentWorldWritePolicy.RejectActiveWriter,
            IncludeCustomDimensions: true,
            CaptureSourceFingerprints: false);
        await using IReadOnlyMinecraftWorld world = await new AnvilWorldMounter().MountAsync(request);
        Dictionary<string, int> compressionCounts = new(StringComparer.Ordinal);
        List<object> failures = new();
        int indexedChunks = 0;
        int readableChunks = 0;
        int failureCount = 0;
        long decompressedBytes = 0;
        MinecraftChunkReadOptions readOptions = new(
            DecompressPayload: true,
            VerifySourceFingerprint: false,
            MaximumDecompressedBytes: 16 * 1024 * 1024);

        foreach(MinecraftDimensionDescriptor dimension in world.Descriptor.Dimensions)
        {
            await foreach(MinecraftChunkIndexEntry entry in world.ChunkIndex.EnumerateAsync(dimension.Id))
            {
                indexedChunks++;
                try
                {
                    RawMinecraftChunk chunk = await world.ChunkReader.ReadAsync(entry, readOptions);
                    readableChunks++;
                    decompressedBytes += chunk.NbtPayload.Length;
                    string compression = chunk.Compression.ToString();
                    compressionCounts[compression] = compressionCounts.GetValueOrDefault(compression) + 1;
                }
                catch(Exception exception) when(exception is IOException or InvalidDataException or NotSupportedException)
                {
                    failureCount++;
                    if(failures.Count < MaximumReportedFailures)
                    {
                        failures.Add(new
                        {
                            dimension = entry.Address.Dimension.Value,
                            x = entry.Address.X,
                            z = entry.Address.Z,
                            region = entry.RegionRelativePath,
                            error = exception.Message
                        });
                    }
                }
            }
        }

        bool ok = failureCount == 0 && readableChunks == indexedChunks;
        WriteJson(new
        {
            ok,
            command = "world.audit",
            inspectionLevel = "allChunkPayloadsDecompressed",
            rootPath = world.Descriptor.RootPath,
            sourceRevision = world.Descriptor.SourceRevision,
            sourceRevisionStrength = world.Descriptor.SourceRevisionStrength.ToString(),
            indexedChunks,
            readableChunks,
            decompressedBytes,
            compressionCounts,
            failureCount,
            failuresTruncated = failureCount > failures.Count,
            failures
        });
        return ok ? 0 : 1;
    }

    private static async Task<int> AnalyzeWorldAsync(string worldDirectory)
    {
        ReadOnlyWorldMountRequest request = new(
            worldDirectory,
            ConcurrentWritePolicy: ConcurrentWorldWritePolicy.RejectActiveWriter,
            IncludeCustomDimensions: true,
            CaptureSourceFingerprints: false);
        await using IReadOnlyMinecraftWorld world = await new AnvilWorldMounter().MountAsync(request);
        if(world.Descriptor.Version.StorageFamily != MinecraftStorageFamily.LegacyNumericAnvil)
        {
            throw new NotSupportedException(
                $"world analyze 当前仅支持 LegacyNumericAnvil，所选世界是 {world.Descriptor.Version.StorageFamily}。高版本 Palette 解码器正在独立实现。");
        }

        MinecraftChunkReadOptions readOptions = new(
            DecompressPayload: true,
            VerifySourceFingerprint: false,
            MaximumDecompressedBytes: LegacyAnvilChunkNormalizer.MaximumChunkNbtBytes);
        LegacyAnvilChunkNormalizer normalizer = new();
        IMinecraftBlockRegistry registry = Minecraft1122VanillaBlockRegistry.Instance;
        MinecraftUnknownDataPolicy unknownDataPolicy = new();
        Dictionary<string, long> stateCounts = new(StringComparer.Ordinal);
        Dictionary<string, long> diagnosticCounts = new(StringComparer.Ordinal);
        List<object> failures = new();
        int normalizedChunks = 0;
        int sections = 0;
        int blockEntities = 0;
        int failureCount = 0;
        long nonAirBlocks = 0;
        long unknownBlocks = 0;

        foreach(MinecraftDimensionDescriptor dimension in world.Descriptor.Dimensions)
        {
            await foreach(MinecraftChunkIndexEntry entry in world.ChunkIndex.EnumerateAsync(dimension.Id))
            {
                try
                {
                    RawMinecraftChunk raw = await world.ChunkReader.ReadAsync(entry, readOptions);
                    MinecraftNormalizationRequest normalizationRequest = new(
                        raw,
                        world.Descriptor.Version,
                        registry,
                        unknownDataPolicy);
                    NormalizedMinecraftChunk chunk = await normalizer.NormalizeAsync(normalizationRequest);
                    normalizedChunks++;
                    sections += chunk.Sections.Count;
                    blockEntities += chunk.BlockEntities.Count;

                    foreach(NormalizedMinecraftSection section in chunk.Sections)
                    {
                        (long sectionBlocks, long sectionUnknownBlocks) = AccumulateSectionStates(
                            section,
                            stateCounts);
                        nonAirBlocks += sectionBlocks;
                        unknownBlocks += sectionUnknownBlocks;
                    }

                    foreach(MinecraftDiagnostic diagnostic in chunk.Diagnostics)
                    {
                        diagnosticCounts[diagnostic.Code] = diagnosticCounts.GetValueOrDefault(diagnostic.Code) + 1;
                    }
                }
                catch(Exception exception) when(exception is IOException or InvalidDataException or NotSupportedException)
                {
                    failureCount++;
                    if(failures.Count < MaximumReportedFailures)
                    {
                        failures.Add(new
                        {
                            dimension = entry.Address.Dimension.Value,
                            x = entry.Address.X,
                            z = entry.Address.Z,
                            region = entry.RegionRelativePath,
                            error = exception.Message
                        });
                    }
                }
            }
        }

        object[] unknownStates = stateCounts
            .Where(pair => pair.Key.StartsWith(
                "zhujiejing:visible_unknown_legacy_block",
                StringComparison.Ordinal))
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => (object)new { state = pair.Key, blocks = pair.Value })
            .ToArray();
        object[] mostUsedStates = stateCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(100)
            .Select(static pair => (object)new { state = pair.Key, blocks = pair.Value })
            .ToArray();
        bool ok = failureCount == 0 && normalizedChunks == world.ChunkIndex.TotalChunkCount;
        WriteJson(new
        {
            ok,
            command = "world.analyze",
            inspectionLevel = "canonicalLegacyBlocks",
            rootPath = world.Descriptor.RootPath,
            sourceRevision = world.Descriptor.SourceRevision,
            sourceRevisionStrength = world.Descriptor.SourceRevisionStrength.ToString(),
            indexedChunks = world.ChunkIndex.TotalChunkCount,
            normalizedChunks,
            sections,
            distinctStates = stateCounts.Count,
            nonAirBlocks,
            unknownBlocks,
            unknownStates,
            blockEntities,
            diagnosticCounts,
            mostUsedStates,
            failureCount,
            failuresTruncated = failureCount > failures.Count,
            failures
        });
        return ok ? 0 : 1;
    }

    private static (long NonAirBlocks, long UnknownBlocks) AccumulateSectionStates(
        NormalizedMinecraftSection section,
        Dictionary<string, long> stateCounts)
    {
        long[] paletteCounts = new long[section.Palette.Count];
        ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
        for(int index = 0; index < indices.Length; index++)
        {
            ushort paletteIndex = indices[index];
            if(paletteIndex >= paletteCounts.Length)
            {
                throw new InvalidDataException(
                    $"Section {section.Coordinate} 的调色板索引 {paletteIndex} 越界。");
            }
            paletteCounts[paletteIndex]++;
        }

        long nonAirBlocks = 0;
        long unknownBlocks = 0;
        for(int index = 0; index < section.Palette.Count; index++)
        {
            BlockState state = section.Palette[index];
            long count = paletteCounts[index];
            if(state.IsAir || count == 0)
            {
                continue;
            }

            nonAirBlocks += count;
            stateCounts[state.CanonicalKey] = stateCounts.GetValueOrDefault(state.CanonicalKey) + count;
            if(string.Equals(
                   state.Name,
                   "zhujiejing:visible_unknown_legacy_block",
                   StringComparison.Ordinal))
            {
                unknownBlocks += count;
            }
        }

        return (nonAirBlocks, unknownBlocks);
    }

    private static async Task<int> InitializeProjectAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        Dictionary<string, string?> options = ParseOptions(args, 3);
        EnsureAllowedOptions(
            options,
            "--name",
            "--projects-root",
            "--dimension",
            "--version",
            "--data-version",
            "--storage-family");
        string? versionName = GetOption(options, "--version");
        int? dataVersion = GetInt32Option(options, "--data-version");
        string? storageFamily = GetOption(options, "--storage-family");
        ZhujieProjectVisualProfile? visualProfile = versionName is null && dataVersion is null && storageFamily is null
            ? null
            : new ZhujieProjectVisualProfile
            {
                Edition = ZjjVisualProfile.JavaEdition,
                VersionName = versionName,
                DataVersion = dataVersion,
                StorageFamily = storageFamily ?? ZjjVisualProfile.UnknownStorageFamily,
        };
        await using ZhujieIncrementalProjectStore project = await ZhujieIncrementalProjectStore.CreateAsync(
            projectPath,
            GetOption(options, "--name"),
            new ZhujieProjectTarget
            {
                Dimension = GetOption(options, "--dimension") ?? "minecraft:overworld",
                VisualProfile = visualProfile,
            },
            GetOption(options, "--projects-root"));
        ZhujieProjectWorkspace workspace = project.Workspace;
        WriteJson(new
        {
            ok = true,
            command = "project.init",
            projectDirectory = workspace.Layout.ProjectDirectory,
            projectId = workspace.Manifest.ProjectId,
            displayName = workspace.Manifest.DisplayName,
            manifest = workspace.Layout.ManifestPath,
            sceneStore = workspace.Layout.SceneStorePath,
            initialRevision = await project.GetCurrentRevisionAsync(),
        });
        return 0;
    }

    private static async Task<int> OpenStudioProjectAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        Dictionary<string, string?> options = ParseOptions(args, 3);
        EnsureAllowedOptions(options, "--projects-root");
        ZhujieProjectWorkspace workspace = await ZhujieProjectWorkspace.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        ZhujieStudioCommand command = ZhujieStudioCommand.OpenProject(workspace.Layout.ProjectDirectory);
        bool delivered = await ZhujieStudioBridgeProtocol.TrySendAsync(command, TimeSpan.FromMilliseconds(750));
        string delivery;
        if(delivered)
        {
            delivery = "existingInstance";
        }
        else
        {
            string studioPath = Path.Combine(AppContext.BaseDirectory, "ZhuJieJing.Studio.exe");
            if(!File.Exists(studioPath))
                throw new FileNotFoundException("找不到与 zjj.exe 同级的 ZhuJieJing.Studio.exe。", studioPath);
            await DetachedStudioLauncher.StartAsync(
                studioPath,
                ["--ai-project", workspace.Layout.ProjectDirectory],
                AppContext.BaseDirectory);
            delivery = "newInstance";
        }

        WriteJson(new
        {
            ok = true,
            command = "studio.open",
            projectDirectory = workspace.Layout.ProjectDirectory,
            delivery,
        });
        return 0;
    }

    private static async Task<int> ValidateProjectAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        Dictionary<string, string?> options = ParseOptions(args, 3);
        EnsureAllowedOptions(options, "--projects-root");
        await using ZhujieIncrementalProjectStore project = await ZhujieIncrementalProjectStore.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        ZhujieProjectWorkspace workspace = project.Workspace;
        IReadOnlyList<ZhujieBatchRecord> batches = await project.ListBatchesAsync();
        string revision = await project.GetCurrentRevisionAsync();
        WriteJson(new
        {
            ok = true,
            command = "project.validate",
            projectDirectory = workspace.Layout.ProjectDirectory,
            projectId = workspace.Manifest.ProjectId,
            mode = "incrementalPatches",
            sceneStore = workspace.Layout.SceneStorePath,
            revision,
            batchCount = batches.Count,
            latestBatch = batches.LastOrDefault(),
        });
        return 0;
    }

    private static async Task<int> WriteArchitectStatusAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        Dictionary<string, string?> options = ParseOptions(args, 3);
        EnsureAllowedOptions(
            options,
            "--projects-root",
            "--state",
            "--phase",
            "--message",
            "--session",
            "--agent",
            "--instruction",
            "--plan-sha",
            "--revision",
            "--completed",
            "--total",
            "--unit");
        ZhujieProjectWorkspace workspace = await ZhujieProjectWorkspace.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        long? completed = GetInt64Option(options, "--completed");
        long? total = GetInt64Option(options, "--total");
        string? unit = GetOption(options, "--unit");
        if((completed is null) != (total is null) || (completed is null) != (unit is null))
            throw new ArgumentException("进度必须同时提供 --completed、--total 与 --unit。");
        Guid? instructionId = GetGuidOption(options, "--instruction");
        ZhujieArchitectStatus status = new()
        {
            ProjectId = workspace.Manifest.ProjectId,
            SessionId = RequireOption(options, "--session"),
            Agent = RequireOption(options, "--agent"),
            State = ParseArchitectState(RequireOption(options, "--state")),
            Phase = RequireOption(options, "--phase"),
            Message = RequireOption(options, "--message"),
            Progress = completed is null ? null : new ZhujieArchitectProgress
            {
                Completed = completed.Value,
                Total = total!.Value,
                Unit = unit!,
            },
            CurrentInstructionId = instructionId,
            PublishedPlanSha256 = GetOption(options, "--plan-sha"),
            PublishedRevision = GetOption(options, "--revision"),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await workspace.WriteArchitectStatusAsync(status);
        WriteJson(new
        {
            ok = true,
            command = "architect.status",
            file = workspace.Layout.ArchitectStatusPath,
            projectId = workspace.Manifest.ProjectId,
            state = ToProtocolName(status.State),
            updatedAtUtc = status.UpdatedAtUtc,
        });
        return 0;
    }

    private static async Task<int> ListInstructionsAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        Dictionary<string, string?> options = ParseOptions(args, 3, "--pending");
        EnsureAllowedOptions(options, "--projects-root", "--pending");
        ZhujieProjectWorkspace workspace = await ZhujieProjectWorkspace.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        bool pendingOnly = options.ContainsKey("--pending");
        IReadOnlyList<ZhujieInstruction> instructions = await workspace.ListInstructionsAsync(pendingOnly);
        WriteJson(new
        {
            ok = true,
            command = "instruction.list",
            projectId = workspace.Manifest.ProjectId,
            pendingOnly,
            count = instructions.Count,
            instructions = instructions.Select(instruction => new
            {
                instructionId = instruction.InstructionId,
                createdAtUtc = instruction.CreatedAtUtc,
                kind = ToProtocolName(instruction.Kind),
                instruction.Text,
                instruction.BasedOnRevision,
                instruction.Context,
            }),
        });
        return 0;
    }

    private static async Task<int> AcknowledgeInstructionAsync(string[] args)
    {
        if(args.Length < 4) throw new ArgumentException("instruction ack 必须提供工程与 instructionId。");
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        string instructionIdText = RequirePositionalOperand(args, 3, "instructionId");
        if(!Guid.TryParse(instructionIdText, out Guid instructionId) || instructionId == Guid.Empty)
            throw new ArgumentException("instructionId 必须是非空 GUID。");
        Dictionary<string, string?> options = ParseOptions(args, 4);
        EnsureAllowedOptions(options, "--projects-root", "--state", "--session", "--message", "--plan-sha", "--revision");
        ZhujieProjectWorkspace workspace = await ZhujieProjectWorkspace.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        ZhujieInstructionAcknowledgement acknowledgement = new()
        {
            ProjectId = workspace.Manifest.ProjectId,
            InstructionId = instructionId,
            SessionId = RequireOption(options, "--session"),
            State = ParseAcknowledgementState(RequireOption(options, "--state")),
            Message = RequireOption(options, "--message"),
            PublishedPlanSha256 = GetOption(options, "--plan-sha"),
            PublishedRevision = GetOption(options, "--revision"),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        await workspace.WriteInstructionAcknowledgementAsync(acknowledgement);
        WriteJson(new
        {
            ok = true,
            command = "instruction.ack",
            projectId = workspace.Manifest.ProjectId,
            instructionId,
            state = ToProtocolName(acknowledgement.State),
            file = Path.Combine(workspace.Layout.AcknowledgementsDirectory, $"{instructionId:D}.json"),
        });
        return 0;
    }

    private static async Task<int> PublishPlanAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        string draftPath = RequirePositionalOperand(args, 3, ".zhujie/staging 直系草稿");
        Dictionary<string, string?> options = ParseOptions(args, 4);
        EnsureAllowedOptions(options, "--projects-root");
        await using ZhujieIncrementalProjectStore project = await ZhujieIncrementalProjectStore.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        ZhujieProjectWorkspace workspace = project.Workspace;
        ZhujiePatchPublishResult result = await project.PublishPatchAsync(draftPath);
        WriteJson(new
        {
            ok = true,
            command = "plan.publish",
            projectId = workspace.Manifest.ProjectId,
            revision = result.Revision,
            baseRevision = result.BaseRevision,
            batch = result.Batch,
            patch = result.PlanFilePath,
            planSha256 = result.PlanSha256,
            sceneDeltaHash = result.SceneDeltaHash,
            changedVoxels = result.ChangedVoxelCount,
            sections = result.SectionCount,
            attemptedWrites = result.AttemptedWriteCount,
            appliedWrites = result.AppliedWriteCount,
            skippedWrites = result.SkippedWriteCount,
        });
        return 0;
    }

    private static async Task<int> ListBatchesAsync(string[] args)
    {
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        Dictionary<string, string?> options = ParseOptions(args, 3);
        EnsureAllowedOptions(options, "--projects-root");
        await using ZhujieIncrementalProjectStore project = await ZhujieIncrementalProjectStore.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        IReadOnlyList<ZhujieBatchRecord> batches = await project.ListBatchesAsync();
        WriteJson(new
        {
            ok = true,
            command = "batch.list",
            revision = await project.GetCurrentRevisionAsync(),
            count = batches.Count,
            batches,
        });
        return 0;
    }

    private static async Task<int> RevertBatchAsync(string[] args)
    {
        if(args.Length < 4) throw new ArgumentException("batch revert 必须提供工程与 revision。");
        string projectPath = RequirePositionalOperand(args, 2, "工程目录");
        string revision = RequirePositionalOperand(args, 3, "revision");
        Dictionary<string, string?> options = ParseOptions(args, 4);
        EnsureAllowedOptions(options, "--projects-root");
        await using ZhujieIncrementalProjectStore project = await ZhujieIncrementalProjectStore.OpenAsync(
            projectPath,
            GetOption(options, "--projects-root"));
        ZhujieBatchRecord batch = await project.RevertBatchAsync(revision);
        WriteJson(new { ok = true, command = "batch.revert", batch });
        return 0;
    }

    private static int Validate(ZjjPlan plan, string inputPath)
    {
        PlanValidationResult result = ZjjPlanValidator.Validate(plan);
        WriteJson(new
        {
            ok = result.IsValid,
            command = "plan.validate",
            file = inputPath,
            planId = plan.PlanId,
            issues = result.Issues
        });
        return result.IsValid ? 0 : 1;
    }

    private static int Compile(ZjjPlan plan, string inputPath)
    {
        if(plan.Base is not null)
        {
            throw new ArgumentException("该蓝图引用了基础场景，必须通过桌面工程的预览事务编译，不能假装在空世界上执行。");
        }

        CompilationResult result = new ZjjPlanCompiler().Compile(plan, new EmptySceneSnapshot());
        WriteJson(new
        {
            ok = true,
            command = "plan.compile",
            file = inputPath,
            planId = plan.PlanId,
            baseRevision = result.Delta.BaseRevision,
            stableHash = result.Delta.StableHash,
            changedVoxels = result.Delta.ChangedVoxelCount,
            attemptedWrites = result.AttemptedWriteCount,
            appliedWrites = result.AppliedWriteCount,
            skippedWrites = result.SkippedWriteCount,
            sections = result.Delta.Sections.Select(section => new
            {
                dimension = section.Section.Dimension,
                x = section.Section.X,
                y = section.Section.Y,
                z = section.Section.Z,
                changes = section.Changes.Count,
                stableHash = section.StableHash
            })
        });
        return 0;
    }

    private static int Format(ZjjPlan plan, string outputPath)
    {
        EnsurePlanExtension(outputPath, "输出蓝图");
        PlanValidationResult validation = ZjjPlanValidator.Validate(plan);
        if(!validation.IsValid)
        {
            WriteJson(new { ok = false, command = "plan.format", issues = validation.Issues });
            return 1;
        }

        string? directory = Path.GetDirectoryName(outputPath);
        if(!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, ZjjPlanJson.Serialize(plan));
        WriteJson(new { ok = true, command = "plan.format", file = outputPath });
        return 0;
    }

    private static void EnsurePlanExtension(string path, string description)
    {
        if(!Path.GetExtension(path).Equals(".zz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{description}必须使用 .zz 扩展名。");
    }

    private static bool IsHelpOption(string value) => value is "--help" or "-h";

    private static string RequirePositionalOperand(string[] args, int index, string description)
    {
        if(index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            throw new ArgumentException($"缺少{description}。");
        string value = args[index];
        if(value.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException($"{description}不能以 '-' 开头：{value}");
        return value;
    }

    private static Dictionary<string, string?> ParseOptions(
        string[] args,
        int startIndex,
        params string[] switchOptions)
    {
        HashSet<string> switches = switchOptions.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for(int index = startIndex; index < args.Length; index++)
        {
            string option = args[index];
            if(!option.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"无法识别的位置参数：{option}");
            if(result.ContainsKey(option)) throw new ArgumentException($"选项重复：{option}");
            if(switches.Contains(option))
            {
                result.Add(option, null);
                continue;
            }
            if(index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"选项 {option} 缺少值。");
            result.Add(option, args[++index]);
        }
        return result;
    }

    private static void EnsureAllowedOptions(Dictionary<string, string?> options, params string[] allowedOptions)
    {
        HashSet<string> allowed = allowedOptions.ToHashSet(StringComparer.Ordinal);
        string? unknown = options.Keys.FirstOrDefault(option => !allowed.Contains(option));
        if(unknown is not null) throw new ArgumentException($"未知选项：{unknown}");
    }

    private static string? GetOption(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out string? value) ? value : null;

    private static string RequireOption(IReadOnlyDictionary<string, string?> options, string name) =>
        GetOption(options, name) ?? throw new ArgumentException($"缺少必需选项 {name}。");

    private static int? GetInt32Option(IReadOnlyDictionary<string, string?> options, string name)
    {
        string? value = GetOption(options, name);
        if(value is null) return null;
        if(!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException($"选项 {name} 必须是整数。");
        return parsed;
    }

    private static long? GetInt64Option(IReadOnlyDictionary<string, string?> options, string name)
    {
        string? value = GetOption(options, name);
        if(value is null) return null;
        if(!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            throw new ArgumentException($"选项 {name} 必须是整数。");
        return parsed;
    }

    private static Guid? GetGuidOption(IReadOnlyDictionary<string, string?> options, string name)
    {
        string? value = GetOption(options, name);
        if(value is null) return null;
        if(!Guid.TryParse(value, out Guid parsed) || parsed == Guid.Empty)
            throw new ArgumentException($"选项 {name} 必须是非空 GUID。");
        return parsed;
    }

    private static ZhujieArchitectState ParseArchitectState(string value) => value switch
    {
        "idle" => ZhujieArchitectState.Idle,
        "planning" => ZhujieArchitectState.Planning,
        "building" => ZhujieArchitectState.Building,
        "validating" => ZhujieArchitectState.Validating,
        "waitingForUser" => ZhujieArchitectState.WaitingForUser,
        "paused" => ZhujieArchitectState.Paused,
        "completed" => ZhujieArchitectState.Completed,
        "error" => ZhujieArchitectState.Error,
        _ => throw new ArgumentException($"未知建筑师状态：{value}"),
    };

    private static ZhujieInstructionAcknowledgementState ParseAcknowledgementState(string value) => value switch
    {
        "accepted" => ZhujieInstructionAcknowledgementState.Accepted,
        "working" => ZhujieInstructionAcknowledgementState.Working,
        "completed" => ZhujieInstructionAcknowledgementState.Completed,
        "blocked" => ZhujieInstructionAcknowledgementState.Blocked,
        "superseded" => ZhujieInstructionAcknowledgementState.Superseded,
        _ => throw new ArgumentException($"未知指令回执状态：{value}"),
    };

    private static string ToProtocolName<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        string name = value.ToString();
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, OutputOptions));

    private static void WriteHelp() => Console.WriteLine(HelpText);
}
