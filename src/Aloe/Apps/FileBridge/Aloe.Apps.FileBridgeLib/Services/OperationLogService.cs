using System.Collections.Concurrent;
using System.Text.Json;
using Aloe.Apps.FileBridgeLib.Models;

namespace Aloe.Apps.FileBridgeLib.Services;

/// <summary>
/// 操作ログサービス
/// </summary>
public class OperationLogService : IDisposable
{
    private class LogCacheEntry
    {
        public List<OperationLogEntry> Logs { get; set; } = new();
        public int? CurrentFileNumber { get; set; }
    }

    private readonly FileBridgeOptions _options;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, LogCacheEntry> _logCache = new();
    private readonly Timer? _cleanupTimer;
    private Func<OperationLogEntry, Task>? _onLogAdded;

    public OperationLogService(FileBridgeOptions options)
    {
        this._options = options;

        // ログディレクトリの作成
        if (!Directory.Exists(this._options.LogDirectory))
        {
            Directory.CreateDirectory(this._options.LogDirectory);
        }

        // 古いログの自動削除タイマー（1日1回実行）
        this._cleanupTimer = new Timer(this.PerformCleanup, null, TimeSpan.FromHours(1), TimeSpan.FromDays(1));
    }

    /// <summary>
    /// ログを追加
    /// </summary>
    public async Task AddLogAsync(LogType logType, string message, string? details = null)
    {
        var entry = new OperationLogEntry
        {
            LogType = logType,
            Message = message,
            Details = details,
            Timestamp = DateTime.UtcNow
        };

        await this._semaphore.WaitAsync();
        try
        {
            var dateKey = entry.Timestamp.ToString("yyyyMMdd");

            // キャッシュから現在のファイル情報を取得または作成
            if (!this._logCache.TryGetValue(dateKey, out var cacheEntry))
            {
                cacheEntry = new LogCacheEntry
                {
                    Logs = await this.LoadLogsFromFileAsync(this.GetLogFileName(dateKey)),
                    CurrentFileNumber = null
                };
                this._logCache[dateKey] = cacheEntry;
            }

            // ログを追加
            cacheEntry.Logs.Add(entry);

            // ファイルサイズ上限チェック
            if (cacheEntry.Logs.Count >= this._options.MaxLogsPerFile)
            {
                // 現在のファイルを保存
                var currentFileName = this.GetLogFileName(dateKey, cacheEntry.CurrentFileNumber);
                await this.SaveLogsToFileAsync(currentFileName, cacheEntry.Logs);

                // 新しいファイル番号に切り替え
                var nextNumber = cacheEntry.CurrentFileNumber.HasValue
                    ? cacheEntry.CurrentFileNumber.Value + 1
                    : this.GetNextFileNumber(currentFileName);
                cacheEntry.CurrentFileNumber = nextNumber;
                cacheEntry.Logs = new List<OperationLogEntry> { entry };
            }

            // ファイルに保存
            var fileName = this.GetLogFileName(dateKey, cacheEntry.CurrentFileNumber);
            await this.SaveLogsToFileAsync(fileName, cacheEntry.Logs);

            // コールバック経由でリアルタイム配信
            if (this._onLogAdded != null)
            {
                try
                {
                    await this._onLogAdded(entry);
                }
                catch
                {
                    // SignalR送信エラーでログ書き込みを失敗させない
                }
            }
        }
        finally
        {
            this._semaphore.Release();
        }
    }

