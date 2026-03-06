using ConnectorManager.Models;
using Newtonsoft.Json;

namespace ConnectorManager.Services;

/// <summary>
/// Scans the CarrierConnector repository for all connectors by finding
/// ConnectorDeploymentInfo.json files, and builds a searchable index.
/// </summary>
public sealed class ConnectorScanService
{
    private List<ConnectorInfo> _connectors = [];

    /// <summary>All discovered connectors.</summary>
    public IReadOnlyList<ConnectorInfo> Connectors => _connectors;

    /// <summary>
    /// Scans the CarrierConnector repo root for all ConnectorDeploymentInfo.json files.
    /// Parses each and builds the connector index.
    /// </summary>
    public Task<IReadOnlyList<ConnectorInfo>> ScanAsync(string carrierConnectorRepoPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var results = new List<ConnectorInfo>();
            var jsonFiles = Directory.GetFiles(
                carrierConnectorRepoPath,
                "ConnectorDeploymentInfo.json",
                SearchOption.AllDirectories);

            foreach (var jsonFile in jsonFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var content = File.ReadAllText(jsonFile);
                    var info = JsonConvert.DeserializeObject<ConnectorDeploymentInfo>(content);
                    if (info is null || string.IsNullOrWhiteSpace(info.ConnectorName))
                    {
                        continue;
                    }

                    var infoDir = Path.GetDirectoryName(jsonFile)!;
                    var region = DeriveRegion(carrierConnectorRepoPath, jsonFile);

                    // Resolve the absolute project path from the relative path in the JSON
                    var projectPath = Path.GetFullPath(Path.Combine(infoDir, info.RelativeProjectPath));

                    // Solution directory is typically the parent of the project folder
                    // (e.g., Australia/AceLogistic/ contains AceLogistic/AceLogistic.csproj)
                    var solutionDir = FindSolutionDirectory(infoDir, carrierConnectorRepoPath);

                    results.Add(new ConnectorInfo
                    {
                        Name = info.ConnectorName,
                        Region = region,
                        ProjectPath = projectPath,
                        DeploymentInfoDirectory = infoDir,
                        SolutionDirectory = solutionDir,
                        DeploymentInfo = info
                    });
                }
                catch (JsonException)
                {
                    // Skip malformed files
                }
            }

            _connectors = results.OrderBy(c => c.Region).ThenBy(c => c.Name).ToList();
            return (IReadOnlyList<ConnectorInfo>)_connectors;
        }, cancellationToken);
    }

    /// <summary>
    /// Searches connectors by name (case-insensitive substring match).
    /// </summary>
    public IReadOnlyList<ConnectorInfo> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _connectors;
        }

        return _connectors
            .Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.Region.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        c.DeploymentInfo.CarrierCode.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Derives the region name from the folder path (first directory under repo root).
    /// e.g., "c:\repo\Australia\AceLogistic\AceLogistic\ConnectorDeploymentInfo.json" → "Australia"
    /// </summary>
    private static string DeriveRegion(string repoRoot, string jsonFilePath)
    {
        var relativePath = Path.GetRelativePath(repoRoot, jsonFilePath);
        var parts = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 ? parts[0] : string.Empty;
    }

    /// <summary>
    /// Finds the solution directory by looking for .sln files, walking up from the given directory.
    /// Falls back to the info directory itself.
    /// </summary>
    private static string FindSolutionDirectory(string startDir, string repoRoot)
    {
        var dir = startDir;
        while (dir is not null && dir.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        // If no .sln found, the info directory's parent is likely the solution root
        return Path.GetDirectoryName(startDir) ?? startDir;
    }
}
