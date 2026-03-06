using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ConnectorManager.Models;
using ConnectorManager.Services;

namespace ConnectorManager.ViewModels;

/// <summary>
/// ViewModel for the Build Chain tab. Drives the full Common → Framework → Core build pipeline.
/// Exposes editable repository paths so the user can confirm / override them before building.
/// </summary>
public sealed partial class BuildChainViewModel : ObservableObject
{
    private readonly BuildChainService _buildChainService = new();

    // ── Repo path properties (shown inline in the Build Chain tab) ──────────

    [ObservableProperty]
    private string _commonRepoPath = string.Empty;

    [ObservableProperty]
    private string _frameworkRepoPath = string.Empty;

    [ObservableProperty]
    private string _coreRepoPath = string.Empty;

    // ── Build state ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isBuilding;

    [ObservableProperty]
    private string _currentStepName = string.Empty;

    [ObservableProperty]
    private string _statusText = "Ready";

    public ObservableCollection<BuildStepViewModel> Steps { get; } = [];
    public ObservableCollection<string> OutputLog { get; } = [];

    [ObservableProperty]
    private string _outputLogText = string.Empty;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// Raised when the user changes a repo path in this tab, so the main VM
    /// can sync the values back to Settings.
    /// </summary>
    public event Action<string, string, string>? RepoPathsChanged;

    public void Initialize(WorkspaceSettings settings)
    {
        Settings = settings;

        // Populate the editable path fields from settings
        CommonRepoPath = settings.CommonRepoPath;
        FrameworkRepoPath = settings.FrameworkRepoPath;
        CoreRepoPath = settings.CoreRepoPath;

        InitializeSteps();
    }

    private WorkspaceSettings? Settings { get; set; }

    // ── Folder-browse commands ──────────────────────────────────────────────

    [RelayCommand]
    private void BrowseCommon()
    {
        if (BrowseFolder(CommonRepoPath) is { } path)
        {
            CommonRepoPath = path;
            SyncPathsToSettings();
        }
    }

    [RelayCommand]
    private void BrowseFramework()
    {
        if (BrowseFolder(FrameworkRepoPath) is { } path)
        {
            FrameworkRepoPath = path;
            SyncPathsToSettings();
        }
    }

    [RelayCommand]
    private void BrowseCore()
    {
        if (BrowseFolder(CoreRepoPath) is { } path)
        {
            CoreRepoPath = path;
            SyncPathsToSettings();
        }
    }

    private void SyncPathsToSettings()
    {
        if (Settings is not null)
        {
            Settings.CommonRepoPath = CommonRepoPath;
            Settings.FrameworkRepoPath = FrameworkRepoPath;
            Settings.CoreRepoPath = CoreRepoPath;
        }

        RepoPathsChanged?.Invoke(CommonRepoPath, FrameworkRepoPath, CoreRepoPath);
    }

    private static string? BrowseFolder(string currentPath)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select repository folder",
            InitialDirectory = Directory.Exists(currentPath) ? currentPath : string.Empty
        };

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void InitializeSteps()
    {
        Steps.Clear();
        Steps.Add(new BuildStepViewModel("Build Common", "dotnet build Common.sln -c DEBUG"));
        Steps.Add(new BuildStepViewModel("Pack Common", "dotnet pack Common.sln --no-build -c DEBUG"));
        Steps.Add(new BuildStepViewModel("Copy Common → Framework", "Copy NuGet packages"));
        Steps.Add(new BuildStepViewModel("Build Framework", "dotnet build Framework.sln -c DEBUG"));
        Steps.Add(new BuildStepViewModel("Pack Framework", "dotnet pack Framework.sln --no-build -c DEBUG"));
        Steps.Add(new BuildStepViewModel("Copy NuGet → Core", "Copy NuGet packages to Core"));
        Steps.Add(new BuildStepViewModel("Build Core", "dotnet build CarrierMessagingBuss.Api.csproj -c DEBUG (excludes .sqlproj)"));
    }

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAllAsync()
    {
        // Always push the latest path values into Settings before building
        SyncPathsToSettings();

        if (string.IsNullOrWhiteSpace(CommonRepoPath) ||
            string.IsNullOrWhiteSpace(FrameworkRepoPath) ||
            string.IsNullOrWhiteSpace(CoreRepoPath) ||
            !Directory.Exists(CommonRepoPath) ||
            !Directory.Exists(FrameworkRepoPath) ||
            !Directory.Exists(CoreRepoPath))
        {
            AppendOutput("✖ One or more repository paths are empty or do not exist. Please set them above.");
            return;
        }

        IsBuilding = true;
        StatusText = "Building...";
        _cts = new CancellationTokenSource();

        foreach (var step in Steps)
        {
            step.Status = BuildStepStatus.Pending;
        }

        OutputLog.Clear();
        OutputLogText = string.Empty;

        try
        {
            var success = await Task.Run(() =>
                _buildChainService.ExecuteFullChainAsync(
                    Settings,
                    OnStepChanged,
                    output => App.Current.Dispatcher.BeginInvoke(() => AppendOutput(output)),
                    _cts.Token)).ConfigureAwait(true);

            StatusText = success ? "✔ Build chain completed" : "✖ Build chain failed";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Build cancelled";
            AppendOutput("Build chain cancelled by user.");
        }
        catch (Exception ex)
        {
            StatusText = $"✖ Error: {ex.Message}";
            AppendOutput($"Unexpected error: {ex}");
        }
        finally
        {
            IsBuilding = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanBuild() => !IsBuilding;

    [RelayCommand]
    private void CancelBuild()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }

    private void OnStepChanged(string stepName, BuildStepStatus status)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentStepName = stepName;
            var step = Steps.FirstOrDefault(s => s.Name == stepName);
            if (step is not null)
            {
                step.Status = status;
            }
        });
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

/// <summary>
/// Represents a single step in the build chain with observable status.
/// </summary>
public sealed partial class BuildStepViewModel : ObservableObject
{
    public BuildStepViewModel(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    private BuildStepStatus _status = BuildStepStatus.Pending;

    public string StatusIcon => Status switch
    {
        BuildStepStatus.Pending => "⏳",
        BuildStepStatus.Running => "⏵",
        BuildStepStatus.Succeeded => "✔",
        BuildStepStatus.Failed => "✖",
        BuildStepStatus.Skipped => "⏭",
        _ => "?"
    };
}
