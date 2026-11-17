using System.Text.Json;
using System.Text.Json.Serialization;

namespace PanelFleet.Client.Dto.Resources;

// P4 资源列表 DTO：全部宽松解析（字段缺失/类型变化不抛异常），只映射展示需要的字段。

public sealed class PagedItemsDto<T>
{
    public long Total { get; set; }

    public List<T>? Items { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class WebsiteItemDto
{
    public long Id { get; set; }

    public string? PrimaryDomain { get; set; }

    public string? Alias { get; set; }

    public string? Type { get; set; }

    public string? Status { get; set; }

    public string? ExpireDate { get; set; }

    public string? SitePath { get; set; }

    public string? AppName { get; set; }

    public string? RuntimeName { get; set; }

    public string? Protocol { get; set; }

    public string? Remark { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class AppInstalledItemDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? AppKey { get; set; }

    public string? AppType { get; set; }

    public string? Version { get; set; }

    public string? Status { get; set; }

    public string? Message { get; set; }

    public int? HttpPort { get; set; }

    public int? HttpsPort { get; set; }

    public string? Path { get; set; }

    public string? AppName { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ContainerItemDto
{
    public string? ContainerID { get; set; }

    public string? Name { get; set; }

    public string? ImageName { get; set; }

    public string? State { get; set; }

    public string? RunTime { get; set; }

    public bool IsFromApp { get; set; }

    public bool IsFromCompose { get; set; }

    public string? AppName { get; set; }

    public string? Description { get; set; }

    public JsonElement? Ports { get; set; }

    public JsonElement? Network { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class DatabaseInstanceDto
{
    public long Id { get; set; }

    public string? Type { get; set; }

    public string? From { get; set; }

    public string? Database { get; set; }

    public string? Version { get; set; }

    public string? Address { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class DatabaseItemDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? MysqlName { get; set; }

    public string? PostgresqlName { get; set; }

    public string? MongodbName { get; set; }

    public string? From { get; set; }

    public string? Format { get; set; }

    public string? Collation { get; set; }

    public string? Description { get; set; }

    public bool? SuperUser { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class GroupItemDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    public bool IsDefault { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class CronjobItemDto
{
    public long Id { get; set; }

    public string? Name { get; set; }

    public string? Type { get; set; }

    [JsonPropertyName("groupID")]
    public long? GroupID { get; set; }

    public string? Spec { get; set; }

    public List<string>? Specs { get; set; }

    public string? Status { get; set; }

    public string? Executor { get; set; }

    public string? Command { get; set; }

    public string? Script { get; set; }

    public string? Website { get; set; }

    public string? AppID { get; set; }

    public string? DbName { get; set; }

    public string? ContainerName { get; set; }

    public string? User { get; set; }

    public string? Url { get; set; }

    public int? RetainCopies { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
