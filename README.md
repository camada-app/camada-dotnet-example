# camada-dotnet-example

An ASP.NET Core minimal API wired with [`Camada`](../camada-dotnet) against a locally running
edge-analyst. This is the hand-test bench for the .NET SDK, the twin of
[`camada-python-example`](../camada-python-example).

## Setup

1. Terminal A — `cd ../camada/edge-analyst && npm run dev` (analyst on :8787), then
   `npm run seed` in a second terminal. Note the printed `CAMADA_KEY`.
2. Here: `cp .env.example .env` (paste the key if it differs), then `dotnet run` →
   http://localhost:3007 (`Properties/launchSettings.json` sets the port; a built DLL takes it from
   `ASPNETCORE_URLS`). The SDK is a `ProjectReference` to the sibling checkout `../camada-dotnet`,
   so it builds with the app.

ASP.NET Core's forwarded-headers middleware is off by default, so the peer camada sees is the
socket's and `X-Forwarded-For` is judged by camada's own trusted-proxy rules
(`CAMADA_TRUSTED_PROXY`, or the tenant config). If `UseForwardedHeaders()` is ever added, keep
`UseCamada()` before it — a rewritten peer would let any caller pick the address camada judges.

## Hand test (what to look for)

1. Browse `http://localhost:3007` — the page renders; devtools → Network shows
   `/_cam/b.js?r=<uuid>` (200, JavaScript) and ~2 s later `POST /_cam/fp` (204). The document
   response carries an `x-rid` header and sets the `_sfp` cookie.
2. Click/type before the beacon fires — the `/_cam/fp` payload's `input` counters are non-zero.
3. `curl -i http://localhost:3007/ -H 'X-Forwarded-For: 203.0.113.66'` → **403** with
   `x-block-reason: rule` and `x-block-rule: builtin:block` (the seed blocks that IP as an entry
   of the built-in block list on snapshot v5). The very first request after boot answers **200**
   instead — see below.
4. Fail three logins (`demo@example.com` / anything wrong) via `/login-form`, then run an
   analysis (`curl -s -X POST -H 'authorization: Bearer dev' 'http://localhost:8787/admin/run?tenant=acme&minutes=10'`) —
   events include `login_failed` rows with a hashed `uid`; your raw email appears nowhere.
5. Kill the analyst (Ctrl-C in terminal A) and reload the page + `curl localhost:3007/api/data` —
   everything still answers 200; the app's console shows at most one
   `[camada] suppressed error` line a minute (through `ILogger`, category `camada`). That is fail-open.
6. Restart this app with `CAMADA_DISABLED=1` — no `x-rid` header, no `/_cam/b.js` requests:
   the kill switch bypasses the SDK entirely.

## The first request after boot is cold

`AddCamada()` registers one engine that the container builds when the middleware first resolves
it, on the first request. That build starts the snapshot poll on a background task and never
blocks, so the request that triggered it is matched against an empty snapshot and falls open: a
`curl` from the blocked IP right after boot gets `200` with an `x-rid`, and requests keep passing
until that first poll lands — a few hundred milliseconds against a local analyst (snapshot-size
and network bound), then `403`. `node scripts/e2e-sdk-dotnet.mjs` in `../camada/edge-analyst`
makes that first request itself and asserts both answers.

If request 1 must be enforced, warm the engine at startup by waiting for the boot poll — a
bounded loop, so an unreachable analyst leaves it cold and the app still starts and fails open:

```csharp
var engine = app.Services.GetRequiredService<CamadaEngine>();   // builds it; the boot poll is already running
if (engine.Snap != null)                                       // null when CAMADA_KEY is unset or CAMADA_DISABLED=1
{
    var deadline = Environment.TickCount64 + 5000;
    while (engine.Snap.Verdict(new MatchInput { Ip = "0.0.0.0" }).Reason == "cold" && Environment.TickCount64 < deadline)
    {
        Thread.Sleep(10);
    }
}
```

`engine.Snap.Refresh()` is not the warm-up: it waits behind the boot poll already in flight and
then polls once more, on the network, before the app can start.

## Challenge (SDK-04)

`/challenge-me` forces the first-party proof-of-work challenge, whatever the snapshot says — the
same page camada serves automatically for a `challenge` verdict.

1. Browse `http://localhost:3007/challenge-me` — "Checking your browser" appears, the inline
   solver hunts a SHA-256 with 16 leading zero bits (tens of milliseconds), the hidden form
   posts to `/__camada/challenge`, and the browser lands on "Challenge passed". Devtools shows
   the `_cch` cookie (`HttpOnly`, `SameSite=Lax`, one hour); reload and the page renders at once.
2. `curl -i http://localhost:3007/challenge-me -H 'accept: text/html' -H 'sec-fetch-dest: document'`
   → **403** with `x-camada-challenge: 1` and the page in the body.
3. A request without an HTML `Accept` (an API client, an image, a fetch) cannot solve a challenge,
   so it passes to the app signalled: the route renders (**200**) with `x-camada-challenge: 1` and
   `cache-control: no-store`, and the app or client decides what to do.
4. The events tell the three apart: a served challenge ships `st: 403, blk: "challenge"`, a passed
   one ships `st: 200, ch: 1`, and a signalled one (step 3) ships `st: 200, chs: 1`.

The nonce and the cookie are bound to the client IP, so camada serves no challenge to a request
it cannot identify (no trusted-proxy config and no socket address). Clear `_cch` — or open a
private window — to see the check again.

## Tests

`dotnet build -warnaserror && dotnet test tests/CamadaExample.Tests` — the routes through
`WebApplicationFactory` against an engine whose analyst URL is a closed port (cold, fail open; the
challenge needs no snapshot), the kill switch, plus a guard that the sibling `camada-dotnet`
checkout is the SDK that was built.
