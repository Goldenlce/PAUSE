using System.Diagnostics;

namespace LeiGodAutoPause;

public sealed record GameMonitorOptions(IReadOnlyList<string> GamePaths, int PollSeconds);

public sealed class GameProcessMonitor : IDisposable
{
    private readonly Func<GameMonitorOptions> _optionsProvider;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool? _lastRunning;

    public GameProcessMonitor(Func<GameMonitorOptions> optionsProvider)
    {
        _optionsProvider = optionsProvider;
    }

    public event EventHandler<bool>? RunningStateChanged;

    public event EventHandler<string>? Faulted;

    public bool IsMonitoring
    {
        get
        {
            lock (_syncRoot)
            {
                return _cts is not null;
            }
        }
    }

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _lastRunning = null;
            _loopTask = Task.Run(() => MonitorLoopAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_syncRoot)
        {
            cts = _cts;
            task = _loopTask;
            _cts = null;
            _loopTask = null;
            _lastRunning = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The monitoring loop is best-effort and can be stopped at any time.
        }
        finally
        {
            cts.Dispose();
        }
    }

    public bool IsAnyGameRunning()
    {
        var options = _optionsProvider();
        return IsAnyGameRunning(options.GamePaths);
    }

    public static bool IsAnyGameRunning(IEnumerable<string> gamePaths)
    {
        var processNames = gamePaths
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (processNames.Count == 0)
        {
            return false;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (processNames.Contains(process.ProcessName))
                    {
                        return true;
                    }
                }
                catch
                {
                    // A protected process may disappear or deny access while it is inspected.
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return false;
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delaySeconds = 3;
            try
            {
                var options = _optionsProvider();
                delaySeconds = Math.Clamp(options.PollSeconds, 1, 60);

                if (options.GamePaths.Count == 0)
                {
                    _lastRunning = null;
                }
                else
                {
                    var running = IsAnyGameRunning(options.GamePaths);
                    if (_lastRunning is null)
                    {
                        // Do not pause merely because the monitor started while no game was running.
                        // A game already running when monitoring starts is still treated as a game start.
                        _lastRunning = running;
                        if (running)
                        {
                            RunningStateChanged?.Invoke(this, true);
                        }
                    }
                    else if (_lastRunning != running)
                    {
                        _lastRunning = running;
                        RunningStateChanged?.Invoke(this, running);
                    }
                }
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(this, ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

