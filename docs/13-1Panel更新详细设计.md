# 13 P12 1Panel 更新详细设计

- 版本：v1.0
- 状态：**已实现（P12，2026-11-16）**
- 日期：2026-11-16
- 关联：[需求与范围](00-需求与范围.md) / [架构设计](01-架构设计.md) / [工作约束](02-工作约束.md) / [功能扩展路线图](08-功能扩展路线图.md) / [P5 写操作内核](09-P5-写操作内核详细设计.md)
- 范围：`src/PanelFleet.Application/PanelUpdates/**`、`src/PanelFleet.Client/UpdateRoutes.cs`、`src/PanelFleet.Client/Dto/Updates/**`、`src/PanelFleet.Web/Endpoints/PanelUpdateEndpoints.cs`、`src/PanelFleet.Web/Components/Pages/PanelUpdate.razor`、`src/PanelFleet.Web/Components/PanelUpdates/**`

## 1. 目标与非目标

**目标**

1. **版本检查（只读）**：跨节点展示 当前版本 / 可用版本 / 更新说明 / 在线与预检结果；
2. **预检**：节点在线、当前版本可读、可用版本可读、已是最新则禁止升级、磁盘可用空间不足则阻断（若可读）；
3. **单节点升级**：Critical 风险，强确认文本为节点名；执行前重新预检，执行后回读版本并做健康检查；
4. **批量升级**（用户已确认允许，强守卫）：强制显式多选、顺序执行、失败即停止、批量强确认文本「升级 N 个节点」、逐节点 + 批次审计；
5. **无自动回滚 / 无自动重试**：升级失败一律人工介入，文档与 UI 明确提示；
6. 真实节点只做**版本检查**（GET 类只读），升级动作仅由假面板/独立测试机验证。

**非目标**

- 自动回滚、自动重试、无人值守定时升级；
- 升级包/任务进度的细粒度轮询（1Panel v2.3.2 升级接口无 taskID、无进度接口）；
- 跨节点并行升级（批量必须顺序，避免同时重启打满网络/磁盘）；
- 1Panel Agent 与应用/容器的升级（仅面板自身 `panel.update`）。

## 2. 真实接口探测结论（1Panel v2.3.2）

> 探测对象：内网真实 1Panel 节点（地址脱敏），时间为 2026-11-16。**全程只执行只读请求，未执行任何升级。**
> 源码核对：1Panel 官方 tag `v2.3.2`（`core/router/ro_setting.go`、`core/app/api/v2/upgrade.go`、`core/app/service/upgrade.go`、`core/init/router/router.go`、`agent/init/router/router.go`）。

### 2.1 路由前缀与鉴权（关键）

| 项目 | 结论 |
|---|---|
| agent API 前缀 | `/api/v2/**`（`/dashboard/base/os` 等，实测可用） |
| core API 前缀 | `/api/v2/core/**`（版本检查/升级均在这里；core 源码 `PrivateGroup := Router.Group("/api/v2/core")`） |
| 鉴权 | 现有 HMAC 头 `1Panel-Token` / `1Panel-Timestamp`（`HMAC-SHA256(key, "1panel:<ts>")`）对 core 路由**同样有效**，无需 session Cookie |
| 空路由行为 | 命中 core 的未知路径会返回安全入口 HTML；因此 404 必须结合响应体判断（客户端已把 HTML 响应归类为协议错误） |

### 2.2 版本与升级接口

