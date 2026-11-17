using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PanelFleet.Application.Cronjobs;
using PanelFleet.Web.Auth;

namespace PanelFleet.Web.Endpoints;

/// <summary>P9 计划任务只读查询端点（Admin only）。写操作统一走 /api/operations 内核。</summary>
public static class CronjobEndpoints
{
    public static IEndpointRouteBuilder MapCronjobEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var cronjobs = app.MapGroup("/api/cronjobs").RequireAuthorization(AuthSetup.AdminPolicy);

        cronjobs.MapGet(
            "/",
            async (
                Guid? nodeId,
                string? groupIds,
                string? info,
                string? status,
                string? orderBy,
                string? order,
                int? page,
                int? pageSize,
                ICronjobService service,
                CancellationToken cancellationToken) =>
            {
                var query = new CronjobQuery(
                    nodeId,
                    ParseIds(groupIds),
                    info,
                    status,
                    orderBy ?? "name",
                    order ?? "null",
                    page ?? 1,
                    pageSize ?? 50);

                return await ExecuteAsync(async () =>
                    Results.Ok(await service.ListAsync(query, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        cronjobs.MapGet(
            "/groups",
            async (Guid? nodeId, ICronjobService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                    Results.Ok(await service.ListGroupsAsync(nodeId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        cronjobs.MapGet(
            "/spec/next",
            async (Guid? nodeId, string? spec, ICronjobService service, CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(spec))
                {
                    return Results.BadRequest(new { message = "缺少周期表达式（spec）。" });
                }

                return await ExecuteAsync(async () =>
                    Results.Ok(await service.PreviewSpecAsync(nodeId, spec, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        cronjobs.MapGet(
            "/{nodeId:guid}/{id:long}",
            async (Guid nodeId, long id, ICronjobService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                    Results.Ok(await service.GetDetailAsync(nodeId, id, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        cronjobs.MapGet(
            "/{nodeId:guid}/{id:long}/records",
            async (
                Guid nodeId,
                long id,
                string? start,
                string? end,
                string? status,
                int? page,
                int? pageSize,
                ICronjobService service,
                CancellationToken cancellationToken) =>
            {
                if (!TryParseTime(start, out var startTime, out var startError))
                {
                    return Results.BadRequest(new { message = startError });
                }

                if (!TryParseTime(end, out var endTime, out var endError))
                {
                    return Results.BadRequest(new { message = endError });
                }

                return await ExecuteAsync(async () =>
                    Results.Ok(await service
                        .GetRecordsAsync(
                            nodeId,
                            id,
                            startTime,
                            endTime,
                            status,
                            page ?? 1,
                            pageSize ?? 20,
                            cancellationToken)
                        .ConfigureAwait(false)))
                    .ConfigureAwait(false);
            });

        cronjobs.MapGet(
            "/{nodeId:guid}/records/{recordId:long}/log",
            async (Guid nodeId, long recordId, ICronjobService service, CancellationToken cancellationToken) =>
                await ExecuteAsync(async () =>
                    Results.Ok(await service.GetRecordLogAsync(nodeId, recordId, cancellationToken).ConfigureAwait(false)))
                    .ConfigureAwait(false));

        return app;
    }

    private static IReadOnlyList<long> ParseIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var ids = new List<long>();
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                ids.Add(id);
            }
        }

        return ids.Distinct().ToArray();
    }

    private static bool TryParseTime(string? value, out DateTimeOffset? time, out string? error)
    {
        time = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            error = $"时间格式不合法（需要 ISO8601，如 2026-11-17T00:00:00Z）：{value}";
            return false;
        }

        time = parsed;
        return true;
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CronjobNodeNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
        catch (CronjobNotFoundException exception)
        {
            return Results.NotFound(new { message = exception.Message });
        }
    }
}
