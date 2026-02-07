using System.Collections.Concurrent;
using System.Diagnostics;
using Aloe.Apps.FileBridgeLib.Models;
using Microsoft.Extensions.Logging;

namespace Aloe.Apps.FileBridgeLib.Services;

/// <summary>
/// プロセス起動サービス
/// </summary>
public class ProcessLauncherService : IDisposable
{
    private readonly FileBridgeOptions _options;
    private readonly OperationLogService _logService;
    private readonly ILogger<ProcessLauncherService>? _logger;
    private readonly ConcurrentDictionary<int, Process> _runningProcesses = new();
    private readonly SemaphoreSlim _concurrencyLimiter;
    private bool _disposed = false;

    public ProcessLauncherService(
        FileBridgeOptions options,
        OperationLogService logService,
        ILogger<ProcessLauncherService>? logger = null)
    {
        _options = options;
        _logService = logService;
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(
            _options.MaxConcurrentProcesses > 0 ? _options.MaxConcurrentProcesses : int.MaxValue,
            _options.MaxConcurrentProcesses > 0 ? _options.MaxConcurrentProcesses : int.MaxValue);
    }

    /// <summary>
    /// ファイルイベントに基づいてプロセスを起動
    /// </summary>
    public async Task LaunchProcessAsync(FileEvent fileEvent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.ExecutablePath))
        {
            _logger?.LogWarning("実行ファイルのパスが設定されていません");
            await _logService.AddLogAsync(LogType.ProcessError, "実行ファイルのパスが設定されていません");
            return;
        }

        if (!File.Exists(_options.ExecutablePath))
        {
            var errorMessage = $"実行ファイルが見つかりません: {_options.ExecutablePath}";
            _logger?.LogError(errorMessage);
            await _logService.AddLogAsync(LogType.ProcessError, errorMessage);
            return;
        }

        // 同時起動数の制限（スロットが空くまでブロック）
        await _concurrencyLimiter.WaitAsync(ct);

        // 起動するプロセスのカレントディレクトリを exe の位置に設定
        var exeDir = Path.GetDirectoryName(Path.GetFullPath(_options.ExecutablePath));
        if (string.IsNullOrEmpty(exeDir))
            exeDir = Environment.CurrentDirectory;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                WorkingDirectory = exeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // 引数の設定（ArgumentList で安全に渡す）
            if (!string.IsNullOrEmpty(_options.Arguments))
            {
                var folderPath = Path.GetDirectoryName(fileEvent.FilePath) ?? string.Empty;
                // 先にトークン分割し、各トークンでプレースホルダーを展開する。
                // これにより {FilePath} にスペースが含まれていてもトークンが分割されない。
                foreach (var token in SplitArguments(_options.Arguments))
                {
                    var expanded = token
                        .Replace("{FilePath}", fileEvent.FilePath)
                        .Replace("{FolderPath}", folderPath);
                    startInfo.ArgumentList.Add(expanded);
                }
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.Exited += (sender, e) =>
            {
                // async void を避け、fire-and-forget で安全に処理
                _ = Task.Run(async () =>
                {
                    try { await OnProcessExited(sender, e); }
                    catch (Exception ex) { _logger?.LogError(ex, "プロセス終了イベント処理エラー"); }
                });
            };
            process.OutputDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger?.LogDebug("プロセス出力: {Output}", e.Data);
                }
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger?.LogError("プロセスエラー出力: {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _runningProcesses[process.Id] = process;

            var argsDisplay = string.Join(" ", startInfo.ArgumentList);
            var message = $"プロセスを起動しました: {Path.GetFileName(_options.ExecutablePath)} (PID: {process.Id})";
            if (!string.IsNullOrEmpty(argsDisplay))
            {
                message += $" 引数: {argsDisplay}";
            }

            _logger?.LogInformation(message);
            await _logService.AddLogAsync(LogType.ProcessLaunch, message, System.Text.Json.JsonSerializer.Serialize(new
            {
                ProcessId = process.Id,
                ExecutablePath = _options.ExecutablePath,
                Arguments = argsDisplay,
                FileEvent = fileEvent
            }));
        }
        catch (Exception ex)
        {
            _concurrencyLimiter.Release();
            var errorMessage = $"プロセス起動エラー: {ex.Message}";
            _logger?.LogError(ex, errorMessage);
            await _logService.AddLogAsync(LogType.ProcessError, errorMessage, ex.ToString());
        }
    }

    /// <summary>
    /// 引数文字列をトークン分割（クォート対応）
    /// </summary>
    private static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;

        for (var i = 0; i < arguments.Length; i++)
        {
            var c = arguments[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (c == ' ' && !inQuote)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    /// <summary>
    /// プロセス終了時の処理
    /// </summary>
    private async Task OnProcessExited(object? sender, EventArgs e)
    {
        _concurrencyLimiter.Release();

        if (sender is Process process)
        {
            _runningProcesses.TryRemove(process.Id, out _);

            var exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                _logger?.LogInformation("プロセスが正常終了しました (PID: {ProcessId})", process.Id);
            }
            else
            {
                var errorMessage = $"プロセスがエラーコード {exitCode} で終了しました (PID: {process.Id})";
                _logger?.LogWarning(errorMessage);
                await _logService.AddLogAsync(LogType.ProcessError, errorMessage);
            }

            process.Dispose();
        }
    }

    /// <summary>
    /// 実行中のプロセス数を取得
    /// </summary>
    public int GetRunningProcessCount()
    {
        // 終了したプロセスをクリーンアップ
        var processesToRemove = _runningProcesses
            .Where(kvp => kvp.Value.HasExited)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var processId in processesToRemove)
        {
            if (_runningProcesses.TryRemove(processId, out var process))
            {
                process.Dispose();
            }
        }

        return _runningProcesses.Count;
    }

    /// <summary>
    /// すべてのプロセスを終了
    /// </summary>
    public async Task StopAllProcessesAsync()
    {
        foreach (var kvp in _runningProcesses.ToList())
        {
            try
            {
                var process = kvp.Value;
                if (!process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "プロセス終了エラー (PID: {ProcessId})", kvp.Key);
            }
        }

        _runningProcesses.Clear();
    }

    /// <summary>
    /// すべてのプロセスを同期的に終了
    /// </summary>
    private void StopAllProcessesSync()
    {
        foreach (var kvp in _runningProcesses.ToList())
        {
            try
            {
                var process = kvp.Value;
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
                process.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "プロセス終了エラー (PID: {ProcessId})", kvp.Key);
            }
        }

        _runningProcesses.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopAllProcessesSync();
        _concurrencyLimiter.Dispose();
        _disposed = true;
    }
}
