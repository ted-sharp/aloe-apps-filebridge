using System.Collections.Concurrent;
using System.Text.Json;
using Aloe.Apps.FileBridgeLib.Models;
using Microsoft.Extensions.Logging;

namespace Aloe.Apps.FileBridgeLib.Services;

/// <summary>
/// アプリ設定管理サービス
/// </summary>
public class AppConfigManagerService : IDisposable
{
    private readonly ConcurrentDictionary<string, FileWatcherService> _watchers = new();
    private readonly ConcurrentDictionary<string, ProcessLauncherService> _launchers = new();
    private readonly ConcurrentDictionary<string, FileBridgeOptions> _configs = new();
    private readonly OperationLogService _logService;
    private readonly ILogger<AppConfigManagerService>? _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly string _configFilePath;
    private bool _disposed = false;

    public AppConfigManagerService(
        OperationLogService logService,
        ILogger<AppConfigManagerService>? logger = null,
        ILoggerFactory? loggerFactory = null,
        string? configFilePath = null)
    {
        _logService = logService;
        _logger = logger;
        _loggerFactory = loggerFactory;
        
        // appsettings.apps.jsonのパスを取得
        if (string.IsNullOrEmpty(configFilePath))
        {
            var basePath = AppContext.BaseDirectory;
            _configFilePath = Path.Combine(basePath, "appsettings.apps.json");
        }
        else
        {
            _configFilePath = configFilePath;
        }
    }

    /// <summary>
    /// 設定ファイルからアプリ設定を読み込む
    /// </summary>
    public async Task LoadConfigsAsync()
    {
        if (!File.Exists(_configFilePath))
        {
            _logger?.LogWarning("設定ファイルが見つかりません: {Path}", _configFilePath);
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_configFilePath);
            var config = JsonSerializer.Deserialize<AppsConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config?.Apps == null || config.Apps.Count == 0)
            {
                _logger?.LogWarning("アプリ設定が見つかりません");
                return;
            }

            // 既存の監視を停止
            await StopAllWatchersAsync();

            // 各アプリ設定を登録
            foreach (var appConfig in config.Apps)
            {
                if (string.IsNullOrEmpty(appConfig.Name))
                {
                    _logger?.LogWarning("名前が設定されていないアプリ設定をスキップしました");
                    continue;
                }

                await AddAppConfigAsync(appConfig);
            }

