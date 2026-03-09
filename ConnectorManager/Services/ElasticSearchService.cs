using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ConnectorManager.Models;

namespace ConnectorManager.Services;

/// <summary>
/// Region identifier for Elasticsearch cluster selection.
/// </summary>
public enum ElasticRegion
{
    APAC,
    AMER,
    EMEA
}

/// <summary>
/// Searches Elasticsearch directly using the _search API with Basic Auth.
/// Mirrors the pattern used by CMB.Core's ElasticSearchClient.
/// </summary>
public sealed class ElasticSearchService : IDisposable
{
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Endpoint pattern: https://elastic.{region}-prod-1.wtg.zone:443/*logs-domesticdelivery.carriermessagingbuss.{env}*/_search
    /// </summary>
    private const string EndpointTemplate =
        "https://elastic.{0}-prod-1.wtg.zone:443/*logs-domesticdelivery.carriermessagingbuss.{1}*/_search";

    private static readonly Dictionary<ElasticRegion, string> RegionSlugs = new()
    {
        [ElasticRegion.APAC] = "apac",
        [ElasticRegion.AMER] = "amer",
        [ElasticRegion.EMEA] = "emea"
    };

    public ElasticSearchService()
    {
        var handler = new HttpClientHandler
        {
            // Enterprise environments may use custom/self-signed certificates
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Searches Elasticsearch for connector request data matching the specified criteria.
    /// </summary>
    /// <param name="region">The Elastic cluster region (APAC, AMER, EMEA)</param>
    /// <param name="environment">Staging or Production</param>
    /// <param name="connectorName">The CMB.Properties.PackageName value to filter on</param>
    /// <param name="timeFrom">Elasticsearch time expression, e.g. "now-30d"</param>
    /// <param name="maxResults">Maximum number of results to return</param>
    /// <param name="userId">Elasticsearch user ID for Basic Auth</param>
    /// <param name="password">Elasticsearch password for Basic Auth</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<ElasticSearchResult> SearchAsync(
        ElasticRegion region,
        string environment,
        string connectorName,
        string timeFrom,
        int maxResults,
        string userId,
        string password,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var regionSlug = RegionSlugs[region];
        var endpoint = string.Format(EndpointTemplate, regionSlug, environment);

        var escapedName = EscapeJsonString(connectorName);
        var searchBody = BuildSearchBody(escapedName, timeFrom, maxResults);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(searchBody, Encoding.UTF8, "application/json");

        // Basic Auth — same pattern as CMB.Core ElasticSearchClient
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{userId}:{password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            return new ElasticSearchResult { Success = false, ErrorMessage = "Search cancelled." };
        }
        catch (TaskCanceledException)
        {
            return new ElasticSearchResult { Success = false, ErrorMessage = "Request timed out (60s)." };
        }
        catch (HttpRequestException ex)
        {
            return new ElasticSearchResult { Success = false, ErrorMessage = $"Connection failed: {ex.Message}" };
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = TryExtractErrorMessage(responseBody) ?? responseBody;
            return new ElasticSearchResult
            {
                Success = false,
                ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(detail, 500)}",
                RawResponse = responseBody
            };
        }

        return ParseResponse(responseBody);
    }

    /// <summary>
    /// Builds the Elasticsearch query DSL body.
    /// </summary>
    private static string BuildSearchBody(string connectorName, string timeFrom, int maxResults)
    {
        return $$"""
        {
          "size": {{maxResults}},
          "query": {
            "bool": {
              "filter": [
                { "match_phrase": { "CMB.Properties.PackageName": "{{connectorName}}" } },
                { "exists": { "field": "CMB.Properties.RequestData" } },
                { "wildcard": { "CMB.Properties.RequestPath": { "value": "/api/execution/{{connectorName}}/*" } } },
                { "range": { "@timestamp": { "gte": "{{timeFrom}}", "lte": "now" } } }
              ]
            }
          },
          "sort": [{ "@timestamp": { "order": "desc" } }],
          "_source": ["@timestamp", "CMB.Properties.RequestPath", "CMB.Properties.RequestData", "CMB.Properties.PackageName"]
        }
        """;
    }

