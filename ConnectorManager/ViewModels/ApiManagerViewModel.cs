using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectorManager.Models;
using ConnectorManager.Services;
using Microsoft.Win32;

namespace ConnectorManager.ViewModels;

/// <summary>
/// ViewModel for the API Manager tab. Controls the local CMB API lifecycle.
/// </summary>
public sealed partial class ApiManagerViewModel : ObservableObject, IDisposable
{
    private readonly ApiProcessService _apiService = new();
    private readonly PayloadCaptureService _payloadCapture = new();

    [ObservableProperty]
    private ApiStatus _status = ApiStatus.Stopped;

    [ObservableProperty]
    private string _statusText = "Stopped";

    [ObservableProperty]
    private string _apiUrl = "(not started)";

    /// <summary>
    /// The CMB.Core repository path used to launch the API.
    /// Editable so the user can override it directly on this tab.
    /// </summary>
    [ObservableProperty]
    private string _coreRepoPath = string.Empty;

    /// <summary>
    /// Raised when the user changes the Core repo path on this tab,
    /// so the main VM can sync it back to Settings.
    /// </summary>
    public event Action<string>? CoreRepoPathChanged;

    partial void OnCoreRepoPathChanged(string value)
    {
        if (_settings is not null)
        {
            _settings.CoreRepoPath = value;
        }

        CoreRepoPathChanged?.Invoke(value);
    }

    [ObservableProperty]
    private string _outputLogText = string.Empty;

    public ObservableCollection<string> OutputLog { get; } = [];

    /// <summary>
    /// Carrier payloads captured from the API's structured log files.
    /// </summary>
    public ObservableCollection<CapturedPayload> CapturedPayloads { get; } = [];

    /// <summary>
    /// The currently selected payload in the list — its content is displayed in the detail view.
    /// </summary>
    [ObservableProperty]
    private CapturedPayload? _selectedPayload;

    private WorkspaceSettings? _settings;
    private TaskCompletionSource<int?>? _readyTcs;

    public ApiManagerViewModel()
    {
        _apiService.OutputReceived += message =>
            App.Current.Dispatcher.BeginInvoke(() => AppendOutput(message));

        _apiService.StatusChanged += status =>
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                Status = status;
                StatusText = status switch
                {
                    ApiStatus.Stopped => "Stopped",
                    ApiStatus.Starting => "Starting...",
                    ApiStatus.Running => "Running",
                    _ => "Unknown"
                };

                // If the API stopped unexpectedly while we're waiting for it, unblock the waiter
                if (status == ApiStatus.Stopped)
                {
                    _readyTcs?.TrySetResult(null);
                }
            });

        _apiService.BaseUrlDetected += url =>
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                ApiUrl = url;
                // Return the actual server PID (child of dotnet run), not the CLI wrapper
                _readyTcs?.TrySetResult(_apiService.ActualServerProcessId ?? _apiService.ProcessId);
            });

        _payloadCapture.PayloadCaptured += payload =>
            App.Current.Dispatcher.BeginInvoke(() =>
            {
                CapturedPayloads.Add(payload);
                SelectedPayload = payload;
            });

        _payloadCapture.DiagnosticMessage += message =>
            App.Current.Dispatcher.BeginInvoke(() => AppendOutput(message));
    }

    [RelayCommand]
    private void BrowseCoreRepo()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select CMB.Core repository folder",
            InitialDirectory = Directory.Exists(CoreRepoPath) ? CoreRepoPath : string.Empty
        };

        if (dialog.ShowDialog() == true)
        {
            CoreRepoPath = dialog.FolderName;
        }
    }

    public void Initialize(WorkspaceSettings settings)
    {
        _settings = settings;
        CoreRepoPath = settings.CoreRepoPath;
    }

    public bool IsRunning => _apiService.IsRunning;

    /// <summary>The PID of the actual web server process (for debugger attachment).</summary>
    public int? ApiProcessId => _apiService.ActualServerProcessId ?? _apiService.ProcessId;

    [RelayCommand]
    private async Task StartApiAsync()
    {
        if (_settings is null || !Directory.Exists(_settings.CoreRepoPath))
        {
            AppendOutput("✖ Core repo path is not configured. Please set it in Settings.");
            return;
        }

        OutputLog.Clear();
        OutputLogText = string.Empty;
        StartPayloadCapture();
        await _apiService.StartAsync(_settings.CoreRepoPath).ConfigureAwait(true);
    }

    /// <summary>
    /// Restarts the API in debug mode for a specific connector.
    /// Stops any running instance, then launches with debug arguments.
    /// </summary>
    public async Task StartWithDebugAsync(string debugPackagePath, string debugPackageName)
    {
        if (_settings is null || !Directory.Exists(_settings.CoreRepoPath))
        {
            AppendOutput("✖ Core repo path is not configured.");
            return;
        }

        if (IsRunning)
        {
            AppendOutput("Stopping current API instance and waiting for port release...");
            await _apiService.StopAndWaitForPortReleaseAsync().ConfigureAwait(true);
            ApiUrl = "(not started)";
        }

        OutputLog.Clear();
        OutputLogText = string.Empty;
        StartPayloadCapture();
        await _apiService.StartWithDebugAsync(_settings.CoreRepoPath, debugPackagePath, debugPackageName).ConfigureAwait(true);
    }

    /// <summary>
    /// Waits until the API reports it is listening, then returns the process PID.
    /// Returns null if the API fails to start within the timeout or stops unexpectedly.
    /// </summary>
    public async Task<int?> WaitForReadyAsync(TimeSpan timeout)
    {
        // Already running
        if (_apiService.IsRunning && _apiService.DetectedHttpUrl is not null && _apiService.ProcessId is not null)
        {
            return _apiService.ProcessId;
        }

        _readyTcs = new TaskCompletionSource<int?>();
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => _readyTcs.TrySetResult(null));

        return await _readyTcs.Task.ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task StopApiAsync()
    {
        await _apiService.StopAsync().ConfigureAwait(true);
        ApiUrl = "(not started)";
    }

    [RelayCommand]
    private async Task RestartApiAsync()
    {
        await StopApiAsync().ConfigureAwait(true);
        await Task.Delay(1000).ConfigureAwait(true);
        await StartApiAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void CopyLog()
    {
        var text = string.Join(Environment.NewLine, OutputLog);
        if (!string.IsNullOrEmpty(text))
        {
            System.Windows.Clipboard.SetText(text);
        }
    }

    [RelayCommand]
    private void CopyPayload()
    {
        if (SelectedPayload?.Content is not null)
        {
            System.Windows.Clipboard.SetText(SelectedPayload.Content);
        }
    }

    [RelayCommand]
    private void ClearPayloads()
    {
        CapturedPayloads.Clear();
        SelectedPayload = null;
    }

    private void StartPayloadCapture()
    {
        if (_settings is not null &&
            !string.IsNullOrEmpty(_settings.CoreRepoPath) &&
            Directory.Exists(_settings.CoreRepoPath))
        {
            _payloadCapture.Start(_settings.CoreRepoPath);
        }
    }

    private void AppendOutput(string message)
    {
        OutputLog.Add(message);
        OutputLogText = string.IsNullOrEmpty(OutputLogText)
            ? message
            : OutputLogText + Environment.NewLine + message;
    }

    public void Dispose()
    {
        _payloadCapture.Dispose();
        _apiService.Dispose();
    }
}
