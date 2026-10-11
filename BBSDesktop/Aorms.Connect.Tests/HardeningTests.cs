// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

/// <summary>A7: the failure paths that matter for a local-first client — outages, expiring leases/tokens, restarts, big projects.</summary>
public class HardeningTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    static string Login(long exp = 1_003_600) => $$"""{"accessToken":"a1","refreshToken":"r1","expiresAt":{{exp}},"userId":"u1","email":"a@x.in"}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":true},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";
    const string Lease = "POST api/aqc/v1/projects/q1/lease";
    const string Rows = "POST api/aqc/v1/projects/q1/rows";

    sealed class Clock { public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000); public void Add(double s) => Now = Now.AddSeconds(s); }

    static async Task<(ConnectClient c, FakeServer s, Clock clock)> SignedIn(FakeServer s)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login()).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var clock = new Clock();
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s, () => clock.Now);
        await c.SignInAsync("a@x.in", "pw", "t");
        return (c, s, clock);
    }

    static void Put(Outbox box, string id, string v = "x") => box.Upsert("columns", id, new JsonObject { ["n"] = v });

    // ── token refresh ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_busy_auth_server_does_not_sign_anyone_out()
    {
        var (c, s, clock) = await SignedIn(new FakeServer().On("POST auth/v1/token", HttpStatusCode.ServiceUnavailable, "{}").On("GET api/aqc/v1/projects", HttpStatusCode.OK, """{"projects":[]}"""));
        int signedOut = 0; c.SignedOut += () => signedOut++;
        clock.Add(3_580); // inside the refresh skew
        var e = await Assert.ThrowsAsync<ConnectException>(() => c.GetAsync<System.Text.Json.JsonElement>("api/aqc/v1/projects"));
        Assert.Equal("unavailable", e.Code);
        Assert.Equal(0, signedOut);
        Assert.True(c.IsConnected); // still signed in; next try may succeed
    }

    [Fact]
    public async Task A_refused_refresh_token_signs_out_and_sync_keeps_the_outbox()
    {
        var (c, s, clock) = await SignedIn(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""")
            .On(Rows, HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_token","message":"expired"}}""")
            .On("POST auth/v1/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        int signedOut = 0; c.SignedOut += () => signedOut++;
        var box = new Outbox();
        var p = new ProjectSync(c, "q1", 0, box, () => clock.Now);
        await p.AcquireLeaseAsync();
        Put(box, "a"); p.NoteChange();
        var res = await p.FlushAsync();
        Assert.Equal(SyncState.SignedOut, p.State);
        Assert.Equal(1, signedOut);
        Assert.Equal(1, box.Count);
        Assert.False(p.CanEdit);
        var before = s.Requests.Count;
        await p.FlushAsync(); await p.TickAsync();
        Assert.Equal(before, s.Requests.Count); // no hammering a dead sign-in
    }

    // ── lease expiry mid-edit ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lease_lost_between_batches_keeps_the_unsent_rows_and_goes_read_only()
    {
        // 700 rows: batch 1 (500) lands, batch 2 is refused (lease expired) and someone else now holds it.
        var server = new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Lease, HttpStatusCode.OK, """{"held":false}""")
            .On(Rows, HttpStatusCode.OK, """{"headSeq":500}""").On(Rows, (HttpStatusCode)423, """{"error":{"code":"lease_required","message":"no lease"}}""");
        var (c, s, clock) = await SignedIn(server);
        var box = new Outbox();
        var p = new ProjectSync(c, "q1", 0, box, () => clock.Now);
        for (var i = 0; i < 700; i++) Put(box, "r" + i);
        p.NoteChange();
        var res = await p.FlushAsync();
        Assert.Equal(500, res.Sent);
        Assert.Equal(200, box.Count);
        Assert.Equal(SyncState.ReadOnlyLeaseHeld, p.State);
        Assert.Equal(500, p.HeadSeq);
        Assert.Equal("r500", box.Snapshot()[0].RowId); // oldest unsent first, nothing lost or reordered
    }

    // ── offline for days, restart, replay ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edits_made_offline_survive_a_restart_and_replay_in_order()
    {
        var store = new InMemoryOutboxStore();
        var offline = new Outbox(store);
        for (var i = 0; i < 5; i++) Put(offline, "r" + i);
        Put(offline, "r2", "edited-twice"); // collapses, keeps first-change position
        offline.Delete("columns", "r4");

        // "days later": new process, new Outbox over the same store
        var (c, s, clock) = await SignedIn(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Rows, HttpStatusCode.OK, """{"headSeq":9}"""));
        var reopened = new Outbox(store);
        var p = new ProjectSync(c, "q1", 4, reopened, () => clock.Now);
        Assert.Equal(SyncState.Pending, p.State); // there is work waiting from the start
        await p.TickAsync(); // debounce already elapsed for restored work
        var body = JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/rows") && r.Method == "POST").Body)!["rows"]!.AsArray();
        Assert.Equal(new[] { "r0", "r1", "r2", "r3", "r4" }, body.Select(r => r!["row_id"]!.GetValue<string>()));
        Assert.Equal("edited-twice", body[2]!["fields"]!["n"]!.GetValue<string>());
        Assert.True(body[4]!["deleted"]!.GetValue<bool>());
        Assert.Equal(0, reopened.Count);
        Assert.Equal(SyncState.Synced, p.State);
        Assert.Equal(9, p.HeadSeq);
    }

    [Fact]
    public async Task A_dead_network_then_recovery_never_loses_a_row()
    {
        var server = new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""");
        for (var i = 0; i < 3; i++) server.OnThrow(Rows);
        server.On(Rows, HttpStatusCode.OK, """{"headSeq":3}""");
        var (c, s, clock) = await SignedIn(server);
        var box = new Outbox();
        var p = new ProjectSync(c, "q1", 0, box, () => clock.Now);
        await p.AcquireLeaseAsync();
        for (var i = 0; i < 3; i++) Put(box, "r" + i);
        p.NoteChange();
        for (var t = 0; t < 12 && box.Count > 0; t++) { clock.Add(30); await p.TickAsync(); } // time passes, back-off grows, it keeps trying
        Assert.Equal(0, box.Count);
        Assert.Equal(SyncState.Synced, p.State);
        Assert.Equal(4, s.Requests.Count(r => r.Path.EndsWith("/rows") && r.Method == "POST")); // 3 failures + 1 success
    }

    [Fact]
    public void UpsertMany_saves_once()
    {
        var saves = 0;
        var box = new Outbox(new CountingStore(() => saves++));
        box.UpsertMany(Enumerable.Range(0, 2_000).Select(i => new SyncRow("columns", "r" + i, new JsonObject())));
        Assert.Equal(1, saves);
        Assert.Equal(2_000, box.Count);
    }

    sealed class CountingStore : IOutboxStore
    {
        readonly Action _onSave;
        public CountingStore(Action onSave) => _onSave = onSave;
        public string? Load() => null;
        public void Save(string json) => _onSave();
    }

    // ── large project ────────────────────────────────────────────────────────────────────────────────────

    static JsonObject BigProject(int n)
    {
        var rows = new JsonArray();
        for (var i = 0; i < n; i++) rows.Add(new JsonObject { ["n"] = "C" + i, ["len"] = i });
        return new JsonObject { ["project"] = new JsonObject { ["name"] = "Big" }, ["columns"] = rows };
    }

    [Fact]
    public async Task A_ten_thousand_row_project_goes_up_in_500_row_batches()
    {
        var server = new FakeServer().On("POST api/aqc/v1/projects", HttpStatusCode.Created, """{"aqcProjectId":"q1","headSeq":500,"rows":500}""")
            .On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Rows, HttpStatusCode.OK, """{"headSeq":10000}""");
        var (c, s, _) = await SignedIn(server);
        var res = await new ProjectsApi(c).PushOnlineAsync("p1", BigProject(10_000));
        Assert.Equal(10_000, res.Rows);
        Assert.Empty(res.Pending);
        var bind = JsonNode.Parse(s.Requests.Single(r => r.Path == "api/aqc/v1/projects" && r.Method == "POST").Body)!;
        Assert.Equal(500, bind["rows"]!.AsArray().Count);
        var batches = s.Requests.Where(r => r.Path.EndsWith("/rows") && r.Method == "POST").Select(r => JsonNode.Parse(r.Body)!["rows"]!.AsArray().Count).ToList();
        Assert.Equal(19, batches.Count);
        Assert.All(batches, n => Assert.Equal(500, n));
    }

    [Fact]
    public async Task A_failed_later_batch_leaves_the_project_bound_and_returns_the_unsent_rows()
    {
        var server = new FakeServer().On("POST api/aqc/v1/projects", HttpStatusCode.Created, """{"aqcProjectId":"q1","headSeq":500,"rows":500}""")
            .On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Rows, HttpStatusCode.OK, """{"headSeq":1000}""");
        server.OnThrow(Rows);
        var (c, _, _) = await SignedIn(server);
        var res = await new ProjectsApi(c).PushOnlineAsync("p1", BigProject(1_700));
        Assert.Equal("q1", res.AqcProjectId); // bound — never lost
        Assert.Equal(1_000, res.Rows);
        Assert.Equal(700, res.Pending.Count);
    }
}
