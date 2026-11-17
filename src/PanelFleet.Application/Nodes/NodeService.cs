using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Models;
using PanelFleet.Domain.Model;

namespace PanelFleet.Application.Nodes;

/// <summary>节点 CRUD、连接测试与健康巡检的用例实现。</summary>
public sealed class NodeService : INodeService
{
    private static readonly JsonSerializerOptions AuditJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly INodeStore _nodes;
    private readonly INodeGroupStore _groups;
    private readonly INodeSecretProtector _protector;
    private readonly IConnectionTester _tester;
    private readonly IAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<NodeService> _logger;

    public NodeService(
        INodeStore nodes,
        INodeGroupStore groups,
        INodeSecretProtector protector,
        IConnectionTester tester,
        IAuditStore audit,
        TimeProvider time,
        ILogger<NodeService> logger)
    {
        _nodes = nodes;
        _groups = groups;
        _protector = protector;
        _tester = tester;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NodeSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var records = await _nodes.ListAsync(cancellationToken).ConfigureAwait(false);
        var groups = await _groups.ListAsync(cancellationToken).ConfigureAwait(false);
        var groupNames = groups.ToDictionary(group => group.Id, group => group.Name);

        return records
            .Select(record => ToSummary(record, ResolveGroupName(record, groupNames)))
            .ToArray();
    }

