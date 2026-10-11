// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

/// <summary>Scripted HTTP: routes by "METHOD path" and records every request (with its headers and body).</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public sealed record Seen(string Method, string Path, string? Auth, string? Session, string? Apikey, string Body);
    public readonly List<Seen> Requests = new();
    public readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> Routes = new();

    public FakeServer On(string route, HttpStatusCode status, string json)
    {
        if (!Routes.TryGetValue(route, out var q)) Routes[route] = q = new();
        q.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        return this;
    }

    public FakeServer OnThrow(string route)
    {
        if (!Routes.TryGetValue(route, out var q)) Routes[route] = q = new();
        q.Enqueue(() => throw new HttpRequestException("network down"));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        string path = req.RequestUri!.AbsolutePath.TrimStart('/');
        string body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
        req.Headers.TryGetValues("x-aqc-session", out var sv);
        req.Headers.TryGetValues("apikey", out var av);
        Requests.Add(new Seen(req.Method.Method, path, req.Headers.Authorization?.ToString(), sv?.FirstOrDefault(), av?.FirstOrDefault(), body));
        var key = $"{req.Method.Method} {path}";
        if (Routes.TryGetValue(key, out var q) && q.Count > 0)
        {
            var next = q.Count > 1 ? q.Dequeue() : q.Peek();
            return next();
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }
}

public class ConnectClientTests
{
    private const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"anon-key","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    private static string Login(long exp = 4_000_000_000, string at = "access-1", string rt = "refresh-1") => "{\"accessToken\":\"" + at + "\",\"refreshToken\":\"" + rt + "\",\"expiresAt\":" + exp + ",\"userId\":\"u1\",\"email\":\"a@x.in\"}";
    private const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"Aurelia","publicId":"AORMS-S-1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":false},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";
    private static string Err(string code, string msg, string extra = "") => "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + msg + "\"" + extra + "}}";
    private static string Refreshed(string at, string rt, long exp) => "{\"access_token\":\"" + at + "\",\"refresh_token\":\"" + rt + "\",\"expires_at\":" + exp + "}";

    private static (ConnectClient c, FakeServer s, InMemoryTokenStore store) Make(FakeServer? server = null, Func<DateTimeOffset>? clock = null)
    {
        var s = server ?? new FakeServer();
        var store = new InMemoryTokenStore();
        return (new ConnectClient(new Uri("https://aorms.example/"), store, s, clock), s, store);
    }

