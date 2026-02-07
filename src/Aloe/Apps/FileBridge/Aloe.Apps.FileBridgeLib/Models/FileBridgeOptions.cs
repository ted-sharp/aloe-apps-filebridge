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

    /// <summary>
    /// 無視する拡張子（一時ファイル＋リネーム方式用。先頭の . は省略可）
    /// </summary>
    public List<string> IgnoreExtensions { get; set; } = new();

    /// <summary>
    /// マーカーファイルパターン（*.ready 形式）。指定時はマーカー検知時のみ処理
    /// </summary>
    public List<string> MarkerFilePatterns { get; set; } = new();

    /// <summary>
    /// サイズ安定チェックの間隔（ミリ秒）。0 で無効
    /// </summary>
    public int SizeCheckIntervalMs { get; set; } = 100;

    /// <summary>
    /// 連続で同じサイズが検出された回数。この回数なら書き込み完了と判断
    /// </summary>
    public int SizeStabilityCheckCount { get; set; } = 2;
}
