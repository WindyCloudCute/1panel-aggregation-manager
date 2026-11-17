using System.Globalization;
using System.Text.Json.Nodes;
using PanelFleet.Application.Operations;

namespace PanelFleet.Application.AppStore;

/// <summary>应用安装参数（来自内核 <see cref="OperationParameters"/> 的强类型解析结果）。</summary>
public sealed record AppInstallArguments(
    long AppDetailId,
    string Name,
    string? AppKey,
    string? DisplayName,
    string? Version,
    JsonObject Parameters,
    double CpuQuota,
    long MemoryLimit,
    string MemoryUnit,
    string? ContainerName,
    bool AllowPort,
    string? SpecifyIp,
    string? RestartPolicy,
    bool PullImage,
    bool GpuConfig,
    bool EditCompose,
    string? DockerCompose,
    string? WebUI);

/// <summary>应用卸载参数。</summary>
public sealed record AppUninstallArguments(
    long InstallId,
    string? Name,
    bool ForceDelete,
    bool DeleteBackup,
    bool DeleteImage,
    bool DeleteDatabase);

/// <summary>应用升级参数。</summary>
public sealed record AppUpdateArguments(
    long InstallId,
    string? Name,
    long DetailId,
    string? Version,
    bool Backup,
    bool PullImage,
    bool DeleteImage,
    string? DockerCompose);

/// <summary>批量升级中的单个应用目标。</summary>
public sealed record AppBatchUpgradeTarget(
    Guid NodeId,
    long InstallId,
    string? Name,
    string Version,
    long DetailId,
    string? DockerCompose);

/// <summary>批量升级参数：一个内核操作批次内按节点、按应用顺序串行升级。</summary>
public sealed record AppBatchUpgradeArguments(
    bool Backup,
    bool PullImage,
    bool DeleteImage,
    IReadOnlyList<AppBatchUpgradeTarget> Targets);

/// <summary>应用配置修改参数。</summary>
public sealed record AppParamsUpdateArguments(
    long InstallId,
    string? Name,
    JsonObject Parameters,
    bool Advanced,
    double? CpuQuota,
    long? MemoryLimit,
    string? MemoryUnit,
    string? ContainerName,
    bool? AllowPort,
    string? SpecifyIp,
    string? RestartPolicy,
    bool? EditCompose,
    string? DockerCompose);

/// <summary>应用商店写操作参数解析与校验（供各 <c>IOperationHandler</c> 与单元测试复用）。</summary>
public static class AppStoreOperationParameters
{
    private static readonly string[] RestartPolicies = ["always", "unless-stopped", "no", "on-failure"];

    private static readonly string[] AdvancedKeys =
    [
        "advanced", "cpuQuota", "memoryLimit", "memoryUnit", "containerName",
        "allowPort", "specifyIP", "restartPolicy", "editCompose", "dockerCompose"
    ];

    /// <summary>解析并校验安装参数。</summary>
    public static AppInstallArguments ReadInstall(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var appDetailId = ReadInt64(parameters, "appDetailId") ?? 0;
        if (appDetailId <= 0)
        {
            throw new OperationValidationException("缺少参数：应用版本 ID（appDetailId），请先在应用商店选择版本。");
        }

        var name = ValidateInstallName(parameters.RequireString("name", "安装名称"));
        var appKey = Trimmed(parameters.GetString("appKey"));
        var displayName = Trimmed(parameters.GetString("appName")) ?? Trimmed(parameters.GetString("displayName"));
        var version = Trimmed(parameters.GetString("version"));
        var parametersObject = parameters.GetObject("params") ?? new JsonObject();

        var memoryUnit = (Trimmed(parameters.GetString("memoryUnit")) ?? "M").ToUpperInvariant();
        if (memoryUnit is not ("M" or "G"))
        {
            throw new OperationValidationException("内存单位只支持 M（MB）或 G（GB）。");
        }

        var cpuQuota = ReadDouble(parameters, "cpuQuota") ?? 0;
        if (cpuQuota < 0)
        {
            throw new OperationValidationException("CPU 限额不能为负数。");
        }

        var memoryLimit = ReadInt64(parameters, "memoryLimit") ?? 0;
        if (memoryLimit < 0)
        {
            throw new OperationValidationException("内存限额不能为负数。");
        }

        var restartPolicy = Trimmed(parameters.GetString("restartPolicy"));
        ValidateRestartPolicy(restartPolicy);

        return new AppInstallArguments(
            appDetailId,
            name,
            appKey,
            displayName ?? name,
            version,
            parametersObject,
            cpuQuota,
            memoryLimit,
            memoryUnit,
            Trimmed(parameters.GetString("containerName")),
            parameters.GetBoolean("allowPort"),
            Trimmed(parameters.GetString("specifyIP")),
            restartPolicy,
            parameters.GetBoolean("pullImage", fallback: true),
            parameters.GetBoolean("gpuConfig"),
            parameters.GetBoolean("editCompose"),
            Trimmed(parameters.GetString("dockerCompose")),
            Trimmed(parameters.GetString("webUI")));
    }