    /// <summary>
    /// Parses the Elasticsearch _search response into structured results.
    /// </summary>
    private static ElasticSearchResult ParseResponse(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            // Direct Elasticsearch response: { "hits": { "total": { "value": N }, "hits": [...] } }
            if (!root.TryGetProperty("hits", out var outerHits) ||
                !outerHits.TryGetProperty("hits", out var hitsArray))
            {
                return new ElasticSearchResult
                {
                    Success = false,
                    ErrorMessage = "Unexpected response structure — could not find hits array.",
                    RawResponse = responseBody
                };
            }

            var results = new List<SampleDataResult>();
            foreach (var hit in hitsArray.EnumerateArray())
            {
                var result = ParseHit(hit);
                if (result is not null)
                {
                    results.Add(result);
                }
            }

            int totalHits = results.Count;
            if (outerHits.TryGetProperty("total", out var total))
            {
                if (total.ValueKind == JsonValueKind.Object && total.TryGetProperty("value", out var val))
                    totalHits = val.GetInt32();
                else if (total.ValueKind == JsonValueKind.Number)
                    totalHits = total.GetInt32();
            }

            return new ElasticSearchResult
            {
                Success = true,
                TotalHits = totalHits,
                Results = results,
                RawResponse = responseBody
            };
        }
        catch (JsonException ex)
        {
            return new ElasticSearchResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse response: {ex.Message}",
                RawResponse = responseBody
            };
        }
    }

    /// <summary>
    /// Parses a single hit from _source format.
    /// </summary>
    private static SampleDataResult? ParseHit(JsonElement hit)
    {
        if (hit.TryGetProperty("_source", out var source))
        {
            var requestData = GetStringProp(source, "CMB.Properties.RequestData");
            if (string.IsNullOrWhiteSpace(requestData))
                return null;

            return new SampleDataResult
            {
                Timestamp = FormatTimestamp(GetStringProp(source, "@timestamp")),
                RequestPath = GetStringProp(source, "CMB.Properties.RequestPath"),
                RequestData = PrettyPrintJson(requestData)
            };
        }

        // Fallback: fields format { "fields": { "field": ["value"] } }
        if (hit.TryGetProperty("fields", out var fields))
        {
            var requestData = GetFirstArrayValue(fields, "CMB.Properties.RequestData");
            if (string.IsNullOrWhiteSpace(requestData))
                return null;

            return new SampleDataResult
            {
                Timestamp = FormatTimestamp(GetFirstArrayValue(fields, "@timestamp")),
                RequestPath = GetFirstArrayValue(fields, "CMB.Properties.RequestPath"),
                RequestData = PrettyPrintJson(requestData)
            };
        }

        return null;
    }

    private static string GetStringProp(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var val) ? val.GetString() ?? "" : "";
    }

    private static string GetFirstArrayValue(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var arr) &&
            arr.ValueKind == JsonValueKind.Array &&
            arr.GetArrayLength() > 0)
        {
            return arr[0].GetString() ?? "";
        }
        return "";
    }

    /// <summary>
    /// Formats an ISO timestamp to local time for display.
    /// </summary>
    private static string FormatTimestamp(string timestamp)
    {
        if (DateTimeOffset.TryParse(timestamp, out var dto))
        {
            return dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
        return timestamp;
    }

    /// <summary>
    /// Pretty-prints JSON if valid; returns as-is otherwise.
    /// </summary>
    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }

    private static string? TryExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString();
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString();
                if (err.TryGetProperty("message", out var errMsg))
                    return errMsg.GetString();
                if (err.TryGetProperty("reason", out var reason))
                    return reason.GetString();
            }
        }
        catch { /* ignore parse errors */ }
        return null;
    }

    /// <summary>
    /// Escapes a string for safe inclusion in a JSON string literal.
    /// </summary>
    private static string EscapeJsonString(string value)
    {
        var escaped = JsonSerializer.Serialize(value);
        return escaped.Length >= 2 ? escaped[1..^1] : value;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Result of an Elasticsearch search operation.
/// </summary>
public sealed class ElasticSearchResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int TotalHits { get; init; }
    public IReadOnlyList<SampleDataResult> Results { get; init; } = [];
    public string? RawResponse { get; init; }
}
