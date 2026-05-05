using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading;

namespace RevitOrchestrator.Pipe;

/// <summary>
/// Manages the lifecycle of the Python orchestrator process.
/// Handles discovery, launch, health monitoring, and graceful shutdown.
/// </summary>
public sealed class PythonProcessManager : IDisposable
{
    private readonly string _pythonPath;
    private readonly string _workingDirectory;
    private readonly string _addinDir;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _monitorTask;
    private EventWaitHandle? _shutdownEvent;
    private string? _shutdownEventName;
    private bool _disposed;

    private const int ReadyTimeoutMs = 30_000;
    private const int ShutdownWaitMs = 5_000;
    private const int MaxRestarts = 5;

    /// <summary>Fired when the process status changes (e.g. "Starting", "Ready", "Crashed").</summary>
    public event Action<string>? OnStatusChanged;

    /// <summary>Fired when the Python process writes to stderr.</summary>
    public event Action<string>? OnLogMessage;

    /// <summary>True once the process has printed ORCHESTRATOR_READY.</summary>
    public bool IsReady { get; private set; }

    public PythonProcessManager(string pythonPath, string workingDirectory)
    {
        _pythonPath = pythonPath;
        _workingDirectory = workingDirectory;
        _addinDir = Path.GetDirectoryName(typeof(PythonProcessManager).Assembly.Location) ?? ".";
    }

    /// <summary>
    /// Resolve the Python interpreter path.
    /// Tries (in order):
    /// 1. ORCHESTRATOR_PYTHON_PATH env var
    /// 2. orchestrator.json config file next to the addin DLL
    /// 3. python-server/.venv/Scripts/python.exe next to addin DLL (production deploy)
    /// 4. Walk up from addin DLL looking for a sibling mcp-server/.venv (dev builds)
    /// Returns null if none found.
    /// </summary>
    public static (string? pythonPath, string? workingDir) ResolvePaths()
    {
        var addinDir = Path.GetDirectoryName(typeof(PythonProcessManager).Assembly.Location) ?? ".";

        // 1. Environment variable override
        var envPath = Environment.GetEnvironmentVariable("ORCHESTRATOR_PYTHON_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            var venvDir = Path.GetDirectoryName(Path.GetDirectoryName(envPath));
            var workDir = venvDir != null ? Path.GetDirectoryName(venvDir) : null;
            Log(addinDir, $"Resolved via ORCHESTRATOR_PYTHON_PATH: {envPath}");
            return (envPath, workDir);
        }

        // 2. Config file next to the DLL: orchestrator.json
        //    { "server_dir": "C:\\path\\to\\mcp-server" }
        var configPath = Path.Combine(addinDir, "orchestrator.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("server_dir", out var serverDirEl))
                {
                    var serverDir = serverDirEl.GetString();
                    if (!string.IsNullOrEmpty(serverDir) && Directory.Exists(serverDir))
                    {
                        var venvPython = Path.Combine(serverDir, ".venv", "Scripts", "python.exe");
                        if (File.Exists(venvPython))
                        {
                            Log(addinDir, $"Resolved via orchestrator.json server_dir: {venvPython}");
                            return (Path.GetFullPath(venvPython), Path.GetFullPath(serverDir));
                        }
                    }
                }

                if (root.TryGetProperty("python_path", out var pythonEl))
                {
                    var python = pythonEl.GetString();
                    var serverDir = root.TryGetProperty("server_dir", out var sd) ? sd.GetString() : null;
                    if (!string.IsNullOrEmpty(python) && File.Exists(python))
                    {
                        if (string.IsNullOrEmpty(serverDir))
                        {
                            var venvDir2 = Path.GetDirectoryName(Path.GetDirectoryName(python));
                            serverDir = venvDir2 != null ? Path.GetDirectoryName(venvDir2) : null;
                        }
                        Log(addinDir, $"Resolved via orchestrator.json python_path: {python}");
                        return (Path.GetFullPath(python), serverDir != null ? Path.GetFullPath(serverDir) : null);
                    }
                }
            }
            catch { /* Ignore malformed config */ }
        }

        // 3a. Bundled exe: python-server/orchestrator.exe (packaged distribution)
        var bundledExe = Path.Combine(addinDir, "python-server", "orchestrator.exe");
        if (File.Exists(bundledExe))
        {
            var workDir = Path.Combine(addinDir, "python-server");
            Log(addinDir, $"Resolved via bundled exe: {bundledExe}");
            return (Path.GetFullPath(bundledExe), Path.GetFullPath(workDir));
        }

