namespace Aloe.Apps.FileBridgeLib.Models;

/// <summary>
/// ファイルイベント情報
/// </summary>
public class FileEvent
{
    /// <summary>
    /// ファイルパス
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// イベントタイプ（Created, Changed, Deleted）
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// 検知方法（FileSystemWatcher, Polling）
    /// </summary>
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>
    /// タイムスタンプ
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
