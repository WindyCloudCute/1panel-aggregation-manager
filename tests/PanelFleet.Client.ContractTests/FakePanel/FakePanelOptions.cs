namespace PanelFleet.Client.ContractTests.FakePanel;

/// <summary>假 1Panel 行为配置。</summary>
public sealed class FakePanelOptions
{
    public string ApiKey { get; init; } = "test-api-key";

    public bool AcceptHmac { get; init; } = true;

    public bool AcceptMd5 { get; init; } = true;

    /// <summary>第一次请求返回 401，并在 Date 头模拟 5 分钟时间偏差。</summary>
    public bool RejectFirstRequest { get; init; }

    /// <summary>所有请求返回 401。</summary>
    public bool RejectAll { get; init; }

    /// <summary>返回 gzip 压缩响应。</summary>
    public bool UseGzip { get; init; }

    /// <summary>响应前延迟。</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    /// <summary>返回业务错误码（HTTP 200 + code）。</summary>
    public int? BusinessErrorCode { get; init; }

    /// <summary>返回 302 重定向。</summary>
    public bool Redirect { get; init; }

    /// <summary>返回非 JSON 内容。</summary>
    public bool MalformedJson { get; init; }

    /// <summary>返回 HTML 页面（模拟 1Panel 安全入口/反向代理登录页）。</summary>
    public bool ReturnHtml { get; init; }

    /// <summary>dashboard/base/{io}/{net} 不返回 currentInfo（模拟字段缺失的旧版本）。</summary>
    public bool OmitCurrentInfo { get; init; }

    /// <summary>五类资源分页列表返回 <c>items:null</c>（模拟空节点/1Panel 空列表语义）。</summary>
    public bool OmitResourceItems { get; init; }

    /// <summary>
    /// 额外假路由：相对路径（如 <c>api/v2/apps/search</c>）→ data 部分 JSON 文本；
    /// 路由同时接受 GET/POST/PUT/PATCH/DELETE，响应固定信封 <c>{code:200,message:"",data:...}</c>；值为 null 表示 <c>data:null</c>。
    /// 供各阶段契约测试扩展，避免修改共享假面板代码。
    /// </summary>
    public IReadOnlyDictionary<string, string?> JsonRoutes { get; init; } = new Dictionary<string, string?>();

    /// <summary>额外假路由：相对路径 → 完整响应体（原样返回，用于模拟业务错误码等）。</summary>
    public IReadOnlyDictionary<string, string> RawJsonRoutes { get; init; } = new Dictionary<string, string>();
}
