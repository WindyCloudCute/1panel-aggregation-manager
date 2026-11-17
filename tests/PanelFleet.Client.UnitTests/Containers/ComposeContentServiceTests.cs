using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Containers;
using PanelFleet.Application.Models;
using PanelFleet.Client;
using PanelFleet.Client.Dto;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Containers;

/// <summary>
/// ComposeContentService 单元测试：验证只按项目名定位 configFile、路径必须位于 workdir 且为 .yml/.yaml、
/// 1MiB 截断、1Panel 错误映射，以及只发起 compose/search + files/content 两次请求。
/// </summary>
public sealed class ComposeContentServiceTests
{
    private const string SearchJson = """
        {"total":1,"items":[
          {"name":"webstack","configFile":"/opt/webstack/docker-compose.yml","workdir":"/opt/webstack","path":"/opt/webstack/docker-compose.yml","composeFileExists":true}
        ]}
        """;

    [Fact]
    public async Task ReadsContentFromProjectConfigFile()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", SearchJson)
            .On("files/content", FileInfoJson("services:\n  web:\n    image: nginx:1.27\n"));
        var service = CreateService(node, factory);

        var result = await service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None);

        Assert.Equal("/opt/webstack/docker-compose.yml", result.Path);
        Assert.Contains("services:", result.Content, StringComparison.Ordinal);
        Assert.False(result.Truncated);

        var fileCall = Assert.Single(factory.Calls, call => call.Path.Contains("files/content", StringComparison.Ordinal));
        Assert.Contains("\"path\":\"/opt/webstack/docker-compose.yml\"", fileCall.Body, StringComparison.Ordinal);
        Assert.Contains("\"isDetail\":false", fileCall.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectNotFound_Throws()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", """{"total":0,"items":[]}""");
        var service = CreateService(node, factory);

        await Assert.ThrowsAsync<ContainerComposeNotFoundException>(
            () => service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None));

        Assert.DoesNotContain(factory.Calls, call => call.Path.Contains("files/content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigFileOutsideWorkdir_IsRejectedWithoutReading()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", """
                {"total":1,"items":[
                  {"name":"webstack","configFile":"/etc/evil.yml","workdir":"/opt/webstack","path":"/opt/webstack"}
                ]}
                """);
        var service = CreateService(node, factory);

        var exception = await Assert.ThrowsAsync<ComposeContentUnsupportedException>(
            () => service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None));

        Assert.Contains("工作目录", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Calls, call => call.Path.Contains("files/content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonYamlConfigFile_IsRejectedWithoutReading()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", """
                {"total":1,"items":[
                  {"name":"webstack","configFile":"/opt/webstack/compose.txt","workdir":"/opt/webstack"}
                ]}
                """);
        var service = CreateService(node, factory);

        var exception = await Assert.ThrowsAsync<ComposeContentUnsupportedException>(
            () => service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None));

        Assert.Contains(".yml", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(factory.Calls, call => call.Path.Contains("files/content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BinaryPreviewError_IsMapped()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", SearchJson)
            .Fail("files/content", "File preview is not supported");
        var service = CreateService(node, factory);

        var exception = await Assert.ThrowsAsync<ComposeContentUnsupportedException>(
            () => service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None));

        Assert.Contains("二进制", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizeError_IsMapped()
    {
        var node = ContainerTestData.Node("node-a");
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", SearchJson)
            .Fail("files/content", "File is larger than 10 MB");
        var service = CreateService(node, factory);

        var exception = await Assert.ThrowsAsync<ComposeContentUnsupportedException>(
            () => service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None));

        Assert.Contains("10 MiB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TooLargeContent_IsTruncatedToEditableLimit()
    {
        var node = ContainerTestData.Node("node-a");
        var big = new string('a', ComposeContentService.MaxEditableContentChars + 128);
        var factory = new ScriptedPanelClientFactory()
            .On("containers/compose/search", SearchJson)
            .On("files/content", FileInfoJson(big));
        var service = CreateService(node, factory);

        var result = await service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(ComposeContentService.MaxEditableContentChars, result.Content.Length);
    }

    [Fact]
    public async Task DisabledNode_IsNotFound()
    {
        var node = ContainerTestData.Node("node-a", enabled: false);
        var service = CreateService(node, new ScriptedPanelClientFactory());

        await Assert.ThrowsAsync<ContainerNodeNotFoundException>(
            () => service.GetComposeContentAsync(node.Id.Value, "webstack", CancellationToken.None));
    }

    private static ComposeContentService CreateService(NodeRecord node, ScriptedPanelClientFactory factory) => new(
        new FakeNodeStore([node]),
        new FakeSecretProtector(),
        factory,
        new ContainerOptions(),
        NullLogger<ComposeContentService>.Instance);

    private static string FileInfoJson(string content, string path = "/opt/webstack/docker-compose.yml") =>
        JsonSerializer.Serialize(new
        {
            content,
            size = content.Length,
            path,
            name = "docker-compose.yml",
            mimeType = "text/yaml",
            extension = ".yml",
            isDir = false
        });

    /// <summary>按路径子串脚本化返回 JSON 或 PanelError 的假 1Panel 客户端工厂。</summary>
    private sealed class ScriptedPanelClientFactory : IPanelClientFactory
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly List<(string PathContains, string? Json, PanelError? Error)> _routes = [];

        public List<FakeCall> Calls { get; } = [];

        public ScriptedPanelClientFactory On(string pathContains, string json)
        {
            _routes.Add((pathContains, json, null));
            return this;
        }

        public ScriptedPanelClientFactory Fail(string pathContains, string message)
        {
            _routes.Add((pathContains, null, new PanelError(PanelErrorKind.PanelServerError, message)));
            return this;
        }

        public IPanelClient Create(NodeDescriptor node) => new ScriptedClient(this, node);

        private PanelCallResult<T> Respond<T>(string nodeName, string relativePath, string method, object? body)
        {
            Calls.Add(new FakeCall(nodeName, relativePath, method, body is null ? null : JsonSerializer.Serialize(body, JsonOptions)));

            foreach (var route in _routes)
            {
                if (!relativePath.Contains(route.PathContains, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (route.Error is not null)
                {
                    return new PanelCallResult<T>(default, route.Error, TimeSpan.FromMilliseconds(1));
                }

                var data = JsonSerializer.Deserialize<T>(route.Json!, JsonOptions);
                return new PanelCallResult<T>(data, null, TimeSpan.FromMilliseconds(1));
            }

            return new PanelCallResult<T>(
                default,
                new PanelError(PanelErrorKind.NotFound, "[" + nodeName + "] 脚本未实现路径：" + relativePath),
                TimeSpan.FromMilliseconds(1));
        }

        private sealed class ScriptedClient : IPanelClient
        {
            private readonly ScriptedPanelClientFactory _factory;

            public ScriptedClient(ScriptedPanelClientFactory factory, NodeDescriptor node)
            {
                _factory = factory;
                Node = node;
            }

            public NodeDescriptor Node { get; }

            public PanelAuthState AuthState => new(AuthMode.HmacSha256, TimeSpan.Zero, true);

            public Task<PanelCallResult<T>> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
                Task.FromResult(_factory.Respond<T>(Node.Name, relativePath, "GET", null));

            public Task<PanelCallResult<T>> PostAsync<T>(
                string relativePath,
                object? body,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(_factory.Respond<T>(Node.Name, relativePath, "POST", body));

            public Task<PanelCallResult<OsInfoDto>> GetOsInfoAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<PanelCallResult<DashboardCurrentDto>> GetCurrentInfoAsync(
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<PanelCallResult<DashboardBaseDto>> GetDashboardBaseAsync(
                string ioOption = "all",
                string netOption = "all",
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public void Dispose()
            {
            }
        }
    }
}
