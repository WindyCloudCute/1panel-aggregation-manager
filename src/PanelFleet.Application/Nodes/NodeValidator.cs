using PanelFleet.Application.Models;

namespace PanelFleet.Application.Nodes;

/// <summary>节点字段校验（纯函数，便于单元测试）。</summary>
public static class NodeValidator
{
    public const int MaxNameLength = 64;
    public const int MaxTags = 10;
    public const int MaxTagLength = 32;
    public const int MaxRemarkLength = 500;

    public static Dictionary<string, string[]> Validate(CreateNodeRequest request) =>
        ValidateCore(
            request.Name,
            request.BaseUrl,
            request.ApiKey,
            requiredApiKey: true,
            request.Tags,
            request.Remark,
            request.SecurityEntrance);

    public static Dictionary<string, string[]> Validate(UpdateNodeRequest request) =>
        ValidateCore(
            request.Name,
            request.BaseUrl,
            request.ApiKey,
            requiredApiKey: false,
            request.Tags,
            request.Remark,
            request.SecurityEntrance);

    public static Dictionary<string, string[]> Validate(TestDraftRequest request) =>
        ValidateCore("draft", request.BaseUrl, request.ApiKey, requiredApiKey: true, tags: null, remark: null, securityEntrance: null);

    public static Uri? NormalizeBaseUrl(string? value, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "请填写节点地址。";
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = "节点地址必须是绝对 URL。";
            return null;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            error = "节点地址只支持 http/https。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "节点地址缺少主机名。";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "节点地址不能包含查询参数或锚点。";
            return null;
        }

        var normalized = string.Concat(uri.GetLeftPart(UriPartial.Authority), uri.AbsolutePath.TrimEnd('/'));
        return new Uri(normalized);
    }

    public static string? NormalizeSecurityEntrance(string? value, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Trim('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > 64)
        {
            error = "安全入口不能超过 64 个字符。";
            return null;
        }

        if (normalized.Contains('/') || normalized.Contains('?') || normalized.Contains('#')
            || normalized.Any(char.IsWhiteSpace))
        {
            error = "安全入口只能是单个路径段，不能包含斜杠、空白或查询参数。";
            return null;
        }

        return normalized;
    }

    private static Dictionary<string, string[]> ValidateCore(
        string? name,
        string? baseUrl,
        string? apiKey,
        bool requiredApiKey,
        IReadOnlyList<string>? tags,
        string? remark,
        string? securityEntrance)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["请填写节点名称。"];
        }
        else if (name.Trim().Length > MaxNameLength)
        {
            errors["name"] = [$"节点名称不能超过 {MaxNameLength} 个字符。"];
        }

        if (NormalizeBaseUrl(baseUrl, out var urlError) is null)
        {
            errors["baseUrl"] = [urlError ?? "节点地址无效。"];
        }

        if (requiredApiKey && string.IsNullOrWhiteSpace(apiKey))
        {
            errors["apiKey"] = ["请填写 API Key。"];
        }
        else if (!string.IsNullOrEmpty(apiKey) && (apiKey.Trim().Length < 8 || apiKey.Trim().Length > 512))
        {
            errors["apiKey"] = ["API Key 长度必须在 8..512 之间。"];
        }

        if (tags is not null)
        {
            if (tags.Count > MaxTags)
            {
                errors["tags"] = [$"标签最多 {MaxTags} 个。"];
            }
            else if (tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Trim().Length > MaxTagLength))
            {
                errors["tags"] = [$"标签不能为空且不能超过 {MaxTagLength} 个字符。"];
            }
        }

        if (remark is not null && remark.Length > MaxRemarkLength)
        {
            errors["remark"] = [$"备注不能超过 {MaxRemarkLength} 个字符。"];
        }

        if (NormalizeSecurityEntrance(securityEntrance, out var entranceError) is null && entranceError is not null)
        {
            errors["securityEntrance"] = [entranceError];
        }

        return errors;
    }
}
