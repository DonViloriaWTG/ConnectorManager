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

        // 1b. Build interop dependencies (SmartFreight, Saas) and copy their packages
        await BuildInteropDependenciesAsync(connector, settings, nugetTarget, "Release", onOutput, cancellationToken)
            .ConfigureAwait(false);

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

        // 1b. Build interop dependencies (SmartFreight, Saas) and copy their packages
        await BuildInteropDependenciesAsync(connector, settings, nugetTarget, "Debug", onOutput, cancellationToken)
            .ConfigureAwait(false);

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

    /// <summary>
    /// Builds interop projects (SmartFreight, Saas) in the CarrierConnector repo and copies
    /// the resulting NuGet packages to the connector's local nuget folder(s).
    /// Interop projects use GeneratePackageOnBuild, so a normal dotnet build produces .nupkg files.
    /// </summary>
    private async Task BuildInteropDependenciesAsync(
        ConnectorInfo connector,
        WorkspaceSettings settings,
        string nugetTarget,
        string configuration,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        var interopRoot = Path.Combine(settings.CarrierConnectorRepoPath, "Interop");
        if (!Directory.Exists(interopRoot))
        {
            return;
        }

        // The regional nuget folder (e.g. Australia/Bin/nuget) is where the region's
        // NuGet.config resolves "bin\nuget" — ensure interop packages land there too.
        var regionDir = Path.GetDirectoryName(connector.SolutionDirectory);
        var regionalNuget = regionDir is not null
            ? Path.Combine(regionDir, "Bin", "nuget")
            : null;

        foreach (var interopDir in Directory.GetDirectories(interopRoot))
        {
            var slnFiles = Directory.GetFiles(interopDir, "*.sln");
            if (slnFiles.Length == 0)
            {
                continue;
            }

            var interopName = Path.GetFileName(interopDir);
            var interopNugetDir = Path.Combine(interopDir, "bin", "nuget");

            onOutput($"  ── Interop: {interopName} ──");

            // Copy Common + Framework packages into the interop's own local nuget feed
            // (the interop's NuGet.config references bin\nuget for WTG.CarrierMessagingBuss.* packages)
            CopyNuGetPackages(settings.CommonRepoPath, interopNugetDir, onOutput);
            CopyNuGetPackages(settings.FrameworkRepoPath, interopNugetDir, onOutput);

            // Build the interop solution (GeneratePackageOnBuild produces .nupkg automatically)
            var buildArgs = $"build \"{slnFiles[0]}\" -c {configuration}";
            onOutput($"  Running: dotnet {buildArgs}");

            var exitCode = await _runner.RunDotnetAsync(
                buildArgs,
                interopDir,
                onOutput,
                cancellationToken).ConfigureAwait(false);

            if (exitCode != 0)
            {
                onOutput($"  ⚠ Interop {interopName} build failed (exit code {exitCode}), continuing...");
                continue;
            }

            onOutput($"  ✔ Interop {interopName} built successfully");

            // Copy interop .nupkg files to the connector's nuget target folder(s)
            // and purge the NuGet global cache so the fresh packages are picked up.
            if (Directory.Exists(interopNugetDir))
            {
                var copiedPackages = CopyInteropPackages(interopNugetDir, nugetTarget, onOutput);

                // Also copy to the regional nuget folder if it differs from the connector-level target
                if (regionalNuget is not null &&
                    !string.Equals(nugetTarget, regionalNuget, StringComparison.OrdinalIgnoreCase))
                {
                    CopyInteropPackages(interopNugetDir, regionalNuget, onOutput);
                }

                // Purge the NuGet global packages cache for each interop package so that
                // dotnet restore doesn't skip the fresh .nupkg (same version 1.0.0).
                PurgeGlobalPackageCache(copiedPackages, onOutput);
            }
        }
    }

    /// <summary>
    /// Copies WTG.CarrierConnector.* NuGet packages from an interop build output directory
    /// to a target nuget folder. Returns the package IDs that were copied.
    /// </summary>
    private static List<string> CopyInteropPackages(string sourceDir, string targetDir, Action<string> onOutput)
    {
        Directory.CreateDirectory(targetDir);
        var packages = Directory.GetFiles(sourceDir, "WTG.CarrierConnector.*.nupkg");
        var packageIds = new List<string>();

        foreach (var pkg in packages)
        {
            var fileName = Path.GetFileName(pkg);
            File.Copy(pkg, Path.Combine(targetDir, fileName), overwrite: true);

            // Extract package ID from filename: "WTG.CarrierConnector.Interop.SmartFreight.1.0.0.nupkg"
            // → "wtg.carrierconnector.interop.smartfreight"
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var versionSuffix = ".1.0.0";
            if (nameWithoutExt.EndsWith(versionSuffix, StringComparison.OrdinalIgnoreCase))
            {
                packageIds.Add(nameWithoutExt[..^versionSuffix.Length].ToLowerInvariant());
            }
        }

        if (packages.Length > 0)
        {
            onOutput($"  Copied {packages.Length} interop package(s) to {targetDir}");
        }

        return packageIds;
    }

    /// <summary>
    /// Deletes cached interop packages from the NuGet global packages folder
    /// (~/.nuget/packages/{packageId}/). This forces NuGet to re-extract the
    /// freshly built .nupkg even though the version number (1.0.0) hasn't changed.
    /// </summary>
    private static void PurgeGlobalPackageCache(List<string> packageIds, Action<string> onOutput)
    {
        var globalPackagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");

        if (!Directory.Exists(globalPackagesDir))
        {
            return;
        }

        var purged = 0;
        foreach (var packageId in packageIds)
        {
            var cachedDir = Path.Combine(globalPackagesDir, packageId);
            if (Directory.Exists(cachedDir))
            {
                try
                {
                    Directory.Delete(cachedDir, recursive: true);
                    purged++;
                }
                catch
                {
                    // Cache entry may be locked; non-fatal
                }
            }
        }

        if (purged > 0)
        {
            onOutput($"  Purged {purged} interop package(s) from NuGet cache");
        }
    }
}

public sealed class ConnectorBuildResult
{
    public bool Success { get; init; }
    public string? PublishDirectory { get; init; }
    public string? ConnectorName { get; init; }
    public string? ErrorMessage { get; init; }
}
