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
        // ファイルロックチェック（ネットワークドライブ対応）
        if (IsFileLocked(e.FullPath))
        {
            return;
        }

        await TryProcessFileEventAsync(e.FullPath, "Created", "FileSystemWatcher");
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

        await TryProcessFileEventAsync(e.FullPath, "Changed", "FileSystemWatcher");
    }

    /// <summary>
    /// ファイル削除イベント
    /// </summary>
    private async void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        await TryProcessFileEventAsync(e.FullPath, "Deleted", "FileSystemWatcher");
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
                            await TryProcessFileEventAsync(currentFile.FullName, "Changed", "Polling");
                        }
                    }
                }
                else
                {
                    // 新規ファイル
                    if (!IsFileLocked(currentFile.FullName))
                    {
                        await TryProcessFileEventAsync(currentFile.FullName, "Created", "Polling");
                    }
                }
            }

            // 削除されたファイルを検出
            foreach (var lastFile in _lastPollingState.Keys)
            {
                if (!currentFiles.ContainsKey(lastFile))
                {
                    await TryProcessFileEventAsync(lastFile, "Deleted", "Polling");
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
    /// フィルタ・マーカー・サイズチェックを適用し、条件を満たす場合にファイルイベントを処理
    /// </summary>
    private async Task TryProcessFileEventAsync(string filePath, string eventType, string detectionMethod)
    {
        // 無視する拡張子に該当するか
        if (ShouldIgnoreFile(filePath))
        {
            return;
        }

        string targetPath;
        var hasMarkerPatterns = _options.MarkerFilePatterns is { Count: > 0 };

        if (hasMarkerPatterns)
        {
            // マーカーモード: マーカーパターンに該当する場合のみ処理
            if (!TryGetTargetFileFromMarker(filePath, out var derivedPath))
            {
                return;
            }
            targetPath = derivedPath;
        }
        else
        {
            targetPath = filePath;
        }

        // Deleted の場合はサイズチェック・ロックチェック不要
        if (eventType != "Deleted")
        {
            if (IsFileLocked(targetPath))
            {
                return;
            }

            if (!await WaitForFileSizeStabilityAsync(targetPath))
            {
                return;
            }
        }
        else
        {
            // Deleted では targetPath はイベントの filePath（削除されたファイル）を使用
            targetPath = filePath;
        }

        await HandleFileEventAsync(targetPath, eventType, detectionMethod);
    }

    /// <summary>
    /// 無視する拡張子に該当するか判定
    /// </summary>
    private bool ShouldIgnoreFile(string filePath)
    {
        if (_options.IgnoreExtensions is not { Count: > 0 })
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        foreach (var ext in _options.IgnoreExtensions)
        {
            var normalized = ext.StartsWith('.') ? ext : $".{ext}";
            if (fileName.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// マーカーファイルから本ファイルパスを導出
    /// </summary>
    private bool TryGetTargetFileFromMarker(string filePath, out string targetPath)
    {
        targetPath = string.Empty;

        if (_options.MarkerFilePatterns is not { Count: > 0 })
        {
            return false;
        }

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        foreach (var pattern in _options.MarkerFilePatterns)
        {
            // *.ready 形式: サフィックスを取得
            if (pattern.StartsWith("*.") && pattern.Length > 2)
            {
                var suffix = pattern.Substring(1); // .ready
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", fileName.Substring(0, fileName.Length - suffix.Length));
                    if (File.Exists(targetPath))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// ファイルサイズが安定するまで待機
    /// </summary>
    private async Task<bool> WaitForFileSizeStabilityAsync(string filePath)
    {
        if (_options.SizeCheckIntervalMs <= 0 || _options.SizeStabilityCheckCount <= 0)
        {
            return true;
        }

        const int timeoutMs = 30_000;
        var startTime = DateTime.UtcNow;
        var sameSizeCount = 0;
        long lastSize = -1;

        while (true)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
            {
                return false;
            }

            try
            {
                if (!File.Exists(filePath))
                {
                    return false;
                }

                var fileInfo = new FileInfo(filePath);
                var currentSize = fileInfo.Length;

                if (currentSize == lastSize)
                {
                    sameSizeCount++;
                    if (sameSizeCount >= _options.SizeStabilityCheckCount)
                    {
                        return true;
                    }
                }
                else
                {
                    sameSizeCount = 1;
                }

                lastSize = currentSize;
            }
            catch
            {
                return false;
            }

            await Task.Delay(_options.SizeCheckIntervalMs);
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
