using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Cronjobs;
using PanelFleet.Client;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Cronjobs;

public sealed class CronjobServiceTests
{
    private const string GroupsJson = """
        [
          {"id":7,"name":"备份","type":"cronjob","isDefault":false},
          {"id":8,"name":"Default","type":"cronjob","isDefault":true},
          {"id":1,"name":"Default","type":"website","isDefault":true}
        ]
        """;

    private const string JobsJson = """
        {
          "total": 2,
          "items": [
            {
              "id": 1, "name": "daily-backup", "type": "shell", "groupID": 7,
              "specCustom": false, "spec": "30 1 * * 1", "executor": "bash",
              "script": "echo hi", "status": "Enable",
              "lastRecordStatus": "Success", "lastRecordTime": "2026-11-17 01:30:00",
              "retainCopies": 7, "retryTimes": 1, "timeout": 3600, "ignoreErr": false
            },
            {
              "id": 2, "name": "weekly-site", "type": "website", "groupID": 0,
              "specCustom": false, "spec": "0 2 * * *", "website": "example.com",
              "status": "Disable", "lastRecordStatus": "Failed", "lastRecordTime": "2026-11-16 02:30:00",
              "retainCopies": 3, "retryTimes": 0, "timeout": 1800, "ignoreErr": true
            }
          ]
        }
        """;

    private static CronjobService Create(
        CronjobPanelClientFactory factory,
        CronjobNodeStore store,
        CronjobOptions? options = null) =>
        new(
            store,
            new CronjobSecretProtector(),
            factory,
            options ?? new CronjobOptions(),
            TimeProvider.System,
            NullLogger<CronjobService>.Instance);

    private static CronjobPanelClientFactory CreateFactory() =>
        new CronjobPanelClientFactory()
            .Map(CronjobRoutes.GroupsSearch, GroupsJson)
            .Map(CronjobRoutes.Search, JobsJson);

    [Fact]
    public async Task List_MapsRowsAndGroupNames()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = CreateFactory();
        var service = Create(factory, new CronjobNodeStore(node));

        var result = await service.ListAsync(
            new CronjobQuery(null, [], null, null, "name", "null", 1, 50),
            CancellationToken.None);

        Assert.Equal(2, result.TotalMatched);
        Assert.Equal(1, result.NodesSucceeded);
        Assert.Equal(0, result.NodesFailed);
        var shell = result.Rows.Single(row => row.Id == 1);
        Assert.Equal("daily-backup", shell.Name);
        Assert.Equal("Shell 脚本", shell.TypeText);
        Assert.Equal("每周一 01:30", shell.SpecText);
        Assert.Equal("备份", shell.GroupName);
        Assert.Equal("Enable", shell.Status);
        Assert.Equal("Success", shell.LastRecordStatus);
        Assert.Equal("bash", shell.SourceText);
        Assert.Equal(7, shell.RetainCopies);

