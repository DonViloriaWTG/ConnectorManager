using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectorManager.Models;
using ConnectorManager.Services;

namespace ConnectorManager.ViewModels;

/// <summary>
/// Environment options for Elastic data view / index selection.
/// </summary>
public enum SearchEnvironment
{
    Staging,
    Production
}

/// <summary>
/// Predefined time range options for Elasticsearch searches.
/// </summary>
public enum SearchTimeRange
{
    Last24Hours,
    Last7Days,
    Last14Days,
    Last30Days,
    Last90Days
}

/// <summary>
/// ViewModel for the Sample Data tab.
/// Searches Elasticsearch directly (same pattern as CMB.Core) for connector request samples
/// and displays the RequestData for copying.
/// </summary>
public sealed partial class SampleDataViewModel : ObservableObject, IDisposable
{
    private static readonly Dictionary<SearchEnvironment, string> DataViewIds = new()
    {
        [SearchEnvironment.Staging] = "global:logs-domesticdelivery.carriermessagingbuss.staging-wtg",
        [SearchEnvironment.Production] = "global:logs-domesticdelivery.carriermessagingbuss.production-wtg"
    };

    private static readonly Dictionary<SearchTimeRange, (string Label, string EsValue)> TimeRanges = new()
    {
        [SearchTimeRange.Last24Hours] = ("Last 24 hours", "now-1d"),
        [SearchTimeRange.Last7Days] = ("Last 7 days", "now-1w"),
        [SearchTimeRange.Last14Days] = ("Last 14 days", "now-2w"),
        [SearchTimeRange.Last30Days] = ("Last 30 days", "now-1M"),
        [SearchTimeRange.Last90Days] = ("Last 90 days", "now-3M"),
    };

    private readonly ElasticSearchService _searchService = new();
    private CancellationTokenSource? _cts;

    // ── Elastic credentials (set from Settings) ─────────────────────

    /// <summary>Elasticsearch user ID for Basic Auth.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchElasticCommand))]
    private string _elasticUserId = string.Empty;

    /// <summary>Elasticsearch password for Basic Auth.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchElasticCommand))]
    private string _elasticPassword = string.Empty;

    /// <summary>Elastic cluster region.</summary>
    [ObservableProperty]
    private ElasticRegion _elasticRegion = ElasticRegion.APAC;

    // ── Connector name ──────────────────────────────────────────────

    /// <summary>
    /// The connector (PackageName) to search for. Editable ComboBox text.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchElasticCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInKibanaCommand))]
    [NotifyCanExecuteChangedFor(nameof(CopyUrlCommand))]
    private string _connectorName = string.Empty;

    /// <summary>Connector names populated from the Deploy tab scan, for ComboBox dropdown.</summary>
    public ObservableCollection<string> ConnectorNames { get; } = [];

    // ── Search options ──────────────────────────────────────────────

    [ObservableProperty]
    private SearchEnvironment _selectedEnvironment = SearchEnvironment.Staging;

    [ObservableProperty]
    private SearchTimeRange _selectedTimeRange = SearchTimeRange.Last30Days;

    [ObservableProperty]
    private int _maxResults = 10;

    // ── State ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchElasticCommand))]
    private bool _isSearching;

    [ObservableProperty]
    private string _statusText = "Select a connector name and click Search.";

    // ── Results ─────────────────────────────────────────────────────

    public ObservableCollection<SampleDataResult> Results { get; } = [];

    [ObservableProperty]
    private SampleDataResult? _selectedResult;

    /// <summary>The RequestData content of the currently selected result, for display and editing.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyRequestDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearRequestDataCommand))]
    private string _requestDataContent = string.Empty;

    // ── Static option sources for UI ────────────────────────────────

    public static IReadOnlyList<KeyValuePair<SearchTimeRange, string>> TimeRangeOptions { get; } =
        TimeRanges.Select(kv => new KeyValuePair<SearchTimeRange, string>(kv.Key, kv.Value.Label)).ToList();

    public static IReadOnlyList<ElasticRegion> RegionOptions { get; } =
        [ElasticRegion.APAC, ElasticRegion.AMER, ElasticRegion.EMEA];

    // ── Change handlers ─────────────────────────────────────────────

    partial void OnSelectedResultChanged(SampleDataResult? value)
    {
        RequestDataContent = value?.RequestData ?? string.Empty;
    }

    // ── Public methods ──────────────────────────────────────────────

