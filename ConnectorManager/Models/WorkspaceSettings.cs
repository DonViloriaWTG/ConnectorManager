namespace ConnectorManager.Models;

/// <summary>
/// Represents the workspace configuration with paths to all CMB repositories.
/// </summary>
public sealed class WorkspaceSettings
{
    public string CommonRepoPath { get; set; } = string.Empty;
    public string FrameworkRepoPath { get; set; } = string.Empty;
    public string CoreRepoPath { get; set; } = string.Empty;
    public string CarrierConnectorRepoPath { get; set; } = string.Empty;
    public string DevToolsRepoPath { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "http://localhost:5000";
    public AuthMode AuthenticationMode { get; set; } = AuthMode.ManualBearer;

    /// <summary>
    /// Raw Authorization header value, e.g. "Basic dXNlcjpwYXNz" or "Bearer eyJhbGci...".
    /// Sent as-is in the Authorization header for API calls.
    /// </summary>
    public string AuthorizationHeader { get; set; } = string.Empty;

    /// <summary>
    /// Attempts to auto-detect CMB repo folders by scanning the given directory
    /// and its immediate subdirectories for CMB.Common, CMB.Framework, CMB.Core,
    /// and CMB.CarrierConnector folders.
    /// </summary>
    public static WorkspaceSettings AutoDetect(string startPath)
    {
        var settings = new WorkspaceSettings();

        // Directories to scan: startPath itself, its parent, and numbered children (1/, 2/, 5/ etc.)
        var candidates = new List<string> { startPath };
        var parent = Path.GetDirectoryName(startPath);
        if (parent is not null)
        {
            candidates.Add(parent);
        }

        // Also scan numbered subdirectories of the parent (e.g. parent/5/CMB.Core)
        if (parent is not null)
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(parent))
                {
                    candidates.Add(sub);
                }
            }
            catch { /* access denied */ }
        }

        string[] repoNames = ["CMB.Common", "CMB.Framework", "CMB.Core", "CMB.CarrierConnector", "CMB.DevTools"];

        foreach (var dir in candidates)
        {
            foreach (var name in repoNames)
            {
                var candidate = Path.Combine(dir, name);
                if (!Directory.Exists(candidate))
                {
                    continue;
                }

                switch (name)
                {
                    case "CMB.Common" when string.IsNullOrEmpty(settings.CommonRepoPath):
                        settings.CommonRepoPath = candidate; break;
                    case "CMB.Framework" when string.IsNullOrEmpty(settings.FrameworkRepoPath):
                        settings.FrameworkRepoPath = candidate; break;
                    case "CMB.Core" when string.IsNullOrEmpty(settings.CoreRepoPath):
                        settings.CoreRepoPath = candidate; break;
                    case "CMB.CarrierConnector" when string.IsNullOrEmpty(settings.CarrierConnectorRepoPath):
                        settings.CarrierConnectorRepoPath = candidate; break;
                    case "CMB.DevTools" when string.IsNullOrEmpty(settings.DevToolsRepoPath):
                        settings.DevToolsRepoPath = candidate; break;
                }
            }
        }

        return settings;
    }

    public bool IsValid =>
        Directory.Exists(CommonRepoPath) &&
        Directory.Exists(FrameworkRepoPath) &&
        Directory.Exists(CoreRepoPath) &&
        Directory.Exists(CarrierConnectorRepoPath);
}

public enum AuthMode
{
    ManualBearer,
    AzureAdCertificate
}
