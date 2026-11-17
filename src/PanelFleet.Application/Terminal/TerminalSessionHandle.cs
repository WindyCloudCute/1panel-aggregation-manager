using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;

namespace PanelFleet.Application.Terminal;

/// <summary>
/// 一次终端会话的句柄：运行桥接、结束时写审计并注销注册（幂等）。
/// 由 <see cref="TerminalSessionManager.OpenAsync"/> 创建，生命周期绑定一次 WebSocket 连接。
/// </summary>
public sealed class TerminalSessionHandle
{
    private static readonly JsonSerializerOptions AuditJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TerminalConnectContext _context;
    private readonly TerminalLimits _limits;
    private readonly ITerminalBackend _backend;
    private readonly TerminalSessionRegistry _registry;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ActorInfo _actor;
    private readonly ILogger<TerminalSessionManager> _logger;

    private int _finished;

    internal TerminalSessionHandle(
        TerminalSessionInfo info,
        TerminalConnectContext context,
        TerminalLimits limits,
        ITerminalBackend backend,
        TerminalSessionRegistry registry,
        IAuditStore audit,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ActorInfo actor,
        ILogger<TerminalSessionManager> logger)
    {
        Info = info;
        _context = context;
        _limits = limits;
        _backend = backend;
        _registry = registry;
        _audit = audit;
        _time = time;
        _loggerFactory = loggerFactory;
        _actor = actor;
        _logger = logger;
    }

    /// <summary>会话信息（不含密钥/输入内容）。</summary>
    public TerminalSessionInfo Info { get; }

    /// <summary>运行桥接直到结束，返回结束原因；结束审计保证只写一次。</summary>
    public async Task<TerminalCloseOutcome> RunAsync(
        ITerminalChannel browser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var telemetry = new TerminalSessionTelemetry();
        TerminalCloseOutcome outcome;

        _registry.UpdateState(Info.SessionId, TerminalSessionState.Active);
        try
        {
            var bridge = new TerminalBridge(
                _backend,
                _context,
                Info,
                _limits,
                _time,
                _loggerFactory.CreateLogger<TerminalBridge>());
            outcome = await bridge.RunAsync(browser, telemetry, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = new TerminalCloseOutcome(TerminalCloseReasons.Server, "终端会话已取消。", false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "终端会话 {SessionId} 异常结束", Info.SessionId);
            outcome = new TerminalCloseOutcome(
                TerminalCloseReasons.UpstreamError,
                "终端会话异常结束，请重新连接。",
                true);
        }
        finally
        {
            _registry.Unregister(Info.SessionId);
        }

        await WriteEndAuditAsync(outcome, telemetry).ConfigureAwait(false);
        return outcome;
    }

    private async Task WriteEndAuditAsync(TerminalCloseOutcome outcome, TerminalSessionTelemetry telemetry)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        try
        {
            var now = _time.GetUtcNow();
            var duration = now - Info.StartedAt;
            var detail = JsonSerializer.Serialize(
                new
                {
                    sessionId = Info.SessionId,
                    mode = TerminalSessionManager.ModeText(Info.Mode),
                    nodeName = Info.NodeName,
                    container = Info.Container,
                    durationSeconds = Math.Round(duration.TotalSeconds, 1),
                    revalidateCount = telemetry.RevalidateCount,
                    inputFrames = telemetry.InputFrames,
                    inputBytes = telemetry.InputBytes,
                    outputBytes = telemetry.OutputBytes,
                    closeReason = outcome.Reason,
                    error = outcome.Message
                },
                AuditJson);

            await _audit
                .AddAsync(
                    new AuditEntry(
                        Guid.NewGuid(),
                        now,
                        _actor,
                        "terminal.session.end",
                        "node",
                        Info.NodeId.ToString(),
                        Info.NodeId,
                        detail),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "终端会话 {SessionId} 结束审计写入失败", Info.SessionId);
        }
    }
}
