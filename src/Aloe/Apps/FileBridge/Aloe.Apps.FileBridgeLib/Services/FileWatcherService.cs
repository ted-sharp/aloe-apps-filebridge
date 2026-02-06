using System.Collections.Concurrent;
using Aloe.Apps.FileBridgeLib.Models;
using Microsoft.Extensions.Logging;

namespace Aloe.Apps.FileBridgeLib.Services;

/// <summary>
/// ファイル監視サービス
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly FileBridgeOptions _options;
    private readonly OperationLogService _logService;
    private readonly ProcessLauncherService? _processLauncher;
    private readonly ILogger<FileWatcherService>? _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _pollingTimer;
    private readonly ConcurrentDictionary<string, DateTime> _processedFiles = new();
    private readonly ConcurrentDictionary<string, FileInfo> _lastPollingState = new();
    private bool _disposed = false;

    public FileWatcherService(
        FileBridgeOptions options,
        OperationLogService logService,
        ProcessLauncherService? processLauncher = null,
        ILogger<FileWatcherService>? logger = null)
    {
        _options = options;
        _logService = logService;
        _processLauncher = processLauncher;
        _logger = logger;
    }

    /// <summary>
    /// 監視を開始
    /// </summary>
    public void Start()
    {
        if (!Directory.Exists(_options.WatchDirectory))
        {
            _logger?.LogWarning("監視ディレクトリが存在しません: {Directory}", _options.WatchDirectory);
            _ = _logService.AddLogAsync(LogType.WatcherError, $"監視ディレクトリが存在しません: {_options.WatchDirectory}");
            return;
        }

        // FileSystemWatcherの設定
        _watcher = new FileSystemWatcher(_options.WatchDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileCreated;
        _watcher.Changed += OnFileChanged;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Error += OnWatcherError;

        // ポーリングタイマーの設定
        _pollingTimer = new Timer(OnPolling, null, TimeSpan.Zero, TimeSpan.FromSeconds(_options.PollingIntervalSeconds));

        // 初期状態を記録
        RecordInitialState();

        _logger?.LogInformation("ファイル監視を開始しました: {Directory}", _options.WatchDirectory);
    }

    /// <summary>
    /// 監視を停止
    /// </summary>
    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _pollingTimer?.Dispose();
        _pollingTimer = null;
        _logger?.LogInformation("ファイル監視を停止しました");
    }

    /// <summary>
    /// ファイル作成イベント
    /// </summary>
    private async void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        await HandleFileEventAsync(e.FullPath, "Created", "FileSystemWatcher");
    }

    /// <summary>
    /// ファイル変更イベント
    /// </summary>
    private async void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // ファイルロックチェック（ネットワークドライブ対応）
        if (IsFileLocked(e.FullPath))
        {
            return;
        }

        await HandleFileEventAsync(e.FullPath, "Changed", "FileSystemWatcher");
    }

    /// <summary>
    /// ファイル削除イベント
    /// </summary>
    private async void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        await HandleFileEventAsync(e.FullPath, "Deleted", "FileSystemWatcher");
    }

    /// <summary>
    /// 監視エラー
    /// </summary>
    private async void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var errorMessage = $"ファイル監視エラー: {e.GetException().Message}";
        _logger?.LogError(e.GetException(), "ファイル監視エラー");
        await _logService.AddLogAsync(LogType.WatcherError, errorMessage, e.GetException().ToString());
    }

    /// <summary>
    /// ポーリング処理
    /// </summary>
    private async void OnPolling(object? state)
    {
        if (!Directory.Exists(_options.WatchDirectory))
            return;

        try
        {
            var currentFiles = new DirectoryInfo(_options.WatchDirectory)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .ToDictionary(f => f.FullName, f => f);

            // 新規ファイルまたは変更されたファイルを検出
            foreach (var currentFile in currentFiles.Values)
            {
                if (_lastPollingState.TryGetValue(currentFile.FullName, out var lastFile))
                {
                    // ファイルが変更されたかチェック
                    if (currentFile.LastWriteTime != lastFile.LastWriteTime ||
                        currentFile.Length != lastFile.Length)
                    {
                        // ファイルロックチェック
                        if (!IsFileLocked(currentFile.FullName))
                        {
                            await HandleFileEventAsync(currentFile.FullName, "Changed", "Polling");
                        }
                    }
                }
                else
                {
                    // 新規ファイル
                    if (!IsFileLocked(currentFile.FullName))
                    {
                        await HandleFileEventAsync(currentFile.FullName, "Created", "Polling");
                    }
                }
            }

            // 削除されたファイルを検出
            foreach (var lastFile in _lastPollingState.Keys)
            {
                if (!currentFiles.ContainsKey(lastFile))
                {
                    await HandleFileEventAsync(lastFile, "Deleted", "Polling");
                }
            }

            // 状態を更新
            _lastPollingState.Clear();
            foreach (var file in currentFiles.Values)
            {
                _lastPollingState[file.FullName] = new FileInfo(file.FullName);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ポーリング処理でエラーが発生しました");
            await _logService.AddLogAsync(LogType.WatcherError, $"ポーリング処理エラー: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// ファイルイベントを処理
    /// </summary>
    private async Task HandleFileEventAsync(string filePath, string eventType, string detectionMethod)
    {
        // 重複イベントの抑制（5秒以内の同一ファイル変更は無視）
        var key = $"{filePath}_{eventType}";
        if (_processedFiles.TryGetValue(key, out var lastProcessed))
        {
            if (DateTime.Now - lastProcessed < TimeSpan.FromSeconds(5))
            {
                return;
            }
        }

        _processedFiles[key] = DateTime.Now;

        // 古いエントリをクリーンアップ（1時間以上前のもの）
        var cutoff = DateTime.Now.AddHours(-1);
        var keysToRemove = _processedFiles
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var keyToRemove in keysToRemove)
        {
            _processedFiles.TryRemove(keyToRemove, out _);
        }

        var fileEvent = new FileEvent
        {
            FilePath = filePath,
            EventType = eventType,
            DetectionMethod = detectionMethod,
            Timestamp = DateTime.Now
        };

        var details = System.Text.Json.JsonSerializer.Serialize(fileEvent);
        await _logService.AddLogAsync(LogType.FileEvent, $"ファイルイベント検知: {eventType} - {Path.GetFileName(filePath)}", details);

        _logger?.LogInformation("ファイルイベント検知: {EventType} - {FilePath} ({Method})", eventType, filePath, detectionMethod);

        // プロセス起動（Created/Changedイベントのみ）
        if (_processLauncher != null && (eventType == "Created" || eventType == "Changed"))
        {
            await _processLauncher.LaunchProcessAsync(fileEvent);
        }
    }

    /// <summary>
    /// 初期状態を記録
    /// </summary>
    private void RecordInitialState()
    {
        if (!Directory.Exists(_options.WatchDirectory))
            return;

        try
        {
            var files = new DirectoryInfo(_options.WatchDirectory)
                .GetFiles("*", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
            {
                _lastPollingState[file.FullName] = new FileInfo(file.FullName);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "初期状態の記録でエラーが発生しました");
        }
    }

    /// <summary>
    /// ファイルがロックされているかチェック
    /// </summary>
    private bool IsFileLocked(string filePath)
    {
        try
        {
            using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Stop();
        _disposed = true;
    }
}