| 用途 | 方法/路径 | 响应关键字段 | 说明 |
|---|---|---|---|
| **可用版本 + 更新说明** | `GET /api/v2/core/settings/upgrade` | `testVersion`, `newVersion`, `latestVersion`, `releaseNote` | 实测返回 `latestVersion=v2.5.1` + 更新说明；`newVersion` 为当前版本线内小版本，`latestVersion` 为跨版本线最新版本；**两者皆空 = 已是最新**（或离线/开发模式） |
| **当前版本** | `POST /api/v2/core/settings/search/base`，body `{}` | `systemVersion`（如 `v2.3.2`）、`edition` | 只读查询，无副作用，1Panel 官方前端同款调用；**面板没有 GET 版本接口** |
| **提交升级** | `POST /api/v2/core/settings/upgrade`，body `{"version":"v2.5.1"}` | `data:null` | 异步执行（goroutine 下载/解包/替换二进制后重启面板），**无 taskID、无进度接口** |
| 版本更新日志列表 | `GET /api/v2/core/settings/upgrade/releases` | `version/createdAt/content/...` | 实测返回 500（上游文档站抓取失败），**不依赖** |
| 健康/磁盘探测 | `GET /api/v2/dashboard/base/all/all`、`GET /api/v2/dashboard/base/os` | `currentInfo.diskData[].free/path` | 升级前后健康检查复用 |

> **当前版本读取方式的决策（Lead 已确认方案 A）**：`POST /api/v2/core/settings/search/base` 是纯查询接口、无副作用，可作为「当前版本」来源；
> 真实节点验收阶段仍只执行 GET 类版本检查，升级执行与写接口只在假面板/测试机验证。

### 2.3 1Panel 升级行为的源码结论

1. `Upgrade(req)` 的校验**同步**执行：检查架构、服务文件名、磁盘空间（`minUpgradeFreeSpace = 500 << 20`，即安装目录可用空间 ≥ **500 MiB**，否则直接报错）；
2. 通过校验后设置 `SystemStatus=Upgrading`，**异步**下载升级包并替换 `1panel-core/1panel-agent/1pctl`，随后 `RestartPanel`；
3. 任何一步失败时 1Panel 会尝试**自身**的备份回滚（`handleBackup/handleRollback`），但 PanelFleet 无法感知其回滚结果，也**不提供**跨节点回滚能力；
4. 升级成功后 `SystemVersion` 被更新，面板与 agent 重启，期间 API 不可用（数十秒到数分钟）。

## 3. 版本比较（`PanelVersionComparer`）

- 支持 `v2.3.2`、`2.3.2`、`v2.5.1-beta.1`、`v2.3.2-lts`、`v2.3.2.4`；去掉 `v` 前缀与 `+build`；
- 点分数字逐段比较（缺失段补 0，最多 4 段）；`-lts` 等同正式版（对齐 1Panel `ComparePanelVersion` 的补 `-lts` 行为）；
- 其它预发布后缀（beta/rc）低于同号正式版；
- **不可解析的版本按「更低」处理，绝不抛异常**（版本来自外部面板，属于不可信输入）；
- `IsUpgrade` 要求两侧都可解析才返回 `true`（当前版本格式异常时不会误判「有更新」）；版本可读性校验同时要求当前版本可解析；
- 字符串直接比较不可用：`v2.10.0 > v2.9.9` 但字符串序相反。

## 4. 版本检查与预检

### 4.1 检查流程（每节点）

```
GET  dashboard/base/all/all            → 在线？；顺带取 currentInfo.diskData 估算磁盘
POST core/settings/search/base {}      → data.systemVersion（当前版本，只读查询）
GET  core/settings/upgrade             → newVersion/latestVersion/releaseNote
  ├─ target = latestVersion ?? newVersion
  ├─ target 为空               → 已是最新（AlreadyLatest）
  └─ target > current          → 有更新（UpdateAvailable）
磁盘：优先 "/" 分区，否则可用空间最大的分区；free < 500 MiB → 阻断（读不到只提示不阻断）
```

- 节点**禁用**时不发起任何网络请求，直接返回「已禁用」；
- 单节点检查总超时 `PanelUpdate:CheckTimeoutSeconds`（默认 30s）；
- 检查为只读，不持有节点写锁，可并发。

### 4.2 阻断项（预检拒绝）

| 场景 | 阻断原因文案（用户可见） | 是否允许升级 |
|---|---|---|
| 节点禁用 | 节点已禁用，不参与版本检查与升级。 | 否 |
| 节点不可达 | 节点不可达，无法检查版本（…）。 | 否 |
| 当前版本不可读 | 无法读取当前版本（…）。 | 否 |
| 可用版本不可读 | 无法读取可用版本（…）。 | 否 |
| 已是最新 | 已是最新版本（当前 vX）。 | 否 |
| 磁盘不足（可读时） | 磁盘可用空间不足：/ 仅 N MiB，1Panel 升级要求至少 500 MiB。 | 否 |
| 磁盘不可读 | 未获取到磁盘信息…（仅提示，不阻断） | 是（由 1Panel 自行校验） |

