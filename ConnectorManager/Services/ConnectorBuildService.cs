using ConnectorManager.Models;

namespace ConnectorManager.Services;

/// <summary>
/// Builds and publishes individual carrier connectors.
/// Handles NuGet dependency copying and dotnet publish.
/// </summary>
public sealed class ConnectorBuildService
{
    private readonly ProcessRunner _runner = new();

    /// <summary>
    /// DLLs excluded from the upload ZIP. These are provided by the host (CMB API) at runtime.
    /// </summary>
    public static readonly HashSet<string> BlacklistedDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "WTG.CarrierMessagingBuss.Integration.dll",
        "Wisetech.UniversalRateService.Api.Queries.Models.dll",
        "Wisetech.UniversalRateService.Domain.dll",
        "Wisetech.UniversalRateService.Integration.DataProvider.dll",
        "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
        "Microsoft.Extensions.Logging.Abstractions.dll"
    };

    /// <summary>
    /// Copies NuGet packages from Common and Framework to the connector's local nuget folder,
    /// then runs dotnet publish on the connector's .csproj.
    /// </summary>
    public async Task<ConnectorBuildResult> BuildAsync(
        ConnectorInfo connector,
        WorkspaceSettings settings,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        var connectorName = connector.Name;
        onOutput($"\n{'=',-60}");
        onOutput($"▶ Building connector: {connector.DisplayName}");
        onOutput($"{'=',-60}");

        // 1. Copy NuGet packages to the connector's dependency folder
        var nugetTarget = FindConnectorNuGetFolder(connector, settings);
        onOutput($"  NuGet target: {nugetTarget}");
        CopyNuGetPackages(settings.CommonRepoPath, nugetTarget, onOutput);
        CopyNuGetPackages(settings.FrameworkRepoPath, nugetTarget, onOutput);

        // 2. Determine publish output directory
        var publishDir = Path.Combine(connector.SolutionDirectory, "bin", connectorName, "publish");
        onOutput($"  Publish output: {publishDir}");

        // 3. Run dotnet publish
        var args = $"publish \"{connector.ProjectPath}\" -c Release -o \"{publishDir}\"";
        onOutput($"  Running: dotnet {args}");

        var exitCode = await _runner.RunDotnetAsync(
            args,
            connector.SolutionDirectory,
            onOutput,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            onOutput($"  ✖ Build failed with exit code {exitCode}");
            return new ConnectorBuildResult { Success = false, ErrorMessage = $"dotnet publish exited with code {exitCode}" };
        }

        if (!Directory.Exists(publishDir))
        {
            onOutput("  ✖ Publish directory was not created");
            return new ConnectorBuildResult { Success = false, ErrorMessage = "Publish output directory not found" };
        }

        onOutput($"  ✔ Connector built successfully");
        return new ConnectorBuildResult
        {
            Success = true,
            PublishDirectory = publishDir,
            ConnectorName = connectorName
        };
    }

    /// <summary>
    /// Builds the connector in Debug configuration for local debugging with the CMB API.
    /// Publishes to the specified output directory (typically bin/publish under the solution root,
    /// which is where the CMB DebugManager expects to find the DLLs).
    /// Also produces a Debug build at the project's standard OutputPath (bin/{projectName}/)
    /// so that the DebugPackageCloner's symlink mirror points to Debug DLLs with matching PDBs.
    /// </summary>
    public async Task<ConnectorBuildResult> BuildForDebugAsync(
        ConnectorInfo connector,
        WorkspaceSettings settings,
        string publishDir,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        var connectorName = connector.Name;
        onOutput($"\n  ▶ Building {connector.DisplayName} (Debug)");

        // 1. Copy NuGet packages
        var nugetTarget = FindConnectorNuGetFolder(connector, settings);
        onOutput($"  NuGet target: {nugetTarget}");
        CopyNuGetPackages(settings.CommonRepoPath, nugetTarget, onOutput);
        CopyNuGetPackages(settings.FrameworkRepoPath, nugetTarget, onOutput);

        // 2. Clean stale Release publish output that would otherwise take precedence over
        //    our Debug DLLs in the DebugPackageCloner's symlink mirror.
        //    The cloner searches bin/{projectName}/ recursively and would find old Release
        //    DLLs in the "publish" subfolder, ignoring the fresh Debug DLLs in bin/publish/.
        var stalePublishDir = Path.Combine(connector.SolutionDirectory, "bin", connectorName, "publish");
        if (Directory.Exists(stalePublishDir))
        {
            try
            {
                Directory.Delete(stalePublishDir, recursive: true);
                onOutput($"  Cleaned stale output: bin/{connectorName}/publish/");
            }
            catch (Exception ex)
            {
                onOutput($"  ⚠ Could not clean stale output: {ex.Message}");
            }
        }

        // 3. Explicit dotnet build in Debug configuration.
        //    This produces the Debug DLL at bin/{projectName}/ (per Directory.Build.props OutputPath),
        //    which is the primary location the DebugPackageCloner checks for "original" DLLs.
        //    This enables the edit→build→debug workflow without re-publishing.
        var buildArgs = $"build \"{connector.ProjectPath}\" -c Debug";
        onOutput($"  Running: dotnet {buildArgs}");

        var buildExitCode = await _runner.RunDotnetAsync(
            buildArgs,
            connector.SolutionDirectory,
            onOutput,
            cancellationToken).ConfigureAwait(false);

        if (buildExitCode != 0)
        {
            onOutput($"  ⚠ dotnet build exited with code {buildExitCode}, continuing with publish...");
        }

        // 4. Run dotnet publish in Debug configuration to produce the full dependency set
        //    at bin/publish/ (which the DebugPackageCloner iterates to create symlinks).
        var publishArgs = $"publish \"{connector.ProjectPath}\" -c Debug -o \"{publishDir}\"";
        onOutput($"  Running: dotnet {publishArgs}");

        var exitCode = await _runner.RunDotnetAsync(
            publishArgs,
            connector.SolutionDirectory,
            onOutput,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            onOutput($"  ✖ Debug publish failed with exit code {exitCode}");
            return new ConnectorBuildResult { Success = false, ErrorMessage = $"dotnet publish exited with code {exitCode}" };
        }

        if (!Directory.Exists(publishDir))
        {
            onOutput("  ✖ Publish directory was not created");
            return new ConnectorBuildResult { Success = false, ErrorMessage = "Publish output directory not found" };
        }

        onOutput($"  ✔ Debug build succeeded");
        return new ConnectorBuildResult
        {
            Success = true,
            PublishDirectory = publishDir,
            ConnectorName = connectorName
        };
    }

    /// <summary>
    /// Determines the correct local NuGet folder for the connector.
    /// The connector's NuGet.config typically references "Bin\nuget" relative to the solution root.
    /// </summary>
    private static string FindConnectorNuGetFolder(ConnectorInfo connector, WorkspaceSettings settings)
    {
        // Check if there's a regional NuGet.config that references a local nuget folder
        // Most connectors use "Bin\nuget" relative to the solution directory
        var binNuget = Path.Combine(connector.SolutionDirectory, "Bin", "nuget");
        if (Directory.Exists(binNuget) || HasLocalNuGetSource(connector.SolutionDirectory))
        {
            return binNuget;
        }

        // Fallback: check parent (region) directory for Bin\nuget
        var regionDir = Path.GetDirectoryName(connector.SolutionDirectory);
        if (regionDir is not null)
        {
            var regionBinNuget = Path.Combine(regionDir, "Bin", "nuget");
            if (Directory.Exists(regionBinNuget))
            {
                return regionBinNuget;
            }
        }

        // Create at solution level
        return binNuget;
    }

    /// <summary>
    /// Checks if the directory or its parents have a NuGet.config with a local dependencies source.
    /// </summary>
    private static bool HasLocalNuGetSource(string directory)
    {
        var nugetConfig = Path.Combine(directory, "NuGet.config");
        if (File.Exists(nugetConfig))
        {
            var content = File.ReadAllText(nugetConfig);
            return content.Contains("Bin\\nuget", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("bin/nuget", StringComparison.OrdinalIgnoreCase) ||
                   content.Contains("dependencies", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void CopyNuGetPackages(string repoPath, string targetDir, Action<string> onOutput)
    {
        var sourceDir = Path.Combine(repoPath, "bin", "nuget");
        if (!Directory.Exists(sourceDir))
        {
            onOutput($"  ⚠ NuGet source not found: {sourceDir}");
            return;
        }

        Directory.CreateDirectory(targetDir);
        var packages = Directory.GetFiles(sourceDir, "*.nupkg");
        foreach (var pkg in packages)
        {
            var fileName = Path.GetFileName(pkg);
            File.Copy(pkg, Path.Combine(targetDir, fileName), overwrite: true);
        }

        onOutput($"  Copied {packages.Length} package(s) from {sourceDir}");
    }
}

public sealed class ConnectorBuildResult
{
    public bool Success { get; init; }
    public string? PublishDirectory { get; init; }
    public string? ConnectorName { get; init; }
    public string? ErrorMessage { get; init; }
}
