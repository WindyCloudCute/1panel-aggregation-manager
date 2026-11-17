namespace PanelFleet.Client.Serialization;

/// <summary>1Panel 统一响应信封：<c>{ code, message, data, errorCode }</c>。</summary>
public sealed class PanelEnvelope<T>
{
    public int Code { get; set; }

    public string? Message { get; set; }

    public string? ErrorCode { get; set; }

    public T? Data { get; set; }
}