> `BlockingReasons` 为阻断项，`Warnings` 为不阻断的提示（如磁盘信息不可读）；`CanUpgrade` 只受阻断项影响。
> 预检在 **预览** 与 **执行** 两个阶段各执行一次：预览阶段把不合格节点从批次中剔除（`OperationValidationException`），执行阶段再次确认该节点仍可升级，避免「预览后节点掉线/已被别人升级」。

## 5. 页面与交互（`/panel-update`）

```
页面标题：面板更新                    [检查更新]
风险提示卡（无自动回滚 / 面板重启 / 维护窗口 / 健康检查 / 磁盘 500MiB）
汇总：可升级 N · 已是最新 N · 异常 N · 共 N 个节点 · 统一目标版本 vX
批量栏：已选择 N 个节点 [批量升级]   （默认单节点；批量必须显式勾选 ≥2 个可升级节点）
表格：选择 | 节点 | 状态 | 当前版本 | 可用版本 | 预检(阻断原因) | 更新说明(折叠) | [单节点升级]
执行结果：OperationResultView（逐节点成功/失败、信息、耗时）+ [查看审计记录] + 失败人工介入提示
```

- 表格行内「单节点升级」：预览 → `OperationConfirmDialog`（Critical、输入节点名）→ 执行 → 结果；
- 批量升级：勾选多个可升级节点 → 预览（确认文本「升级 N 个节点」）→ 执行 → 结果；
- 确认弹窗复用内核 `OperationConfirmDialog`（风险徽标 + 不可回滚提示 + 目标/警告 + 强确认输入）；
- 执行结果复用内核 `OperationResultView`；「查看审计记录」跳转 `/audit`；
- 升级完成后自动重新执行一次版本检查并刷新表格；取消/确认失败后必须重新预览（Token 一次性）。

## 6. 单节点升级执行流程（`panel.update` 处理器）

```
ExecuteAsync(单节点)
  ├─ 预检该节点（在线/版本可读/有更新/磁盘）      失败 → 步骤失败，停止批次
  ├─ target = 参数 targetVersion ?? 预检 TargetVersion
  ├─ target 必须可解析且 > 当前版本                 否则 → 步骤失败
  ├─ POST core/settings/upgrade {"version": target}  失败 → 步骤失败（不轮询）
  └─ 轮询健康检查（默认每 3s，最长 90s）：
       GET dashboard/base/all/all   失败 → 面板可能在重启，继续
       POST search/base {} 回读 systemVersion
       ├─ 回读 ≥ target → 成功：消息含「当前 → 目标」版本变化
       └─ 超时：
            ├─ 面板可达但版本未变 → 失败：「升级命令已提交，但 N 秒内未回读到目标版本…无自动回滚」
            └─ 面板不可达        → 失败：「升级命令已提交，但 N 秒内面板不可达…请稍后人工确认」
```

- 单节点执行预算受写内核 `Operations:ExecuteTimeoutSeconds`（默认 120s）约束，健康检查预算默认 90s，留出请求开销；
- 提交成功但健康检查失败时，**不重试**、不回滚，结果标失败并提示人工介入；
- 步骤 `DetailJson` 记录：`currentVersion`、`targetVersion`、`observedVersion`、`panelReachable`、`healthChecks`、`healthDurationMs`、`phases`（`submitted/restarted/healthy`）；审计的 `message` 中包含版本变化文本（脱敏、截断 500 字符）。

## 7. 批量升级守卫（Critical）

