// The example against a configured engine that can never load a snapshot: the analyst URL is a
// closed port, so every request runs cold and falls open, and the routes the app gates itself
// (challenge) still work because the challenge kit needs no snapshot. One test class, run
// serially: the engine reads the process environment, which the fixture sets per test.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Camada;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CamadaExample.Tests;

public sealed class Bench : IDisposable
{
    public const string BlockedIp = "203.0.113.66";
    public static readonly (string, string)[] Browser = { ("x-forwarded-for", "198.51.100.7"), ("accept", "text/html"), ("sec-fetch-dest", "document") };

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; }
    public string Dead { get; }

    public Bench()
    {
        Dead = $"http://127.0.0.1:{ClosedPort()}";
        Environment.SetEnvironmentVariable("CAMADA_KEY", "tok-example.snap-example");
        Environment.SetEnvironmentVariable("CAMADA_INGEST_URL", Dead);
        Environment.SetEnvironmentVariable("CAMADA_SNAPSHOT_URL", Dead + "/snapshot");
        Environment.SetEnvironmentVariable("CAMADA_TRUSTED_PROXY", "hops:1");
        Environment.SetEnvironmentVariable("CAMADA_DISABLED", null);
        // the .env loader reads the working directory: the test binary's, never a developer's checkout
        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public CamadaEngine Engine => Factory.Services.GetRequiredService<CamadaEngine>();

    public async Task<HttpResponseMessage> Get(string path, params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (k, v) in headers)
        {
            req.Headers.TryAddWithoutValidation(k, v);
        }
        return await Client.SendAsync(req);
    }

    public static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var vs) ? string.Join(", ", vs) : r.Content.Headers.TryGetValues(name, out var cs) ? string.Join(", ", cs) : null;

    private static int ClosedPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();   // stops the host: ApplicationStopping drains the queue against the dead port and gives up
    }
}

public class ServerTests
{
    private static readonly Regex Uuid = new("^[0-9a-f-]{36}$");

    [Fact]
    public async Task PagesRenderWithTheFirstPartyBeacon()
    {
        using var b = new Bench();
        var first = await b.Get("/");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.StartsWith("_sfp=", Bench.Header(first, "set-cookie"));
        foreach (var path in new[] { "/", "/pricing", "/login-form" })
        {
            var r = await b.Get(path);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var rid = Bench.Header(r, "x-rid");
            Assert.Matches(Uuid, rid!);
            Assert.Contains($"/_cam/b.js?r={rid}", await r.Content.ReadAsStringAsync());
        }
        var js = await b.Get("/_cam/b.js");
        Assert.Equal("application/javascript", Bench.Header(js, "content-type"));
    }

    [Fact]
    public async Task ApiAnswersJson()
    {
        using var b = new Bench();
        var r = await b.Get("/api/data");
        Assert.True(JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task LoginReportsTheOutcome()
    {
        using var b = new Bench();
        var bad = await b.Client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "demo@example.com", ["pass"] = "nope" }));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Contains("login failed", await bad.Content.ReadAsStringAsync());
        var ok = await b.Client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "demo@example.com", ["pass"] = "demo" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("login succeeded", await ok.Content.ReadAsStringAsync());
        // a body that is not UTF-8 is a failed login, not a 500
        var raw = new ByteArrayContent(new byte[] { (byte)'u', (byte)'s', (byte)'e', (byte)'r', (byte)'=', 0xff, (byte)'&', (byte)'p', (byte)'a', (byte)'s', (byte)'s', (byte)'=', (byte)'x' });
        raw.Headers.ContentType = new("application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.Client.PostAsync("/login", raw)).StatusCode);
    }

    [Fact]
    public async Task EachTestBuildsItsOwnEngine()
    {
        // the factory's container builds an engine from this test's env, the lazy first-request build as in production
        using var b = new Bench();
        await b.Get("/");
        var engine = b.Engine;
        Assert.NotNull(engine.Env);
        Assert.Equal(b.Dead + "/snapshot", engine.Env!.SnapshotUrl);
        using var other = new Bench();
        await other.Get("/");
        Assert.NotSame(engine, other.Engine);
    }

    [Fact]
    public async Task AColdSnapshotFallsOpen()
    {
        using var b = new Bench();
        var r = await b.Get("/", ("x-forwarded-for", Bench.BlockedIp));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(Bench.Header(r, "x-block-reason"));
        Assert.NotNull(Bench.Header(r, "x-rid"));
    }

    [Fact]
    public async Task ChallengeMeServesThePageToABrowser()
    {
        using var b = new Bench();
        var r = await b.Get("/challenge-me", Bench.Browser);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("1", Bench.Header(r, "x-camada-challenge"));
        Assert.StartsWith("text/html", Bench.Header(r, "content-type"));
        Assert.Contains("/__camada/challenge", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ChallengeMeSignalsAnApiClient()
    {
        using var b = new Bench();
        var r = await b.Get("/challenge-me", ("x-forwarded-for", "198.51.100.7"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("1", Bench.Header(r, "x-camada-challenge"));
        Assert.Equal("no-store", Bench.Header(r, "cache-control"));
    }

    [Fact]
    public async Task ChallengeMeRendersOnceTheBrowserHoldsACch()
    {
        using var b = new Bench();
        await b.Get("/");   // builds the engine
        var cch = b.Engine.Kit!.Issue("198.51.100.7", b.Engine.NowMs());
        var r = await b.Get("/challenge-me", Bench.Browser.Append(("cookie", $"_cch={cch}")).ToArray());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Challenge passed", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownPathRendersThe404Page()
    {
        using var b = new Bench();
        var r = await b.Get("/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("Nothing here", await r.Content.ReadAsStringAsync());
        Assert.NotNull(Bench.Header(r, "x-rid"));
    }

    [Fact]
    public async Task TheKillSwitchBypassesTheSdk()
    {
        using var b = new Bench();
        Assert.NotNull(Bench.Header(await b.Get("/"), "x-rid"));   // the engine is built and active before the switch flips
        Environment.SetEnvironmentVariable("CAMADA_DISABLED", "1");   // read per request: no restart needed
        try
        {
            var r = await b.Get("/");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Null(Bench.Header(r, "x-rid"));
            Assert.Equal(HttpStatusCode.NotFound, (await b.Get("/_cam/b.js")).StatusCode);   // no beacon endpoint: the app's own 404
        }
        finally
        {
            Environment.SetEnvironmentVariable("CAMADA_DISABLED", null);
        }
    }

    [Fact]
    public void TheSiblingSdkIsTheOneReferenced()
    {
        // a ProjectReference is always current, unlike a lockfile; what can drift is the checkout itself
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CamadaExample.csproj")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var csproj = Path.Combine(dir!.Parent!.FullName, "camada-dotnet", "src", "Camada", "Camada.csproj");
        Assert.True(File.Exists(csproj), $"no camada-dotnet checkout beside this repo ({csproj})");
        var shipped = Regex.Match(File.ReadAllText(csproj), "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(CamadaVersion.Value, shipped);   // the built SDK is the checkout's
        Assert.Contains("../camada-dotnet/src/Camada/Camada.csproj", File.ReadAllText(Path.Combine(dir.FullName, "CamadaExample.csproj")));
    }
}