    /// <summary>
    /// ログを取得
    /// </summary>
    public async Task<(List<OperationLogEntry> Logs, int TotalCount)> GetLogsAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        LogType? logType = null,
        int page = 1,
        int pageSize = 50)
    {
        await this._semaphore.WaitAsync();
        try
        {
            var allLogs = new List<OperationLogEntry>();

            // 日付範囲内のログファイルを読み込む
            var start = startDate?.Date ?? DateTime.UtcNow.Date.AddDays(-7);
            var end = endDate?.Date ?? DateTime.UtcNow.Date;

            for (var date = start; date <= end; date = date.AddDays(1))
            {
                var dateKey = date.ToString("yyyyMMdd");

                // キャッシュから取得（今日の分はキャッシュを優先）
                if (this._logCache.TryGetValue(dateKey, out var cacheEntry) && date.Date == DateTime.UtcNow.Date)
                {
                    allLogs.AddRange(cacheEntry.Logs);
                }
                else
                {
                    // ベースファイル読み込み
                    var fileName = this.GetLogFileName(dateKey);
                    if (File.Exists(fileName))
                    {
                        var logs = await this.LoadLogsFromFileAsync(fileName);
                        allLogs.AddRange(logs);
                    }

                    // 連番ファイルもチェック
                    var fileNumber = 1;
                    while (true)
                    {
                        var numberedFileName = this.GetLogFileName(dateKey, fileNumber);
                        if (!File.Exists(numberedFileName))
                            break;

                        var logs = await this.LoadLogsFromFileAsync(numberedFileName);
                        allLogs.AddRange(logs);
                        fileNumber++;
                    }
                }
            }

            // フィルタリング
            if (logType.HasValue)
            {
                allLogs = allLogs.Where(l => l.LogType == logType.Value).ToList();
            }

            // ソート（新しい順）
            allLogs = allLogs.OrderByDescending(l => l.Timestamp).ToList();

            var totalCount = allLogs.Count;

            // ページネーション
            var skip = (page - 1) * pageSize;
            var pagedLogs = allLogs.Skip(skip).Take(pageSize).ToList();

            return (pagedLogs, totalCount);
        }
        finally
        {
            this._semaphore.Release();
        }
    }

    /// <summary>
    /// ログファイル名を取得
    /// </summary>
    private string GetLogFileName(string dateKey, int? fileNumber = null)
    {
        var baseName = $"filebridge_monitor_{dateKey}";
        if (fileNumber.HasValue)
        {
            baseName += $"_{fileNumber.Value:D4}";
        }
        return Path.Combine(this._options.LogDirectory, $"{baseName}.json");
    }

    /// <summary>
    /// 次のファイル番号を取得
    /// </summary>
    private int GetNextFileNumber(string currentFileName)
    {
        var directory = Path.GetDirectoryName(currentFileName) ?? this._options.LogDirectory;
        var baseName = Path.GetFileNameWithoutExtension(currentFileName);
        var dateKey = baseName.Replace("filebridge_monitor_", "").Split('_')[0];

        var existingFiles = Directory.GetFiles(directory, $"filebridge_monitor_{dateKey}_*.json");
        if (existingFiles.Length == 0)
            return 1;

        var numbers = existingFiles
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(f => f.Replace($"filebridge_monitor_{dateKey}_", ""))
            .Where(s => Int32.TryParse(s, out _))
            .Select(Int32.Parse)
            .ToList();

        return numbers.Count > 0 ? numbers.Max() + 1 : 1;
    }

    /// <summary>
    /// ファイルからログを読み込む
    /// </summary>
    private async Task<List<OperationLogEntry>> LoadLogsFromFileAsync(string fileName)
    {
        if (!File.Exists(fileName))
            return new List<OperationLogEntry>();

        try
        {
            var json = await File.ReadAllTextAsync(fileName);
            if (String.IsNullOrWhiteSpace(json))
                return new List<OperationLogEntry>();

            var logs = JsonSerializer.Deserialize<List<OperationLogEntry>>(json) ?? new List<OperationLogEntry>();
            return logs;
        }
        catch (JsonException)
        {
            // JSON破損の場合は空リストを返す
            return new List<OperationLogEntry>();
        }
        catch (IOException)
        {
            // ファイルアクセスエラーは空リストを返す
            return new List<OperationLogEntry>();
        }
    }

    /// <summary>
    /// ログをファイルに保存
    /// </summary>
    private async Task SaveLogsToFileAsync(string fileName, List<OperationLogEntry> logs)
    {
        var directory = Path.GetDirectoryName(fileName);
        if (!String.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(logs, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        await File.WriteAllTextAsync(fileName, json);
    }

    /// <summary>
    /// ログ追加時のコールバックを設定
    /// </summary>
    public void SetOnLogAddedCallback(Func<OperationLogEntry, Task> callback)
    {
        this._onLogAdded = callback;
    }

    /// <summary>
    /// 古いログの削除
    /// </summary>
    private async void PerformCleanup(object? state)
    {
        await this._semaphore.WaitAsync();
        try
        {
            var cutoffDate = DateTime.UtcNow.Date.AddDays(-this._options.LogRetentionDays);
            var files = Directory.GetFiles(this._options.LogDirectory, "filebridge_monitor_*.json");

            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                // ファイル名から日付を抽出: filebridge_monitor_YYYYMMDD または filebridge_monitor_YYYYMMDD_0001
                var parts = fileName.Replace("filebridge_monitor_", "").Split('_');
                if (parts.Length > 0 && parts[0].Length == 8 && DateTime.TryParseExact(parts[0], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var fileDate))
                {
                    if (fileDate < cutoffDate)
                    {
                        File.Delete(file);
                    }
                }
            }

            // 古い日付のキャッシュもクリア
            var keysToRemove = this._logCache.Keys
                .Where(dateKey => DateTime.TryParseExact(dateKey, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date) && date < cutoffDate)
                .ToList();
            foreach (var key in keysToRemove)
            {
                this._logCache.TryRemove(key, out _);
            }
        }
        finally
        {
            this._semaphore.Release();
        }
    }

    public void Dispose()
    {
        this._cleanupTimer?.Dispose();
        this._semaphore.Dispose();
    }
}
