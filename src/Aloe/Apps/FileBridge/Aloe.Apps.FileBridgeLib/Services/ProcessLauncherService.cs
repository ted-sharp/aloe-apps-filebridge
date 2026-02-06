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
    private bool _disposed = false;

    public ProcessLauncherService(
        FileBridgeOptions options,
        OperationLogService logService,
        ILogger<ProcessLauncherService>? logger = null)
    {
        _options = options;
        _logService = logService;
        _logger = logger;
    }

    /// <summary>
    /// ファイルイベントに基づいてプロセスを起動
    /// </summary>
    public async Task LaunchProcessAsync(FileEvent fileEvent)
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

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.ExecutablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // 引数の設定
            if (!string.IsNullOrEmpty(_options.Arguments))
            {
                // {FilePath} を実際のファイルパスに置換
                var arguments = _options.Arguments.Replace("{FilePath}", fileEvent.FilePath);
                startInfo.Arguments = arguments;
            }

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.Exited += async (sender, e) => await OnProcessExited(sender, e);
            process.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger?.LogError("プロセスエラー出力: {Error}", e.Data);
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            _runningProcesses[process.Id] = process;

            var message = $"プロセスを起動しました: {Path.GetFileName(_options.ExecutablePath)} (PID: {process.Id})";
            if (!string.IsNullOrEmpty(startInfo.Arguments))
            {
                message += $" 引数: {startInfo.Arguments}";
            }

            _logger?.LogInformation(message);
            await _logService.AddLogAsync(LogType.ProcessLaunch, message, System.Text.Json.JsonSerializer.Serialize(new
            {
                ProcessId = process.Id,
                ExecutablePath = _options.ExecutablePath,
                Arguments = startInfo.Arguments,
                FileEvent = fileEvent
            }));
        }
        catch (Exception ex)
        {
            var errorMessage = $"プロセス起動エラー: {ex.Message}";
            _logger?.LogError(ex, errorMessage);
            await _logService.AddLogAsync(LogType.ProcessError, errorMessage, ex.ToString());
        }
    }

    /// <summary>
    /// プロセス終了時の処理
    /// </summary>
    private async Task OnProcessExited(object? sender, EventArgs e)
    {
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

    public void Dispose()
    {
        if (_disposed)
            return;

        StopAllProcessesAsync().Wait();
        _disposed = true;
    }
}
