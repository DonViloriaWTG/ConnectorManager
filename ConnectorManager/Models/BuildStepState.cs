namespace ConnectorManager.Models;

/// <summary>
/// Represents the status of a build step in the chain.
/// </summary>
public enum BuildStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped
}

/// <summary>
/// Tracks the state of a single build step.
/// </summary>
public sealed class BuildStepState
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public BuildStepStatus Status { get; set; } = BuildStepStatus.Pending;
    public string Output { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
    public string? ErrorMessage { get; set; }
}
