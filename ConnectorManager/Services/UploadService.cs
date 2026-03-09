using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ConnectorManager.Services;

/// <summary>
/// Handles uploading connector packages to the CMB API.
/// Manages the full upload lifecycle: check/create package, upload ZIP, set alias.
///
/// API contract notes (from PackageController.cs):
///   POST /api/package/info                          — Consumes application/xml, body is bare string
///   POST /api/package/upload/{name}/{version}       — Consumes application/zip | application/octet-stream, [InternalSystemAuthorize]
///   POST /api/package/addAlias                      — Consumes application/json, body is PackageAliasInfo (PascalCase)
///   GET  /api/package/CheckPackageExists/{name}     — Returns ResponseResult&lt;bool&gt;
///   GET  /api/package/GetPackageVersionsList/{name}  — Returns ResponseResult&lt;IEnumerable&lt;PackageVersion&gt;&gt;
///
/// All responses are wrapped in ResponseResult { IsSuccess, Message, Value }.
/// </summary>
public sealed class UploadService : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private string _baseUrl = "http://localhost:5000";

    /// <summary>
    /// Configures the service with the API base URL and a raw Authorization header value.
    /// The value should include the scheme, e.g. "Basic dXNlcjpwYXNz" or "Bearer eyJhbGci...".
    /// </summary>
    public void Configure(string baseUrl, string authorizationHeader)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient.DefaultRequestHeaders.Remove("Authorization");

        if (!string.IsNullOrWhiteSpace(authorizationHeader))
        {
            // Parse "Scheme Value" format
            var trimmed = authorizationHeader.Trim();
            var spaceIndex = trimmed.IndexOf(' ');
            if (spaceIndex > 0)
            {
                var scheme = trimmed[..spaceIndex];
                var parameter = trimmed[(spaceIndex + 1)..];
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(scheme, parameter);
            }
            else
            {
                // Assume Bearer if no scheme prefix
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", trimmed);
            }
        }
    }

    /// <summary>
    /// Performs the full upload workflow:
    /// 1. Check if package exists; if not, create it
    /// 2. Upload the ZIP file
    /// 3. Set the "latest" alias
    /// </summary>
    public async Task<UploadResult> UploadAsync(
        string connectorName,
        string version,
        string zipFilePath,
        Action<string> onOutput,
        CancellationToken cancellationToken = default)
    {
        onOutput($"\n{'=',-60}");
        onOutput($"▶ Uploading {connectorName} v{version} to {_baseUrl}");
        onOutput($"{'=',-60}");

        try
        {
            // Step 1: Check if package exists
            var exists = await CheckPackageExistsAsync(connectorName, onOutput, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                onOutput($"  Package '{connectorName}' does not exist. Creating...");
                await CreatePackageAsync(connectorName, onOutput, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                onOutput($"  Package '{connectorName}' already exists.");
            }

            // Step 2: Upload ZIP
            onOutput($"  Uploading ZIP ({new FileInfo(zipFilePath).Length / 1024}KB)...");
            await UploadZipAsync(connectorName, version, zipFilePath, onOutput, cancellationToken).ConfigureAwait(false);

            // Step 3: Set alias
            onOutput("  Setting 'latest' alias...");
            await SetAliasAsync(connectorName, version, onOutput, cancellationToken).ConfigureAwait(false);

            onOutput($"  ✔ Upload completed successfully: {connectorName} v{version}");
            return new UploadResult { Success = true };
        }
        catch (Exception ex)
        {
            onOutput($"  ✖ Upload failed: {ex.Message}");
            return new UploadResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Gets the latest version number for a package to support auto-increment.
    /// Returns null if the package has no versions.
    ///
    /// API: GET /api/package/GetPackageVersionsList/{name}
    /// Response: ResponseResult&lt;IEnumerable&lt;PackageVersion&gt;&gt;
    ///   where PackageVersion has "PackageVersion" (the version string) and "Summary".
    /// </summary>
    public async Task<string?> GetLatestVersionAsync(
        string connectorName,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_baseUrl}/api/package/GetPackageVersionsList/{Uri.EscapeDataString(connectorName)}";
        var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var wrapper = JObject.Parse(content);

        // ResponseResult wraps the list in "value"
        var valueToken = wrapper["value"];
        if (valueToken is null || valueToken.Type == JTokenType.Null)
        {
            return null;
        }

        var versions = valueToken.ToObject<List<PackageVersionEntry>>();
        if (versions is null || versions.Count == 0)
        {
            return null;
        }

        return versions
            .OrderByDescending(v => v.Version)
            .Select(v => v.Version)
            .FirstOrDefault();
    }

    /// <summary>
    /// Computes the next version based on the connector's major.minor prefix and the
    /// latest deployed version (auto-incrementing the build number).
    /// </summary>
    public async Task<string> ComputeNextVersionAsync(
        string connectorName,
        int majorVersion,
        int minorVersion,
        CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestVersionAsync(connectorName, cancellationToken).ConfigureAwait(false);
        int buildNumber = 1;
        if (latest is not null)
        {
            var parts = latest.Split('.');
            if (parts.Length >= 3 && int.TryParse(parts[2], out var lastBuild))
            {
                buildNumber = lastBuild + 1;
            }
        }

        return $"{majorVersion}.{minorVersion}.{buildNumber}";
    }

    /// <summary>
    /// API: GET /api/package/CheckPackageExists/{name}
    /// Response: ResponseResult&lt;bool&gt; — the actual bool is in "value".
    /// </summary>
    private async Task<bool> CheckPackageExistsAsync(string name, Action<string> onOutput, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/package/CheckPackageExists/{Uri.EscapeDataString(name)}";
        onOutput($"  GET {url}");
        var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var wrapper = JObject.Parse(content);
        return wrapper["value"]?.Value<bool>() == true;
    }

    /// <summary>
    /// API: POST /api/package/info
    /// API contract has changed over time:
    /// - Newer servers expect JSON (either a DTO with PackageName, or a bare JSON string)
    /// - Older servers expected application/xml with an XML-serialized string payload
    ///
    /// We attempt JSON first, then fall back to legacy XML for compatibility.
    /// </summary>
    private async Task CreatePackageAsync(string name, Action<string> onOutput, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/package/info";
        onOutput($"  POST {url}");

        // Server contract (CMB.Core PackageController):
        //   POST /api/package/info
        //   [Consumes("application/xml")] and binds [FromBody] string packageName
        // In practice, the server expects the *raw package name text* in the body.
        // If we send XML-wrapped values (e.g. <string>...</string>) the regex validator
        // sees the angle brackets and rejects it.
        //
        // We still keep a few fallback shapes for older/newer servers.
        // 1) application/xml raw text
        // 2) JSON object: { "PackageName": "MainFreightGlobal" }
        // 3) JSON string: "MainFreightGlobal"
        // 4) XML-wrapped string payload
        var jsonObjectBody = JsonConvert.SerializeObject(new { PackageName = name });
        var jsonStringBody = JsonConvert.SerializeObject(name);

        var xmlRawTextBody = name;
        var xmlBody = $"<string xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/\">{System.Security.SecurityElement.Escape(name)}</string>";

        (HttpStatusCode status, string body)? lastFailure = null;

        foreach (var attempt in new (string MediaType, string Body, string Label)[]
                 {
                     ("application/xml", xmlRawTextBody, "xml-rawtext"),
                     ("application/json", jsonObjectBody, "json-object"),
                     ("application/json", jsonStringBody, "json-string"),
                     ("application/xml", xmlBody, "xml-string"),
                 })
        {
            using var requestContent = new StringContent(attempt.Body, Encoding.UTF8, attempt.MediaType);
            var response = await _httpClient.PostAsync(url, requestContent, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                onOutput($"  ✔ Package created ({attempt.Label}).");
                return;
            }

            lastFailure = (response.StatusCode, body);

            // If the server doesn't like the content-type or binding shape, try the next format.
            if (response.StatusCode is HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest)
            {
                onOutput($"  ⚠ CreatePackage returned {response.StatusCode} using {attempt.Label}; retrying...");
                continue;
            }

            // Other failures are unlikely to be solved by switching body format.
            throw new HttpRequestException($"CreatePackage failed ({response.StatusCode}): {body}");
        }

        if (lastFailure is not null)
        {
            throw new HttpRequestException($"CreatePackage failed ({lastFailure.Value.status}): {lastFailure.Value.body}");
        }

        throw new HttpRequestException("CreatePackage failed: no response.");
    }

    /// <summary>
    /// API: POST /api/package/upload/{name}/{version}
    /// Controller declares [Consumes("application/zip", "application/octet-stream")].
    /// Protected by [InternalSystemAuthorize] — in Development environment this passes
    /// for any authenticated request.
    /// </summary>
    private async Task UploadZipAsync(string name, string version, string zipPath, Action<string> onOutput, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/package/upload/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}";
        onOutput($"  POST {url}");

        using var fileStream = File.OpenRead(zipPath);
        using var content = new StreamContent(fileStream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        var response = await _httpClient.PostAsync(url, content, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Upload failed ({response.StatusCode}): {responseBody}");
        }

        onOutput("  ✔ ZIP uploaded.");
    }

    /// <summary>
    /// API: POST /api/package/addAlias
    /// Controller declares [Consumes("application/json")] with [FromBody] PackageAliasInfo.
    /// PackageAliasInfo uses System.Text.Json [JsonPropertyName] with PascalCase:
    ///   PackageName, PackageVersion, PackageAlias
    /// </summary>
    private async Task SetAliasAsync(string name, string version, Action<string> onOutput, CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/package/addAlias";
        var body = JsonConvert.SerializeObject(new
        {
            PackageName = name,
            PackageVersion = version,
            PackageAlias = "latest"
        });
        onOutput($"  POST {url}");
        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(url, requestContent, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            onOutput($"  ⚠ Alias set returned {response.StatusCode}: {responseBody}");
        }
        else
        {
            onOutput("  ✔ Alias 'latest' set.");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    /// <summary>
    /// Maps the "PackageVersion" JSON property from the GetPackageVersionsList response.
    /// </summary>
    private sealed class PackageVersionEntry
    {
        [JsonProperty("PackageVersion")]
        public string Version { get; set; } = string.Empty;
    }
}

public sealed class UploadResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}