            _logger?.LogInformation("{Count}個のアプリ設定を読み込みました", config.Apps.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "設定ファイルの読み込みでエラーが発生しました");
            await _logService.AddLogAsync(LogType.WatcherError, $"設定ファイルの読み込みエラー: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// アプリ設定を追加
    /// </summary>
    public async Task<bool> AddAppConfigAsync(FileBridgeOptions config)
    {
        if (string.IsNullOrEmpty(config.Name))
        {
            _logger?.LogWarning("アプリ設定の名前が設定されていません");
            return false;
        }

        if (_configs.ContainsKey(config.Name))
        {
            _logger?.LogWarning("アプリ設定 '{Name}' は既に存在します", config.Name);
            return false;
        }

        try
        {
            // 設定を保存
            _configs[config.Name] = config;

            // サービスを作成
            var processLauncherLogger = _loggerFactory?.CreateLogger<ProcessLauncherService>();
            var watcherLogger = _loggerFactory?.CreateLogger<FileWatcherService>();
            var processLauncher = new ProcessLauncherService(config, _logService, processLauncherLogger);
            var watcher = new FileWatcherService(config, _logService, processLauncher, watcherLogger);

            _launchers[config.Name] = processLauncher;
            _watchers[config.Name] = watcher;

            // 監視を開始
            watcher.Start();

            _logger?.LogInformation("アプリ設定 '{Name}' を追加しました", config.Name);
            await _logService.AddLogAsync(LogType.FileEvent, $"アプリ設定 '{config.Name}' を追加しました");

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "アプリ設定 '{Name}' の追加でエラーが発生しました", config.Name);
            await _logService.AddLogAsync(LogType.WatcherError, $"アプリ設定 '{config.Name}' の追加エラー: {ex.Message}", ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// アプリ設定を更新
    /// </summary>
    public async Task<bool> UpdateAppConfigAsync(string name, FileBridgeOptions newConfig)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(newConfig.Name))
        {
            _logger?.LogWarning("アプリ設定の名前が設定されていません");
            return false;
        }

        if (!_configs.ContainsKey(name))
        {
            _logger?.LogWarning("アプリ設定 '{Name}' が見つかりません", name);
            return false;
        }

        try
        {
            // 既存の監視を停止
            if (_watchers.TryGetValue(name, out var oldWatcher))
            {
                oldWatcher.Stop();
                oldWatcher.Dispose();
            }

            if (_launchers.TryGetValue(name, out var oldLauncher))
            {
                await oldLauncher.StopAllProcessesAsync();
                oldLauncher.Dispose();
            }

            // 設定を更新
            _configs[name] = newConfig;

            // 新しいサービスを作成
            var processLauncherLogger = _loggerFactory?.CreateLogger<ProcessLauncherService>();
            var watcherLogger = _loggerFactory?.CreateLogger<FileWatcherService>();
            var processLauncher = new ProcessLauncherService(newConfig, _logService, processLauncherLogger);
            var watcher = new FileWatcherService(newConfig, _logService, processLauncher, watcherLogger);

            _launchers[name] = processLauncher;
            _watchers[name] = watcher;

            // 監視を開始
            watcher.Start();

            _logger?.LogInformation("アプリ設定 '{Name}' を更新しました", name);
            await _logService.AddLogAsync(LogType.FileEvent, $"アプリ設定 '{name}' を更新しました");

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "アプリ設定 '{Name}' の更新でエラーが発生しました", name);
            await _logService.AddLogAsync(LogType.WatcherError, $"アプリ設定 '{name}' の更新エラー: {ex.Message}", ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// アプリ設定を削除
    /// </summary>
    public async Task<bool> RemoveAppConfigAsync(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            _logger?.LogWarning("アプリ設定の名前が設定されていません");
            return false;
        }

        if (!_configs.ContainsKey(name))
        {
            _logger?.LogWarning("アプリ設定 '{Name}' が見つかりません", name);
            return false;
        }

        try
        {
            // 監視を停止
            if (_watchers.TryRemove(name, out var watcher))
            {
                watcher.Stop();
                watcher.Dispose();
            }

            if (_launchers.TryRemove(name, out var launcher))
            {
                await launcher.StopAllProcessesAsync();
                launcher.Dispose();
            }

            _configs.TryRemove(name, out _);

            _logger?.LogInformation("アプリ設定 '{Name}' を削除しました", name);
            await _logService.AddLogAsync(LogType.FileEvent, $"アプリ設定 '{name}' を削除しました");

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "アプリ設定 '{Name}' の削除でエラーが発生しました", name);
            await _logService.AddLogAsync(LogType.WatcherError, $"アプリ設定 '{name}' の削除エラー: {ex.Message}", ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// すべてのアプリ設定を取得
    /// </summary>
    public List<FileBridgeOptions> GetAllConfigs()
    {
        return _configs.Values.ToList();
    }

    /// <summary>
    /// アプリ設定を取得
    /// </summary>
    public FileBridgeOptions? GetConfig(string name)
    {
        return _configs.TryGetValue(name, out var config) ? config : null;
    }

    /// <summary>
    /// 設定をファイルに保存
    /// </summary>
    public async Task SaveConfigsAsync()
    {
        try
        {
            var config = new AppsConfig
            {
                Apps = _configs.Values.ToList()
            };

            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            await File.WriteAllTextAsync(_configFilePath, json);
            _logger?.LogInformation("設定ファイルを保存しました: {Path}", _configFilePath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "設定ファイルの保存でエラーが発生しました");
            await _logService.AddLogAsync(LogType.WatcherError, $"設定ファイルの保存エラー: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// すべての監視を開始
    /// </summary>
    public void StartAllWatchers()
    {
        foreach (var watcher in _watchers.Values)
        {
            try
            {
                watcher.Start();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "監視の開始でエラーが発生しました");
            }
        }
    }

    /// <summary>
    /// すべての監視を停止
    /// </summary>
    public async Task StopAllWatchersAsync()
    {
        foreach (var kvp in _watchers.ToList())
        {
            try
            {
                kvp.Value.Stop();
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "監視の停止でエラーが発生しました: {Name}", kvp.Key);
            }
        }

        foreach (var kvp in _launchers.ToList())
        {
            try
            {
                await kvp.Value.StopAllProcessesAsync();
                kvp.Value.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "プロセス起動サービスの停止でエラーが発生しました: {Name}", kvp.Key);
            }
        }

        _watchers.Clear();
        _launchers.Clear();
        _configs.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopAllWatchersAsync().Wait();
        _disposed = true;
    }

    /// <summary>
    /// アプリ設定のコンテナクラス
    /// </summary>
    private class AppsConfig
    {
        public List<FileBridgeOptions> Apps { get; set; } = new();
    }
}
