using System.Collections.Concurrent;
using System.Threading.Channels;
using Aloe.Apps.FileBridgeLib.Models;
using Microsoft.Extensions.Logging;

namespace Aloe.Apps.FileBridgeLib.Services;

/// <summary>
/// ファイル監視サービス（ワークキュー方式）
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly FileBridgeOptions _options;
    private readonly OperationLogService _logService;
    private readonly ProcessLauncherService? _processLauncher;
    private readonly ILogger<FileWatcherService>? _logger;
    private readonly object _watcherLock = new();

    private readonly Channel<string> _workQueue = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, byte> _activeFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _completedFiles = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _pollingTimer;
    private CancellationTokenSource? _cts;
    private Task[]? _workers;
    private bool _disposed;

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

        _cts = new CancellationTokenSource();

        // ワーカーを起動
        var workerCount = Math.Max(2, _options.MaxConcurrentProcesses);
        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = Task.Run(() => RunWorkerAsync(_cts.Token));
        }

        StartFileSystemWatcher();

        // ポーリングタイマー（初回即実行 → 起動時の既存ファイルもスキャン）
        _pollingTimer = new Timer(OnPolling, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        _logger?.LogInformation("ファイル監視を開始しました: {Directory} (ワーカー数: {WorkerCount})", _options.WatchDirectory, workerCount);
    }

    /// <summary>
    /// FileSystemWatcher を生成・開始
    /// </summary>
    private void StartFileSystemWatcher()
    {
        lock (_watcherLock)
        {
            _watcher?.Dispose();

            _watcher = new FileSystemWatcher(_options.WatchDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                InternalBufferSize = 65536,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFileCreated;
            _watcher.Changed += OnFileChanged;
            _watcher.Deleted += OnFileDeleted;
            _watcher.Error += OnWatcherError;
        }
    }

    /// <summary>
    /// 監視を停止
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _workQueue.Writer.TryComplete();

        lock (_watcherLock)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _pollingTimer?.Dispose();
        _pollingTimer = null;

        // ワーカーの完了待ち（タイムアウト5秒）
        if (_workers != null)
        {
            Task.WhenAll(_workers).Wait(TimeSpan.FromSeconds(5));
            _workers = null;
        }

        _cts?.Dispose();
        _cts = null;

        _logger?.LogInformation("ファイル監視を停止しました");
    }

    /// <summary>
    /// 監視ディレクトリを即時スキャンし、見つかったファイルをキューに投入する。
    /// </summary>
    /// <returns>キューに投入されたファイル数</returns>
    public int TriggerImmediateScan()
    {
        if (!Directory.Exists(_options.WatchDirectory))
            return 0;

        var enqueuedCount = 0;
        var files = Directory.GetFiles(_options.WatchDirectory, "*", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            if (TryEnqueueFile(file, skipCooldown: true))
                enqueuedCount++;
        }
        return enqueuedCount;
    }

    /// <summary>
    /// ファイルをワークキューに投入（重複排除付き）
    /// </summary>
    private bool TryEnqueueFile(string filePath, bool skipCooldown = false)
    {
        // ディレクトリは無視
        if (Directory.Exists(filePath))
            return false;

        // 無視する拡張子チェック
        if (ShouldIgnoreFile(filePath))
            return false;

        // マーカーファイルパターンの処理
        string targetPath;
        if (_options.MarkerFilePatterns is { Count: > 0 })
        {
            if (!TryGetTargetFileFromMarker(filePath, out var derivedPath))
                return false;
            targetPath = derivedPath;
        }
        else
        {
            targetPath = filePath;
        }

        // 処理中なら無視
        if (_activeFiles.ContainsKey(targetPath))
            return false;

        // クールダウン期間中なら無視（手動取り込み時はスキップ）
        if (!skipCooldown)
        {
            var cooldownSeconds = Math.Max(_options.PollingIntervalSeconds * 2, 60);
            if (_completedFiles.TryGetValue(targetPath, out var completedAt))
            {
                if (DateTime.UtcNow - completedAt < TimeSpan.FromSeconds(cooldownSeconds))
                    return false;
            }
        }

        // _activeFiles に追加（競合時は先勝ち）
        if (!_activeFiles.TryAdd(targetPath, 0))
            return false;

        // Channel に書き込み（TryWrite で満杯なら破棄 → 次回ポーリングでリトライ）
        if (!_workQueue.Writer.TryWrite(targetPath))
        {
            _activeFiles.TryRemove(targetPath, out _);
            _logger?.LogWarning("ワークキューが満杯のためスキップしました: {FilePath}", targetPath);
            return false;
        }

        return true;
    }

    /// <summary>
    /// ワーカーループ（Channel から読み取り → 処理）
    /// </summary>
    private async Task RunWorkerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var filePath in _workQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    // ファイル存在チェック
                    if (!File.Exists(filePath))
                    {
                        _logger?.LogDebug("ファイルが存在しません（スキップ）: {FilePath}", filePath);
                        continue;
                    }

                    // ロックチェック
                    if (IsFileLocked(filePath))
                    {
                        _logger?.LogDebug("ファイルがロックされています（リトライ対象）: {FilePath}", filePath);
                        continue; // _completedFiles に入れない → 次回ポーリングでリトライ
                    }

                    // サイズ安定待ち
                    if (!await WaitForFileSizeStabilityAsync(filePath))
                    {
                        _logger?.LogWarning("ファイルサイズが安定しませんでした（リトライ対象）: {FilePath}", filePath);
                        continue; // _completedFiles に入れない → 次回ポーリングでリトライ
                    }

                    // FileEvent 作成・ログ記録・プロセス起動
                    var fileEvent = new FileEvent
                    {
                        FilePath = filePath,
                        EventType = "Created",
                        DetectionMethod = "WorkQueue",
                        Timestamp = DateTime.UtcNow
                    };

                    var details = System.Text.Json.JsonSerializer.Serialize(fileEvent);
                    await _logService.AddLogAsync(LogType.FileEvent, $"ファイルイベント検知: {Path.GetFileName(filePath)}", details);
                    _logger?.LogInformation("ファイル処理開始: {FilePath}", filePath);

                    if (_processLauncher != null)
                    {
                        await _processLauncher.LaunchProcessAsync(fileEvent, ct);
                    }

                    // 処理完了 → クールダウン登録
                    _completedFiles[filePath] = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "ファイル処理でエラーが発生しました: {FilePath}", filePath);
                    await _logService.AddLogAsync(LogType.WatcherError, $"ファイル処理エラー: {ex.Message}", ex.ToString());
                }
                finally
                {
                    _activeFiles.TryRemove(filePath, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常な停止
        }
    }

    /// <summary>
    /// ファイル作成イベント
    /// </summary>
    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        TryEnqueueFile(e.FullPath);
    }

    /// <summary>
    /// ファイル変更イベント
    /// </summary>
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        TryEnqueueFile(e.FullPath);
    }

    /// <summary>
    /// ファイル削除イベント
    /// </summary>
    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("ファイル削除検知: {FilePath}", e.FullPath);
    }

    /// <summary>
    /// 監視エラー（ウォッチャーを再起動して復帰）
    /// </summary>
    private async void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var errorMessage = $"ファイル監視エラー: {e.GetException().Message}";
        _logger?.LogError(e.GetException(), "ファイル監視エラー。ウォッチャーを再起動します");
        await _logService.AddLogAsync(LogType.WatcherError, errorMessage, e.GetException().ToString());

        try
        {
            await Task.Delay(1000);
            if (!_disposed && Directory.Exists(_options.WatchDirectory))
            {
                StartFileSystemWatcher();
                _logger?.LogInformation("ファイル監視を再開しました: {Directory}", _options.WatchDirectory);
                await _logService.AddLogAsync(LogType.FileEvent, "ファイル監視を再開しました");
            }
        }
        catch (Exception restartEx)
        {
            _logger?.LogError(restartEx, "ファイル監視の再起動に失敗しました");
            await _logService.AddLogAsync(LogType.WatcherError, $"ファイル監視の再起動に失敗: {restartEx.Message}");
        }
    }

    /// <summary>
    /// ポーリング処理（全ファイルスキャン → TryEnqueueFile）
    /// </summary>
    private async void OnPolling(object? state)
    {
        if (_disposed)
            return;

        try
        {
            if (!Directory.Exists(_options.WatchDirectory))
                return;

            var files = Directory.GetFiles(_options.WatchDirectory, "*", SearchOption.TopDirectoryOnly);
            foreach (var file in files)
            {
                TryEnqueueFile(file);
            }

            // 古い _completedFiles エントリをクリーンアップ
            var cooldownSeconds = Math.Max(_options.PollingIntervalSeconds * 2, 60);
            var cutoff = DateTime.UtcNow.AddSeconds(-cooldownSeconds * 2);
            foreach (var key in _completedFiles.Keys)
            {
                if (_completedFiles.TryGetValue(key, out var ts) && ts < cutoff)
                {
                    _completedFiles.TryRemove(key, out _);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "ポーリング処理でエラーが発生しました");
            await _logService.AddLogAsync(LogType.WatcherError, $"ポーリング処理エラー: {ex.Message}", ex.ToString());
        }
        finally
        {
            // 再入防止: コールバック完了後に次回をスケジュール
            if (!_disposed)
            {
                _pollingTimer?.Change(TimeSpan.FromSeconds(_options.PollingIntervalSeconds), Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>
    /// 無視する拡張子に該当するか判定
    /// </summary>
    private bool ShouldIgnoreFile(string filePath)
    {
        if (_options.IgnoreExtensions is not { Count: > 0 })
            return false;

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
            return false;

        foreach (var ext in _options.IgnoreExtensions)
        {
            var normalized = ext.StartsWith('.') ? ext : $".{ext}";
            if (fileName.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                return true;
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
            return false;

        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(fileName))
            return false;

        foreach (var pattern in _options.MarkerFilePatterns)
        {
            if (pattern.StartsWith("*.") && pattern.Length > 2)
            {
                var suffix = pattern.Substring(1); // .ready
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    targetPath = Path.Combine(Path.GetDirectoryName(filePath) ?? "", fileName.Substring(0, fileName.Length - suffix.Length));
                    if (File.Exists(targetPath))
                        return true;
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
            return true;

        await Task.Delay(Math.Min(200, _options.SizeCheckIntervalMs));

        const int timeoutMs = 30_000;
        var startTime = DateTime.UtcNow;
        var sameSizeCount = 0;
        long lastSize = -1;

        while (true)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
                return false;

            try
            {
                if (!File.Exists(filePath))
                    return false;

                var fileInfo = new FileInfo(filePath);
                fileInfo.Refresh();
                var currentSize = fileInfo.Length;

                if (currentSize == lastSize)
                {
                    sameSizeCount++;
                    if (sameSizeCount >= _options.SizeStabilityCheckCount)
                        return true;
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

        _disposed = true;
        Stop();
    }
}
