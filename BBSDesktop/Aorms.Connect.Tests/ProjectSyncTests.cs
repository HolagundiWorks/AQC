// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

public class ProjectMapperTests
{
    static JsonObject Pilot() => (JsonObject)JsonNode.Parse(File.ReadAllText("pilot-sample.json"))!;

    [Fact]
    public void Pilot_sample_maps_to_many_rows_and_every_takeoff_row_gets_a_stable_rid()
    {
        var doc = Pilot();
        var sync = ProjectMapper.ToSync(doc);
        Assert.True(sync.Rows.Count >= 20);
        var take = sync.Rows.Where(r => r.Section == "masonry").ToList();
        Assert.NotEmpty(take);
        Assert.All(take, r => Assert.Equal(32, r.RowId.Length));
        // minted ids are written back into the document, so a second pass reuses them
        var again = ProjectMapper.ToSync(doc);
        Assert.Equal(take.Select(r => r.RowId), again.Rows.Where(r => r.Section == "masonry").Select(r => r.RowId));
        Assert.Equal(sync.Rows.Count, again.Rows.Count);
    }

    [Fact]
    public void Settings_skeleton_has_no_rows_and_leaves_out_last_estimate_and_takeoff()
    {
        var doc = Pilot();
        doc["last_estimate"] = new JsonObject { ["total"] = 1 };
        doc["takeoff"] = new JsonObject { ["pdf"] = "C:\\x.pdf" };
        var sync = ProjectMapper.ToSync(doc);
        Assert.Null(sync.Settings["last_estimate"]);
        Assert.Null(sync.Settings["takeoff"]);
        Assert.Empty((JsonArray)sync.Settings["masonry"]!);
        Assert.NotNull(sync.Settings["project"]);
    }

    [Fact]
    public void Round_trip_rebuilds_the_same_takeoff_rows()
    {
        var doc = Pilot();
        var sync = ProjectMapper.ToSync(doc);
        var back = ProjectMapper.FromSync(sync.Settings, sync.Rows);
        foreach (var s in new[] { "columns", "beams", "slabs", "masonry", "plaster", "doors", "windows" })
            if (doc[s] is JsonArray a)
                Assert.True(JsonNode.DeepEquals(a, back[s]), $"section {s} changed in the round trip");
        Assert.True(JsonNode.DeepEquals(doc["project"], back["project"]));
    }

