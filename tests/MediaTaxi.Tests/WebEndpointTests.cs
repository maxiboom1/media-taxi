using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MediaTaxi.Domain;
using MediaTaxi.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaTaxi.Tests;

public sealed class WebEndpointTests
{
    [Fact]
    public async Task ReadOnlyEndpointsExposeCamelCaseViewsAndCorrectReadiness()
    {
        await using var host = await TestWebHost.StartAsync();
        var jobsResponse = await host.Client.GetAsync("/api/jobs?view=older");
        Assert.Equal(HttpStatusCode.OK, jobsResponse.StatusCode);
        Assert.Equal("no-store", jobsResponse.Headers.CacheControl?.ToString());
        using var jobs = JsonDocument.Parse(await jobsResponse.Content.ReadAsStringAsync());
        Assert.Equal(7, jobs.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(1, jobs.RootElement.GetProperty("olderCount").GetInt32());
        Assert.Equal("older", host.View.LastView);
        Assert.False(jobs.RootElement.TryGetProperty("Revision", out _));
        Assert.DoesNotContain("durationStatus", jobs.RootElement.GetRawText());
        Assert.DoesNotContain("Unconfirmed", jobs.RootElement.GetRawText());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/jobs?view=invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Client.GetAsync("/health/ready")).StatusCode);
        host.View.Ready = true;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.Client.PostAsync("/api/jobs", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/resync")).StatusCode);

        using var statistics = await host.Client.GetFromJsonAsync<JsonDocument>("/api/statistics");
        Assert.Equal(3, statistics!.RootElement.GetProperty("imported").GetInt32());
        using var system = await host.Client.GetFromJsonAsync<JsonDocument>("/api/system");
        Assert.Equal("0.2.0", system!.RootElement.GetProperty("version").GetString());
        Assert.Equal(2, system.RootElement.GetProperty("stages").GetProperty("Wrapping").GetInt32());
        Assert.Equal(1, system.RootElement.GetProperty("stages").GetProperty("Proxy").GetInt32());
    }

    [Fact]
    public async Task PagesAndStaticAssetsComeOnlyFromWebRoot()
    {
        await using var host = await TestWebHost.StartAsync();
        Assert.Equal("activity", await host.Client.GetStringAsync("/"));
        Assert.Equal("system", await host.Client.GetStringAsync("/admin"));
        Assert.Equal("body{}", await host.Client.GetStringAsync("/styles.css"));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/media/private.clip")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/proxies/private.mp4")).StatusCode);
    }

    [Fact]
    public async Task ServerSentEventsDeliverInitialRevisionAndLaterChanges()
    {
        await using var host = await TestWebHost.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await host.Client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("event: change", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("data: {\"revision\":7}", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("", await reader.ReadLineAsync(timeout.Token));
        host.View.Change();
        Assert.Equal("event: change", await reader.ReadLineAsync(timeout.Token));
        Assert.Equal("data: {\"revision\":8}", await reader.ReadLineAsync(timeout.Token));
    }

    private sealed class TestWebHost(WebApplication app, HttpClient client, FakeView view, string root) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;
        public FakeView View { get; } = view;

        public static async Task<TestWebHost> StartAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "media-taxi-web-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "activity");
            await File.WriteAllTextAsync(Path.Combine(root, "system.html"), "system");
            await File.WriteAllTextAsync(Path.Combine(root, "styles.css"), "body{}");
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { WebRootPath = root });
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var view = new FakeView();
            DashboardEndpoints.Map(app, view);
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new TestWebHost(app, new HttpClient { BaseAddress = new Uri(address) }, view, root);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeView : IApplicationView
    {
        private TaskCompletionSource change = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Revision { get; private set; } = 7;
        public bool Ready { get; set; }
        public string? LastView { get; private set; }
        public bool IsReady => Ready;

        public void Change()
        {
            var previous = change;
            change = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Revision++;
            previous.TrySetResult();
        }

        public JobsSnapshot GetJobs(string view)
        {
            LastView = view;
            return new(Revision, "Asia/Jerusalem", 0, 1, []);
        }
        public SystemSnapshot GetSystem() => new("0.2.0", 60, Ready, null, [], new(true, "ffmpeg test", "ffprobe test", ["libx264"], null), [], new Dictionary<string, int>(), 0, [], [], new Dictionary<string, int> { ["Wrapping"] = 2, ["Proxy"] = 1 });
        public StatisticsSnapshot GetStatistics() => new(5, 4, 1, 3, 2, 1, 0, 1, 5);
        public Task WaitForChangeAsync(long since, CancellationToken cancellationToken) => since != Revision ? Task.CompletedTask : change.Task.WaitAsync(cancellationToken);
    }
}
