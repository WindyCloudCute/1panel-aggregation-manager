using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Operations;
using PanelFleet.Domain.Model;

namespace PanelFleet.IntegrationTests;

/// <summary>写操作内核 HTTP 端点集成测试（用测试处理器，不访问真实 1Panel 写接口）。</summary>
public sealed class OperationApiTests
{
    [Fact]
    public async Task Preview_WithoutAuthentication_Returns401()
    {
        using var factory = new PanelFleetWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/operations/preview",
            new { kind = "test.ping", nodeIds = new[] { Guid.NewGuid() } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PreviewAndExecute_LowRisk_WorksAndAudits()
    {
        using var customized = CreateFactoryWithHandlers();
        using var client = customized.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "op-node");

        using var previewDocument = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "test.ping", nodeIds = new[] { nodeId } },
            token,
            cookie,
            HttpStatusCode.OK);

        var tokenId = previewDocument.RootElement.GetProperty("tokenId").GetGuid();
        Assert.False(previewDocument.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal(1, previewDocument.RootElement.GetProperty("targets").GetArrayLength());

        using var executionDocument = await PostJsonAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = (string?)null },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.Equal("Succeeded", executionDocument.RootElement.GetProperty("status").GetString());
        var steps = executionDocument.RootElement.GetProperty("steps");
        Assert.Equal(1, steps.GetArrayLength());
        Assert.True(steps[0].GetProperty("success").GetBoolean());

        var recent = await client.GetAsync("/api/operations/");
        Assert.Equal(HttpStatusCode.OK, recent.StatusCode);
        using var recentDocument = JsonDocument.Parse(await recent.Content.ReadAsStringAsync());
        Assert.Contains(
            recentDocument.RootElement.EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "test.ping");

        using var scope = customized.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditStore>();
        var entries = await audit.ListAsync(50, "operation.executed", null, default);
        Assert.Contains(entries, entry => entry.NodeId == new NodeId(nodeId));
    }

    [Fact]
    public async Task Execute_HighRisk_WrongTypedConfirmation_Returns409()
    {
        using var customized = CreateFactoryWithHandlers();
        using var client = customized.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "danger-node");

        using var previewDocument = await PostJsonAsync(
            client,
            "/api/operations/preview",
            new { kind = "test.danger", nodeIds = new[] { nodeId } },
            token,
            cookie,
            HttpStatusCode.OK);

        Assert.True(previewDocument.RootElement.GetProperty("requiresTypedConfirmation").GetBoolean());
        Assert.Equal("danger", previewDocument.RootElement.GetProperty("confirmText").GetString());
        var tokenId = previewDocument.RootElement.GetProperty("tokenId").GetGuid();

        var response = await PostAsync(
            client,
            $"/api/operations/{tokenId:D}/execute",
            new { typedConfirmation = "wrong" },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Execute_WithoutCsrf_Returns400()
    {
        using var customized = CreateFactoryWithHandlers();
        using var client = customized.CreateAdminClient();

        var response = await client.PostAsJsonAsync(
            $"/api/operations/{Guid.NewGuid():D}/execute",
            new { typedConfirmation = (string?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_UnknownKind_Returns400()
    {
        using var customized = CreateFactoryWithHandlers();
        using var client = customized.CreateAdminClient();
        var (token, cookie) = await GetCsrfAsync(client);
        var nodeId = await CreateNodeAsync(client, token, cookie, "unknown-node");

        var response = await PostAsync(
            client,
            "/api/operations/preview",
            new { kind = "unknown.kind", nodeIds = new[] { nodeId } },
            token,
            cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static PanelFleetWebApplicationFactory CreateFactoryWithHandlers()
    {
        var factory = new PanelFleetWebApplicationFactory();
        factory.AdditionalServices = services =>
        {
            services.AddSingleton<IOperationHandler>(new IntegrationTestOperationHandler("test.ping", OperationRisk.Low));
            services.AddSingleton<IOperationHandler>(
                new IntegrationTestOperationHandler("test.danger", OperationRisk.High, "danger"));
        };

        return factory;
    }

    private static async Task<Guid> CreateNodeAsync(
        HttpClient client,
        string token,
        string cookie,
        string name)
    {
        var request = CreateJsonRequest(
            HttpMethod.Post,
            "/api/nodes",
            new
            {
                name,
                baseUrl = $"https://{name}.example.test",
                apiKey = "test-api-key-1234",
                authMode = "Auto",
                tlsPolicy = "Strict",
                isEnabled = true,
                tags = Array.Empty<string>()
            },
            token,
            cookie);

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<JsonDocument> PostJsonAsync(
        HttpClient client,
        string url,
        object body,
        string token,
        string cookie,
        HttpStatusCode expected)
    {
        var response = await PostAsync(client, url, body, token, cookie);
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        object body,
        string token,
        string cookie) =>
        await client.SendAsync(CreateJsonRequest(HttpMethod.Post, url, body, token, cookie));

    private static HttpRequestMessage CreateJsonRequest(
        HttpMethod method,
        string url,
        object? body,
        string token,
        string cookie)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", token);
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/antiforgery/token");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = document.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("缺少防伪令牌。");

        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith("PanelFleet.Antiforgery=", StringComparison.Ordinal))
            : null;

        var cookie = setCookie?.Split(';')[0]
            ?? throw new InvalidOperationException("缺少防伪 Cookie。");

        return (token, cookie);
    }

    private sealed class IntegrationTestOperationHandler : IOperationHandler
    {
        private readonly OperationRisk _risk;
        private readonly string? _typedConfirmationText;

        public IntegrationTestOperationHandler(string kind, OperationRisk risk, string? typedConfirmationText = null)
        {
            Kind = kind;
            _risk = risk;
            _typedConfirmationText = typedConfirmationText;
        }

        public string Kind { get; }

        public Task<OperationPlan> PlanAsync(OperationPlanContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationPlan(
                "集成测试操作",
                "由集成测试处理器执行。",
                _risk,
                context.Nodes.Select(node => new OperationTarget(node.Id, node.Name)).ToArray(),
                [],
                _typedConfirmationText,
                "test"));

        public Task<OperationStepResult> ExecuteAsync(OperationNodeContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new OperationStepResult(
                context.Node.Id,
                context.Node.Name,
                true,
                "集成测试执行成功",
                null,
                null,
                null,
                TimeSpan.Zero,
                "target-1"));
    }
}