        var website = result.Rows.Single(row => row.Id == 2);
        Assert.Equal("example.com", website.SourceText);
        Assert.Equal("默认分组", website.GroupName);
    }

    [Fact]
    public async Task List_StatusFilter_AppliesAfterMerge()
    {
        var node = CronjobTestData.Node("node-a");
        var service = Create(CreateFactory(), new CronjobNodeStore(node));

        var result = await service.ListAsync(
            new CronjobQuery(null, [], null, "Disable", "name", "null", 1, 50),
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        Assert.Equal(2, row.Id);
        Assert.Equal(1, result.TotalMatched);
    }

    [Fact]
    public async Task List_NodeFailure_IsIsolated()
    {
        var first = CronjobTestData.Node("node-a");
        var second = CronjobTestData.Node("node-b");
        var factory = CreateFactory()
            .Fail("node-b", new PanelError(PanelErrorKind.Network, "连接超时"));
        var service = Create(factory, new CronjobNodeStore(first, second));

        var result = await service.ListAsync(
            new CronjobQuery(null, [], null, null, "name", "null", 1, 50),
            CancellationToken.None);

        Assert.Equal(1, result.NodesSucceeded);
        Assert.Equal(1, result.NodesFailed);
        Assert.Contains(result.Rows, row => row.NodeName == "node-a");
        Assert.DoesNotContain(result.Rows, row => row.NodeName == "node-b");
        Assert.Contains(result.Nodes, node => node.State == CronjobNodeState.Failed);
    }

    [Fact]
    public async Task List_NodeMissing_Throws()
    {
        var service = Create(CreateFactory(), new CronjobNodeStore());

        await Assert.ThrowsAsync<CronjobNodeNotFoundException>(
            () => service.ListAsync(
                new CronjobQuery(Guid.NewGuid(), [], null, null, "name", "null", 1, 50),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetDetail_ExcludesSecretAndMapsEditableModel()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(
            CronjobRoutes.Info,
            """
            {"id":5,"name":"job-a","type":"shell","groupID":7,"specCustom":false,"spec":"30 1 * * 1","executor":"bash","scriptMode":"input","script":"echo hi","containerName":"","user":"","appID":"","website":"","dbType":"","dbName":"","url":"","sourceDir":"","retainCopies":7,"retryTimes":0,"timeout":3600,"ignoreErr":false,"args":"","secret":"super-secret"}
            """);
        var service = Create(factory, new CronjobNodeStore(node));

        var detail = await service.GetDetailAsync(node.Id.Value, 5, CancellationToken.None);

        Assert.Null(detail.Error);
        Assert.True(detail.SecretSet);
        Assert.NotNull(detail.Editable);
        Assert.Equal("job-a", detail.Editable!.Name);
        Assert.Equal("echo hi", detail.Editable.Script);
        Assert.Equal(7, detail.Editable.RetainCopies);
        Assert.False(detail.Fields.ContainsKey("secret"));
        Assert.DoesNotContain("super-secret", string.Join('|', detail.Fields.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDetail_RecordNotFound_Throws()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().FailNext(
            CronjobRoutes.Info,
            new PanelError(PanelErrorKind.PanelServerError, "服务错误: record not found", PanelCode: "500"));
        var service = Create(factory, new CronjobNodeStore(node));

        await Assert.ThrowsAsync<CronjobNotFoundException>(
            () => service.GetDetailAsync(node.Id.Value, 99, CancellationToken.None));
    }

    [Fact]
    public async Task GetRecords_MapsFieldsAndSendsTimeRange()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(
            CronjobRoutes.RecordsSearch,
            """
            {"total":1,"items":[{"id":11,"cronjobID":5,"taskID":"task-1","startTime":"2026-11-17 01:30:00","interval":12.5,"status":"Success","message":"","targetPath":"/tmp"}]}
            """);
        var service = Create(factory, new CronjobNodeStore(node));
        var end = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        var start = end.AddDays(-1);

        var result = await service.GetRecordsAsync(node.Id.Value, 5, start, end, "Success", 1, 20, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, result.Total);
        var record = Assert.Single(result.Rows);
        Assert.Equal(11, record.Id);
        Assert.Equal("成功", record.StatusText);
        Assert.Equal(12.5, record.IntervalSeconds);

        var call = Assert.Single(factory.CallsFor(CronjobRoutes.RecordsSearch));
        Assert.Contains("\"cronjobID\":5", call.Body, StringComparison.Ordinal);
        Assert.Contains("2026-11-16T00:00:00Z", call.Body, StringComparison.Ordinal);
        Assert.Contains("2026-11-17T00:00:00Z", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Success\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRecordLog_ReturnsText()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(
            CronjobRoutes.RecordLog,
            "\"line1\\nline2\"");
        var service = Create(factory, new CronjobNodeStore(node));

        var log = await service.GetRecordLogAsync(node.Id.Value, 11, CancellationToken.None);

        Assert.Null(log.Error);
        Assert.Contains("line1", log.Content, StringComparison.Ordinal);
        Assert.Contains("line2", log.Content, StringComparison.Ordinal);
        Assert.False(log.Truncated);
    }

    [Fact]
    public async Task ListGroups_FiltersCronjobTypeAndMergesNodes()
    {
        var first = CronjobTestData.Node("node-a");
        var second = CronjobTestData.Node("node-b");
        var factory = new CronjobPanelClientFactory().Map(CronjobRoutes.GroupsSearch, GroupsJson);
        var service = Create(factory, new CronjobNodeStore(first, second));

        var groups = await service.ListGroupsAsync(null, CancellationToken.None);

        Assert.Equal(2, groups.Count);
        var group = groups.Single(item => item.Name == "备份");
        Assert.Equal(7, group.Id);
        Assert.False(group.IsDefault);
        Assert.Equal(2, group.NodeIds.Count);
        Assert.Contains(groups, item => item.IsDefault && item.Name == "Default");
    }

    [Fact]
    public async Task PreviewSpec_UsesNodeNextEndpoint()
    {
        var node = CronjobTestData.Node("node-a");
        var factory = new CronjobPanelClientFactory().Map(
            CronjobRoutes.Next,
            "[\"2026-11-17 02:30:00\",\"2026-11-18 02:30:00\"]");
        var service = Create(factory, new CronjobNodeStore(node));

        var preview = await service.PreviewSpecAsync(null, "30 2 * * *", CancellationToken.None);

        Assert.Null(preview.Error);
        Assert.Equal(2, preview.NextTimes.Count);
        Assert.Single(factory.CallsFor(CronjobRoutes.Next));
    }

    [Fact]
    public async Task PreviewSpec_InvalidSpec_DoesNotCallPanel()
    {
        var factory = new CronjobPanelClientFactory();
        var service = Create(factory, new CronjobNodeStore(CronjobTestData.Node("node-a")));

        var preview = await service.PreviewSpecAsync(null, "not-a-cron", CancellationToken.None);

        Assert.NotNull(preview.Error);
        Assert.Empty(preview.NextTimes);
        Assert.Empty(factory.CallsFor(CronjobRoutes.Next));
    }
}