    /// <summary>解析并校验卸载参数。</summary>
    public static AppUninstallArguments ReadUninstall(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var installId = RequireInstallId(parameters);

        return new AppUninstallArguments(
            installId,
            Trimmed(parameters.GetString("name")),
            parameters.GetBoolean("forceDelete"),
            parameters.GetBoolean("deleteBackup"),
            parameters.GetBoolean("deleteImage"),
            parameters.GetBoolean("deleteDB"));
    }

    /// <summary>解析并校验升级参数。</summary>
    public static AppUpdateArguments ReadUpdate(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var installId = RequireInstallId(parameters);

        var detailId = ReadInt64(parameters, "detailId") ?? 0;
        if (detailId <= 0)
        {
            throw new OperationValidationException("缺少参数：目标版本 ID（detailId），请先查询可用升级版本。");
        }

        return new AppUpdateArguments(
            installId,
            Trimmed(parameters.GetString("name")),
            detailId,
            Trimmed(parameters.GetString("version")),
            parameters.GetBoolean("backup", fallback: true),
            parameters.GetBoolean("pullImage", fallback: true),
            parameters.GetBoolean("deleteImage"),
            Trimmed(parameters.GetString("dockerCompose")));
    }

    /// <summary>解析并校验批量升级参数。</summary>
    public static AppBatchUpgradeArguments ReadBatchUpgrade(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var targetsNode = parameters.GetArray("targets")
            ?? throw new OperationValidationException("缺少参数：targets（待升级应用列表）。");

        var targets = new List<AppBatchUpgradeTarget>(targetsNode.Count);
        var seen = new HashSet<(Guid NodeId, long InstallId)>();
        foreach (var item in targetsNode)
        {
            if (item is not JsonObject target)
            {
                throw new OperationValidationException("参数 targets 中的每一项都必须是对象。");
            }

            var nodeIdText = NodeString(target, "nodeId");
            if (!Guid.TryParse(nodeIdText, out var nodeId))
            {
                throw new OperationValidationException("升级目标缺少有效节点 ID（nodeId）。");
            }

            var installId = NodeInt64(target, "installId") ?? 0;
            if (installId <= 0)
            {
                throw new OperationValidationException("缺少参数：已安装应用 ID（installId）。");
            }

            var detailId = NodeInt64(target, "detailId") ?? 0;
            if (detailId <= 0)
            {
                throw new OperationValidationException("缺少参数：目标版本 ID（detailId），请先查询可用升级版本。");
            }

            var version = NodeString(target, "version")
                ?? throw new OperationValidationException("缺少参数：目标版本（version）。");

            if (!seen.Add((nodeId, installId)))
            {
                throw new OperationValidationException(
                    $"升级目标重复：节点 {nodeId:D} 中的应用安装 ID {installId} 出现了多次。");
            }

            targets.Add(new AppBatchUpgradeTarget(
                nodeId,
                installId,
                NodeString(target, "name"),
                version,
                detailId,
                NodeString(target, "dockerCompose")));
        }

        if (targets.Count == 0)
        {
            throw new OperationValidationException("缺少参数：targets（待升级应用列表）。");
        }

        return new AppBatchUpgradeArguments(
            parameters.GetBoolean("backup", fallback: true),
            parameters.GetBoolean("pullImage", fallback: true),
            parameters.GetBoolean("deleteImage"),
            targets);
    }

