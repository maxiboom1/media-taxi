using System.Text.Json;
using MediaTaxi.Domain;

namespace MediaTaxi.Web;

/// <summary>Read-only HTTP surface; configured media folders are never web roots.</summary>
public static class DashboardEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app, IApplicationView view)
    {
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
        });

        app.MapGet("/", context => SendPageAsync(app, context, "index.html"));
        app.MapGet("/admin", context => SendPageAsync(app, context, "system.html"));
        app.MapGet("/api/jobs", (HttpContext context) =>
        {
            NoCache(context);
            string requested = context.Request.Query["view"].ToString();
            if (string.IsNullOrEmpty(requested)) requested = "active";
            if (requested is not ("active" or "older"))
                return Results.BadRequest(new { error = "view must be active or older" });
            return Results.Json(view.GetJobs(requested), JsonOptions);
        });
        app.MapGet("/api/system", (HttpContext context) =>
        {
            NoCache(context);
            return Results.Json(view.GetSystem(), JsonOptions);
        });
        app.MapGet("/api/statistics", (HttpContext context) =>
        {
            NoCache(context);
            return Results.Json(view.GetStatistics(), JsonOptions);
        });
        app.MapGet("/api/events", context => StreamEventsAsync(context, view));
        app.MapGet("/health/live", () => Results.Json(new { status = "live" }, JsonOptions));
        app.MapGet("/health/ready", () => Results.Json(
            new { status = view.IsReady ? "ready" : "not-ready" }, JsonOptions,
            statusCode: view.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));
    }

    private static void NoCache(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private static async Task SendPageAsync(WebApplication app, HttpContext context, string filename)
    {
        var file = app.Environment.WebRootFileProvider.GetFileInfo(filename);
        if (!file.Exists)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await using var source = file.CreateReadStream();
        await source.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task StreamEventsAsync(HttpContext context, IApplicationView view)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var aborted = context.RequestAborted;
        try
        {
            long revision = view.Revision;
            await SendRevisionAsync(context, revision, aborted);
            while (!aborted.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(aborted);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try { await view.WaitForChangeAsync(revision, heartbeat.Token); }
                catch (OperationCanceledException) when (!aborted.IsCancellationRequested) { }

                long current = view.Revision;
                if (current != revision)
                {
                    revision = current;
                    await SendRevisionAsync(context, revision, aborted);
                }
                else
                {
                    await context.Response.WriteAsync(": heartbeat\n\n", aborted);
                    await context.Response.Body.FlushAsync(aborted);
                }
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested) { }
        catch (IOException) { /* The browser disconnected while an event was being written. */ }
    }

    private static async Task SendRevisionAsync(HttpContext context, long revision, CancellationToken token)
    {
        await context.Response.WriteAsync($"event: change\ndata: {{\"revision\":{revision}}}\n\n", token);
        await context.Response.Body.FlushAsync(token);
    }
}
