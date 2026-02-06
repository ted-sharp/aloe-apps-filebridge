namespace Aloe.Apps.FileBridgeLib.Models;

/// <summary>
/// 操作ログのタイプ
/// </summary>
public enum LogType
{
    /// <summary>
    /// ファイルイベント検知
    /// </summary>
    FileEvent,

    /// <summary>
    /// プロセス起動成功
    /// </summary>
    ProcessLaunch,

    /// <summary>
    /// プロセス起動エラー
    /// </summary>
    ProcessError,

    /// <summary>
    /// 監視エラー
    /// </summary>
    WatcherError
}
