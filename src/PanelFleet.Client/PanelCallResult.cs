using PanelFleet.Domain.Model;

namespace PanelFleet.Client;

/// <summary>一次 1Panel 调用的结果（封装成功数据或错误）。</summary>
public sealed record PanelCallResult<T>(T? Data, PanelError? Error, TimeSpan Elapsed)
{
    public bool IsSuccess => Error is null;
}
