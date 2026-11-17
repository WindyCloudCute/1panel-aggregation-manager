using Microsoft.AspNetCore.Builder;

namespace PanelFleet.Web.Endpoints;

public static class EndpointRegistration
{
    public static IEndpointRouteBuilder MapPanelFleetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAuthEndpoints();
        app.MapNodeEndpoints();
        app.MapOverviewEndpoints();
        app.MapResourceEndpoints();
        app.MapOperationEndpoints();
        app.MapAppStoreEndpoints();
        app.MapContainerEndpoints();
        app.MapPanelUpdateEndpoints();
        app.MapWebsiteEndpoints();
        app.MapDatabaseEndpoints();
        app.MapCronjobEndpoints();
        app.MapLogEndpoints();
        app.MapSettingsEndpoints();
        return app;
    }
}
