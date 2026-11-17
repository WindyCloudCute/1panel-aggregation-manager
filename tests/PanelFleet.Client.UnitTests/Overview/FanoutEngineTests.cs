using Microsoft.Extensions.Logging.Abstractions;
using PanelFleet.Application.Abstractions;
using PanelFleet.Application.Overview;
using PanelFleet.Domain.Model;

namespace PanelFleet.Client.UnitTests.Overview;

public sealed class FanoutEngineTests
{
    private static FanoutEngine CreateEngine() => new(TimeProvider.System, NullLogger<FanoutEngine>.Instance);

    private static FanoutTarget<int> Target(NodeId id, Func<CancellationToken, Task<int>> call) =>
        new(id, id.ToString(), call);

    private static FanoutOptions Options(int concurrency = 3, int nodeTimeoutMs = 2000, int budgetMs = 5000) => new()
    {
        MaxConcurrency = concurrency,
        PerTargetTimeout = TimeSpan.FromMilliseconds(nodeTimeoutMs),
        Budget = TimeSpan.FromMilliseconds(budgetMs)
    };

    private static async Task<List<FanoutResult<int>>> CollectAsync(IAsyncEnumerable<FanoutResult<int>> source)
    {
        var results = new List<FanoutResult<int>>();
        await foreach (var result in source)
        {
            results.Add(result);
        }

        return results;
    }

    [Fact]
    public async Task MaxConcurrency_IsRespected_AndAllSucceed()
    {
        var sync = new object();
        var active = 0;
        var max = 0;
        var targets = Enumerable.Range(0, 9)
            .Select(index => Target(
                NodeId.New(),
                async token =>
                {
                    lock (sync)
                    {
                        active++;
                        max = Math.Max(max, active);
                    }

                    try
                    {
                        await Task.Delay(25, token);
                        return index;
                    }
                    finally
                    {
                        lock (sync)
                        {
                            active--;
                        }
                    }
                }))
            .ToArray();

        var results = await CollectAsync(CreateEngine().RunAsync(targets, Options(concurrency: 3), CancellationToken.None));

        Assert.Equal(9, results.Count);
        Assert.All(results, result => Assert.Equal(FanoutStatus.Success, result.Status));
        Assert.InRange(max, 1, 3);
    }

    [Fact]
    public async Task SlowTarget_TimesOut_WithoutAffectingFastTarget()
    {
        var fastId = NodeId.New();
        var slowId = NodeId.New();
        var targets = new[]
        {
            Target(fastId, async token =>
            {
                await Task.Delay(10, token);
                return 1;
            }),
            Target(slowId, async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return 2;
            })
        };

        var results = await CollectAsync(CreateEngine().RunAsync(targets, Options(concurrency: 2, nodeTimeoutMs: 80, budgetMs: 2000), CancellationToken.None));

        Assert.Equal(2, results.Count);
        Assert.Equal(FanoutStatus.Success, results.Single(result => result.NodeId == fastId).Status);
        var slow = results.Single(result => result.NodeId == slowId);
        Assert.Equal(FanoutStatus.TimedOut, slow.Status);
        Assert.Equal(PanelErrorKind.Timeout, slow.Error?.Kind);
    }

    [Fact]
    public async Task BudgetExhausted_EmitsOneResultPerTarget()
    {
        var targets = Enumerable.Range(0, 5)
            .Select(_ => Target(
                NodeId.New(),
                async token =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    return 1;
                }))
            .ToArray();

        var results = await CollectAsync(CreateEngine().RunAsync(targets, Options(concurrency: 1, nodeTimeoutMs: 1500, budgetMs: 80), CancellationToken.None));

        Assert.Equal(targets.Length, results.Count);
        Assert.Contains(results, result => result.Status == FanoutStatus.BudgetExceeded);
        Assert.All(results, result => Assert.NotEqual(FanoutStatus.Success, result.Status));
    }

    [Fact]
    public async Task Results_AreStreamedInCompletionOrder()
    {
        var fastId = NodeId.New();
        var slowId = NodeId.New();
        var targets = new[]
        {
            Target(fastId, async token =>
            {
                await Task.Delay(10, token);
                return 1;
            }),
            Target(slowId, async token =>
            {
                await Task.Delay(120, token);
                return 2;
            })
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = CreateEngine()
            .RunAsync(targets, Options(concurrency: 2), timeout.Token)
            .GetAsyncEnumerator(timeout.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(fastId, enumerator.Current.NodeId);
    }

    [Fact]
    public async Task PanelCallException_IsReportedAsFailedWithOriginalKind()
    {
        var targets = new[]
        {
            Target(NodeId.New(), _ => throw new PanelCallException(new PanelError(PanelErrorKind.Unauthorized, "bad key")))
        };

        var results = await CollectAsync(CreateEngine().RunAsync(targets, Options(), CancellationToken.None));

        var result = Assert.Single(results);
        Assert.Equal(FanoutStatus.Failed, result.Status);
        Assert.Equal(PanelErrorKind.Unauthorized, result.Error?.Kind);
    }

    [Fact]
    public async Task UnexpectedException_IsReportedAsFailedProtocol()
    {
        var targets = new[]
        {
            Target(NodeId.New(), _ => throw new InvalidOperationException("boom"))
        };

        var results = await CollectAsync(CreateEngine().RunAsync(targets, Options(), CancellationToken.None));

        var result = Assert.Single(results);
        Assert.Equal(FanoutStatus.Failed, result.Status);
        Assert.Equal(PanelErrorKind.Protocol, result.Error?.Kind);
    }

    [Fact]
    public async Task DuplicateNodeId_IsRejected()
    {
        var id = NodeId.New();
        var targets = new[]
        {
            Target(id, _ => Task.FromResult(1)),
            Target(id, _ => Task.FromResult(2))
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CollectAsync(CreateEngine().RunAsync(targets, Options(), CancellationToken.None)));
    }

    [Fact]
    public async Task EmptyTargets_CompleteImmediately()
    {
        var results = await CollectAsync(CreateEngine().RunAsync<int>([], Options(), CancellationToken.None));

        Assert.Empty(results);
    }

    [Fact]
    public async Task CallerCancellation_ThrowsOperationCanceled()
    {
        var targets = Enumerable.Range(0, 4)
            .Select(_ => Target(NodeId.New(), async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                return 1;
            }))
            .ToArray();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CollectAsync(CreateEngine().RunAsync(targets, Options(nodeTimeoutMs: 4000, budgetMs: 4000), cts.Token)));
    }
}