| 约束 | 实现 |
|---|---|
| 默认单节点 | 页面行内按钮仅升级 1 个节点；批量按钮在勾选 < 2 个节点时禁用 |
| 批量必须显式多选 | 无「全选」；每个节点独立复选框，且只有预检合格节点可选 |
| 强确认文本 | 单节点：节点名（内核默认）；批量：`升级 N 个节点`（处理器 `TypedConfirmationText`） |
| 风险等级 | `OperationRisk.Critical`，`TargetType = "panel"` |
| 顺序执行 | 写内核逐节点串行，每节点获取节点写锁 |
| 失败即停止 | 写内核语义：某节点失败后不再执行后续节点 |
| 每节点 + 批次审计 | `operation.executed` / `operation.failed`（TargetType=panel）+ `operation.batch`（nodeCount/succeeded/failed/status） |
| 升级后健康检查 | 每节点执行后回读版本 + `dashboard/base/os` 探测 |
| 无自动回滚 | 处理器不重试、不回滚；预览警告 + 结果页 + 文档三处明确 |
| 维护窗口建议 | 预览警告与页面风险提示均要求选择维护窗口 |
| 上限 | 写内核 `Operations:MaxBatchNodes`（默认 50） |

## 8. Client 层

### 8.1 路由常量 `src/PanelFleet.Client/UpdateRoutes.cs`

```csharp
public const string PanelSettingBase = "api/v2/core/settings/search/base"; // POST 只读查询，data.systemVersion
public const string UpgradeInfo      = "api/v2/core/settings/upgrade";     // GET 可用版本+更新说明
public const string UpgradeExecute   = "api/v2/core/settings/upgrade";     // POST {"version": "..."}
public static string HealthProbe => PanelRoutes.DashboardBase("all", "all");
```

### 8.2 DTO `src/PanelFleet.Client/Dto/Updates/PanelUpdateDtos.cs`

- `PanelSettingBaseDto`：`SystemVersion`、`Edition`、`PanelName` + `[JsonExtensionData]`；
- `PanelUpgradeInfoDto`：`TestVersion`、`NewVersion`、`LatestVersion`、`ReleaseNote` + `[JsonExtensionData]`；
- `PanelUpdateAckDto`：命令类接口的空数据确认。

全部宽松解析，字段缺失/类型变化不抛异常（与 P4 资源 DTO 约定一致）。

## 9. Application 层

### 9.1 类型

```csharp
public sealed class PanelUpdateOptions
{
    public int CheckTimeoutSeconds { get; set; } = 30;
    public int HealthCheckTimeoutSeconds { get; set; } = 90;
    public int HealthCheckIntervalSeconds { get; set; } = 3;
    public long MinimumFreeDiskBytes { get; set; } = 500L * 1024 * 1024;
    public void Validate();
}

public sealed record PanelNodeVersionInfo(
    NodeId NodeId, string NodeName, bool NodeEnabled, bool Online, bool VersionReadable,
    string? CurrentVersion, string? AvailableVersion, string? ReleaseNote,
    bool UpdateAvailable, bool AlreadyLatest, long? DiskFreeBytes, string? DiskPath,
    IReadOnlyList<string> BlockingReasons, PanelError? Error, TimeSpan Duration)
{
    public bool CanUpgrade { get; }
}

public sealed record PanelVersionCheckResult(DateTimeOffset CheckedAt, IReadOnlyList<PanelNodeVersionInfo> Nodes);
public sealed record PanelUpdatePrecheckItem(NodeId NodeId, string NodeName, string? CurrentVersion,
    string? TargetVersion, bool CanUpgrade, long? DiskFreeBytes,
    IReadOnlyList<string> BlockingReasons, PanelError? Error);
public sealed record PanelUpdatePrecheckResult(DateTimeOffset CheckedAt, IReadOnlyList<PanelUpdatePrecheckItem> Items);

public interface IPanelUpdateService
{
    Task<PanelVersionCheckResult> CheckVersionsAsync(IReadOnlyList<NodeId>? nodeIds, CancellationToken ct);
    Task<PanelUpdatePrecheckResult> PrecheckAsync(IReadOnlyList<NodeId> nodeIds, CancellationToken ct);
}
```

### 9.2 写操作处理器

