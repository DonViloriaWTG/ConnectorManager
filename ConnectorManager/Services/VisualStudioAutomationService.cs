using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ConnectorManager.Services;

/// <summary>
/// Provides COM-based automation for Visual Studio: opening solutions,
/// finding running VS instances via the Running Object Table (ROT),
/// and attaching the debugger to a target process.
/// </summary>
public static class VisualStudioAutomationService
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable pprot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(uint reserved, out IBindCtx ppbc);

    /// <summary>
    /// Opens a .sln file in Visual Studio via shell association.
    /// </summary>
    public static void OpenSolution(string slnPath, Action<string>? log = null)
    {
        log?.Invoke($"  Opening solution: {Path.GetFileName(slnPath)}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = slnPath,
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Finds a running Visual Studio DTE instance that has the given solution open.
    /// Polls the Running Object Table up to <paramref name="timeout"/>, checking every 3 seconds.
    /// </summary>
    /// <returns>The DTE COM object (dynamic), or null if not found within the timeout.</returns>
    public static async Task<dynamic?> FindVisualStudioAsync(string slnPath, TimeSpan timeout, Action<string>? log = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var normalizedSln = Path.GetFullPath(slnPath);

        log?.Invoke($"  Waiting for Visual Studio to open {Path.GetFileName(slnPath)}...");

        while (DateTime.UtcNow < deadline)
        {
            var dte = FindDteForSolution(normalizedSln);
            if (dte is not null)
            {
                log?.Invoke("  ✔ Found Visual Studio instance.");
                return dte;
            }

            await Task.Delay(3000).ConfigureAwait(false);
        }

        log?.Invoke("  ⚠ Timed out waiting for Visual Studio to register in the Running Object Table.");
        return null;
    }

    /// <summary>
    /// Attaches the Visual Studio debugger to the specified process ID.
    /// </summary>
    /// <returns>True if successfully attached, false otherwise.</returns>
    public static bool AttachDebugger(dynamic dte, int processId, Action<string>? log = null)
    {
        try
        {
            log?.Invoke($"  Attaching debugger to PID {processId}...");

            foreach (dynamic process in dte.Debugger.LocalProcesses)
            {
                if (process.ProcessID == processId)
                {
                    process.Attach();
                    log?.Invoke($"  ✔ Debugger attached to PID {processId}.");
                    return true;
                }
            }

            log?.Invoke($"  ⚠ PID {processId} not found in VS local processes list.");
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke($"  ⚠ Failed to attach debugger: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Scans the Running Object Table (ROT) for a VisualStudio.DTE moniker
    /// whose Solution.FullName matches the given solution path.
    /// </summary>
    private static dynamic? FindDteForSolution(string normalizedSlnPath)
    {
        try
        {
            if (GetRunningObjectTable(0, out var rot) != 0)
            {
                return null;
            }

            rot.EnumRunning(out var enumMoniker);
            if (CreateBindCtx(0, out var bindCtx) != 0)
            {
                return null;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                try
                {
                    monikers[0].GetDisplayName(bindCtx, null, out var displayName);
                    if (displayName is not null && displayName.StartsWith("!VisualStudio.DTE", StringComparison.OrdinalIgnoreCase))
                    {
                        rot.GetObject(monikers[0], out var obj);
                        dynamic dte = obj;
                        string? openSolution = dte.Solution.FullName;
                        if (openSolution is not null &&
                            string.Equals(Path.GetFullPath(openSolution), normalizedSlnPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return dte;
                        }
                    }
                }
                catch
                {
                    // Skip inaccessible monikers (e.g., different-user VS instances)
                }
            }
        }
        catch
        {
            // COM interop failure — ROT not available
        }

        return null;
    }
}
