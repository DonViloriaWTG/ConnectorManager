using ConnectorManager.Models;

namespace ConnectorManager.Services;

/// <summary>
/// Executes the full build chain: Common → Framework → Core.
/// Builds each repo, packs NuGet packages, and copies them to downstream repos.
/// </summary>
public sealed class BuildChainService
{
    private readonly ProcessRunner _runner = new();

    public async Task<bool> ExecuteFullChainAsync(
        WorkspaceSettings settings,
        Action<string, BuildStepStatus> onStepChanged,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        var steps = CreateSteps(settings);

        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onStepChanged(step.Name, BuildStepStatus.Running);
            onOutput($"\n{'=',-60}");
            onOutput($"▶ {step.Name}: {step.Description}");
            onOutput($"{'=',-60}");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool success;
            if (step.CopyAction is not null)
            {
                success = ExecuteCopy(step.CopyAction, onOutput);
            }
            else
            {
                var exitCode = await _runner.RunDotnetAsync(
                    step.DotnetArguments!,
                    step.WorkingDirectory!,
                    onOutput,
                    cancellationToken).ConfigureAwait(false);
                success = exitCode == 0;
            }

            sw.Stop();
            var status = success ? BuildStepStatus.Succeeded : BuildStepStatus.Failed;
            onStepChanged(step.Name, status);
            onOutput($"  ⏱ {step.Name} {status} in {sw.Elapsed.TotalSeconds:F1}s");

            if (!success)
            {
                onOutput($"  ✖ Build chain aborted at: {step.Name}");
                return false;
            }
        }

        onOutput("\n✔ Full build chain completed successfully.");
        return true;
    }

    private static List<ChainStep> CreateSteps(WorkspaceSettings settings)
    {
        return
        [
            new ChainStep
            {
                Name = "Build Common",
                Description = "dotnet build Common.sln -c DEBUG",
                WorkingDirectory = settings.CommonRepoPath,
                DotnetArguments = "build Common.sln -c DEBUG"
            },
            new ChainStep
            {
                Name = "Pack Common",
                Description = "dotnet pack Common.sln --no-build -c DEBUG",
                WorkingDirectory = settings.CommonRepoPath,
                DotnetArguments = "pack Common.sln --no-build -c DEBUG -o bin\\nuget"
            },
            new ChainStep
            {
                Name = "Copy Common → Framework",
                Description = "Copy NuGet packages from Common to Framework",
                CopyAction = () => CopyNuGetPackages(
                    Path.Combine(settings.CommonRepoPath, "bin", "nuget"),
                    Path.Combine(settings.FrameworkRepoPath, "bin", "nuget"))
            },
            new ChainStep
            {
                Name = "Build Framework",
                Description = "dotnet build Framework.sln -c DEBUG",
                WorkingDirectory = settings.FrameworkRepoPath,
                DotnetArguments = "build Framework.sln -c DEBUG"
            },
            new ChainStep
            {
                Name = "Pack Framework",
                Description = "dotnet pack Framework.sln --no-build -c DEBUG",
                WorkingDirectory = settings.FrameworkRepoPath,
                DotnetArguments = "pack Framework.sln --no-build -c DEBUG -o bin\\nuget"
            },
            new ChainStep
            {
                Name = "Copy NuGet → Core",
                Description = "Copy Common NuGet packages to Core",
                CopyAction = () => CopyNuGetPackages(
                    Path.Combine(settings.CommonRepoPath, "bin", "nuget"),
                    Path.Combine(settings.CoreRepoPath, "Bin", "nuget"))
            },
            new ChainStep
            {
                Name = "Build Core",
                Description = "dotnet build CarrierMessagingBuss.Api.csproj -c DEBUG (excludes .sqlproj)",
                WorkingDirectory = settings.CoreRepoPath,
                // Build the API project directly instead of the whole solution.
                // The .sqlproj (SSDT Database project) requires Visual Studio's MSBuild
                // with SSDT targets — it cannot be built via the dotnet CLI.
                DotnetArguments = "build CarrierMessagingBuss.Api\\CarrierMessagingBuss.Api.csproj -c DEBUG"
            }
        ];
    }

    private static bool ExecuteCopy(Func<List<string>> copyAction, Action<string> onOutput)
    {
        try
        {
            var copied = copyAction();
            foreach (var file in copied)
            {
                onOutput($"  Copied: {file}");
            }

            onOutput($"  {copied.Count} package(s) copied.");
            return true;
        }
        catch (Exception ex)
        {
            onOutput($"  ✖ Copy failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Copies all .nupkg files from source to target directory, creating target if needed.
    /// Returns list of copied file names.
    /// </summary>
    private static List<string> CopyNuGetPackages(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var copied = new List<string>();

        if (!Directory.Exists(sourceDir))
        {
            throw new DirectoryNotFoundException($"Source NuGet directory not found: {sourceDir}");
        }

        foreach (var sourceFile in Directory.GetFiles(sourceDir, "*.nupkg"))
        {
            var fileName = Path.GetFileName(sourceFile);
            var destFile = Path.Combine(targetDir, fileName);
            File.Copy(sourceFile, destFile, overwrite: true);
            copied.Add(fileName);
        }

        return copied;
    }

    private sealed class ChainStep
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? WorkingDirectory { get; init; }
        public string? DotnetArguments { get; init; }
        public Func<List<string>>? CopyAction { get; init; }
    }
}
