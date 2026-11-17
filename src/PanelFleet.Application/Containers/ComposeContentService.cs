using System.Text.Json;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Containers;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Containers;

/// <summary>
/// Compose YAML 只读读取（用于编辑弹窗预填）：先查项目列表定位 configFile，再经 1Panel files/content 读取。
/// <para>
/// 只接受项目名，绝不接受浏览器传入的文件路径；服务端把 configFile/workdir 规范化后校验路径必须位于
/// 项目 workdir 下、文件名以 .yml/.yaml 结尾，校验不通过时不发起 1Panel 请求。
/// </para>
/// <para>读取到的 YAML 只返回给编辑器，不进入审计、日志或缓存。</para>
/// </summary>
public interface IComposeContentService
{
    Task<ComposeContentResult> GetComposeContentAsync(
        Guid nodeId,
        string projectName,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class ComposeContentService : IComposeContentService
{
    /// <summary>浏览器编辑器允许的最大字符数（1 MiB）；超过时截断并提示，避免把大文件塞进页面。</summary>
    public const int MaxEditableContentChars = 1024 * 1024;

    private readonly INodeStore _nodeStore;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly ContainerOptions _options;
    private readonly ILogger<ComposeContentService> _logger;

    public ComposeContentService(
        INodeStore nodeStore,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        ContainerOptions options,
        ILogger<ComposeContentService> logger)
    {
        _nodeStore = nodeStore ?? throw new ArgumentNullException(nameof(nodeStore));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ComposeContentResult> GetComposeContentAsync(
        Guid nodeId,
        string projectName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            throw new ComposeContentUnsupportedException("Compose 项目名不能为空。");
        }

        var name = projectName.Trim();
        var node = await ResolveNodeAsync(new NodeId(nodeId), cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.NodeTimeout);
        using var client = _clientFactory.Create(BuildDescriptor(node));

        // 1) 只在项目列表中定位项目；项目名由浏览器提供，路径一律以 1Panel 返回的 configFile/workdir 为准。
        var search = await client
            .PostAsync<PagedItemsDto<ComposeItemDto>>(
                ContainerRoutes.ComposeSearch,
                new ComposeSearchRequest(1, _options.PerNodePageSize, name, false),
                timeoutCts.Token)
            .ConfigureAwait(false);

        if (search.Error is not null)
        {
            throw new ComposeContentUnsupportedException("读取 Compose 项目列表失败：" + search.Error.Message);
        }

        var project = (search.Data?.Items ?? [])
            .FirstOrDefault(item => string.Equals(item.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (project is null)
        {
            throw new ContainerComposeNotFoundException(nodeId, name);
        }

        var filePath = ResolveConfigPath(project);
        var workdir = ResolveWorkdir(project, filePath);
        if (!IsUnderDirectory(filePath, workdir))
        {
            throw new ComposeContentUnsupportedException("不支持读取该 Compose 文件（文件不在项目工作目录下）。");
        }

        if (!EndsWithYaml(filePath))
        {
            throw new ComposeContentUnsupportedException("不支持读取该 Compose 文件（仅支持 .yml/.yaml）。");
        }

        // 2) 1Panel v2.3.2：POST /api/v2/files/content { path, isDetail:false }，data 为 FileInfo（含 content）。
        var result = await client
            .PostAsync<JsonElement>(
                ContainerRoutes.FileContent,
                new FileContentRequest(filePath, false),
                timeoutCts.Token)
            .ConfigureAwait(false);

        if (result.Error is not null)
        {
            throw MapPanelError(result.Error);
        }

        if (result.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ComposeContentUnsupportedException("1Panel 未返回文件内容。");
        }

        var content = ParseContent(result.Data);
        var truncated = content.Length > MaxEditableContentChars;
        if (truncated)
        {
            content = SafeTruncate(content, MaxEditableContentChars);
        }

        _logger.LogInformation(
            "已读取 Compose 文件 节点 {NodeId} 项目 {Project} 路径长度 {PathLength} 截断 {Truncated}",
            nodeId,
            name,
            filePath.Length,
            truncated);

        return new ComposeContentResult(filePath, content, truncated);
    }

    private static string ResolveConfigPath(ComposeItemDto project)
    {
        var configFile = NullIfEmpty(project.ConfigFile);
        if (configFile is not null)
        {
            return NormalizeAbsolutePath(configFile, "Compose 文件路径");
        }

        var workdir = NullIfEmpty(project.Workdir);
        if (workdir is not null)
        {
            return NormalizeAbsolutePath(Combine(workdir, "docker-compose.yml"), "Compose 文件路径");
        }

        var path = NullIfEmpty(project.Path);
        if (path is null)
        {
            throw new ComposeContentUnsupportedException("未找到 Compose 配置路径。");
        }

        var normalized = NormalizeAbsolutePath(path, "Compose 路径");
        return EndsWithYaml(normalized)
            ? normalized
            : NormalizeAbsolutePath(Combine(normalized, "docker-compose.yml"), "Compose 文件路径");
    }

    private static string ResolveWorkdir(ComposeItemDto project, string filePath)
    {
        var workdir = NullIfEmpty(project.Workdir);
        return workdir is not null
            ? NormalizeAbsolutePath(workdir, "Compose 工作目录")
            : DirectoryName(filePath);
    }

    private static string NormalizeAbsolutePath(string path, string label)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 4096 || trimmed.Any(char.IsControl))
        {
            throw new ComposeContentUnsupportedException($"{label}不合法。");
        }

        var replaced = trimmed.Replace('\\', '/');
        if (!replaced.StartsWith('/'))
        {
            throw new ComposeContentUnsupportedException($"{label}必须是绝对路径。");
        }

        var segments = replaced.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                throw new ComposeContentUnsupportedException($"{label}包含不允许的路径片段。");
            }

            stack.Add(segment);
        }

        return stack.Count == 0 ? "/" : "/" + string.Join('/', stack);
    }

