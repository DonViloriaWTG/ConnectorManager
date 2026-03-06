namespace ConnectorManager.Models;

/// <summary>
/// Represents a discovered carrier connector in the workspace with its metadata and file paths.
/// </summary>
public sealed class ConnectorInfo
{
    /// <summary>The connector identity name from ConnectorDeploymentInfo.json.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Region derived from folder path (e.g., "Australia", "UK").</summary>
    public string Region { get; init; } = string.Empty;

    /// <summary>Display string: "Region / Name".</summary>
    public string DisplayName => string.IsNullOrEmpty(Region) ? Name : $"{Region} / {Name}";

    /// <summary>Absolute path to the .csproj file.</summary>
    public string ProjectPath { get; init; } = string.Empty;

    /// <summary>Absolute path to the directory containing ConnectorDeploymentInfo.json.</summary>
    public string DeploymentInfoDirectory { get; init; } = string.Empty;

    /// <summary>Absolute path to the connector solution directory (parent of the project folder).</summary>
    public string SolutionDirectory { get; init; } = string.Empty;

    /// <summary>The parsed deployment info.</summary>
    public ConnectorDeploymentInfo DeploymentInfo { get; init; } = new();

    /// <summary>Version string "{major}.{minor}".</summary>
    public string VersionPrefix => $"{DeploymentInfo.MajorVersion}.{DeploymentInfo.MinorVersion}";

    public override string ToString() => DisplayName;
}
