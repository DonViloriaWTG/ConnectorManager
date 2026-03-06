using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectorManager.Models;
using ConnectorManager.Services;

namespace ConnectorManager.ViewModels;

/// <summary>
/// ViewModel for the Deploy Connector tab.
/// Handles connector search, build, package, and upload workflow.
/// </summary>
public sealed partial class DeployConnectorViewModel : ObservableObject
{
    private readonly ConnectorScanService _scanService = new();
    private readonly ConnectorBuildService _buildService = new();
    private readonly ConnectorPackageService _packageService = new();
    private readonly UploadService _uploadService = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildConnectorCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildAndUploadCommand))]
    [NotifyCanExecuteChangedFor(nameof(DebugConnectorCommand))]
    private ConnectorInfo? _selectedConnector;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _versionOverride = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private int _connectorCount;

    [ObservableProperty]
    private string _outputLogText = string.Empty;

    /// <summary>
    /// Path to the CMB.CarrierConnector repository, editable inline.
    /// </summary>
    [ObservableProperty]
    private string _carrierConnectorRepoPath = string.Empty;

    /// <summary>
    /// The Authorization header value used for API calls.
    /// Editable on the Deploy Connector tab so users can paste auth
    /// without leaving the page (e.g. "Basic dXNlcjpwYXNz").
    /// </summary>
    [ObservableProperty]
    private string _authorizationHeader = string.Empty;

    public ObservableCollection<ConnectorInfo> SearchResults { get; } = [];
    public ObservableCollection<string> OutputLog { get; } = [];

    /// <summary>
    /// Raised when the user changes the CarrierConnector repo path, so MainViewModel can sync it back.
    /// </summary>
    public event Action<string>? CarrierConnectorRepoPathChanged;

    /// <summary>
    /// Raised when the user requests to debug a connector. MainViewModel handles restarting the API
    /// with the debug arguments and waits for it to be ready.
    /// Parameters: (debugPackagePath, debugPackageName) → returns the API process PID (null if failed).
    /// </summary>
    public event Func<string, string, Task<int?>>? DebugConnectorRequested;

    private WorkspaceSettings? _settings;
    private CancellationTokenSource? _cts;

    /// <summary>The API URL override set at runtime when the API Manager detects the real port.</summary>
    private string? _runtimeApiUrl;

    public void Initialize(WorkspaceSettings settings)
    {
        _settings = settings;
        CarrierConnectorRepoPath = settings.CarrierConnectorRepoPath;
        AuthorizationHeader = settings.AuthorizationHeader;
    }

    partial void OnCarrierConnectorRepoPathChanged(string value)
    {
        if (_settings is not null)
        {
            _settings.CarrierConnectorRepoPath = value;
        }
        CarrierConnectorRepoPathChanged?.Invoke(value);
    }

    [RelayCommand]
    private void BrowseCarrierConnector()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select CMB.CarrierConnector Repository Folder"
        };
        if (!string.IsNullOrEmpty(CarrierConnectorRepoPath) && Directory.Exists(CarrierConnectorRepoPath))
        {
            dialog.InitialDirectory = CarrierConnectorRepoPath;
        }
        if (dialog.ShowDialog() == true)
        {
            CarrierConnectorRepoPath = dialog.FolderName;
        }
    }

    /// <summary>
    /// Called by MainViewModel when the API Manager detects the actual listening URL.
    /// </summary>
    public void UpdateApiUrl(string url)
    {
        _runtimeApiUrl = url;
    }

    [RelayCommand]
    private async Task ScanConnectorsAsync()
    {
        if (string.IsNullOrWhiteSpace(CarrierConnectorRepoPath) || !Directory.Exists(CarrierConnectorRepoPath))
        {
            AppendOutput("✖ CarrierConnector repo path not configured.");
            return;
        }

        IsBusy = true;
        StatusText = "Scanning connectors...";
        AppendOutput($"Scanning {CarrierConnectorRepoPath} for connectors...");

        try
        {
            var connectors = await _scanService.ScanAsync(CarrierConnectorRepoPath).ConfigureAwait(true);
            ConnectorCount = connectors.Count;
            AppendOutput($"✔ Found {connectors.Count} connectors.");
            StatusText = $"{connectors.Count} connectors found";
            UpdateSearchResults();
        }
        catch (Exception ex)
        {
            AppendOutput($"✖ Scan failed: {ex.Message}");
            StatusText = "Scan failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        UpdateSearchResults();
    }

    private void UpdateSearchResults()
    {
        SearchResults.Clear();
        var results = _scanService.Search(SearchText);
        foreach (var connector in results)
        {
            SearchResults.Add(connector);
        }
    }

    [RelayCommand(CanExecute = nameof(CanBuildConnector))]
    private async Task BuildConnectorAsync()
    {
        await DoBuildAsync(uploadAfterBuild: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanBuildConnector))]
    private async Task BuildAndUploadAsync()
    {
        await DoBuildAsync(uploadAfterBuild: true).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanBuildConnector))]
    private async Task DebugConnectorAsync()
    {
        if (SelectedConnector is null || _settings is null)
        {
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = "Building connector for debug...";

        try
        {
            var connector = SelectedConnector;
            var connectorName = connector.Name;

            // 1. Copy NuGet packages (same as normal build)
            AppendOutput($"\n{'=',-60}");
            AppendOutput($"🐛 Debug: {connector.DisplayName}");
            AppendOutput($"{'=',-60}");

            // 2. Build in Debug configuration, publish to bin/publish (where DebugManager expects it)
            var publishDir = Path.Combine(connector.SolutionDirectory, "bin", "publish");
            AppendOutput($"  Publish output: {publishDir}");

            var buildResult = await Task.Run(() =>
                _buildService.BuildForDebugAsync(
                    connector,
                    _settings,
                    publishDir,
                    output => App.Current.Dispatcher.BeginInvoke(() => AppendOutput(output)),
                    _cts.Token)).ConfigureAwait(true);

            if (!buildResult.Success)
            {
                StatusText = $"✖ Debug build failed: {buildResult.ErrorMessage}";
                return;
            }

            AppendOutput($"  ✔ Debug build complete. Restarting API with debug args...");
            AppendOutput($"  Package path: {connector.SolutionDirectory}");
            AppendOutput($"  Package name: {connectorName}");

            // 3. Open the connector solution in Visual Studio (gives VS time to load while API starts)
            var slnFile = Directory.GetFiles(connector.SolutionDirectory, "*.sln").FirstOrDefault();
            if (slnFile is not null)
            {
                VisualStudioAutomationService.OpenSolution(slnFile, AppendOutput);
            }
            else
            {
                AppendOutput("  ⚠ No .sln file found in solution directory.");
            }

            // 4. Request the API Manager to restart with debug arguments and wait for it to be ready
            int? apiPid = null;
            if (DebugConnectorRequested is not null)
            {
                AppendOutput("  Waiting for API to be ready...");
                apiPid = await DebugConnectorRequested.Invoke(connector.SolutionDirectory, connectorName).ConfigureAwait(true);
            }
            else
            {
                AppendOutput("  ⚠ Debug event not wired. Start the API manually with debug args.");
            }

            // 5. Auto-attach the VS debugger via COM automation (ROT → DTE → Debugger.Attach)
            if (apiPid is not null && slnFile is not null)
            {
                AppendOutput($"  ✔ API is running (PID: {apiPid})");

                // Find the VS instance that has this solution open (poll for up to 60s)
                var dte = await VisualStudioAutomationService.FindVisualStudioAsync(
                    slnFile,
                    TimeSpan.FromSeconds(60),
                    msg => App.Current.Dispatcher.BeginInvoke(() => AppendOutput(msg))).ConfigureAwait(true);

                if (dte is not null)
                {
                    VisualStudioAutomationService.AttachDebugger(dte, apiPid.Value, (Action<string>)(msg => AppendOutput(msg)));
                }
                else
                {
                    AppendOutput($"  ⚠ Could not find VS instance. Attach manually: Debug → Attach to Process → PID {apiPid}");
                }
            }
            else if (apiPid is not null)
            {
                AppendOutput($"  ✔ API is running (PID: {apiPid}). Attach debugger manually.");
            }
            else
            {
                AppendOutput("  ⚠ API did not start in time. Attach debugger manually.");
            }

            AppendOutput("");
            AppendOutput($"  🐛 Debug session ready for '{connectorName}'.");
            AppendOutput("  Use version 'debug' in API requests.");

            StatusText = $"🐛 Debug mode: {connectorName}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
            AppendOutput("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Error: {ex.Message}";
            AppendOutput($"Unexpected error: {ex}");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanBuildConnector() => SelectedConnector is not null && !IsBusy;

    private async Task DoBuildAsync(bool uploadAfterBuild)
    {
        if (SelectedConnector is null || _settings is null)
        {
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusText = "Building connector...";

        try
        {
            // Build
            var buildResult = await Task.Run(() =>
                _buildService.BuildAsync(
                    SelectedConnector,
                    _settings,
                    output => App.Current.Dispatcher.BeginInvoke(() => AppendOutput(output)),
                    _cts.Token)).ConfigureAwait(true);

            if (!buildResult.Success)
            {
                StatusText = $"✖ Build failed: {buildResult.ErrorMessage}";
                return;
            }

            if (!uploadAfterBuild)
            {
                StatusText = "✔ Build succeeded";
                return;
            }

            // Configure upload service — prefer the runtime-detected URL from API Manager
            var apiUrl = _runtimeApiUrl ?? _settings.ApiBaseUrl;
            _uploadService.Configure(apiUrl, AuthorizationHeader);
            AppendOutput($"  API URL: {apiUrl}");

            // Compute version
            StatusText = "Computing version...";
            string version;
            if (!string.IsNullOrWhiteSpace(VersionOverride))
            {
                version = VersionOverride.Trim();
                AppendOutput($"  Using manual version: {version}");
            }
            else
            {
                version = await _uploadService.ComputeNextVersionAsync(
                    SelectedConnector.Name,
                    SelectedConnector.DeploymentInfo.MajorVersion,
                    SelectedConnector.DeploymentInfo.MinorVersion,
                    _cts.Token).ConfigureAwait(true);
                AppendOutput($"  Auto-computed version: {version}");
            }

            // Package
            StatusText = "Creating package...";
            var zipPath = _packageService.CreatePackage(
                buildResult.PublishDirectory!,
                SelectedConnector.Name,
                version,
                output => App.Current.Dispatcher.BeginInvoke(() => AppendOutput(output)));

            // Upload
            StatusText = "Uploading...";
            var uploadResult = await _uploadService.UploadAsync(
                SelectedConnector.Name,
                version,
                zipPath,
                output => App.Current.Dispatcher.BeginInvoke(() => AppendOutput(output)),
                _cts.Token).ConfigureAwait(true);

            StatusText = uploadResult.Success
                ? $"✔ Uploaded {SelectedConnector.Name} v{version}"
                : $"✖ Upload failed: {uploadResult.ErrorMessage}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
            AppendOutput("Operation cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Error: {ex.Message}";
            AppendOutput($"Unexpected error: {ex}");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    [RelayCommand]
    private void CopyLog()
    {
        var text = string.Join(Environment.NewLine, OutputLog);
        System.Windows.Clipboard.SetText(text);
        StatusText = "Log copied to clipboard";
    }

    private void AppendOutput(string message)
    {
        OutputLog.Add(message);
        OutputLogText = string.IsNullOrEmpty(OutputLogText)
            ? message
            : OutputLogText + Environment.NewLine + message;
    }
}