        // 3b. Production layout: python-server/ next to the DLL
        var prodPython = Path.Combine(addinDir, "python-server", ".venv", "Scripts", "python.exe");
        if (File.Exists(prodPython))
        {
            var workDir = Path.Combine(addinDir, "python-server");
            Log(addinDir, $"Resolved via production layout: {prodPython}");
            return (Path.GetFullPath(prodPython), Path.GetFullPath(workDir));
        }

        // 4. Dev layout: walk up from DLL dir looking for mcp-server/.venv
        var dir = addinDir;
        for (var i = 0; i < 10; i++)
        {
            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;

            var candidate = Path.Combine(dir, "mcp-server", ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate))
            {
                var workDir = Path.Combine(dir, "mcp-server");
                Log(addinDir, $"Resolved via dev layout walk-up: {candidate}");
                return (Path.GetFullPath(candidate), Path.GetFullPath(workDir));
            }
        }

        // Nothing found — write diagnostic log
        Log(addinDir,
            "ERROR: Could not find Python server.\n" +
            $"  DLL location: {addinDir}\n" +
            $"  Checked env var ORCHESTRATOR_PYTHON_PATH: (not set or missing)\n" +
            $"  Checked config: {configPath} (not found)\n" +
            $"  Checked production: {prodPython} (not found)\n" +
            $"  Checked dev walk-up from: {addinDir} (not found)\n" +
            "  Set ORCHESTRATOR_PYTHON_PATH or place python-server/ next to the DLL.");
        return (null, null);
    }

    /// <summary>
    /// Write a diagnostic line to a log file next to the DLL.
    /// Useful for troubleshooting when Debug.WriteLine is not visible.
    /// </summary>
    private static void Log(string addinDir, string message)
    {
        try
        {
            var logPath = Path.Combine(addinDir, "orchestrator-startup.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Start the Python process manager. Non-blocking — launches a background monitor task.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Stop the Python process and dispose resources.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();

        if (_process != null && !_process.HasExited)
        {
            // Signal graceful shutdown via named event
            try
            {
                _shutdownEvent?.Set();
            }
            catch { }

            // Wait for graceful exit
            if (!_process.WaitForExit(ShutdownWaitMs))
            {
                try
                {
                    _process.Kill();
                    OnLogMessage?.Invoke("Python process killed after timeout");
                }
                catch { }
            }
        }

        _shutdownEvent?.Dispose();
        _shutdownEvent = null;
        _process?.Dispose();
        _process = null;
        IsReady = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        var restartCount = 0;
        var backoffMs = 1000;

        Log(_addinDir, "MonitorLoop started");

        // Reap orchestrator processes left behind by previous Revit sessions.
        // The pipe server uses PIPE_UNLIMITED_INSTANCES, so an orphan with a
        // stale tool registry can grab incoming connections from this Revit's
        // chat panel before our fresh server gets a chance — which is exactly
        // what made csharp tools invisible in the wild. Doing this once,
        // before LaunchProcess, makes the new server the only orchestrator
        // listening on the pipe.
        CleanupOrphanedOrchestrators();

        while (!ct.IsCancellationRequested && restartCount < MaxRestarts)
        {
            try
            {
                OnStatusChanged?.Invoke("Starting");
                IsReady = false;

                Log(_addinDir, $"Launching Python: {_pythonPath} | WorkDir: {_workingDirectory}");
                if (!LaunchProcess())
                {
                    OnStatusChanged?.Invoke("Failed to launch");
                    Log(_addinDir, "LaunchProcess returned false — aborting");
                    return;
                }
                Log(_addinDir, $"Process launched, PID={_process?.Id}. Waiting for ORCHESTRATOR_READY...");

                // Wait for ORCHESTRATOR_READY on stdout
                var ready = await WaitForReadyAsync(ct);
                if (!ready)
                {
                    var hasExited = _process?.HasExited ?? true;
                    var exitCode = hasExited ? _process?.ExitCode.ToString() ?? "?" : "still running";
                    Log(_addinDir, $"Ready signal NOT received. HasExited={hasExited}, ExitCode={exitCode}");
                    OnStatusChanged?.Invoke("Timeout waiting for ready signal");
                    KillProcess();
                    restartCount++;
                    await Task.Delay(backoffMs, ct);
                    backoffMs = Math.Min(backoffMs * 2, 30_000);
                    continue;
                }

                IsReady = true;
                restartCount = 0;
                backoffMs = 1000;
                OnStatusChanged?.Invoke("Ready");
                Log(_addinDir, "Python server is READY");

                // Wait for process to exit
                await _process!.WaitForExitAsync(ct);

                IsReady = false;
                var code = _process.ExitCode;
                OnStatusChanged?.Invoke($"Exited (code {code})");
                OnLogMessage?.Invoke($"Python process exited with code {code}");
                Log(_addinDir, $"Python process exited with code {code}");

                // Don't restart if we're shutting down
                if (ct.IsCancellationRequested) break;

                restartCount++;
                if (restartCount < MaxRestarts)
                {
                    OnStatusChanged?.Invoke($"Restarting in {backoffMs / 1000}s (attempt {restartCount}/{MaxRestarts})...");
                    await Task.Delay(backoffMs, ct);
                    backoffMs = Math.Min(backoffMs * 2, 30_000);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log(_addinDir, $"Monitor exception: {ex}");
                OnLogMessage?.Invoke($"Monitor error: {ex.Message}");
                restartCount++;
                if (!ct.IsCancellationRequested && restartCount < MaxRestarts)
                {
                    await Task.Delay(backoffMs, ct);
                    backoffMs = Math.Min(backoffMs * 2, 30_000);
                }
            }
        }

        if (restartCount >= MaxRestarts)
        {
            OnStatusChanged?.Invoke("Max restarts exceeded");
            OnLogMessage?.Invoke($"Python process failed after {MaxRestarts} restart attempts");
            Log(_addinDir, $"Max restarts ({MaxRestarts}) exceeded");
        }
    }

    private bool LaunchProcess()
    {
        try
        {
            // Create named shutdown event
            _shutdownEventName = $"Global\\RevitOrchestrator_Shutdown_{Environment.ProcessId}_{Guid.NewGuid():N}";
            _shutdownEvent = new EventWaitHandle(false, EventResetMode.ManualReset, _shutdownEventName);

            // Bundled exe gets simple args; python.exe needs -m orchestrator
            var isBundledExe = _pythonPath.EndsWith("orchestrator.exe", StringComparison.OrdinalIgnoreCase);
            var arguments = isBundledExe ? "--mode pipe" : "-m orchestrator --mode pipe";

            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = arguments,
                WorkingDirectory = _workingDirectory,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Pass shutdown event name
            psi.Environment["ORCHESTRATOR_SHUTDOWN_EVENT"] = _shutdownEventName;

            // Forward relevant env vars
            ForwardEnvVar(psi, "ANTHROPIC_API_KEY");
            ForwardEnvVar(psi, "OPENAI_API_KEY");
            ForwardEnvVar(psi, "ORCHESTRATOR_PIPE_NAME");
            ForwardEnvVar(psi, "ORCHESTRATOR_LLM_PROVIDER");
            ForwardEnvVar(psi, "ANTHROPIC_MODEL");
            ForwardEnvVar(psi, "OPENAI_MODEL");

            // Log whether API key is available
            var hasAnthropicKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
            var hasOpenAiKey = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
            Log(_addinDir, $"Env: ANTHROPIC_API_KEY={hasAnthropicKey}, OPENAI_API_KEY={hasOpenAiKey}");

            _process = Process.Start(psi);
            if (_process == null)
            {
                OnLogMessage?.Invoke("Process.Start returned null");
                Log(_addinDir, "Process.Start returned null");
                return false;
            }

            Log(_addinDir, $"Process started: PID={_process.Id}");

            // Start stderr reader on background thread
            _ = Task.Run(() => ReadStderrAsync());

            return true;
        }
        catch (Exception ex)
        {
            OnLogMessage?.Invoke($"Failed to launch Python: {ex.Message}");
            Log(_addinDir, $"LaunchProcess exception: {ex}");
            return false;
        }
    }

    private static void ForwardEnvVar(ProcessStartInfo psi, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(value) && !psi.Environment.ContainsKey(name))
        {
            psi.Environment[name] = value;
        }
    }

    private async Task<bool> WaitForReadyAsync(CancellationToken ct)
    {
        if (_process == null) return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ReadyTimeoutMs);

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeoutCts.Token);
                if (line == null)
                {
                    Log(_addinDir, "stdout returned null (process exited)");
                    return false;
                }
                Log(_addinDir, $"[stdout] {line}");
                if (line.Trim() == "ORCHESTRATOR_READY") return true;
                OnLogMessage?.Invoke($"[stdout] {line}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log(_addinDir, "Timed out (30s) waiting for ORCHESTRATOR_READY");
            OnLogMessage?.Invoke("Timed out waiting for ORCHESTRATOR_READY");
        }

        return false;
    }

    private async Task ReadStderrAsync()
    {
        if (_process == null) return;

        try
        {
            while (!_process.HasExited)
            {
                var line = await _process.StandardError.ReadLineAsync();
                if (line == null) break;
                Log(_addinDir, $"[stderr] {line}");
                OnLogMessage?.Invoke(line);
            }
        }
        catch { }
    }

    private void KillProcess()
    {
        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill();
        }
        catch { }

        _process?.Dispose();
        _process = null;
        _shutdownEvent?.Dispose();
        _shutdownEvent = null;
    }

    /// <summary>
    /// Find and terminate any python.exe processes running our orchestrator
    /// whose ancestry no longer leads to a live Revit. These are leftovers
    /// from previous Revit sessions that didn't shut down cleanly. Because
    /// the pipe server uses PIPE_UNLIMITED_INSTANCES, an orphan can grab a
    /// pipe connection from this Revit's chat panel and serve a stale tool
    /// list — symptom: tool defs added recently are silently absent from
    /// the chat panel.
    ///
    /// Ancestry walk: from each candidate process's parent, follow PPID
    /// chains until we hit a non-python ancestor (typically Revit.exe) or
    /// a dead PID. If the first non-python ancestor is alive, the candidate
    /// has a real owner and we leave it alone. Otherwise, kill it.
    /// </summary>
    private void CleanupOrphanedOrchestrators()
    {
        try
        {
            var procs = QueryProcesses();
            var ourPid = Process.GetCurrentProcess().Id;

            var orphans = procs
                .Where(p => string.Equals(p.Name, "python.exe", StringComparison.OrdinalIgnoreCase))
                .Where(p => p.CommandLine.Contains("orchestrator --mode pipe", StringComparison.Ordinal))
                .Where(p => p.Pid != ourPid)
                .Where(p => IsOrphan(p, procs))
                .ToList();

            if (orphans.Count == 0)
            {
                Log(_addinDir, "Orphan cleanup: no orphaned orchestrator processes found");
                return;
            }

            Log(_addinDir, $"Orphan cleanup: terminating {orphans.Count} stale orchestrator(s)");
            foreach (var orphan in orphans)
            {
                try
                {
                    using var proc = Process.GetProcessById(orphan.Pid);
                    proc.Kill(entireProcessTree: true);
                    Log(_addinDir, $"  killed PID {orphan.Pid} (parent {orphan.ParentPid} dead)");
                }
                catch (Exception ex)
                {
                    Log(_addinDir, $"  could not kill PID {orphan.Pid}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // Cleanup failure is never fatal — we'd rather start the new
            // orchestrator and risk a pipe collision than fail to start at all.
            Log(_addinDir, $"Orphan cleanup failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Walk the parent chain of <paramref name="candidate"/>, skipping over
    /// other python.exe processes (so we ignore the venv-launcher in
    /// .venv\Scripts\python.exe → real CPython chain). Returns true if the
    /// first non-python ancestor is dead — i.e. the candidate has no live
    /// owner.
    /// </summary>
    private static bool IsOrphan(ProcInfo candidate, List<ProcInfo> all)
    {
        var byPid = all.ToDictionary(p => p.Pid);
        var pid = candidate.ParentPid;
        // Walk up at most 8 levels to avoid pathological cycles.
        for (var i = 0; i < 8; i++)
        {
            if (!byPid.TryGetValue(pid, out var parent))
            {
                // Parent isn't in the snapshot — it's dead.
                return true;
            }
            if (!string.Equals(parent.Name, "python.exe", StringComparison.OrdinalIgnoreCase))
            {
                // First non-python ancestor (typically Revit.exe). It's
                // listed in our snapshot, so it's alive — not an orphan.
                return false;
            }
            pid = parent.ParentPid;
        }
        // Walked too far — assume orphan rather than risk leaving it.
        return true;
    }

    private static List<ProcInfo> QueryProcesses()
    {
        var list = new List<ProcInfo>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process");
        foreach (var item in searcher.Get())
        {
            try
            {
                list.Add(new ProcInfo
                {
                    Pid = Convert.ToInt32(item["ProcessId"]),
                    ParentPid = Convert.ToInt32(item["ParentProcessId"]),
                    Name = item["Name"]?.ToString() ?? "",
                    CommandLine = item["CommandLine"]?.ToString() ?? "",
                });
            }
            catch { /* skip processes we can't read */ }
        }
        return list;
    }

    private struct ProcInfo
    {
        public int Pid;
        public int ParentPid;
        public string Name;
        public string CommandLine;
    }
}
