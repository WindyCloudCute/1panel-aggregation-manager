using System.Text;
using System.Text.Json;

namespace PanelFleet.Application.Terminal;

/// <summary>1Panel 终端服务端帧类型。</summary>
public enum TerminalServerFrameKind
{
    Unknown = 0,
    Session = 1,
    Output = 2,
    Heartbeat = 3,
    AiNotice = 4,
    Closed = 5
}

/// <summary>1Panel 终端服务端帧（已解码）。</summary>
public sealed record TerminalServerFrame(
    TerminalServerFrameKind Kind,
    byte[]? Data = null,
    string? SessionId = null,
    int? CloseCode = null,
    string? CloseReason = null);

/// <summary>浏览器发来的帧类型。</summary>
public enum TerminalBrowserFrameKind
{
    Unknown = 0,
    Input = 1,
    Resize = 2,
    Ping = 3
}

/// <summary>浏览器发来的帧（已解码）。</summary>
public sealed record TerminalBrowserFrame(
    TerminalBrowserFrameKind Kind,
    byte[]? Data = null,
    int Cols = 0,
    int Rows = 0,
    long Timestamp = 0);

/// <summary>
/// 终端消息编解码：1Panel 侧帧（<c>{"type":"cmd","data":"base64"}</c> 等）与
/// PanelFleet 浏览器侧帧（<c>{"type":"output|input|resize|...","data":"base64"}</c>）。
/// 纯函数实现，便于单元测试。
/// </summary>
public static class TerminalProtocol
{
    public const string TypeCmd = "cmd";
    public const string TypeResize = "resize";
    public const string TypeHeartbeat = "heartbeat";
    public const string TypeSession = "session";
    public const string TypeAiNotice = "ai_notice";
    public const string TypeReady = "ready";
    public const string TypeOutput = "output";
    public const string TypeInput = "input";
    public const string TypePing = "ping";
    public const string TypePong = "pong";
    public const string TypeError = "error";
    public const string TypeClosed = "closed";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string EncodeInput(ReadOnlySpan<byte> data) =>
        JsonSerializer.Serialize(new { type = TypeCmd, data = Convert.ToBase64String(data) }, JsonOptions);

    public static string EncodeResize(int cols, int rows) =>
        JsonSerializer.Serialize(new { type = TypeResize, cols, rows }, JsonOptions);

    public static string EncodeHeartbeat(long timestamp) =>
        JsonSerializer.Serialize(new { type = TypeHeartbeat, timestamp }, JsonOptions);

    public static string EncodeReady(
        string sessionId,
        TerminalMode mode,
        string nodeId,
        string nodeName,
        string? container,
        int cols,
        int rows)
    {
        return JsonSerializer.Serialize(
            new
            {
                type = TypeReady,
                sessionId,
                mode = mode == TerminalMode.Host ? "host" : "container",
                nodeId,
                nodeName,
                container,
                cols,
                rows
            },
            JsonOptions);
    }

    public static string EncodeOutput(ReadOnlySpan<byte> data) =>
        JsonSerializer.Serialize(new { type = TypeOutput, data = Convert.ToBase64String(data) }, JsonOptions);

    public static string EncodePong(long timestamp) =>
        JsonSerializer.Serialize(new { type = TypePong, timestamp }, JsonOptions);

    public static string EncodeError(string code, string message) =>
        JsonSerializer.Serialize(new { type = TypeError, code, message }, JsonOptions);

    public static string EncodeClosed(string reason, string? message, bool canReconnect) =>
        JsonSerializer.Serialize(new { type = TypeClosed, reason, message, canReconnect }, JsonOptions);

    /// <summary>把 1Panel 的文本消息解析为服务端帧；畸形 JSON 返回 <see cref="TerminalServerFrameKind.Unknown"/>。</summary>
    public static TerminalServerFrame ParseServerFrame(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new TerminalServerFrame(TerminalServerFrameKind.Unknown);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new TerminalServerFrame(TerminalServerFrameKind.Unknown);
            }

            var root = document.RootElement;
            var type = GetString(root, "type");
            switch (type)
            {
                case TypeSession:
                    return new TerminalServerFrame(TerminalServerFrameKind.Session, SessionId: GetString(root, "id"));
                case TypeCmd:
                    return new TerminalServerFrame(
                        TerminalServerFrameKind.Output,
                        Data: DecodeBase64(GetString(root, "data")) ?? []);
                case TypeHeartbeat:
                    return new TerminalServerFrame(TerminalServerFrameKind.Heartbeat);
                case TypeAiNotice:
                    return new TerminalServerFrame(TerminalServerFrameKind.AiNotice);
                default:
                    return new TerminalServerFrame(TerminalServerFrameKind.Unknown);
            }
        }
        catch (JsonException)
        {
            return new TerminalServerFrame(TerminalServerFrameKind.Unknown);
        }
    }

    /// <summary>解析浏览器帧；无法识别时返回 false（由桥接层忽略）。</summary>
    public static bool TryParseBrowserFrame(string payload, out TerminalBrowserFrame frame)
    {
        frame = new TerminalBrowserFrame(TerminalBrowserFrameKind.Unknown);

        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 1024 * 1024)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            switch (GetString(root, "type"))
            {
                case TypeInput:
                {
                    var raw = GetString(root, "data");
                    if (raw is null)
                    {
                        return false;
                    }

                    var data = DecodeBase64(raw);
                    if (data is null)
                    {
                        return false;
                    }

                    frame = new TerminalBrowserFrame(TerminalBrowserFrameKind.Input, Data: data);
                    return true;
                }

                case TypeResize:
                {
                    var cols = GetInt32(root, "cols");
                    var rows = GetInt32(root, "rows");
                    if (cols <= 0 || rows <= 0)
                    {
                        return false;
                    }

                    frame = new TerminalBrowserFrame(TerminalBrowserFrameKind.Resize, Cols: cols, Rows: rows);
                    return true;
                }

                case TypePing:
                {
                    frame = new TerminalBrowserFrame(
                        TerminalBrowserFrameKind.Ping,
                        Timestamp: GetInt64(root, "timestamp"));
                    return true;
                }

                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>解码 base64（宽松：非法时返回 null）。</summary>
    public static byte[]? DecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string DecodeBase64Text(string? value)
    {
        var bytes = DecodeBase64(value);
        return bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : 0;

    private static long GetInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result)
            ? result
            : 0;
}
