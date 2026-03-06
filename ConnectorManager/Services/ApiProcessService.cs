using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ConnectorManager.Services;

/// <summary>
/// Manages the lifecycle of the locally-running CMB API process.
/// Launches, monitors, and stops the ASP.NET Core application.
/// Ports are determined by the project's launchSettings.json — the actual
/// HTTP URL is auto-detected from Kestrel's "Now listening on:" stdout output.
/// </summary>
public sealed partial class ApiProcessService : IDisposable
{
    private Process? _apiProcess;

    public bool IsRunning => _apiProcess is not null && !_apiProcess.HasExited;

    /// <summary>
    /// The HTTP base URL actually detected from the API's stdout output.
    /// Parsed from "Now listening on: http://..." log lines emitted by Kestrel.
    /// </summary>
    public string? DetectedHttpUrl { get; private set; }

    public event Action<string>? OutputReceived;
    public event Action<ApiStatus>? StatusChanged;

    /// <summary>
    /// Fired when the API emits a "Now listening on: http://..." log line.
    /// Delivers the detected HTTP base URL.
    /// </summary>
    public event Action<string>? BaseUrlDetected;

    [GeneratedRegex(@"Now listening on:\s+(https?://\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListeningUrlRegex();

    /// <summary>
    /// Starts the CMB API as a child process using "dotnet run".
    /// The API runs in Development environment so upload endpoints are accessible.
    /// No URL is imposed — the ports come from launchSettings.json.
    /// </summary>
    public Task StartAsync(string coreRepoPath, CancellationToken cancellationToken = default)
    {
        return StartCoreAsync(coreRepoPath, additionalArgs: null, cancellationToken);
    }

    /// <summary>
    /// Starts the CMB API in debug mode for a specific connector.
    /// Passes --Package:DebugPackagePath and --Package:DebugPackageName so the API
    /// loads the connector from the local filesystem instead of the database.
    /// </summary>
    public Task StartWithDebugAsync(string coreRepoPath, string debugPackagePath, string debugPackageName, CancellationToken cancellationToken = default)
    {
        var debugArgs = $"--Package:DebugPackagePath \"{debugPackagePath}\" --Package:DebugPackageName \"{debugPackageName}\"";
        return StartCoreAsync(coreRepoPath, debugArgs, cancellationToken);
    }

    /// <summary>The PID of the dotnet run host process, or null if not started.</summary>
    public int? ProcessId => _apiProcess is not null && !_apiProcess.HasExited ? _apiProcess.Id : null;

    /// <summary>
    /// The PID of the actual web server process listening on the detected port.
    /// When using "dotnet run", the host process spawns a child process that is the
    /// real ASP.NET Core application. The debugger must attach to this PID, not the
    /// dotnet CLI wrapper, to hit breakpoints in connector code.
    /// </summary>
    public int? ActualServerProcessId { get; private set; }

    private async Task StartCoreAsync(string coreRepoPath, string? additionalArgs, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            OutputReceived?.Invoke("API is already running.");
            return;
        }

        // Ensure no orphaned processes are holding the ports
        KillProcessesOnPort(9104);
        KillProcessesOnPort(9105);

        // Brief wait for port release
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && (IsPortInUse(9104) || IsPortInUse(9105)))
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        DetectedHttpUrl = null;
        ActualServerProcessId = null;
        StatusChanged?.Invoke(ApiStatus.Starting);
        OutputReceived?.Invoke($"Starting CMB API from: {coreRepoPath}");

        var arguments = "run --project CarrierMessagingBuss.Api";
        if (!string.IsNullOrWhiteSpace(additionalArgs))
        {
            arguments += $" -- {additionalArgs}";
            OutputReceived?.Invoke($"Debug args: {additionalArgs}");
        }

        _apiProcess = new Process();
        _apiProcess.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = coreRepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development"
            }
        };

        _apiProcess.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data);
                TryDetectListeningUrl(e.Data);
            }
        };
        _apiProcess.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                OutputReceived?.Invoke(e.Data);
            }
        };
        _apiProcess.EnableRaisingEvents = true;
        _apiProcess.Exited += (_, _) =>
        {
            OutputReceived?.Invoke("CMB API process exited.");
            StatusChanged?.Invoke(ApiStatus.Stopped);
        };

        _apiProcess.Start();
        _apiProcess.BeginOutputReadLine();
        _apiProcess.BeginErrorReadLine();

        OutputReceived?.Invoke($"API process started (PID: {_apiProcess.Id}).");
        OutputReceived?.Invoke("Waiting for Kestrel to report listening URLs...");
    }

    /// <summary>
    /// Parses Kestrel "Now listening on: ..." stdout lines to discover the actual
    /// HTTP and HTTPS URLs. The first HTTP URL is stored as <see cref="DetectedHttpUrl"/>
    /// and triggers <see cref="BaseUrlDetected"/> / a status change to Running.
    /// </summary>
    private void TryDetectListeningUrl(string line)
    {
        var match = ListeningUrlRegex().Match(line);
        if (!match.Success)
        {
            return;
        }

        var url = match.Groups[1].Value.TrimEnd('/');

        // Prefer the plain HTTP URL for local API calls (no cert validation needed)
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && DetectedHttpUrl is null)
        {
            DetectedHttpUrl = url;
            OutputReceived?.Invoke($"✔ Detected API base URL: {url}");

            // "dotnet run" spawns a child process — find the actual server PID from the port
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var serverPid = FindProcessOnPort(uri.Port);
                if (serverPid is not null && serverPid != _apiProcess?.Id)
                {
                    ActualServerProcessId = serverPid;
                    OutputReceived?.Invoke($"  Actual server PID: {serverPid} (dotnet run wrapper: {_apiProcess?.Id})");
                }
                else
                {
                    ActualServerProcessId = _apiProcess?.Id;
                }
            }
            else
            {
                ActualServerProcessId = _apiProcess?.Id;
            }

            StatusChanged?.Invoke(ApiStatus.Running);
            BaseUrlDetected?.Invoke(url);
        }
    }

    /// <summary>
    /// Stops the API process and waits for it to fully exit.
    /// </summary>
    public async Task StopAsync()
    {
        if (_apiProcess is null || _apiProcess.HasExited)
        {
            StatusChanged?.Invoke(ApiStatus.Stopped);
            return;
        }

        OutputReceived?.Invoke("Stopping CMB API...");
        try
        {
            _apiProcess.Kill(entireProcessTree: true);
            // Wait off the calling thread so the UI stays responsive
            await Task.Run(() => _apiProcess?.WaitForExit(10000)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke($"Warning: Could not stop API cleanly: {ex.Message}");
        }

        _apiProcess?.Dispose();
        _apiProcess = null;
        DetectedHttpUrl = null;
        ActualServerProcessId = null;
        StatusChanged?.Invoke(ApiStatus.Stopped);
        OutputReceived?.Invoke("API stopped.");
    }

    /// <summary>
    /// Stops the API process and waits for the port to be fully released.
    /// Also kills any orphaned processes holding the API ports.
    /// Use this before restarting to avoid "address already in use" errors.
    /// </summary>
    public async Task StopAndWaitForPortReleaseAsync(int maxWaitMs = 10000)
    {
        await StopAsync().ConfigureAwait(false);

        // Also kill any orphaned processes holding the API ports (e.g. from a previous session)
        KillProcessesOnPort(9104);
        KillProcessesOnPort(9105);

        // Wait for the OS to release the socket (TCP TIME_WAIT)
        var deadline = Environment.TickCount64 + maxWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!IsPortInUse(9104) && !IsPortInUse(9105))
            {
                return;
            }
            await Task.Delay(500).ConfigureAwait(false);
        }

        OutputReceived?.Invoke("Warning: Ports may still be in use after timeout.");
    }

    /// <summary>
    /// Finds and kills any process listening on the specified port.
    /// </summary>
    private void KillProcessesOnPort(int port)
    {
        try
        {
            var connections = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(ep => ep.Port == port);

            if (!connections.Any())
            {
                return;
            }

            // Use netstat to find the owning PID (IPGlobalProperties doesn't expose PIDs)
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = $"-ano -p TCP",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                // Match lines like "  TCP    127.0.0.1:9104    0.0.0.0:0    LISTENING    12345"
                if (line.Contains($":{port}") && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && pid != 0)
                    {
                        try
                        {
                            OutputReceived?.Invoke($"  Killing orphaned process on port {port} (PID: {pid})");
                            System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // Process may have already exited
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort — don't fail the restart if cleanup fails
        }
    }

    /// <summary>
    /// Finds the PID of the process listening on the given TCP port via netstat.
    /// Returns null if no process is found.
    /// </summary>
    private int? FindProcessOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p TCP",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains($":{port}") && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid) && pid != 0)
                    {
                        return pid;
                    }
                }
            }
        }
        catch
        {
            // Best effort
        }

        return null;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        if (_apiProcess is not null && !_apiProcess.HasExited)
        {
            try
            {
                _apiProcess.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort on dispose
            }
        }

        _apiProcess?.Dispose();
        _apiProcess = null;
    }
}

public enum ApiStatus
{
    Stopped,
    Starting,
    Running
}
