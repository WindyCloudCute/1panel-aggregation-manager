using System.Text.Json.Nodes;

namespace PanelFleet.Web.Components.Cronjobs;

/// <summary>计划任务表单提交结果（父组件据此构造写操作预览）。</summary>
public sealed record CronjobFormResult(
    JsonObject Payload,
    long Id,
    Guid NodeId,
    string Name,
    string Type);
