using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Terminal;

/// <summary>
/// 1Panel 终端相关路由与 URL 拼接。
/// 说明：1Panel 路由常量按架构约束集中维护，终端属于新增模块，路由放在本模块内
/// （<c>PanelFleet.Client.PanelRoutes</c> 为 P1 的只读共享文件，由 Lead 维护）。
/// BaseUri 拼接规则与 <c>PanelFleet.Client.PanelEndpoint.Combine</c> 一致：
/// 支持 BaseUri 带反向代理子路径（如 <c>http://host:1000/panel</c>）。
/// </summary>
public static class TerminalRoutes
{
    /// <summary>主机终端（1Panel 本机 shell）。</summary>
    public const string HostTerminal = "api/v2/hosts/terminal/local";

    /// <summary>主机 SSH 终端（按 1Panel 主机记录 id）。</summary>
    public const string SshTerminal = "api/v2/hosts/terminal/ssh";

    /// <summary>容器 exec 终端。</summary>
    public const string ContainerTerminal = "api/v2/hosts/terminal/container";

    /// <summary>容器列表（用于打开前校验容器存在性/运行状态）。</summary>
    public const string ContainerSearch = "api/v2/containers/search";

    /// <summary>1Panel 本机 SSH 连接配置（仅做存在性预检）。</summary>
    public const string LocalSshConnection = "api/v2/settings/ssh/conn";

    /// <summary>拼接节点 BaseUri 与 1Panel 相对路径（支持 query 与反代子路径）。</summary>
    public static Uri Combine(Uri baseUri, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var (path, query) = SplitQuery(relativePath);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            Path = string.Concat(basePath, "/", path.TrimStart('/'))
        };

        if (query.Length > 0)
        {
            builder.Query = query.TrimStart('?');
        }

        return builder.Uri;
    }

    /// <summary>1Panel 终端 WS 地址（http→ws、https→wss）。</summary>
    public static Uri BuildTerminalUri(TerminalConnectContext context, string? sessionId, bool revalidate)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Mode == TerminalMode.Host ? HostTerminal : ContainerTerminal;
        var query = BuildQuery(context);
        if (!string.IsNullOrEmpty(sessionId))
        {
            query += "&session=" + Uri.EscapeDataString(sessionId);
        }

        if (revalidate)
        {
            query += "&terminalRevalidate=1";
        }

        var httpUri = Combine(context.Node.BaseUri, path + "?" + query);
        var builder = new UriBuilder(httpUri)
        {
            Scheme = httpUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = httpUri.IsDefaultPort ? -1 : httpUri.Port
        };

        return builder.Uri;
    }

    private static string BuildQuery(TerminalConnectContext context)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("cols=").Append(context.Cols);
        builder.Append("&rows=").Append(context.Rows);

        if (context.Mode == TerminalMode.Container)
        {
            builder.Append("&source=container");
            builder.Append("&containerid=").Append(Uri.EscapeDataString(context.Container ?? string.Empty));
            builder.Append("&user=").Append(Uri.EscapeDataString(context.ContainerUser ?? string.Empty));
            builder.Append("&command=").Append(Uri.EscapeDataString(context.ContainerCommand ?? "/bin/sh"));
        }

        return builder.ToString();
    }

    private static (string Path, string Query) SplitQuery(string relativePath)
    {
        var index = relativePath.IndexOf('?', StringComparison.Ordinal);
        return index < 0
            ? (relativePath, string.Empty)
            : (relativePath[..index], relativePath[(index + 1)..]);
    }
}
