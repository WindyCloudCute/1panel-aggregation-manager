using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Terminal;

/// <summary>终端会话用例（scoped）：预检、容量、注册与开始审计。</summary>
public interface ITerminalSessionManager
{
    /// <summary>打开一个终端会话；失败抛出 <see cref="TerminalException"/>（Web 层映射为 HTTP 或错误帧）。</summary>
    Task<TerminalSessionHandle> OpenAsync(
        TerminalSessionRequest request,
        ActorInfo actor,
        CancellationToken cancellationToken);

    /// <summary>当前活跃会话快照（不含任何密钥/输入内容）。</summary>
    IReadOnlyList<TerminalSessionInfo> ListActiveSessions();
}

/// <summary>终端会话管理器实现。</summary>
public sealed partial class TerminalSessionManager : ITerminalSessionManager
{
    private static readonly JsonSerializerOptions AuditJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly INodeStore _nodes;
    private readonly INodeSecretProtector _protector;
    private readonly IPanelClientFactory _clientFactory;
    private readonly IAuditStore _audit;
    private readonly ITerminalBackend _backend;
    private readonly TerminalSessionRegistry _registry;
    private readonly TerminalOptions _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TerminalSessionManager> _logger;

    public TerminalSessionManager(
        INodeStore nodes,
        INodeSecretProtector protector,
        IPanelClientFactory clientFactory,
        IAuditStore audit,
        ITerminalBackend backend,
        TerminalSessionRegistry registry,
        TerminalOptions options,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ILogger<TerminalSessionManager> logger)
    {
        _nodes = nodes;
        _protector = protector;
        _clientFactory = clientFactory;
        _audit = audit;
        _backend = backend;
        _registry = registry;
        _options = options;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public IReadOnlyList<TerminalSessionInfo> ListActiveSessions() => _registry.Snapshot();

    public async Task<TerminalSessionHandle> OpenAsync(
        TerminalSessionRequest request,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        if (!_options.Enabled)
        {
            throw new TerminalDisabledException();
        }

        var normalized = Normalize(request);

        var record = await _nodes.FindAsync(normalized.NodeId, cancellationToken).ConfigureAwait(false)
            ?? throw new TerminalNotFoundException("node_not_found", "节点不存在。");
        if (!record.IsEnabled)
        {
            throw new TerminalNotFoundException("node_disabled", "节点已禁用，无法打开终端。");
        }

        var descriptor = new NodeDescriptor(
            record.Id,
            record.Name,
            record.BaseUrl,
            _protector.Unprotect(record.ApiKeyProtected),
            record.AuthMode,
            record.TlsPolicy,
            record.ApiKeyHint,
            record.Remark,
            record.Tags,
            record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

        AuthMode? resolvedAuthMode;
        using (var client = _clientFactory.Create(descriptor))
        {
            if (normalized.Mode == TerminalMode.Host)
            {
                await EnsureLocalSshConfiguredAsync(client, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await EnsureContainerRunnableAsync(client, normalized.Container!, cancellationToken).ConfigureAwait(false);
            }

            resolvedAuthMode = client.AuthState.Resolved ? client.AuthState.EffectiveMode : null;
        }

        var now = _time.GetUtcNow();
        var info = new TerminalSessionInfo(
            Guid.NewGuid().ToString("N"),
            record.Id,
            record.Name,
            normalized.Mode,
            normalized.Mode == TerminalMode.Container ? normalized.Container : null,
            now,
            normalized.Cols,
            normalized.Rows)
        {
            State = TerminalSessionState.Connecting
        };

        if (!_registry.TryRegister(info))
        {
            throw new TerminalCapacityException(_registry.Limit);
        }

        var context = new TerminalConnectContext(
            descriptor,
            info.Mode,
            normalized.Container,
            normalized.ContainerCommand,
            normalized.ContainerUser,
            info.Cols,
            info.Rows)
        {
            ResolvedAuthMode = resolvedAuthMode
        };

        await WriteStartAuditAsync(info, actor, context, cancellationToken).ConfigureAwait(false);

        return new TerminalSessionHandle(
            info,
            context,
            TerminalLimits.FromOptions(_options),
            _backend,
            _registry,
            _audit,
            _time,
            _loggerFactory,
            actor,
            _logger);
    }

    private static TerminalSessionRequest Normalize(TerminalSessionRequest request)
    {
        var cols = Math.Clamp(request.Cols, 20, 500);
        var rows = Math.Clamp(request.Rows, 5, 200);

        switch (request.Mode)
        {
            case TerminalMode.Host:
                return request with { Cols = cols, Rows = rows, Container = null, ContainerCommand = null, ContainerUser = null };

            case TerminalMode.Container:
            {
                var container = request.Container?.Trim();
                if (string.IsNullOrEmpty(container))
                {
                    throw new TerminalValidationException("bad_request", "容器终端必须指定容器名或容器 ID。");
                }

                if (!ContainerPattern().IsMatch(container))
                {
                    throw new TerminalValidationException("bad_request", "容器名包含非法字符。");
                }

                var command = string.IsNullOrWhiteSpace(request.ContainerCommand)
                    ? "/bin/sh"
                    : request.ContainerCommand.Trim();
                if (!CommandPattern().IsMatch(command))
                {
                    throw new TerminalValidationException("bad_request", "容器命令包含非法字符（只允许单个可执行文件，如 /bin/sh）。");
                }

                var user = request.ContainerUser?.Trim() ?? string.Empty;
                if (user.Length > 0 && !UserPattern().IsMatch(user))
                {
                    throw new TerminalValidationException("bad_request", "容器用户包含非法字符。");
                }

                return request with
                {
                    Cols = cols,
                    Rows = rows,
                    Container = container,
                    ContainerCommand = command,
                    ContainerUser = user
                };
            }

            default:
                throw new TerminalValidationException("bad_request", "未知终端模式。");
        }
    }

    private static async Task EnsureLocalSshConfiguredAsync(
        IPanelClient client,
        CancellationToken cancellationToken)
    {
        var result = await client
            .GetAsync<TerminalLocalSshConnectionDto>(TerminalRoutes.LocalSshConnection, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw result.Error?.Kind == PanelErrorKind.Unauthorized
                ? new TerminalUpstreamException("upstream_auth_failed", "1Panel 鉴权失败：请检查 API Key、接口状态与 IP 白名单。")
                : new TerminalUpstreamException("upstream_unreachable", "无法读取节点 SSH 配置，请稍后重试。");
        }

        if (result.Data is null || string.IsNullOrWhiteSpace(result.Data.Addr))
        {
            throw new TerminalValidationException(
                "host_ssh_not_configured",
                "节点未配置本机 SSH 连接：请在 1Panel「设置 → SSH 连接」中配置后重试。");
        }
    }

    private static async Task EnsureContainerRunnableAsync(
        IPanelClient client,
        string container,
        CancellationToken cancellationToken)
    {
        var result = await client
            .PostAsync<PagedItemsDto<ContainerItemDto>>(
                TerminalRoutes.ContainerSearch,
                new
                {
                    page = 1,
                    pageSize = 500,
                    state = "all",
                    orderBy = "name",
                    order = "ascending"
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw result.Error?.Kind == PanelErrorKind.Unauthorized
                ? new TerminalUpstreamException("upstream_auth_failed", "1Panel 鉴权失败：请检查 API Key、接口状态与 IP 白名单。")
                : new TerminalUpstreamException("container_check_failed", "无法读取节点容器列表，暂时无法打开容器终端。");
        }

        var match = result.Data?.Items?.FirstOrDefault(item =>
            string.Equals(item.Name, container, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.ContainerID, container, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            throw new TerminalNotFoundException("container_not_found", "容器不存在，或不属于该节点。");
        }

        if (!string.Equals(match.State, "running", StringComparison.OrdinalIgnoreCase))
        {
            throw new TerminalValidationException("container_not_running", "容器未运行，无法打开终端。");
        }
    }

    private async Task WriteStartAuditAsync(
        TerminalSessionInfo info,
        ActorInfo actor,
        TerminalConnectContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var detail = JsonSerializer.Serialize(
                new
                {
                    sessionId = info.SessionId,
                    mode = ModeText(info.Mode),
                    nodeName = info.NodeName,
                    container = info.Container,
                    command = context.ContainerCommand,
                    user = context.ContainerUser,
                    cols = info.Cols,
                    rows = info.Rows
                },
                AuditJson);

            await _audit
                .AddAsync(
                    new AuditEntry(
                        Guid.NewGuid(),
                        _time.GetUtcNow(),
                        actor,
                        "terminal.session.start",
                        "node",
                        info.NodeId.ToString(),
                        info.NodeId,
                        detail),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "终端会话 {SessionId} 开始审计写入失败", info.SessionId);
        }
    }

    internal static string ModeText(TerminalMode mode) => mode == TerminalMode.Host ? "host" : "container";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerPattern();

    [GeneratedRegex("^[A-Za-z0-9_./-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UserPattern();
}

/// <summary>
/// 1Panel 本机 SSH 连接配置的只读视图：仅用于判断「是否已配置」。
/// 响应中还包含私钥字段，本类型不映射、不记录、不回传。
/// </summary>
public sealed class TerminalLocalSshConnectionDto
{
    public string? Addr { get; set; }

    public int? Port { get; set; }

    public string? User { get; set; }

    public string? LocalSSHConnShow { get; set; }
}
