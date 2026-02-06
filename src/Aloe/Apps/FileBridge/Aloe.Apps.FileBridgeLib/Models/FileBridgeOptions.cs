namespace Aloe.Apps.FileBridgeLib.Models;

/// <summary>
/// FileBridgeの設定オプション
/// </summary>
public class FileBridgeOptions
{
    /// <summary>
    /// アプリ設定の名前（識別用）
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 監視対象ディレクトリのパス
    /// </summary>
    public string WatchDirectory { get; set; } = string.Empty;

    /// <summary>
    /// ポーリング間隔（秒）
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// 起動するexeのパス
    /// </summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// exeに渡す引数
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// 操作ログの保存ディレクトリ
    /// </summary>
    public string LogDirectory { get; set; } = "logs";

    /// <summary>
    /// 操作ログの保持日数
    /// </summary>
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>
    /// 1ファイルあたりの最大ログ数
    /// </summary>
    public int MaxLogsPerFile { get; set; } = 10000;
}