    [Fact]
    public async Task SignIn_logs_in_then_starts_the_session_and_stores_the_refresh_token()
    {
        var (c, s, store) = Make(new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login()).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess));
        var info = await c.SignInAsync("a@x.in", "pw", "AQC test");
        Assert.True(c.IsConnected);
        Assert.True(info.CanWrite);
        Assert.False(info.Can("cost:approve"));
        Assert.Equal("Aurelia", info.Studio.Name);
        Assert.Equal("Bearer access-1", s.Requests.Single(r => r.Path.EndsWith("/session")).Auth);
        Assert.Null(s.Requests.Single(r => r.Path.EndsWith("/session")).Session); // starting a session carries no session id
        Assert.Contains("\"clientLabel\":\"AQC test\"", s.Requests.Last().Body);
        Assert.Equal(new StoredSession("refresh-1", "s1", "a@x.in"), store.Load());
    }

    [Theory]
    [InlineData(401, "invalid_credentials")]
    [InlineData(403, "not_linked")]
    [InlineData(403, "pending")]
    public async Task Login_errors_keep_their_code(int status, string code)
    {
        var (c, _, _) = Make(new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", (HttpStatusCode)status, Err(code, "nope")));
        var ex = await Assert.ThrowsAsync<ConnectException>(() => c.SignInAsync("a@x.in", "bad", "t"));
        Assert.Equal(code, ex.Code);
        Assert.Equal(status, ex.Status);
        Assert.Null(c.Session);
    }

    [Fact]
    public async Task Rate_limit_reports_when_to_retry()
    {
        var (c, _, _) = Make(new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", (HttpStatusCode)429, Err("rate_limited", "slow down", ",\"retryAfterSeconds\":42")));
        var ex = await Assert.ThrowsAsync<ConnectException>(() => c.SignInAsync("a@x.in", "x", "t"));
        Assert.True(ex.IsRateLimited);
        Assert.Equal(42, ex.RetryAfterSeconds);
    }

    [Fact]
    public async Task A_server_on_another_contract_is_refused()
    {
        var (c, _, _) = Make(new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg.Replace("2026-10-aqc-1", "2027-01-aqc-2")));
        var ex = await Assert.ThrowsAsync<ConnectException>(() => c.GetConfigAsync());
        Assert.Equal("update_required", ex.Code);
    }

    [Fact]
    public async Task Not_connected_surfaces_the_reason_and_raises_the_event()
    {
        var (c, _, _) = Make(new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login())
            .On("POST api/aqc/v1/session", HttpStatusCode.Forbidden, Err("not_connected", "Connected mode isn't available for this studio.", ",\"reason\":\"PLAN_NOT_CONNECTED\",\"plan\":\"FREE\"")));
        ConnectException? seen = null;
        c.NotConnected += e => seen = e;
        var ex = await Assert.ThrowsAsync<ConnectException>(() => c.SignInAsync("a@x.in", "pw", "t"));
        Assert.True(ex.IsNotConnected);
        Assert.Equal("PLAN_NOT_CONNECTED", ex.Reason);
        Assert.NotNull(seen);
        Assert.False(c.IsConnected);
    }

    [Fact]
    public async Task Restore_with_nothing_stored_says_so()
    {
        var (c, s, _) = Make();
        Assert.Equal(RestoreOutcome.NoStoredSession, await c.RestoreAsync());
        Assert.Empty(s.Requests);
    }

    [Fact]
    public async Task Restore_refreshes_then_reuses_the_stored_session_id()
    {
        var server = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("access-2", "refresh-2", 4_000_000_000)).On("GET api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var (c, s, store) = Make(server);
        store.Save(new StoredSession("refresh-1", "s1", "a@x.in"));
        Assert.Equal(RestoreOutcome.Restored, await c.RestoreAsync());
        var refresh = s.Requests.Single(r => r.Path == "auth/v1/token");
        Assert.Equal("anon-key", refresh.Apikey);
        Assert.Contains("\"refresh_token\":\"refresh-1\"", refresh.Body);
        var get = s.Requests.Single(r => r.Path == "api/aqc/v1/session");
        Assert.Equal("Bearer access-2", get.Auth);
        Assert.Equal("s1", get.Session);
        Assert.Equal("refresh-2", store.Load()!.RefreshToken); // rotated refresh token saved
    }

    [Fact]
    public async Task Restore_never_steals_a_session_taken_elsewhere()
    {
        var server = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("a2", "r2", 4_000_000_000))
            .On("GET api/aqc/v1/session", HttpStatusCode.Conflict, Err("session_replaced", "You signed in to AQC on another computer."));
        var (c, s, store) = Make(server);
        store.Save(new StoredSession("r1", "old", "a@x.in"));
        Assert.Equal(RestoreOutcome.SessionReplaced, await c.RestoreAsync());
        Assert.DoesNotContain(s.Requests, r => r.Method == "POST" && r.Path == "api/aqc/v1/session");
        Assert.Null(c.Session);
        Assert.NotNull(store.Load()); // the user may choose "Sign in here"
    }

    [Fact]
    public async Task Restore_with_a_dead_refresh_token_signs_out_and_clears_the_store()
    {
        var server = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST auth/v1/token", HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
        var (c, _, store) = Make(server);
        store.Save(new StoredSession("dead", "s1", "a@x.in"));
        int signedOut = 0;
        c.SignedOut += () => signedOut++;
        Assert.Equal(RestoreOutcome.SignedOut, await c.RestoreAsync());
        Assert.Null(store.Load());
        Assert.Equal(1, signedOut);
    }

    [Fact]
    public async Task Calls_carry_the_bearer_and_the_session_header_and_refresh_before_expiry()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        var server = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg)
            .On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login(exp: 1_003_600)) // valid for an hour
            .On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess)
            .On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("access-2", "refresh-2", 1_007_200))
            .On("GET api/aqc/v1/projects", HttpStatusCode.OK, "{\"projects\":[]}");
        var (c, s, _) = Make(server, () => now);
        await c.SignInAsync("a@x.in", "pw", "t");
        Assert.DoesNotContain(s.Requests, r => r.Path == "auth/v1/token"); // fresh token: no refresh yet
        now = now.AddSeconds(3_560); // 40 s before expiry — inside the 60 s skew
        s.Requests.Clear();
        await c.GetAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects");
        Assert.Equal("auth/v1/token", s.Requests[0].Path); // refreshed first
        Assert.Equal("Bearer access-2", s.Requests[1].Auth);
        Assert.Equal("s1", s.Requests[1].Session);
    }

    [Fact]
    public async Task A_401_refreshes_once_and_retries()
    {
        var server = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login()).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess)
            .On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("access-2", "refresh-2", 4_000_000_000));
        server.On("GET api/aqc/v1/projects", HttpStatusCode.Unauthorized, Err("invalid_token", "expired"));
        server.On("GET api/aqc/v1/projects", HttpStatusCode.OK, "{\"projects\":[]}");
        var (c, s, _) = Make(server);
        await c.SignInAsync("a@x.in", "pw", "t");
        await c.GetAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects");
        Assert.Equal(2, s.Requests.Count(r => r.Path == "api/aqc/v1/projects"));
        Assert.Equal("Bearer access-2", s.Requests.Last(r => r.Path == "api/aqc/v1/projects").Auth);
    }

    [Fact]
    public async Task Session_replaced_during_a_call_stops_the_session_and_raises_the_event_once()
    {
        var server = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login()).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess)
            .On("POST api/aqc/v1/projects/p1/rows", HttpStatusCode.Conflict, Err("session_replaced", "You signed in to AQC on another computer."));
        var (c, _, _) = Make(server);
        int raised = 0;
        c.SessionReplaced += () => raised++;
        await c.SignInAsync("a@x.in", "pw", "t");
        var ex = await Assert.ThrowsAsync<ConnectException>(() => c.PostAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects/p1/rows", new { rows = Array.Empty<int>() }));
        Assert.True(ex.IsSessionReplaced);
        Assert.Equal(1, raised);
        Assert.Null(c.Session);
        // and the next call fails fast instead of hitting the server with a dead session
        await Assert.ThrowsAsync<ConnectException>(() => c.PostAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects/p1/rows", new { }));
    }

    [Fact]
    public async Task SignOut_clears_everything()
    {
        var (c, _, store) = Make(new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login()).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess));
        await c.SignInAsync("a@x.in", "pw", "t");
        c.SignOut();
        Assert.Null(store.Load());
        Assert.Null(c.Session);
        Assert.False(c.IsConnected);
    }
}
