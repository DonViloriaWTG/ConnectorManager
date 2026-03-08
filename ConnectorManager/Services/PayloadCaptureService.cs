using System.IO;
using System.Text.Json;

namespace ConnectorManager.Services;

/// <summary>
/// Monitors the CMB Core API's structured JSON log files and extracts
/// carrier request/response payloads in real time.
///
/// The framework's <c>OrchestrationLogger.LogCarrierPayload()</c> writes
/// separate Serilog log entries for each request and response with structured
/// properties (<c>CarrierRequest</c> / <c>CarrierResponse</c>) into hourly-
/// rolled JSON log files at <c>./logs/CarrierMessagingBus-*.log</c> relative
/// to the API's runtime working directory.
/// </summary>
public sealed class PayloadCaptureService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _pollTimer;
    private string? _currentFilePath;
    private long _lastReadPosition;
    private readonly object _lock = new();
    private bool _disposed;
    private string? _logsDir;

    /// <summary>
    /// Raised when a carrier payload (request or response) is captured from the log.
    /// </summary>
    public event Action<CapturedPayload>? PayloadCaptured;

    /// <summary>
    /// Raised for diagnostic messages (errors, status updates).
    /// </summary>
    public event Action<string>? DiagnosticMessage;

    /// <summary>
    /// Starts watching the API's log directory for payload entries.
    /// </summary>
    /// <param name="coreRepoPath">Root path of the CMB.Core repository.</param>
    public void Start(string coreRepoPath)
    {
        Stop();

        // The API's Directory.Build.props sets:
        //   <OutputPath>..\bin\$(MSBuildProjectName)</OutputPath>
        //   <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
        // and Program.cs does Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory).
        // So logs land in {coreRepoPath}\bin\CarrierMessagingBuss.Api\logs\.
        var logsDir = ResolveLogsDirectory(coreRepoPath);

        if (logsDir is null)
        {
            // Default to the expected path — will be created when the API starts
            logsDir = Path.Combine(coreRepoPath, "bin", "CarrierMessagingBuss.Api", "logs");
            DiagnosticMessage?.Invoke($"Payload capture: logs directory not found yet. Watching: {logsDir}");
        }

        if (!Directory.Exists(logsDir))
        {
            Directory.CreateDirectory(logsDir);
        }

        DiagnosticMessage?.Invoke($"Payload capture: watching {logsDir}");

        _watcher = new FileSystemWatcher(logsDir, "CarrierMessagingBus-*.log")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnLogFileChanged;
        _watcher.Created += OnLogFileChanged;

        _logsDir = logsDir;

        // Tail any existing latest file to capture only new entries going forward
        SwitchToLatestFile(logsDir);

        // FileSystemWatcher alone is unreliable for detecting Serilog writes because
        // the file handle stays open and the OS may not fire Changed events until the
        // buffer is flushed or the handle is released.  Poll every 500 ms as a fallback.
        _pollTimer = new System.Threading.Timer(OnPollTick, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Stops watching and releases resources.
    /// </summary>
    public void Stop()
    {
        if (_pollTimer is not null)
        {
            _pollTimer.Dispose();
            _pollTimer = null;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnLogFileChanged;
            _watcher.Created -= OnLogFileChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _currentFilePath = null;
        _lastReadPosition = 0;
        _logsDir = null;
    }

    private static string? ResolveLogsDirectory(string coreRepoPath)
    {
        // Primary: {coreRepoPath}\bin\CarrierMessagingBuss.Api\logs
        // (OutputPath = ..\bin\$(MSBuildProjectName), no TFM, no configuration subfolder)
        string[] candidates =
        [
            Path.Combine(coreRepoPath, "bin", "CarrierMessagingBuss.Api", "logs"),
            Path.Combine(coreRepoPath, "bin", "CarrierMessagingBuss.Api", "Debug", "logs"),
            Path.Combine(coreRepoPath, "bin", "CarrierMessagingBuss.Api", "Release", "logs"),
        ];

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (e.ChangeType == WatcherChangeTypes.Created || _currentFilePath != e.FullPath)
            {
                if (IsNewerLogFile(e.FullPath))
                {
                    _currentFilePath = e.FullPath;
                    _lastReadPosition = 0;
                }
            }

            if (string.Equals(_currentFilePath, e.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                ReadNewEntries();
            }
        }
    }

    /// <summary>
    /// Timer callback that polls for new log data. FileSystemWatcher is unreliable
    /// for files held open by Serilog, so we also poll at short intervals.
    /// </summary>
    private void OnPollTick(object? state)
    {
        if (_disposed) return;

        lock (_lock)
        {
            // Check if a newer log file has appeared (hourly roll)
            if (_logsDir is not null)
            {
                try
                {
                    var latestFile = Directory.GetFiles(_logsDir, "CarrierMessagingBus-*.log")
                        .OrderByDescending(f => f)
                        .FirstOrDefault();

                    if (latestFile is not null && IsNewerLogFile(latestFile))
                    {
                        if (!string.Equals(_currentFilePath, latestFile, StringComparison.OrdinalIgnoreCase))
                        {
                            _currentFilePath = latestFile;
                            _lastReadPosition = 0;
                        }
                    }
                }
                catch
                {
                    // Directory may be momentarily inaccessible
                }
            }

            ReadNewEntries();
        }
    }

    private bool IsNewerLogFile(string newFilePath)
    {
        if (_currentFilePath is null) return true;

        // Lexicographic comparison works because filenames include timestamps:
        // CarrierMessagingBus-yyyyMMddHH.log
        return string.Compare(
            Path.GetFileName(newFilePath),
            Path.GetFileName(_currentFilePath),
            StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SwitchToLatestFile(string logsDir)
    {
        try
        {
            var latestFile = Directory.GetFiles(logsDir, "CarrierMessagingBus-*.log")
                .OrderByDescending(f => f)
                .FirstOrDefault();

            if (latestFile is not null)
            {
                lock (_lock)
                {
                    _currentFilePath = latestFile;
                    // Start from end — only capture new entries going forward
                    _lastReadPosition = new FileInfo(latestFile).Length;
                }

                DiagnosticMessage?.Invoke($"Payload capture: tailing {Path.GetFileName(latestFile)}");
            }
        }
        catch (Exception ex)
        {
            DiagnosticMessage?.Invoke($"Payload capture: error finding latest log: {ex.Message}");
        }
    }

    private void ReadNewEntries()
    {
        if (_currentFilePath is null) return;

        try
        {
            using var fs = new FileStream(_currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length <= _lastReadPosition)
            {
                return;
            }

            fs.Seek(_lastReadPosition, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                TryExtractPayload(line);
            }

            _lastReadPosition = fs.Position;
        }
        catch (IOException)
        {
            // File may be locked briefly during write — will retry on next change event
        }
        catch (Exception ex)
        {
            DiagnosticMessage?.Invoke($"Payload capture: read error: {ex.Message}");
        }
    }

    private void TryExtractPayload(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;

            // Only process log entries that have the Properties bag
            if (!root.TryGetProperty("Properties", out var props))
            {
                return;
            }

            // Check for CarrierRequest or CarrierResponse structured property
            // (written by OrchestrationLogger.LogCarrierPayload)
            string? request = null;
            string? response = null;

            if (props.TryGetProperty("CarrierRequest", out var reqEl))
            {
                request = reqEl.GetString();
            }

            if (props.TryGetProperty("CarrierResponse", out var resEl))
            {
                response = resEl.GetString();
            }

            // Skip entries that don't contain payload data
            if (request is null && response is null) return;

            var payload = new CapturedPayload
            {
                Timestamp = root.TryGetProperty("Timestamp", out var ts)
                    ? ts.GetString() ?? DateTime.Now.ToString("O")
                    : DateTime.Now.ToString("O"),
                StepName = props.TryGetProperty("StepName", out var sn) ? sn.GetString() : null,
                TransformerType = props.TryGetProperty("TransformerType", out var tt) ? tt.GetString() : null,
                PayloadType = props.TryGetProperty("PayloadType", out var pt) ? pt.GetString() : null,
                Content = request ?? response,
                IsEmpty = props.TryGetProperty("IsEmpty", out var ie) && ie.GetBoolean()
            };

            PayloadCaptured?.Invoke(payload);
        }
        catch (JsonException)
        {
            // Malformed JSON line — skip
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// Represents a carrier request or response payload captured from the structured JSON log.
/// </summary>
public sealed class CapturedPayload
{
    public string Timestamp { get; init; } = string.Empty;
    public string? StepName { get; init; }
    public string? TransformerType { get; init; }
    public string? PayloadType { get; init; }
    public string? Content { get; init; }
    public bool IsEmpty { get; init; }

    /// <summary>
    /// One-line header for the list display, e.g. "14:30:00 [Request] Book Shipment (BookingTransformer)"
    /// </summary>
    public string DisplayHeader
    {
        get
        {
            var time = DateTime.TryParse(Timestamp, out var dt) ? dt.ToString("HH:mm:ss") : Timestamp;
            var type = PayloadType ?? "Payload";
            var step = StepName ?? "Unknown Step";
            return $"{time} [{type}] {step}" + (TransformerType is not null ? $" ({TransformerType})" : "");
        }
    }
}
