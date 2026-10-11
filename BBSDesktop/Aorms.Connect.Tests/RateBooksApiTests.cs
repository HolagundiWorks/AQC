// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

public class RateBooksApiTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    const string Login = """{"accessToken":"a","refreshToken":"r","expiresAt":4000000000,"userId":"u1","email":"a@x.in"}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":false},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";

    static async Task<(RateBooksApi api, FakeServer s)> Make(FakeServer s)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s);
        await c.SignInAsync("a@x.in", "pw", "t");
        return (new RateBooksApi(c), s);
    }

    [Fact]
    public async Task List_and_get_keep_rupee_rates_untouched_and_item_order()
    {
        var (api, _) = await Make(new FakeServer()
            .On("GET api/aqc/v1/rate-books", HttpStatusCode.OK, """{"versions":[{"id":"v1","client_id":"c1","name":"Default","notes":"","revision":3,"item_count":2,"is_active":true,"updated_at":"2026-10-01T00:00:00Z"}]}""")
            .On("GET api/aqc/v1/rate-books/v1", HttpStatusCode.OK, """{"version":{"id":"v1","client_id":"c1","name":"Default","notes":"","revision":3,"item_count":2,"is_active":true},"items":[{"code":"RCC-M25","category":"Concrete","description":"RCC M25","unit":"cum","rate":9800.5},{"code":"PCC-148","category":"Concrete","description":"PCC","unit":"cum","rate":5200}]}"""));
        var list = await api.ListAsync();
        Assert.True(list[0].IsActive); Assert.Equal(3, list[0].Revision);
        var d = await api.GetAsync("v1");
        Assert.Equal(new[] { "RCC-M25", "PCC-148" }, d.Items.Select(i => i.Code));
        Assert.Equal(9800.5, d.Items[0].Rate);
    }

    [Fact]
    public async Task Publish_sends_the_book_and_reports_unchanged_republish()
    {
        var (api, s) = await Make(new FakeServer()
            .On("POST api/aqc/v1/rate-books", HttpStatusCode.Created, """{"versionId":"v9","revision":1,"changed":true}""")
            .On("POST api/aqc/v1/rate-books", HttpStatusCode.OK, """{"versionId":"v9","revision":1,"changed":false}"""));
        var items = new[] { new RateItem("A", "Cat", "Desc", "nos", 12.5) };
        var first = await api.PublishAsync("c1", "Default", "", items, activate: true);
        Assert.True(first.Changed);
        var again = await api.PublishAsync("c1", "Default", "", items, activate: true);
        Assert.False(again.Changed);
        var body = JsonNode.Parse(s.Requests.First(r => r.Path.EndsWith("/rate-books") && r.Method == "POST").Body)!;
        Assert.Equal("c1", body["clientId"]!.GetValue<string>());
        Assert.True(body["activate"]!.GetValue<bool>());
        Assert.Equal(12.5, body["items"]![0]!["rate"]!.GetValue<double>());
    }

    [Fact]
    public async Task Duplicate_codes_are_refused_before_any_request()
    {
        var (api, s) = await Make(new FakeServer());
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => api.PublishAsync("c1", "D", "", new[] { new RateItem("A", "", "", "", 1), new RateItem("A", "", "", "", 2) }, false));
        Assert.Contains("A", ex.Message);
        Assert.DoesNotContain(s.Requests, r => r.Path.EndsWith("/rate-books"));
    }

    [Fact]
    public async Task Activate_posts_to_the_activate_route()
    {
        var (api, s) = await Make(new FakeServer().On("POST api/aqc/v1/rate-books/v1/activate", HttpStatusCode.OK, """{"active":"v1"}"""));
        await api.ActivateAsync("v1");
        Assert.Contains(s.Requests, r => r.Path.EndsWith("/v1/activate"));
    }

    [Fact]
    public void Plan_matches_on_client_id_and_never_overwrites_silently()
    {
        OnlineRateBook O(string cid, int rev) => new("id-" + cid, cid, cid, "", rev, 1, false, null);
        var plan = RateBooksApi.Plan(
            new[] { new LocalRateBook("same", "same", 2), new LocalRateBook("behind", "b", 1), new LocalRateBook("ahead", "a", 5), new LocalRateBook("mine", "m", 1) },
            new[] { O("same", 2), O("behind", 4), O("ahead", 3), O("theirs", 1) });
        RateBookAction Act(string cid) => plan.Single(p => p.ClientId == cid).Action;
        Assert.Equal(RateBookAction.InSync, Act("same"));
        Assert.Equal(RateBookAction.OfferUpdateFromOnline, Act("behind"));
        Assert.Equal(RateBookAction.LocalIsNewer, Act("ahead"));
        Assert.Equal(RateBookAction.OnlyLocal, Act("mine"));
        Assert.Equal(RateBookAction.OnlyOnline, Act("theirs"));
        Assert.Equal(5, plan.Count);
    }
}