    public async Task<NodeSummary> CreateAsync(
        CreateNodeRequest request,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureValid(NodeValidator.Validate(request));
        var name = request.Name!.Trim();
        var baseUrl = NodeValidator.NormalizeBaseUrl(request.BaseUrl, out _)!;
        var securityEntrance = NodeValidator.NormalizeSecurityEntrance(request.SecurityEntrance, out _);
        await EnsureGroupExistsAsync(request.GroupId, cancellationToken).ConfigureAwait(false);
        await EnsureUniqueAsync(name, baseUrl, null, cancellationToken).ConfigureAwait(false);

        var secret = _protector.Protect(request.ApiKey!.Trim());
        var now = _time.GetUtcNow();
        var record = new NodeRecord(
            NodeId.New(),
            name,
            baseUrl,
            securityEntrance,
            secret.CipherText,
            secret.Hint,
            request.AuthMode,
            null,
            null,
            request.TlsPolicy,
            request.GroupId,
            NormalizeTags(request.Tags),
            NormalizeRemark(request.Remark),
            request.IsEnabled,
            null,
            PanelCapability.None,
            NodeHealth.Unknown,
            null,
            null,
            null,
            now,
            now);

        await _nodes.AddAsync(record, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            "node.created",
            "node",
            record.Id.ToString(),
            record.Id,
            new { name, baseUrl = record.BaseUrl.ToString(), authMode = record.AuthMode.ToString(), tlsPolicy = record.TlsPolicy.ToString() },
            actor,
            cancellationToken).ConfigureAwait(false);

        return ToSummary(record, await GroupNameAsync(record.GroupId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<NodeSummary> UpdateAsync(
        NodeId id,
        UpdateNodeRequest request,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _nodes.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NodeNotFoundException(id);

        EnsureValid(NodeValidator.Validate(request));
        var name = request.Name!.Trim();
        var baseUrl = NodeValidator.NormalizeBaseUrl(request.BaseUrl, out _)!;
        var securityEntrance = NodeValidator.NormalizeSecurityEntrance(request.SecurityEntrance, out _);
        await EnsureGroupExistsAsync(request.GroupId, cancellationToken).ConfigureAwait(false);
        await EnsureUniqueAsync(name, baseUrl, id, cancellationToken).ConfigureAwait(false);

        var apiKeyProtected = existing.ApiKeyProtected;
        var apiKeyHint = existing.ApiKeyHint;
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            var secret = _protector.Protect(request.ApiKey.Trim());
            apiKeyProtected = secret.CipherText;
            apiKeyHint = secret.Hint;
        }

        var now = _time.GetUtcNow();
        var updated = existing with
        {
            Name = name,
            BaseUrl = baseUrl,
            SecurityEntrance = securityEntrance,
            ApiKeyProtected = apiKeyProtected,
            ApiKeyHint = apiKeyHint,
            AuthMode = request.AuthMode,
            TlsPolicy = request.TlsPolicy,
            GroupId = request.GroupId,
            Tags = NormalizeTags(request.Tags),
            Remark = NormalizeRemark(request.Remark),
            IsEnabled = request.IsEnabled,
            UpdatedAt = now
        };

        var changed = ChangedFields(existing, updated);
        await _nodes.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            "node.updated",
            "node",
            updated.Id.ToString(),
            updated.Id,
            new { changed },
            actor,
            cancellationToken).ConfigureAwait(false);

        return ToSummary(updated, await GroupNameAsync(updated.GroupId, cancellationToken).ConfigureAwait(false));
    }

    public async Task DeleteAsync(NodeId id, ActorInfo actor, CancellationToken cancellationToken)
    {
        var existing = await _nodes.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NodeNotFoundException(id);

        await _nodes.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync(
            "node.deleted",
            "node",
            existing.Id.ToString(),
            existing.Id,
            new { name = existing.Name, baseUrl = existing.BaseUrl.ToString() },
            actor,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeTestResult> TestSavedAsync(NodeId id, ActorInfo actor, CancellationToken cancellationToken)
    {
        var existing = await _nodes.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NodeNotFoundException(id);

        var result = await _tester.TestAsync(BuildInput(existing), cancellationToken).ConfigureAwait(false);
        var updated = ApplyTestResult(existing, result);
        await _nodes.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(
            "node.tested",
            "node",
            existing.Id.ToString(),
            existing.Id,
            new
            {
                reachable = result.Reachable,
                authorized = result.Authorized,
                latencyMs = result.LatencyMs,
                panelVersion = result.PanelVersion,
                errorKind = result.ErrorKind?.ToString()
            },
            actor,
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<NodeTestResult> TestDraftAsync(TestDraftRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        EnsureValid(NodeValidator.Validate(request));
        var baseUrl = NodeValidator.NormalizeBaseUrl(request.BaseUrl, out _)!;
        var input = new NodeConnectionInput(
            "draft",
            baseUrl,
            request.ApiKey!.Trim(),
            request.AuthMode,
            request.TlsPolicy,
            TimeSpan.Zero);

        return await _tester.TestAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NodeGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken) =>
        await _groups.ListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<NodeGroupSummary> CreateGroupAsync(
        GroupCreateRequest request,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ValidateGroupName(request.Name);
        await EnsureGroupNameUniqueAsync(name, null, cancellationToken).ConfigureAwait(false);

        var group = new NodeGroupSummary(Guid.NewGuid(), name, 0);
        await _groups.AddAsync(group, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync("nodegroup.created", "group", group.Id.ToString(), null, new { name }, actor, cancellationToken)
            .ConfigureAwait(false);

        return group;
    }

    public async Task<NodeGroupSummary> UpdateGroupAsync(
        Guid id,
        GroupUpdateRequest request,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = await _groups.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NodeGroupNotFoundException(id);

        var name = ValidateGroupName(request.Name);
        await EnsureGroupNameUniqueAsync(name, id, cancellationToken).ConfigureAwait(false);

        var updated = existing with { Name = name, SortOrder = request.SortOrder };
        await _groups.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync("nodegroup.updated", "group", id.ToString(), null, new { name, sortOrder = request.SortOrder }, actor, cancellationToken)
            .ConfigureAwait(false);

        return updated;
    }

    public async Task DeleteGroupAsync(Guid id, ActorInfo actor, CancellationToken cancellationToken)
    {
        var existing = await _groups.FindAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new NodeGroupNotFoundException(id);

        await _groups.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        await WriteAuditAsync("nodegroup.deleted", "group", id.ToString(), null, new { name = existing.Name }, actor, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<NodeId>> ListEnabledIdsAsync(CancellationToken cancellationToken) =>
        _nodes.ListEnabledIdsAsync(cancellationToken);

    public async Task CheckNodeHealthAsync(NodeId id, CancellationToken cancellationToken)
    {
        var existing = await _nodes.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || !existing.IsEnabled)
        {
            return;
        }

        var result = await _tester.TestAsync(BuildInput(existing), cancellationToken).ConfigureAwait(false);
        var updated = ApplyTestResult(existing, result);
        await _nodes.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);

        if (existing.Health != updated.Health)
        {
            _logger.LogInformation(
                "节点 {NodeId} 健康状态变化 {OldStatus} -> {NewStatus}",
                id,
                existing.Health,
                updated.Health);
        }
    }

    private NodeRecord ApplyTestResult(NodeRecord existing, NodeTestResult result)
    {
        var error = result.ErrorKind is { } kind
            ? new PanelError(kind, result.ErrorMessage ?? "连接测试失败。")
            : null;
        var (status, message) = NodeHealthEvaluator.Evaluate(error);

        var now = _time.GetUtcNow();
        var updated = existing with
        {
            Health = status,
            HealthMessage = message,
            LastCheckedAt = now,
            LastSeenAt = result.Authorized ? now : existing.LastSeenAt,
            UpdatedAt = now
        };

        if (result.Authorized)
        {
            updated = updated with
            {
                PanelVersion = result.PanelVersion ?? existing.PanelVersion,
                DetectedAuthMode = result.DetectedAuthMode,
                ClockOffsetSeconds = result.ClockOffsetSeconds,
                Capabilities = existing.Capabilities | PanelCapability.Dashboard
            };
        }

        return updated;
    }

    private NodeConnectionInput BuildInput(NodeRecord record) => new(
        record.Name,
        record.BaseUrl,
        _protector.Unprotect(record.ApiKeyProtected),
        record.AuthMode,
        record.TlsPolicy,
        record.ClockOffsetSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);

    private async Task<string?> GroupNameAsync(Guid? groupId, CancellationToken cancellationToken)
    {
        if (groupId is not { } id)
        {
            return null;
        }

        var group = await _groups.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return group?.Name;
    }

    private static string? ResolveGroupName(NodeRecord record, IReadOnlyDictionary<Guid, string> groupNames) =>
        record.GroupId is { } groupId && groupNames.TryGetValue(groupId, out var name) ? name : null;

    private static NodeSummary ToSummary(NodeRecord record, string? groupName) => new(
        record.Id,
        record.Name,
        record.BaseUrl,
        record.SecurityEntrance,
        record.GroupId,
        groupName,
        record.Tags,
        record.Remark,
        record.ApiKeyHint,
        record.AuthMode,
        record.DetectedAuthMode,
        record.TlsPolicy,
        record.PanelVersion,
        record.Capabilities,
        record.Health,
        record.HealthMessage,
        record.LastCheckedAt,
        record.IsEnabled,
        record.CreatedAt,
        record.UpdatedAt);

    private async Task EnsureUniqueAsync(string name, Uri baseUrl, NodeId? exceptId, CancellationToken cancellationToken)
    {
        if (await _nodes.NameExistsAsync(name, exceptId, cancellationToken).ConfigureAwait(false))
        {
            throw new NodeConflictException("name", "节点名称已存在。");
        }

        if (await _nodes.BaseUrlExistsAsync(baseUrl, exceptId, cancellationToken).ConfigureAwait(false))
        {
            throw new NodeConflictException("baseUrl", "节点地址已存在。");
        }
    }

    private async Task EnsureGroupExistsAsync(Guid? groupId, CancellationToken cancellationToken)
    {
        if (groupId is { } id && await _groups.FindAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new NodeValidationException(new Dictionary<string, string[]> { ["groupId"] = ["分组不存在。"] });
        }
    }

    private async Task EnsureGroupNameUniqueAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        if (await _groups.NameExistsAsync(name, exceptId, cancellationToken).ConfigureAwait(false))
        {
            throw new NodeConflictException("name", "分组名称已存在。");
        }
    }

    private static string ValidateGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new NodeValidationException(new Dictionary<string, string[]> { ["name"] = ["请填写分组名称。"] });
        }

        var trimmed = name.Trim();
        if (trimmed.Length > NodeValidator.MaxNameLength)
        {
            throw new NodeValidationException(
                new Dictionary<string, string[]>
                {
                    ["name"] = [$"分组名称不能超过 {NodeValidator.MaxNameLength} 个字符。"]
                });
        }

        return trimmed;
    }

    private static void EnsureValid(IReadOnlyDictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new NodeValidationException(errors);
        }
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        tags is null
            ? []
            : tags.Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string? NormalizeRemark(string? remark) =>
        string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();

    private static List<string> ChangedFields(NodeRecord before, NodeRecord after)
    {
        var changed = new List<string>();
        if (before.Name != after.Name) changed.Add("name");
        if (before.BaseUrl != after.BaseUrl) changed.Add("baseUrl");
        if (before.SecurityEntrance != after.SecurityEntrance) changed.Add("securityEntrance");
        if (before.ApiKeyProtected != after.ApiKeyProtected) changed.Add("apiKey");
        if (before.AuthMode != after.AuthMode) changed.Add("authMode");
        if (before.TlsPolicy != after.TlsPolicy) changed.Add("tlsPolicy");
        if (before.GroupId != after.GroupId) changed.Add("groupId");
        if (!before.Tags.SequenceEqual(after.Tags, StringComparer.Ordinal)) changed.Add("tags");
        if (before.Remark != after.Remark) changed.Add("remark");
        if (before.IsEnabled != after.IsEnabled) changed.Add("isEnabled");
        return changed;
    }

    private async Task WriteAuditAsync(
        string action,
        string targetType,
        string? targetId,
        NodeId? nodeId,
        object? detail,
        ActorInfo actor,
        CancellationToken cancellationToken)
    {
        var entry = new AuditEntry(
            Guid.NewGuid(),
            _time.GetUtcNow(),
            actor,
            action,
            targetType,
            targetId,
            nodeId,
            detail is null ? null : JsonSerializer.Serialize(detail, AuditJson));

        try
        {
            await _audit.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "写入审计失败：{Action}", action);
        }
    }
}
