namespace PanelFleet.Client;

/// <summary>拼接节点 BaseUri 与 1Panel 相对路径，支持 BaseUri 带反向代理子路径。</summary>
internal static class PanelEndpoint
{
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

    private static (string Path, string Query) SplitQuery(string relativePath)
    {
        var index = relativePath.IndexOf('?', StringComparison.Ordinal);
        return index < 0
            ? (relativePath, string.Empty)
            : (relativePath[..index], relativePath[(index + 1)..]);
    }
}
