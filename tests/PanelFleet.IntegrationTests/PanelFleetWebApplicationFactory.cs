using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Client;

namespace PanelFleet.IntegrationTests;

public sealed class PanelFleetWebApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly (string Key, string Value)[] EnvironmentOverrides =
    [
        ("Oidc__Authority", "https://oidc.example.test"),
        ("Oidc__ClientId", "test-client"),
        ("Oidc__ClientSecret", "test-secret"),
        ("Oidc__CallbackPath", "/sso/OIDC/Callback"),
        ("Oidc__RequireHttpsMetadata", "true"),
        ("Health__IntervalSeconds", "3600"),
        ("Health__MaxConcurrency", "1"),
        ("Health__TimeoutSeconds", "5"),
        ("Overview__RefreshIntervalSeconds", "0"),
        ("Overview__NodeTimeoutSeconds", "3"),
        ("Overview__BudgetSeconds", "8"),
        ("Resources__RefreshIntervalSeconds", "0"),
        ("Resources__NodeTimeoutSeconds", "4"),
        ("Resources__BudgetSeconds", "8"),
        ("Logging__LogLevel__Default", "Warning")
    ];

    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

    public PanelFleetWebApplicationFactory()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "panelfleet-it-" + Guid.NewGuid().ToString("N"));

        SetEnvironmentVariable("PanelFleet__DataDirectory", DataDirectory);
        SetEnvironmentVariable("PanelFleet__ForwardedHeaders__Enabled", "false");
        SetEnvironmentVariable("PanelFleet__Listen__Url", string.Empty);

        foreach (var (key, value) in EnvironmentOverrides)
        {
            SetEnvironmentVariable(key, value);
        }
    }

    public string DataDirectory { get; }

    /// <summary>各测试可追加自己的服务注册（在 Program 默认注册之后执行，可覆盖/追加）。须在首次创建客户端前设置。</summary>
    public Action<IServiceCollection>? AdditionalServices { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConnectionTester>();
            services.AddSingleton<IConnectionTester, FakeConnectionTester>();

            services.AddSingleton<FakePanelClientFactory>();
            services.RemoveAll<IPanelClientFactory>();
            services.AddSingleton<IPanelClientFactory>(serviceProvider =>
                serviceProvider.GetRequiredService<FakePanelClientFactory>());

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultSignInScheme = TestAuthHandler.SchemeName;
                    options.DefaultSignOutScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName,
                    _ => { });

            AdditionalServices?.Invoke(services);
        });
    }

    public FakePanelClientFactory Panels => Services.GetRequiredService<FakePanelClientFactory>();

    public HttpClient CreateAdminClient() => CreateAuthedClient("admin");

    public HttpClient CreateAuthedClient(string user)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", user);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var (key, value) in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            if (Directory.Exists(DataDirectory))
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 文件句柄释放可能滞后，忽略清理失败。
        }
    }

    private void SetEnvironmentVariable(string key, string? value)
    {
        _previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }
}
