// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

public class ProjectSyncEngineTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    const string Login = """{"accessToken":"a","refreshToken":"r","expiresAt":4000000000,"userId":"u1","email":"a@x.in"}""";
    static string Sess(bool write) => """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":""" + (write ? "true" : "false") + ""","fees:manage":true,"cost:approve":false},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";
    const string Rows = "POST api/aqc/v1/projects/q1/rows";
    const string Lease = "POST api/aqc/v1/projects/q1/lease";

    sealed class Clock { public DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero); public void Add(double s) => Now = Now.AddSeconds(s); }

    static async Task<(ProjectSync sync, FakeServer s, Clock clock)> Make(FakeServer s, bool write = true, Outbox? outbox = null, long head = 0)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess(write));
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s);
        await c.SignInAsync("a@x.in", "pw", "t");
        var clock = new Clock();
        return (new ProjectSync(c, "q1", head, outbox ?? new Outbox(), () => clock.Now), s, clock);
    }

    static void Edit(ProjectSync p, string id, string val = "x") { p.Outbox.Upsert("columns", id, new JsonObject { ["n"] = val }); p.NoteChange(); }
    static int Count(FakeServer s, string suffix) => s.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith(suffix));

    [Fact]
    public async Task Flush_sends_in_batches_of_500_and_ends_synced()
    {
        var (p, s, _) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Rows, HttpStatusCode.OK, """{"headSeq":77}"""));
        for (var i = 0; i < 1200; i++) Edit(p, "r" + i);
        var res = await p.FlushAsync();
        Assert.Equal(1200, res.Sent);
        Assert.Equal(3, Count(s, "/rows"));
        Assert.Equal(0, p.Outbox.Count);
        Assert.Equal(SyncState.Synced, p.State);
        Assert.Equal(77, p.HeadSeq);
    }

    [Fact]
    public async Task Tick_waits_for_the_two_second_debounce_then_flushes()
    {
        var (p, s, clock) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Rows, HttpStatusCode.OK, """{"headSeq":1}"""));
        await p.AcquireLeaseAsync();
        Edit(p, "a");
        clock.Add(1); await p.TickAsync();
        Assert.Equal(0, Count(s, "/rows"));
        clock.Add(1.5); await p.TickAsync();
        Assert.Equal(1, Count(s, "/rows"));
        Assert.Equal(SyncState.Synced, p.State);
    }

    [Fact]
    public async Task Lease_is_renewed_every_60s_and_retried_every_30s_when_read_only()
    {
        var (p, s, clock) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}"""));
        await p.AcquireLeaseAsync();
        clock.Add(59); await p.TickAsync(); Assert.Equal(1, Count(s, "/lease"));
        clock.Add(2); await p.TickAsync(); Assert.Equal(2, Count(s, "/lease"));

        var (q, s2, c2) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":false}"""));
        Assert.False(await q.AcquireLeaseAsync());
        Assert.Equal(SyncState.ReadOnlyLeaseHeld, q.State);
        Assert.False(q.CanEdit);
        c2.Add(31); await q.TickAsync(); Assert.Equal(2, Count(s2, "/lease"));
    }

    [Fact]
    public async Task Lease_required_reacquires_and_retries_the_batch()
    {
        var (p, s, _) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""")
            .On(Rows, (HttpStatusCode)423, """{"error":{"code":"lease_required","message":"no lease"}}""").On(Rows, HttpStatusCode.OK, """{"headSeq":5}"""));
        await p.AcquireLeaseAsync();
        Edit(p, "a");
        var res = await p.FlushAsync();
        Assert.Equal(1, res.Sent);
        Assert.Equal(2, Count(s, "/lease"));
        Assert.Equal(0, p.Outbox.Count);
    }

    [Fact]
    public async Task Someone_else_holding_the_lease_keeps_rows_and_goes_read_only()
    {
        var (p, s, _) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":false}"""));
        Edit(p, "a");
        var res = await p.FlushAsync();
        Assert.Equal(0, res.Sent);
        Assert.Equal(1, p.Outbox.Count);
        Assert.Equal(SyncState.ReadOnlyLeaseHeld, p.State);
        Assert.Equal(0, Count(s, "/rows"));
    }

    [Fact]
    public async Task Network_failure_keeps_everything_and_retries_with_backoff()
    {
        var (p, s, clock) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""").OnThrow(Rows).On(Rows, HttpStatusCode.OK, """{"headSeq":9}"""));
        await p.AcquireLeaseAsync();
        Edit(p, "a");
        await p.FlushAsync();
        Assert.Equal(SyncState.Offline, p.State);
        Assert.Equal(1, p.Outbox.Count);
        clock.Add(1); await p.TickAsync();
        Assert.Equal(1, Count(s, "/rows")); // still backing off
        clock.Add(5); await p.TickAsync();
        Assert.Equal(2, Count(s, "/rows"));
        Assert.Equal(SyncState.Synced, p.State);
        Assert.Equal(0, p.Outbox.Count);
    }

    [Fact]
    public async Task One_bad_row_is_dropped_and_reported_without_blocking_the_rest()
    {
        var bad = """{"error":{"code":"bad_request","message":"Invalid row."}}""";
        // batch of 3 fails, then singles: ok, bad, ok
        var server = new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""")
            .On(Rows, HttpStatusCode.BadRequest, bad).On(Rows, HttpStatusCode.OK, """{"headSeq":1}""").On(Rows, HttpStatusCode.BadRequest, bad).On(Rows, HttpStatusCode.OK, """{"headSeq":2}""");
        var (p, s, _) = await Make(server);
        await p.AcquireLeaseAsync();
        var rejected = new List<string>();
        p.RowRejected += (r, _) => rejected.Add(r.RowId);
        Edit(p, "a"); Edit(p, "b"); Edit(p, "c");
        var res = await p.FlushAsync();
        Assert.Equal(new[] { "b" }, rejected);
        Assert.Equal(2, res.Sent);
        Assert.Equal(0, p.Outbox.Count);
    }

    [Fact]
    public async Task Viewer_never_calls_lease_or_rows()
    {
        var (p, s, _) = await Make(new FakeServer(), write: false);
        Assert.Equal(SyncState.ReadOnlyRole, p.State);
        Assert.False(await p.AcquireLeaseAsync());
        await p.FlushAsync();
        Assert.Equal(0, Count(s, "/lease") + Count(s, "/rows"));
    }

    [Fact]
    public async Task Session_replaced_stops_syncing_but_keeps_the_outbox()
    {
        var (p, s, _) = await Make(new FakeServer().On(Lease, HttpStatusCode.OK, """{"held":true}""").On(Rows, HttpStatusCode.Conflict, """{"error":{"code":"session_replaced","message":"moved"}}"""));
        await p.AcquireLeaseAsync();
        Edit(p, "a");
        await p.FlushAsync();
        Assert.Equal(SyncState.SessionReplaced, p.State);
        Assert.Equal(1, p.Outbox.Count);
        Assert.False(p.CanEdit);
        await p.FlushAsync();
        Assert.Equal(1, Count(s, "/rows")); // no hammering
    }

    [Fact]
    public async Task Catch_up_applies_cleanly_when_nothing_is_unsent_and_asks_for_review_otherwise()
    {
        var json = """{"rows":[{"section":"columns","row_id":"z","fields":{"n":"Z"}}],"headSeq":9,"more":false,"nextSince":9}""";
        var (p, _, _) = await Make(new FakeServer().On("GET api/aqc/v1/projects/q1/rows", HttpStatusCode.OK, json), head: 4);
        var clean = await p.CatchUpAsync();
        Assert.False(clean.NeedsReview);
        Assert.Equal(9, p.HeadSeq);

        var (q, _, _) = await Make(new FakeServer().On("GET api/aqc/v1/projects/q1/rows", HttpStatusCode.OK, json), head: 4);
        Edit(q, "mine");
        var dirty = await q.CatchUpAsync();
        Assert.True(dirty.NeedsReview);
        Assert.Equal(4, q.HeadSeq); // not advanced until the user decides
        q.AdoptHead(dirty.HeadSeq);
        Assert.Equal(9, q.HeadSeq);
    }

    [Fact]
    public void Outbox_collapses_repeat_edits_keeps_order_survives_restart_and_never_loses_a_newer_edit()
    {
        var store = new InMemoryOutboxStore();
        var box = new Outbox(store);
        box.Upsert("columns", "a", new JsonObject { ["n"] = "1" });
        box.Upsert("columns", "b", new JsonObject { ["n"] = "1" });
        box.Upsert("columns", "a", new JsonObject { ["n"] = "2" });
        box.Delete("columns", "b");
        var snap = box.Snapshot();
        Assert.Equal(new[] { "a", "b" }, snap.Select(r => r.RowId));
        Assert.Equal("2", snap[0].Fields["n"]!.GetValue<string>());
        Assert.True(snap[1].Deleted);

        var reopened = new Outbox(store);
        Assert.Equal(2, reopened.Count);

        // an edit made while a batch was in flight survives that batch's acknowledgement
        var inFlight = box.Snapshot();
        box.Upsert("columns", "a", new JsonObject { ["n"] = "3" });
        box.Acknowledge(inFlight);
        var left = box.Snapshot();
        Assert.Single(left);
        Assert.Equal("3", left[0].Fields["n"]!.GetValue<string>());
    }
}