    [Fact]
    public void Contracts_and_bills_keep_their_lines_in_order()
    {
        var doc = new JsonObject
        {
            ["contracts"] = new JsonObject { ["contracts"] = new JsonArray(new JsonObject { ["id"] = "c1", ["name"] = "Civil", ["lines"] = new JsonArray(new JsonObject { ["d"] = "a" }, new JsonObject { ["d"] = "b" }, new JsonObject { ["d"] = "c" }) }) },
            ["accounts"] = new JsonObject { ["bills"] = new JsonArray(), ["transactions"] = new JsonArray(new JsonObject { ["id"] = "t1", ["amt"] = 5 }) },
        };
        var sync = ProjectMapper.ToSync(doc);
        Assert.Contains(sync.Rows, r => r.Section == "contract_lines" && r.RowId == "c1:2");
        Assert.DoesNotContain("lines", sync.Rows.First(r => r.Section == "contracts").Fields.Select(k => k.Key));
        // shuffled input still rebuilds in line order
        var back = ProjectMapper.FromSync(sync.Settings, sync.Rows.AsEnumerable().Reverse());
        var lines = (JsonArray)back["contracts"]!["contracts"]![0]!["lines"]!;
        Assert.Equal(new[] { "a", "b", "c" }, lines.Select(l => l!["d"]!.GetValue<string>()));
        Assert.Equal("t1", back["accounts"]!["transactions"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Deleted_rows_are_dropped_on_rebuild()
    {
        var doc = new JsonObject { ["columns"] = new JsonArray(new JsonObject { ["n"] = "C1" }, new JsonObject { ["n"] = "C2" }) };
        var sync = ProjectMapper.ToSync(doc);
        var rows = sync.Rows.Select((r, i) => i == 0 ? r with { Deleted = true } : r).ToList();
        var back = ProjectMapper.FromSync(sync.Settings, rows);
        Assert.Single((JsonArray)back["columns"]!);
    }

    [Fact]
    public void Wire_shape_marks_deletions()
    {
        var wire = ProjectMapper.ToWire(new[] { new SyncRow("columns", "r1", new JsonObject { ["n"] = "C1" }), new SyncRow("columns", "r2", new JsonObject(), true) });
        Assert.Null(wire[0]!["deleted"]);
        Assert.True(wire[1]!["deleted"]!.GetValue<bool>());
        Assert.Equal("r1", wire[0]!["row_id"]!.GetValue<string>());
    }
}

public class ProjectsApiTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    const string Login = """{"accessToken":"a","refreshToken":"r","expiresAt":4000000000,"userId":"u1","email":"a@x.in"}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":false},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";

    static async Task<(ProjectsApi api, FakeServer s)> Make(FakeServer s)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s);
        await c.SignInAsync("a@x.in", "pw", "test");
        return (new ProjectsApi(c), s);
    }

    [Fact]
    public async Task List_reads_bound_and_unbound_projects()
    {
        var (api, _) = await Make(new FakeServer().On("GET api/aqc/v1/projects", HttpStatusCode.OK,
            """{"projects":[{"projectOfficeId":"p1","ref":"P-1","title":"Lakeview","status":"ACTIVE","city":"Hospet","aqc":{"id":"q1","headSeq":12,"formatVersion":17,"editedByOther":true,"updatedAt":"2026-10-01T00:00:00Z"}},{"projectOfficeId":"p2","ref":"P-2","title":"Hill","aqc":null}]}"""));
        var list = await api.ListAsync();
        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsBound); Assert.True(list[0].Aqc!.EditedByOther); Assert.Equal(12, list[0].Aqc!.HeadSeq);
        Assert.False(list[1].IsBound);
    }

    [Fact]
    public async Task PushOnline_sends_rows_with_minted_ids_and_returns_the_binding()
    {
        var (api, s) = await Make(new FakeServer().On("POST api/aqc/v1/projects", HttpStatusCode.Created, """{"aqcProjectId":"q9","headSeq":3,"rows":2}"""));
        var doc = new JsonObject { ["project"] = new JsonObject { ["name"] = "X" }, ["columns"] = new JsonArray(new JsonObject { ["n"] = "C1" }, new JsonObject { ["n"] = "C2" }) };
        var res = await api.PushOnlineAsync("p1", doc);
        Assert.Equal("q9", res.AqcProjectId);
        var body = JsonNode.Parse(s.Requests.Last(r => r.Method == "POST" && r.Path.EndsWith("/projects")).Body)!;
        Assert.Equal("p1", body["projectOfficeId"]!.GetValue<string>());
        Assert.Equal(17, body["formatVersion"]!.GetValue<int>());
        Assert.Equal(2, ((JsonArray)body["rows"]!).Count);
        Assert.NotNull(doc["columns"]![0]!["_rid"]); // minted into the live document
    }

    [Fact]
    public async Task Pull_pages_until_done_and_keeps_the_first_pages_settings()
    {
        var (api, s) = await Make(new FakeServer()
            .On("GET api/aqc/v1/projects/q1/rows", HttpStatusCode.OK, """{"settings":{"project":{"name":"X"}},"rows":[{"section":"columns","row_id":"a","fields":{"n":"C1"}}],"headSeq":2,"more":true,"nextSince":1}""")
            .On("GET api/aqc/v1/projects/q1/rows", HttpStatusCode.OK, """{"rows":[{"section":"columns","row_id":"b","fields":{"n":"C2"}},{"section":"columns","row_id":"a","fields":{},"deleted":true}],"headSeq":2,"more":false,"nextSince":2}"""));
        var p = await api.PullAsync("q1");
        Assert.Equal(3, p.Rows.Count);
        Assert.Equal("X", p.Settings["project"]!["name"]!.GetValue<string>());
        Assert.Equal(2, p.HeadSeq);
        Assert.Equal(2, s.Requests.Count(r => r.Path.EndsWith("/rows")));
    }
}
