using PanelFleet.Application.Databases;

namespace PanelFleet.Web.Components.Databases;

/// <summary>数据库页面各弹窗的表单模型（UI 内部使用）。</summary>
public enum DatabaseUserDialogMode
{
    Create = 0,
    Password = 1
}

/// <summary>新建数据库表单。</summary>
public sealed record DatabaseCreateForm(
    string Name,
    string Format,
    string Collation,
    string Username,
    string Password,
    string Permission,
    bool SuperUser,
    string Description);

/// <summary>创建用户 / 重置密码表单。</summary>
public sealed record DatabaseUserForm(
    string Username,
    string Host,
    string Password,
    string Description,
    IReadOnlyList<string> Dbs);

/// <summary>授权 / 取消授权表单。</summary>
public sealed record DatabaseGrantForm(string Db, string Username, string Host);

/// <summary>Redis 配置更新表单。</summary>
public sealed record RedisConfigForm(string Timeout, string Maxclients, string Maxmemory);

/// <summary>实例操作目标（页面内状态）。</summary>
public sealed record DatabaseInstanceKey(Guid NodeId, string NodeName, DatabaseEngine Engine, string Instance, string From);