    private static bool IsUnderDirectory(string filePath, string directory)
    {
        if (string.Equals(filePath, directory, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = directory.TrimEnd('/') + "/";
        return filePath.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static bool EndsWithYaml(string path) =>
        path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase);

    private static string Combine(string directory, string fileName) =>
        directory.TrimEnd('/') + "/" + fileName;

    private static string DirectoryName(string filePath)
    {
        var index = filePath.LastIndexOf('/');
        return index <= 0 ? "/" : filePath[..index];
    }

    private static string ParseContent(JsonElement data)
    {
        string? content = null;
        if (data.ValueKind == JsonValueKind.String)
        {
            content = data.GetString();
        }
        else if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("isDir", out var isDir) && isDir.ValueKind == JsonValueKind.True)
            {
                throw new ComposeContentUnsupportedException("目标路径是目录，不是 Compose 文件。");
            }

            if (data.TryGetProperty("content", out var contentProperty))
            {
                content = contentProperty.ValueKind switch
                {
                    JsonValueKind.String => contentProperty.GetString(),
                    JsonValueKind.Null => string.Empty,
                    _ => null
                };
            }
        }

        if (content is null)
        {
            throw new ComposeContentUnsupportedException("1Panel 未返回文件内容。");
        }

        if (content.Contains('\0'))
        {
            throw new ComposeContentUnsupportedException("该文件为二进制，无法在浏览器中编辑。");
        }

        return content;
    }

    private static string SafeTruncate(string content, int max)
    {
        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(content[cut - 1]))
        {
            cut--;
        }

        return content[..cut];
    }

    private static Exception MapPanelError(PanelError error)
    {
        var message = error.Message ?? string.Empty;
        if (message.Contains("larger than 10 MB", StringComparison.OrdinalIgnoreCase))
        {
            return new ComposeContentUnsupportedException("Compose 文件超过 10 MiB，1Panel 拒绝读取。");
        }

        if (message.Contains("preview is not supported", StringComparison.OrdinalIgnoreCase))
        {
            return new ComposeContentUnsupportedException("该文件为二进制或 1Panel 不支持预览。");
        }

        if (message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return new ComposeContentUnsupportedException("Compose 文件不存在。");
        }

        return new ComposeContentUnsupportedException("读取 Compose 文件失败：" + message);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<NodeRecord> ResolveNodeAsync(NodeId nodeId, CancellationToken cancellationToken)
    {
        var node = await _nodeStore.FindAsync(nodeId, cancellationToken).ConfigureAwait(false);
        if (node is null || !node.IsEnabled)
        {
            throw new ContainerNodeNotFoundException(nodeId.Value);
        }

        return node;
    }

    private NodeDescriptor BuildDescriptor(NodeRecord record) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        ClockOffset: record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
}
