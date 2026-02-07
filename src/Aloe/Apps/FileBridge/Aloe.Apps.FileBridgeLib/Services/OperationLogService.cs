using System.Collections.Concurrent;
using System.Text.Json;
using Aloe.Apps.FileBridgeLib.Models;

namespace Aloe.Apps.FileBridgeLib.Services;

/// <summary>
/// 操作ログサービス
/// </summary>
public class OperationLogService : IDisposable
{
    private readonly FileBridgeOptions _options;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, List<OperationLogEntry>> _logCache = new();
    private readonly Timer? _cleanupTimer;
    private Func<OperationLogEntry, Task>? _onLogAdded;

    public OperationLogService(FileBridgeOptions options)
    {
        _options = options;

        // ログディレクトリの作成
        if (!Directory.Exists(_options.LogDirectory))
        {
            Directory.CreateDirectory(_options.LogDirectory);
        }

        // 古いログの自動削除タイマー（1日1回実行）
        _cleanupTimer = new Timer(PerformCleanup, null, TimeSpan.FromHours(1), TimeSpan.FromDays(1));
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
            Timestamp = DateTime.Now
        };

        await _semaphore.WaitAsync();
        try
        {
            var dateKey = entry.Timestamp.ToString("yyyyMMdd");
            var fileName = GetLogFileName(dateKey);

            // キャッシュから読み込みまたはファイルから読み込み
            if (!_logCache.TryGetValue(dateKey, out var logs))
            {
                logs = await LoadLogsFromFileAsync(fileName);
                _logCache[dateKey] = logs;
            }

            // ログを追加
            logs.Add(entry);

            // ファイルサイズ上限チェック
            if (logs.Count >= _options.MaxLogsPerFile)
            {
                // 現在のファイルを保存して、新しいファイル名に切り替え
                await SaveLogsToFileAsync(fileName, logs);
                var newFileName = GetLogFileName(dateKey, GetNextFileNumber(fileName));
                logs.Clear();
                _logCache[dateKey] = logs;
                fileName = newFileName;
            }

            // ファイルに保存
            await SaveLogsToFileAsync(fileName, logs);

            // コールバック経由でリアルタイム配信
            if (_onLogAdded != null)
            {
                await _onLogAdded(entry);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// ログを取得
    /// </summary>
    public async Task<List<OperationLogEntry>> GetLogsAsync(
        DateTime? startDate = null,
        DateTime? endDate = null,
        LogType? logType = null,
        int page = 1,
        int pageSize = 50)
    {
        await _semaphore.WaitAsync();
        try
        {
            var allLogs = new List<OperationLogEntry>();

            // 日付範囲内のログファイルを読み込む
            var start = startDate?.Date ?? DateTime.Today.AddDays(-7);
            var end = endDate?.Date ?? DateTime.Today;

            for (var date = start; date <= end; date = date.AddDays(1))
            {
                var dateKey = date.ToString("yyyyMMdd");
                var fileName = GetLogFileName(dateKey);

                if (File.Exists(fileName))
                {
                    var logs = await LoadLogsFromFileAsync(fileName);
                    allLogs.AddRange(logs);
                }

                // 連番ファイルもチェック
                var fileNumber = 1;
                while (true)
                {
                    var numberedFileName = GetLogFileName(dateKey, fileNumber);
                    if (!File.Exists(numberedFileName))
                        break;

                    var logs = await LoadLogsFromFileAsync(numberedFileName);
                    allLogs.AddRange(logs);
                    fileNumber++;
                }
            }

            // フィルタリング
            if (logType.HasValue)
            {
                allLogs = allLogs.Where(l => l.LogType == logType.Value).ToList();
            }

            // ソート（新しい順）
            allLogs = allLogs.OrderByDescending(l => l.Timestamp).ToList();

            // ページネーション
            var skip = (page - 1) * pageSize;
            return allLogs.Skip(skip).Take(pageSize).ToList();
        }
        finally
        {
            _semaphore.Release();
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
        return Path.Combine(_options.LogDirectory, $"{baseName}.json");
    }

    /// <summary>
    /// 次のファイル番号を取得
    /// </summary>
    private int GetNextFileNumber(string currentFileName)
    {
        var directory = Path.GetDirectoryName(currentFileName) ?? _options.LogDirectory;
        var baseName = Path.GetFileNameWithoutExtension(currentFileName);
        var dateKey = baseName.Replace("filebridge_monitor_", "").Split('_')[0];

        var existingFiles = Directory.GetFiles(directory, $"filebridge_monitor_{dateKey}_*.json");
        if (existingFiles.Length == 0)
            return 1;

        var numbers = existingFiles
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(f => f.Replace($"filebridge_monitor_{dateKey}_", ""))
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
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
            if (string.IsNullOrWhiteSpace(json))
                return new List<OperationLogEntry>();

            var logs = JsonSerializer.Deserialize<List<OperationLogEntry>>(json) ?? new List<OperationLogEntry>();
            return logs;
        }
        catch
        {
            return new List<OperationLogEntry>();
        }
    }

    /// <summary>
    /// ログをファイルに保存
    /// </summary>
    private async Task SaveLogsToFileAsync(string fileName, List<OperationLogEntry> logs)
    {
        var directory = Path.GetDirectoryName(fileName);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
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
        _onLogAdded = callback;
    }

    /// <summary>
    /// 古いログの削除
    /// </summary>
    private async void PerformCleanup(object? state)
    {
        await _semaphore.WaitAsync();
        try
        {
            var cutoffDate = DateTime.Today.AddDays(-_options.LogRetentionDays);
            var files = Directory.GetFiles(_options.LogDirectory, "filebridge_monitor_*.json");

            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.CreationTime < cutoffDate)
                {
                    File.Delete(file);
                }
            }

            // キャッシュもクリア
            _logCache.Clear();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _semaphore.Dispose();
    }
}
