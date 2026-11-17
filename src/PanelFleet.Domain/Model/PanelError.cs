namespace PanelFleet.Domain.Model;

/// <summary>
/// 1Panel 调用失败的结构化描述。
/// 注意：Message 与异常信息中不得包含 API Key、Token 等敏感内容。
/// </summary>
public sealed record PanelError(
    PanelErrorKind Kind,
    string Message,
    int? HttpStatus = null,
    string? PanelCode = null,
    string? ErrorCode = null,
    Exception? Cause = null);
