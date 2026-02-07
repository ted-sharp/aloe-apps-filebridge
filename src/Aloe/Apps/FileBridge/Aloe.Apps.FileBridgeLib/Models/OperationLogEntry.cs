using System.Text.Json.Serialization;

namespace Aloe.Apps.FileBridgeLib.Models;

/// <summary>
/// 操作ログエントリ
/// </summary>
public class OperationLogEntry
{
    /// <summary>
    /// ログID（一意の識別子）
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// タイムスタンプ
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// ログタイプ
    /// </summary>
    [JsonPropertyName("logType")]
    public LogType LogType { get; set; }

    /// <summary>
    /// メッセージ
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 詳細情報（JSON形式の文字列）
    /// </summary>
    [JsonPropertyName("details")]
    public string? Details { get; set; }
}