    /// <summary>
    /// Updates the available connector names. Called when connectors are scanned on the Deploy tab.
    /// </summary>
    public void UpdateConnectors(IReadOnlyList<ConnectorInfo> connectors)
    {
        ConnectorNames.Clear();
        foreach (var c in connectors.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!ConnectorNames.Contains(c.Name))
            {
                ConnectorNames.Add(c.Name);
            }
        }
    }

    /// <summary>
    /// Applies Elastic credentials from saved settings.
    /// Called when settings are loaded or saved.
    /// </summary>
    public void ApplySettings(WorkspaceSettings settings)
    {
        ElasticUserId = settings.ElasticUserId;
        ElasticPassword = settings.ElasticPassword;

        if (Enum.TryParse<ElasticRegion>(settings.ElasticRegion, ignoreCase: true, out var region))
        {
            ElasticRegion = region;
        }
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchElasticAsync()
    {
        var name = ConnectorName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        IsSearching = true;
        Results.Clear();
        SelectedResult = null;
        RequestDataContent = string.Empty;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        var envLabel = SelectedEnvironment == SearchEnvironment.Staging ? "Staging" : "Production";
        StatusText = $"Searching {envLabel} ({ElasticRegion}) for '{name}'...";

        try
        {
            var environment = SelectedEnvironment == SearchEnvironment.Staging ? "staging" : "production";
            var timeFrom = TimeRanges[SelectedTimeRange].EsValue;

            var result = await _searchService.SearchAsync(
                ElasticRegion,
                environment,
                name,
                timeFrom,
                MaxResults > 0 ? MaxResults : 10,
                ElasticUserId.Trim(),
                ElasticPassword,
                _cts.Token).ConfigureAwait(true);

            if (result.Success)
            {
                foreach (var r in result.Results)
                {
                    Results.Add(r);
                }

                if (Results.Count > 0)
                {
                    SelectedResult = Results[0];
                }

                var timeLabel = TimeRanges[SelectedTimeRange].Label;
                StatusText = result.TotalHits > result.Results.Count
                    ? $"✔ Showing {result.Results.Count} of {result.TotalHits} results for '{name}' in {envLabel} ({timeLabel})"
                    : $"✔ Found {result.Results.Count} results for '{name}' in {envLabel} ({timeLabel})";

                if (Results.Count == 0)
                {
                    StatusText = $"No results found for '{name}' in {envLabel} ({timeLabel}). Try a wider time range or check the connector name.";
                }
            }
            else
            {
                StatusText = $"✖ Search failed: {result.ErrorMessage}";
                if (!string.IsNullOrEmpty(result.RawResponse))
                {
                    RequestDataContent = $"--- Raw Response ---\n{result.RawResponse}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private bool CanSearch() =>
        !IsSearching &&
        !string.IsNullOrWhiteSpace(ConnectorName) &&
        !string.IsNullOrWhiteSpace(ElasticUserId) &&
        !string.IsNullOrWhiteSpace(ElasticPassword);

    [RelayCommand(CanExecute = nameof(HasConnectorName))]
    private void OpenInKibana()
    {
        var url = BuildKibanaDiscoverUrl();
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            StatusText = $"✔ Opened Kibana for '{ConnectorName.Trim()}' in default browser.";
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Failed to open browser: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasConnectorName))]
    private void CopyUrl()
    {
        var url = BuildKibanaDiscoverUrl();
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            Clipboard.SetText(url);
            StatusText = "✔ Kibana URL copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Failed to copy: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasRequestData))]
    private void CopyRequestData()
    {
        if (string.IsNullOrWhiteSpace(RequestDataContent)) return;

        try
        {
            Clipboard.SetText(RequestDataContent);
            StatusText = "✔ Request data copied to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Failed to copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearRequestData()
    {
        RequestDataContent = string.Empty;
        SelectedResult = null;
        Results.Clear();
        StatusText = "Cleared.";
    }

    private bool HasConnectorName() => !string.IsNullOrWhiteSpace(ConnectorName);
    private bool HasRequestData() => !string.IsNullOrWhiteSpace(RequestDataContent);

    // ── Kibana Discover URL builder (fallback / Open in Kibana) ─────

    private string BuildKibanaDiscoverUrl()
    {
        var name = ConnectorName.Trim();
        if (string.IsNullOrEmpty(name)) return string.Empty;

        // Build Kibana base URL using the selected region
        var regionSlug = ElasticRegion.ToString().ToLowerInvariant();
        var kibanaBaseUrl = $"https://kibana.{regionSlug}-prod-1.wtg.zone/s/domesticdelivery";

        var dataViewId = DataViewIds[SelectedEnvironment];
        var timeFrom = TimeRanges[SelectedTimeRange].EsValue;

        var kqlQuery = $"CMB.Properties.RequestData: * and CMB.Properties.RequestPath: /api/execution/{name}/*";

        var globalState = $"_g=(filters:!(),refreshInterval:(pause:!t,value:60000),time:(from:{timeFrom},to:now))";

        var appState =
            "_a=(" +
            "columns:!(CMB.Properties.RequestPath,CMB.Properties.RequestData)," +
            $"dataSource:(dataViewId:'{dataViewId}',type:dataView)," +
            "filters:!(" +
                "('$state':(store:appState)," +
                $"meta:(alias:!n,disabled:!f,index:'{dataViewId}'," +
                $"key:CMB.Properties.PackageName,negate:!f," +
                $"params:(query:{name}),type:phrase)," +
                $"query:(match_phrase:(CMB.Properties.PackageName:{name})))" +
            ")," +
            "interval:auto," +
            $"query:(language:kuery,query:'{kqlQuery}')," +
            "sort:!(!('@timestamp',desc)))";

        return $"{kibanaBaseUrl}/app/discover#/?{globalState}&{appState}";
    }

    // ── Cleanup ─────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _searchService.Dispose();
    }
}
