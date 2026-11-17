using System.Text.Json;
using PanelFleet.Client;
using PanelFleet.Client.ContractTests.FakePanel;
using PanelFleet.Client.Dto.Cronjobs;
using PanelFleet.Client.Dto.Resources;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.ContractTests;

/// <summary>
/// P9 计划任务契约测试：真实 PanelClientFactory + 进程内假面板。
/// search / load-info 使用共享假面板已内置的 P4 计划任务路由；
/// 记录/日志/周期/分组/写路由通过 FakePanelOptions.JsonRoutes 扩展。
/// </summary>
public sealed class CronjobContractTests
{
    [Fact]
    public async Task CronjobsSearch_UsesSharedFakeFixture_AndSendsRequiredParameters()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<CronjobInfoDto>>(
            CronjobRoutes.Search,
            new CronjobSearchRequest(1, 50, "backup", [7], "name", "null"));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data?.Total);
        var item = Assert.Single(result.Data!.Items!);
        Assert.Equal(1, item.Id);
        Assert.Equal("备份网站数据", item.Name);
        Assert.Equal("backupWebsite", item.Type);
        Assert.Equal("0 3 * * *", item.Spec);
        Assert.Equal("example.com", item.Website);
        Assert.Equal("Enable", item.Status);
        Assert.Equal("Success", item.LastRecordStatus);
        Assert.Equal(7, item.RetainCopies);
        Assert.Equal(3, item.RetryTimes);

        var body = server.GetLastRequestBody(CronjobRoutes.Search);
        Assert.NotNull(body);
        Assert.Contains("\"orderBy\":\"name\"", body, StringComparison.Ordinal);
        Assert.Contains("\"order\":\"null\"", body, StringComparison.Ordinal);
        Assert.Contains("\"groupIDs\":[7]", body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobLoadInfo_UsesSharedFakeFixture()
    {
        var options = new FakePanelOptions();
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<JsonElement>(CronjobRoutes.Info, new CronjobIdRequest(5));

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Data.GetProperty("id").GetInt64());
        Assert.Equal("备份网站数据", result.Data.GetProperty("name").GetString());
        Assert.Equal("backupWebsite", result.Data.GetProperty("type").GetString());
        Assert.Equal("0 3 * * *", result.Data.GetProperty("spec").GetString());
        Assert.Equal("example.com", result.Data.GetProperty("website").GetString());
        Assert.Contains("\"id\":5", server.GetLastRequestBody(CronjobRoutes.Info)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobRecordsSearch_BusinessError_MapsToPanelError()
    {
        var options = new FakePanelOptions
        {
            RawJsonRoutes = new Dictionary<string, string>
            {
                [CronjobRoutes.RecordsSearch] = """{"code":500,"message":"服务错误: record not found","data":null}"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<PagedItemsDto<CronjobRecordDto>>(
            CronjobRoutes.RecordsSearch,
            new CronjobRecordSearchRequest(1, 20, 5, "2026-11-16T00:00:00Z", "2026-11-17T00:00:00Z", string.Empty));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal(PanelErrorKind.PanelServerError, result.Error!.Kind);
    }

    [Fact]
    public async Task CronjobRecordsAndLog_MapFields()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [CronjobRoutes.RecordsSearch] = """
                    {"total":1,"items":[{"id":11,"cronjobID":5,"taskID":"task-1","startTime":"2026-11-17 01:30:00","interval":12.5,"status":"Success","message":"ok","targetPath":"/tmp"}]}
                    """,
                [CronjobRoutes.RecordLog] = "\"backup done\\nfiles: 3\""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var records = await client.PostAsync<PagedItemsDto<CronjobRecordDto>>(
            CronjobRoutes.RecordsSearch,
            new CronjobRecordSearchRequest(1, 20, 5, "2026-11-16T00:00:00Z", "2026-11-17T00:00:00Z", "Success"));
        Assert.True(records.IsSuccess);
        var record = Assert.Single(records.Data!.Items!);
        Assert.Equal(11, record.Id);
        Assert.Equal("task-1", record.TaskID);
        Assert.Equal(12.5, record.Interval);
        Assert.Equal("Success", record.Status);
        Assert.Contains("\"cronjobID\":5", server.GetLastRequestBody(CronjobRoutes.RecordsSearch)!, StringComparison.Ordinal);

        var log = await client.PostAsync<string>(CronjobRoutes.RecordLog, new CronjobIdRequest(11));
        Assert.True(log.IsSuccess);
        Assert.Contains("backup done", log.Data!, StringComparison.Ordinal);
        Assert.Contains("files: 3", log.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobNext_ReturnsTimeList()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [CronjobRoutes.Next] = """["2026-11-17 02:30:00","2026-11-18 02:30:00"]"""
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<List<string>>(CronjobRoutes.Next, new CronjobSpecRequest("30 2 * * *"));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.Count);
        Assert.Contains("\"spec\":\"30 2 * * *\"", server.GetLastRequestBody(CronjobRoutes.Next)!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CronjobGroups_ReturnsGroupItems()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [CronjobRoutes.GroupsSearch] = """
                    [{"id":7,"name":"备份","type":"cronjob","isDefault":true},{"id":1,"name":"Default","type":"website","isDefault":true}]
                    """
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var result = await client.PostAsync<List<GroupItemDto>>(CronjobRoutes.GroupsSearch, new { });

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.Count);
        Assert.Equal("cronjob", result.Data![0].Type);
        Assert.True(result.Data![0].IsDefault);
    }

    [Fact]
    public async Task CronjobWriteRoutes_AcceptExpectedBodies()
    {
        var options = new FakePanelOptions
        {
            JsonRoutes = new Dictionary<string, string?>
            {
                [CronjobRoutes.Create] = "{}",
                [CronjobRoutes.Update] = "{}",
                [CronjobRoutes.Delete] = "{}",
                [CronjobRoutes.Status] = "{}",
                [CronjobRoutes.Handle] = "{}"
            }
        };
        await using var server = new FakePanelServer(options);
        await server.StartAsync();
        var client = CreateClient(server, options);

        var create = await client.PostAsync<JsonElement>(CronjobRoutes.Create, new
        {
            name = "job-a",
            type = "shell",
            spec = "30 1 * * 1",
            script = "echo hi",
            retainCopies = 7,
            retryTimes = 0,
            timeout = 3600,
            ignoreErr = false
        });
        Assert.True(create.IsSuccess);

        var update = await client.PostAsync<JsonElement>(CronjobRoutes.Update, new
        {
            id = 3,
            name = "job-a",
            type = "snapshot",
            spec = "30 1 * * 1",
            snapshotRule = new { withImage = true },
            secret = "keep-me",
            retainCopies = 7
        });
        Assert.True(update.IsSuccess);
        Assert.Contains("\"secret\":\"keep-me\"", server.GetLastRequestBody(CronjobRoutes.Update)!, StringComparison.Ordinal);

        var delete = await client.PostAsync<JsonElement>(
            CronjobRoutes.Delete,
            new CronjobDeleteRequest([1, 2], CleanData: false, CleanRemoteData: false));
        Assert.True(delete.IsSuccess);
        var deleteBody = server.GetLastRequestBody(CronjobRoutes.Delete)!;
        Assert.Contains("\"ids\":[1,2]", deleteBody, StringComparison.Ordinal);
        Assert.Contains("\"cleanData\":false", deleteBody, StringComparison.Ordinal);

        var status = await client.PostAsync<JsonElement>(CronjobRoutes.Status, new CronjobStatusRequest(1, "Disable"));
        Assert.True(status.IsSuccess);
        Assert.Contains("\"status\":\"Disable\"", server.GetLastRequestBody(CronjobRoutes.Status)!, StringComparison.Ordinal);

        var handle = await client.PostAsync<JsonElement>(CronjobRoutes.Handle, new CronjobIdRequest(1));
        Assert.True(handle.IsSuccess);
        Assert.Contains("\"id\":1", server.GetLastRequestBody(CronjobRoutes.Handle)!, StringComparison.Ordinal);
    }

    private static IPanelClient CreateClient(FakePanelServer server, FakePanelOptions options)
    {
        var factory = new PanelClientFactory(
            new PanelHttpOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(5),
                ConnectTimeout = TimeSpan.FromSeconds(2)
            },
            TimeProvider.System);

        return factory.Create(new NodeDescriptor(
            NodeId.New(),
            "node-1",
            server.BaseUri,
            options.ApiKey,
            AuthMode.HmacSha256));
    }
}