```csharp
public sealed class PanelUpdateOperationHandler : IOperationHandler
{
    public const string HandlerKind = "panel.update";
    public string Kind => HandlerKind;

    Task<OperationPlan> PlanAsync(OperationPlanContext ctx, CancellationToken ct);
    Task<OperationStepResult> ExecuteAsync(OperationNodeContext ctx, CancellationToken ct);
}
```

- `PlanAsync`：运行预检（剔除非合格节点）→ 计算目标版本 → `OperationPlan(Title, Summary, Critical, Targets, Warnings, TypedConfirmationText, TargetType="panel")`；
- `ExecuteAsync`：见第 6 节；`TargetId = 节点 Id`，`TaskId = null`（无 taskID）。

### 9.3 模块注册

```csharp
public static IServiceCollection AddPanelUpdateModule(this IServiceCollection services);
public static IServiceCollection AddPanelUpdateModule(this IServiceCollection services, Action<PanelUpdateOptions>? configure);
```

注册内容：`PanelUpdateOptions`（默认值）、`IPanelUpdateService → PanelUpdateService`（Scoped）、`IOperationHandler → PanelUpdateOperationHandler`（Singleton）。
**幂等**：已注册过 `PanelUpdateOptions` 时直接返回（生产接线与测试重复注册都安全）。

## 10. Web 层

### 10.1 只读 API（Admin only）

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/panel-update/versions` | 全部节点版本检查；可选 `?nodeIds=` 重复参数过滤；返回 `PanelVersionCheckResult` |

只读 GET，无需 CSRF；升级动作走写内核 `POST /api/operations/preview` + `POST /api/operations/{token}/execute`（已有端点）。

### 10.2 页面与组件

| 文件 | 职责 |
|---|---|
| `Components/Pages/PanelUpdate.razor` | `/panel-update`，`[Authorize]`；检查更新、批量选择、预览/确认/执行、结果、审计跳转 |
| `Components/PanelUpdates/PanelVersionTable.razor + .razor.css` | 节点版本表格、预检徽标、更新说明折叠、行内单节点升级 |
| `Components/PanelUpdates/PanelUpdateRiskNotice.razor + .razor.css` | 升级前风险提示（无回滚/重启/维护窗口/健康检查/磁盘） |

### 10.3 接线（由 Lead 统一修改共享文件）

```csharp
// Program.cs（using PanelFleet.Application.PanelUpdates;）
builder.Services.AddPanelUpdateModule();              // 可传 configure 覆盖 PanelUpdateOptions

// Endpoints/EndpointRegistration.cs
app.MapPanelUpdateEndpoints();

// Components/Layout/NavMenu.razor（审计之前或之后）
<NavLink class="nav-link" href="/panel-update">面板更新</NavLink>