    /// <summary>解析并校验配置修改参数。</summary>
    public static AppParamsUpdateArguments ReadParamsUpdate(OperationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var installId = RequireInstallId(parameters);
        var parametersObject = parameters.GetObject("params")
            ?? throw new OperationValidationException("缺少参数：params（应用参数对象）。");

        var advanced = parameters.GetBoolean("advanced") || AdvancedKeys.Any(parameters.Contains);
        if (parametersObject.Count == 0 && !advanced)
        {
            throw new OperationValidationException("没有需要修改的参数。");
        }

        var memoryUnit = Trimmed(parameters.GetString("memoryUnit"))?.ToUpperInvariant();
        if (memoryUnit is not null && memoryUnit is not ("M" or "G"))
        {
            throw new OperationValidationException("内存单位只支持 M（MB）或 G（GB）。");
        }

        var restartPolicy = Trimmed(parameters.GetString("restartPolicy"));
        ValidateRestartPolicy(restartPolicy);

        return new AppParamsUpdateArguments(
            installId,
            Trimmed(parameters.GetString("name")),
            parametersObject,
            advanced,
            ReadDouble(parameters, "cpuQuota"),
            ReadInt64(parameters, "memoryLimit"),
            memoryUnit,
            Trimmed(parameters.GetString("containerName")),
            ReadNullableBool(parameters, "allowPort"),
            Trimmed(parameters.GetString("specifyIP")),
            restartPolicy,
            ReadNullableBool(parameters, "editCompose"),
            Trimmed(parameters.GetString("dockerCompose")));
    }

    /// <summary>安装名称规则：1~64 位，首字符为字母或数字，其余允许字母、数字、<c>-</c><c>_</c><c>.</c>。</summary>
    public static string ValidateInstallName(string? name)
    {
        var value = Trimmed(name) ?? throw new OperationValidationException("缺少参数：安装名称。");
        if (value.Length > 64)
        {
            throw new OperationValidationException("安装名称不能超过 64 个字符。");
        }

        if (!char.IsAsciiLetterOrDigit(value[0]))
        {
            throw new OperationValidationException("安装名称必须以字母或数字开头。");
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.'))
            {
                throw new OperationValidationException("安装名称只能包含字母、数字、-、_、.。");
            }
        }

        return value;
    }

    private static string? NodeString(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<string>(out var text))
        {
            return Trimmed(text);
        }

        if (value.TryGetValue<Guid>(out var guid))
        {
            return guid.ToString("D");
        }

        return null;
    }

    private static long? NodeInt64(JsonObject node, string name)
    {
        if (node[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (value.TryGetValue<int>(out var intValue))
        {
            return intValue;
        }

        if (value.TryGetValue<string>(out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static long RequireInstallId(OperationParameters parameters)
    {
        var installId = ReadInt64(parameters, "installId") ?? 0;
        if (installId <= 0)
        {
            throw new OperationValidationException("缺少参数：已安装应用 ID（installId）。");
        }

        return installId;
    }

    private static void ValidateRestartPolicy(string? restartPolicy)
    {
        if (restartPolicy is null)
        {
            return;
        }

        if (!RestartPolicies.Contains(restartPolicy, StringComparer.Ordinal))
        {
            throw new OperationValidationException("重启策略只支持 always、unless-stopped、no、on-failure。");
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long? ReadInt64(OperationParameters parameters, string name)
    {
        if (parameters.GetInt64(name) is { } value)
        {
            return value;
        }

        // JsonObject 直接写入 int 时底层是 JsonValue<int>，GetInt64 只识别 long；
        // 再从字符串形式兜底，保证 HTTP JSON / UI 构造的 JsonObject 都能解析。
        return long.TryParse(parameters.GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? ReadDouble(OperationParameters parameters, string name)
    {
        var text = parameters.GetString(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new OperationValidationException($"参数 {name} 不是合法数字。");
    }

    private static bool? ReadNullableBool(OperationParameters parameters, string name)
    {
        if (!parameters.Contains(name))
        {
            return null;
        }

        return parameters.GetBoolean(name);
    }
}
