namespace ConnectorManager.Models;

/// <summary>
/// Represents a single search result from Kibana containing a connector's request data.
/// </summary>
public sealed class SampleDataResult
{
    /// <summary>Formatted local timestamp of the log entry.</summary>
    public string Timestamp { get; init; } = string.Empty;

    /// <summary>The API request path, e.g. /api/execution/BlueStar/booking.</summary>
    public string RequestPath { get; init; } = string.Empty;

    /// <summary>The raw request body data (JSON or XML).</summary>
    public string RequestData { get; init; } = string.Empty;

    /// <summary>Display string for the results list.</summary>
    public string Summary => string.IsNullOrEmpty(RequestPath)
        ? Timestamp
        : $"[{Timestamp}]  {RequestPath}";
}