// Components/App.razor（使用 .razor.css 时需要；若无该 link 则 scoped CSS 不生效）
<link rel="stylesheet" href="PanelFleet.Web.styles.css" />
```

页面自身不需要 `_Imports` 修改（内部 `@using PanelFleet.Web.Components.PanelUpdates`）。

## 11. 数据模型与迁移

无新增表、无迁移：版本信息全部实时读取 1Panel，不缓存到数据库；执行结果在写内核内存历史（最近 200 条）与审计表。节点表 `PanelVersion` 字段不由本模块更新（实时值以本页为准；节点页「测试」可刷新持久化版本）。

## 12. 配置项

| 名称 | 默认值 | 必填 | 说明 |
|---|---|---|---|
| `PanelUpdate:CheckTimeoutSeconds` | 30 | 否 | 单节点版本检查总超时 |
| `PanelUpdate:HealthCheckTimeoutSeconds` | 90 | 否 | 升级后健康检查/版本回读总超时（需小于 `Operations:ExecuteTimeoutSeconds`） |
| `PanelUpdate:HealthCheckIntervalSeconds` | 3 | 否 | 健康检查轮询间隔 |
| `PanelUpdate:MinimumFreeDiskBytes` | 524288000（500 MiB） | 否 | 升级前磁盘可用空间阈值（1Panel 硬性 500 MiB） |
| `Operations:ExecuteTimeoutSeconds` | 120 | 否 | 写内核单节点执行预算，必须大于健康检查超时 + 请求开销 |

配置通过 `AddPanelUpdateModule(options => { ... })` 或接线处读取 `IConfiguration` 后传入；未配置时使用上表默认值。

## 13. 审计

| Action | 时机 | TargetType | 关键 Detail |
|---|---|---|---|
| `operation.executed` | 单节点升级成功 | `panel` | operationId/kind/risk/nodeName/targetId/success/message（含版本变化）/durationMs |
| `operation.failed` | 单节点升级失败 | `panel` | 同上，另含失败原因 |
| `operation.batch` | 批量（>1 节点）结束 | `panel` | nodeCount/succeeded/failed/status |
| `operation.rejected` | Token 失效/确认文本错误/重复提交 | `operation` | reason（内核统一写入） |

审计不记录 API Key、升级下载地址等敏感信息；`message` 截断 500 字符。

## 14. 错误矩阵

| 场景 | 用户可见提示 | 日志级别 | 处理 |
|---|---|---|---|
| 节点离线 | 节点不可达，无法检查版本（…） | Warning | 阻断该节点升级 |
| 版本不可读 | 无法读取当前/可用版本（…） | Warning | 阻断 |
| 已是最新 | 已是最新版本（当前 vX） | Information | 阻断，提示无需升级 |
| 磁盘不足 | 磁盘可用空间不足：/ 仅 N MiB… | Warning | 阻断 |
| 目标版本不高于当前 | 目标版本 X 不高于节点 Y 的当前版本 Z，无法升级。 | Warning | 预览拒绝 |
| 升级命令提交失败 | 提交升级命令失败：… | Warning | 步骤失败，失败停止 |
| 升级后版本未变 | 升级命令已提交，但 N 秒内未回读到目标版本…无自动回滚 | Warning | 步骤失败，人工介入 |
| 升级后面板不可达 | 升级命令已提交，但 N 秒内面板不可达… | Warning | 步骤失败，人工介入 |
| 确认文本错误 | 确认文本不匹配，请输入：升级 N 个节点 | Information | 写内核 `operation.rejected`，需重新预览 |
| 检查请求异常 | 检查更新失败：… | Warning | 页面错误提示，不影响其他节点 |

## 15. 测试清单

### 15.1 单元测试（`tests/PanelFleet.Client.UnitTests/PanelUpdates`）

| 文件 | 覆盖点 |
|---|---|
| `PanelVersionComparerTests` | 版本解析/比较、`v2.10.0 > v2.9.9`、`-lts` 等同正式版、beta 低于正式版、非法输入不抛异常 |
| `PanelUpdateServiceTests` | 当前/可用版本与更新说明、已最新、离线、版本不可读、磁盘不足、禁用节点不发请求、`nodeIds` 顺序、预检拒绝与合格节点、节点不存在 |
| `PanelUpdateOperationHandlerTests` | Single/Batch 预览（Critical、目标版本、强确认文本「升级 N 个节点」、无回滚/维护窗口警告）、已最新与低目标版本拒绝、执行成功（提交版本+回读版本+DetailJson）、版本未变失败、面板不可达失败、提交失败不轮询、写内核批量顺序与失败停止（第三节点不执行）+ 批次/节点审计 |

### 15.2 契约测试（`tests/PanelFleet.Client.ContractTests/PanelUpdateContractTests.cs`）

- `GET core/settings/upgrade` 字段映射与更新说明；
- `POST core/settings/search/base` 只读查询返回 `systemVersion` 且请求体为 `{}`；
- `POST core/settings/upgrade` 请求体包含 `{"version":"v2.5.1"}`、响应 `data:null` 视为成功；
- 空版本（已是最新）语义；
- 路由常量锁定（`/api/v2/core` 前缀）。

### 15.3 集成测试（`tests/PanelFleet.IntegrationTests/PanelUpdateApiTests.cs`）

使用 `PanelFleetWebApplicationFactory` + `AdditionalServices` 注册 `AddPanelUpdateModule` + 自建假 `IPanelClientFactory`（内存，不访问真实节点）：

1. 版本检查：当前 v2.3.2 / 可用 v2.5.1，可升级；
2. 已最新节点与离线节点的预检阻断；
3. 已最新节点预览被拒（`OperationValidationException`）；
4. 单节点：预览 Critical + 强确认=节点名 → 错误文本抛 409 语义异常 → 重新预览 → 执行成功 → `operation.executed`（TargetType=panel，Detail 含版本）；
5. 批量 3 节点：确认文本「升级 3 个节点」→ 第二节点提交失败即停止（第三节点未调用）→ `PartiallySucceeded` + 逐节点/批次审计。

**全程不执行真实升级。**

## 16. 验收步骤

### 16.1 真实节点（只读）

1. 启动 PanelFleet（Lead 接线后），打开 `/panel-update`；
2. 点击「检查更新」：应显示真实节点当前版本、可用版本、更新说明、磁盘与预检结果；
3. 在浏览器开发者工具确认只发出 `GET /api/panel-update/versions`（后台为 `GET dashboard/base/all/all`、`POST core/settings/search/base` 只读查询、`GET core/settings/upgrade`），**不发出** `POST core/settings/upgrade`；
4. 真实节点不要点击升级（预检通过后页面仍会要求预览 + 强确认，人工不确认不会执行）。

### 16.2 假面板（升级全流程）

用集成测试或独立测试机（假面板）验证：预览 → 强确认 → 单节点成功 → 批量顺序 → 失败停止 → 审计完整；真实节点仅承担版本检查。

## 17. 无自动回滚与人工介入（必须告知）

1. PanelFleet **不提供**跨节点回滚，也不会自动重试升级；
2. 1Panel 升级程序自身在部分步骤失败时会尝试用备份恢复，但 PanelFleet 无法确认其结果；
3. 升级后健康检查失败时的处理：
   - 记录失败节点、目标版本、回读版本、耗时（审计 + 结果页）；
   - 停止批次，后续节点不执行；
   - 人工登录节点执行 `1pctl status` / 查看 `1panel-core`、`1panel-agent` 服务与 `/opt/1panel/log`（按实际安装目录），必要时按 1Panel 官方文档用升级备份目录（`1panel/tmp/upgrade/<version>/original`）恢复；
   - 恢复后回到 `/panel-update` 重新检查更新，再决定是否重试；
4. **维护窗口建议**：升级会重启面板并短暂中断 API；批量升级耗时 ≈ 节点数 ×（升级时长 + 健康检查），请安排在业务低峰并提前通知。

## 18. 风险与未决问题

| 风险 | 影响 | 对策/现状 |
|---|---|---|
| 面板无 GET 当前版本接口 | 需用只读 POST 查询 | 已记录决策 A：`POST core/settings/search/base` 纯查询、无副作用；真实节点验收只做 GET |
| 升级无 taskID/进度接口 | 无法精确展示进度 | 用「已提交 → 重启中 → 健康检查/版本回读」三阶段 + 超时判定 |
| 面板重启时间长于健康检查预算 | 误判失败 | 预算可配置（默认 90s），失败文案明确「命令已提交，可能仍在重启，请人工确认」 |
| 升级接口跳版本限制 | 跨版本升级不兼容 | 目标版本取面板官方 `latestVersion`（其次 `newVersion`），与 1Panel 官方 UI 一致 |
| 批量升级风险放大 | 多节点同时不可用 | Critical + 强确认 + 显式多选 + 顺序 + 失败停止 + 无回滚提示 |
| API Key 在升级后被重置 | 升级后无法回读版本 | 健康检查失败提示人工确认；不重试、不自动改节点配置 |
| 真实节点误操作 | 生产事故 | 真实节点只做版本检查；升级仅在假面板/测试机验证 |

## 19. 实施记录

- 2026-11-16：完成真实接口探测（只读）、Client/Application/Web/测试与本文档；
- 待 Lead 接线：`AddPanelUpdateModule()`、`MapPanelUpdateEndpoints()`、`NavMenu` 导航、`App.razor` scoped CSS `<link>`，随后跑全量构建与测试。
