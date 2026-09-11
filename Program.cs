// camada-dotnet-example: a small ASP.NET Core minimal API wired with camada against a local
// edge-analyst. Setup: cp .env.example .env (paste the CAMADA_KEY printed by `npm run seed`),
// then `dotnet run` (http://localhost:3007 via Properties/launchSettings.json) or
// ASPNETCORE_URLS=http://127.0.0.1:3007 dotnet bin/Release/net8.0/CamadaExample.dll after
// `dotnet build -c Release`. ForwardedHeaders is off by default in ASP.NET Core, so the peer camada
// sees is the socket's; X-Forwarded-For is judged by camada's own trusted-proxy rules
// (CAMADA_TRUSTED_PROXY, or the tenant config). If UseForwardedHeaders() is ever added, keep
// UseCamada() before it.
using System.Text.RegularExpressions;
using Camada.AspNetCore;
using Microsoft.AspNetCore.WebUtilities;

// minimal .env loader so the example has zero extra dependencies (fills unset variables only)
try
{
    foreach (var line in File.ReadLines(".env"))
    {
        var m = Regex.Match(line.Trim(), "^([A-Z_]+)=(.*)$");
        if (m.Success && Environment.GetEnvironmentVariable(m.Groups[1].Value) == null)
        {
            Environment.SetEnvironmentVariable(m.Groups[1].Value, m.Groups[2].Value);
        }
    }
}
catch (IOException)
{
    // no .env: rely on the environment
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCamada();   // <- the two-line install; the engine builds itself on the first request

var app = builder.Build();
app.UseCamada();   // FIRST in the pipeline: camada answers before routing

static IResult Page(HttpContext ctx, string title, string body, int status = 200) => Results.Content($"""
    <!doctype html>
    <html><head><meta charset="utf-8"><title>{title}</title>{ctx.CamadaScriptTag()}</head>
    <body style="font-family: system-ui; max-width: 40rem; margin: 3rem auto">
    <nav><a href="/">home</a> · <a href="/pricing">pricing</a> · <a href="/login-form">login</a> · <a href="/challenge-me">challenge</a></nav>
    <h1>{title}</h1>{body}</body></html>
    """, "text/html", statusCode: status);

app.MapGet("/", (HttpContext ctx) => Page(ctx, "camada example shop", """

      <p>Every request here is captured by camada; the beacon below fingerprints this browser first-party.</p>
      <p><button onclick="fetch('/api/data').then(r=>r.json()).then(d=>alert(JSON.stringify(d)))">call the API</button></p>
    """));

app.MapGet("/pricing", (HttpContext ctx) => Page(ctx, "Pricing", "<p>Free while unreleased.</p>"));

app.MapGet("/login-form", (HttpContext ctx) => Page(ctx, "Log in", """

      <form method="post" action="/login">
        <input name="user" placeholder="email"> <input name="pass" type="password"> <button>go</button>
      </form>
    """));

app.MapPost("/login", async (HttpContext ctx) =>
{
    // parse the urlencoded form by hand: a body that is not UTF-8 is a failed login, not a 500
    using var reader = new StreamReader(ctx.Request.Body);
    var form = QueryHelpers.ParseQuery(await reader.ReadToEndAsync());
    var user = form.TryGetValue("user", out var u) ? u.ToString() : "";
    var ok = user == "demo@example.com" && form.TryGetValue("pass", out var p) && p.ToString() == "demo";
    ctx.CamadaTrack(ok ? "login_succeeded" : "login_failed", user);   // uid is HMAC-hashed in the SDK
    return Page(ctx, ok ? "Welcome" : "Nope", $"<p>login {(ok ? "succeeded" : "failed")}</p>", ok ? 200 : 401);
});

app.MapGet("/api/data", () => Results.Json(new { ok = true, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));

// SDK-04 demo: force the challenge for this route, whatever the snapshot says. In production
// the same page is served automatically for a `challenge` verdict. Once solved, the `_cch`
// cookie is good for an hour and this route renders normally.
app.MapGet("/challenge-me", (HttpContext ctx) => ctx.CamadaServeChallenge() ?? Page(ctx, "Challenge passed", """

        <p>The <code>_cch</code> cookie is set for an hour. Clear it (or open a private window) to see the check again.</p>
    """));

app.MapFallback((HttpContext ctx) => Page(ctx, "404", "<p>Nothing here.</p>", 404));

app.Run();

public partial class Program
{
}
