// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

/// <summary>One AORMS sign-in shared by AAD and AQC: per-app sessions, rotation-safe refresh, and the CAD → portal → AQC hand-off.</summary>
public class SuiteSignInTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    static string Login(string rt = "r1", long exp = 1_003_600) => $$"""{"accessToken":"a1","refreshToken":"{{rt}}","expiresAt":{{exp}},"userId":"u1","email":"a@x.in"}""";
    static string Refreshed(string at, string rt, long exp) => $$"""{"access_token":"{{at}}","refresh_token":"{{rt}}","expires_at":{{exp}}}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":true},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";
    sealed class Clock { public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000); public void Add(double s) => Now = Now.AddSeconds(s); }

    static FakeServer Base() => new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login()).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);

    static (ConnectClient c, Clock clock, InMemoryTokenStore perApp, InMemorySharedSignInStore shared) Make(FakeServer s, string app = "aqc", InMemorySharedSignInStore? shared = null)
    {
        var clock = new Clock(); var perApp = new InMemoryTokenStore(); shared ??= new InMemorySharedSignInStore();
        return (new ConnectClient(new Uri("https://aorms.example/"), perApp, s, () => clock.Now, shared, app), clock, perApp, shared);
    }

    // ── per-app sessions ─────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("aqc")]
    [InlineData("aad")]
    public async Task Session_start_names_the_app(string app)
    {
        var s = Base();
        var (c, _, _, _) = Make(s, app);
        await c.SignInAsync("a@x.in", "pw", "t");
        Assert.Equal(app, c.App);
        Assert.Equal(app, JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/session") && r.Method == "POST").Body)!["app"]!.GetValue<string>());
    }

    [Fact]
    public async Task Default_app_is_aqc_so_existing_callers_are_unchanged()
    {
        var s = Base();
        var clock = new Clock();
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s, () => clock.Now); // no shared store, no app: the pre-suite call shape
        await c.SignInAsync("a@x.in", "pw", "t");
        Assert.Equal("aqc", JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/session") && r.Method == "POST").Body)!["app"]!.GetValue<string>());
    }

    // ── shared sign-in ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sign_in_publishes_the_refresh_token_to_the_shared_store_and_keeps_the_session_id_per_app()
    {
        var (c, _, perApp, shared) = Make(Base());
        await c.SignInAsync("a@x.in", "pw", "t");
        Assert.Equal("r1", shared.Load()!.RefreshToken);
        Assert.Equal("s1", perApp.Load()!.SessionId);
    }

    [Fact]
    public async Task Refresh_uses_the_token_another_app_already_rotated()
    {
        var server = Base().On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("a2", "r3", 1_007_200)).On("GET api/aqc/v1/projects", HttpStatusCode.OK, """{"projects":[]}""");
        var (c, clock, _, shared) = Make(server);
        await c.SignInAsync("a@x.in", "pw", "t");
        shared.Save(new SharedSignIn("r2", "a@x.in", clock.Now)); // "AAD" rotated r1 → r2
        clock.Add(3_580); // inside the refresh skew
        await c.GetAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects");
        var sent = JsonNode.Parse(server.Requests.First(r => r.Path == "auth/v1/token").Body)!["refresh_token"]!.GetValue<string>();
        Assert.Equal("r2", sent); // never the stale r1
        Assert.Equal("r3", shared.Load()!.RefreshToken); // the new token goes straight back for the other app
    }

    [Fact]
    public async Task A_refused_refresh_retries_once_when_the_shared_token_changed_during_the_call()
    {
        SharedSignIn? swap = null; InMemorySharedSignInStore? store = null;
        var server = Base()
            .OnDo("POST auth/v1/token", () => { store!.Save(swap!); return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") }; })
            .On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("a2", "r9", 1_007_200))
            .On("GET api/aqc/v1/projects", HttpStatusCode.OK, """{"projects":[]}""");
        var (c, clock, _, shared) = Make(server);
        store = shared;
        await c.SignInAsync("a@x.in", "pw", "t");
        swap = new SharedSignIn("r-from-other-app", "a@x.in", clock.Now);
        clock.Add(3_580);
        int signedOut = 0; c.SignedOut += () => signedOut++;
        await c.GetAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects"); // succeeds: lost the race, retried with the winner's token
        Assert.Equal(0, signedOut);
        var tokens = server.Requests.Where(r => r.Path == "auth/v1/token").Select(r => JsonNode.Parse(r.Body)!["refresh_token"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "r1", "r-from-other-app" }, tokens);
        Assert.Equal("r9", shared.Load()!.RefreshToken);
    }

    [Fact]
    public async Task A_refused_refresh_with_nothing_newer_signs_out_and_clears_the_dead_shared_token()
    {
        var server = Base().On("POST auth/v1/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var (c, clock, _, shared) = Make(server);
        await c.SignInAsync("a@x.in", "pw", "t");
        clock.Add(3_580);
        int signedOut = 0; c.SignedOut += () => signedOut++;
        var e = await Assert.ThrowsAsync<ConnectException>(() => c.GetAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects"));
        Assert.Equal(401, e.Status);
        Assert.Equal(1, signedOut);
        Assert.Equal(1, server.Requests.Count(r => r.Path == "auth/v1/token")); // no retry with the same token
        Assert.Null(shared.Load());
    }

    [Fact]
    public async Task Signing_out_of_one_app_clears_the_shared_login()
    {
        var (c, _, perApp, shared) = Make(Base());
        await c.SignInAsync("a@x.in", "pw", "t");
        c.SignOut();
        Assert.Null(shared.Load()); Assert.Null(perApp.Load());
    }

    [Fact]
    public async Task Second_app_restores_from_the_shared_login_without_a_password_and_gets_its_own_session()
    {
        var shared = new InMemorySharedSignInStore();
        // AQC signs in
        var (aqc, clock, _, _) = Make(Base(), "aqc", shared);
        await aqc.SignInAsync("a@x.in", "pw", "AQC");
        // AAD starts later: nothing of its own stored, only the shared login
        var aadServer = new FakeServer().On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST auth/v1/token", HttpStatusCode.OK, Refreshed("a-aad", "r-next", 1_007_200))
            .On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess.Replace("\"s1\"", "\"s-aad\""));
        var (aad, _, aadStore, _) = Make(aadServer, "aad", shared);
        Assert.Equal(RestoreOutcome.SessionReplaced, await aad.RestoreAsync()); // signed in, but this app has no session yet — user starts one (never stolen silently)
        var s = await aad.StartSessionAsync("AAD");
        Assert.Equal("s-aad", s.SessionId);
        Assert.Equal("aad", JsonNode.Parse(aadServer.Requests.Last(r => r.Path.EndsWith("/session") && r.Method == "POST").Body)!["app"]!.GetValue<string>());
        Assert.Equal("s-aad", aadStore.Load()!.SessionId);
    }
}

public class CadBridgeApiTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    const string Login = """{"accessToken":"a","refreshToken":"r","expiresAt":4000000000,"userId":"u1","email":"a@x.in"}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":true},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";

    static async Task<(CadBridgeApi api, FakeServer s)> Make(FakeServer s)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s, shared: null, app: "aad");
        await c.SignInAsync("a@x.in", "pw", "t");
        return (new CadBridgeApi(c), s);
    }

    [Fact]
    public async Task Publish_sends_the_bridge_json_as_a_cad_bridge_version_with_its_sha_as_content_hash()
    {
        var json = File.ReadAllText("small-house.bridge.json");
        var (api, s) = await Make(new FakeServer()
            .On("POST api/aqc/v1/projects/q1/files", HttpStatusCode.Created, """{"storageKey":"f1/q1/cad_bridge/K.json","uploadUrl":"https://storage.example/up?t=1"}""")
            .On("PUT up", HttpStatusCode.OK, "{}")
            .On("POST api/aqc/v1/projects/q1/versions", HttpStatusCode.Created, """{"version":2}"""));
        var v = await api.PublishAsync("q1", json, new JsonObject { ["drawing_id"] = "house-1" });
        Assert.Equal("2", v);
        var init = JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/files")).Body)!;
        Assert.Equal("cad_bridge", init["kind"]!.GetValue<string>());
        Assert.Equal("application/json", init["contentType"]!.GetValue<string>());
        var add = JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/versions")).Body)!;
        Assert.Equal(VersionsApi.Sha256Hex(Encoding.UTF8.GetBytes(json)), add["contentHash"]!.GetValue<string>());
        Assert.Equal("house-1", add["summary"]!["drawing_id"]!.GetValue<string>());
        Assert.Equal("application/json", s.Requests.Single(r => r.Method == "PUT").Path == "up" ? "application/json" : "");
    }

    [Fact]
    public async Task Latest_download_returns_the_newest_version_with_a_file_byte_for_byte()
    {
        var json = File.ReadAllText("small-house.bridge.json");
        var (api, s) = await Make(new FakeServer()
            .On("GET api/aqc/v1/projects/q1/versions", HttpStatusCode.OK, """{"versions":[{"id":"v-old","kind":"cad_bridge","version":1,"summary":{},"storage_key":"f1/q1/cad_bridge/a.json","created_at":"2026-10-01T00:00:00Z"},{"id":"v-new","kind":"cad_bridge","version":2,"summary":{"drawing_id":"house-1"},"storage_key":"f1/q1/cad_bridge/b.json","created_at":"2026-10-05T00:00:00Z"},{"id":"v-nofile","kind":"cad_bridge","version":3,"summary":{},"storage_key":null,"created_at":"2026-10-06T00:00:00Z"}]}""")
            .On("GET api/aqc/v1/files/version/v-new", HttpStatusCode.OK, """{"url":"https://storage.example/signed?t=1","expiresInSeconds":300}""")
            .On("GET signed", HttpStatusCode.OK, json));
        var got = await api.DownloadLatestAsync("q1");
        Assert.NotNull(got);
        Assert.Equal("v-new", got!.Value.version.Id); // v3 has no file, so v2 is the newest usable one
        Assert.Equal(2, got.Value.version.Version);
        Assert.Equal(json, got.Value.json);
        Assert.Contains(s.Requests, r => r.Path.EndsWith("/versions") && r.Method == "GET");
        Assert.Null(s.Requests.Last(r => r.Path == "signed").Auth);
    }

    [Fact]
    public async Task No_published_take_off_is_null_not_an_error()
    {
        var (api, _) = await Make(new FakeServer().On("GET api/aqc/v1/projects/q1/versions", HttpStatusCode.OK, """{"versions":[]}"""));
        Assert.Null(await api.DownloadLatestAsync("q1"));
    }

    [Fact]
    public void The_cad_bridge_kind_needs_write_only()
    {
        Assert.Equal("write", VersionKinds.RequiredCapability("cad_bridge"));
    }
}
