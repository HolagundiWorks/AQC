// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

public class InboxApiTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    const string Login = """{"accessToken":"a","refreshToken":"r","expiresAt":4000000000,"userId":"u1","email":"a@x.in"}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":true},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";

    static async Task<(InboxApi api, FakeServer s)> Make(FakeServer s)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s);
        await c.SignInAsync("a@x.in", "pw", "t");
        return (new InboxApi(c), s);
    }

    const string InboxJson = """
    {"bills":[{"id":"b1","project_id":"p1","package_id":"k1","ref":"RA-1","bill_no":1,"period_start":"2026-09-01","period_end":"2026-09-30","status":"DRAFT","gross_paise":1000000,"retention_pct":5,"gst_pct":18,"tds_pct":2,"cess_pct":1,"gst_tds_pct":2,"advance_recovery_paise":0,"narrative":"Sept","attachment_name":"mb.pdf","submitted_at":"2026-10-01T00:00:00Z","backup":"/api/aqc/v1/files/bill/b1",
      "lines":[{"description":"PCC","unit":"cum","previous_qty":0,"this_qty":12.5,"rate_paise":500000,"amount_paise":6250000,"sort_order":0}]}],
     "submissions":[{"id":"s1","project_id":"p1","kind":"JOINT_MEASUREMENT","subject":"Footings","body":"pls check","status":"OPEN","percent_complete":null,"meeting_at":null,"created_at":"2026-10-02T00:00:00Z"}],
     "now":"2026-10-11T08:00:00.000Z"}
    """;

    [Fact]
    public async Task Inbox_parses_bills_with_lines_and_submissions_and_sends_filters()
    {
        var (api, s) = await Make(new FakeServer().On("GET api/aqc/v1/inbox", HttpStatusCode.OK, InboxJson));
        var inbox = await api.GetAsync("p1", "2026-10-01T00:00:00Z");
        var b = Assert.Single(inbox.Bills);
        Assert.Equal(1000000, b.GrossPaise);
        Assert.Equal(18, b.GstPct);
        Assert.True(b.HasBackup);
        Assert.Equal(12.5, b.Lines[0].ThisQty);
        Assert.Equal("JOINT_MEASUREMENT", Assert.Single(inbox.Submissions).Kind);
        Assert.Equal("2026-10-11T08:00:00.000Z", inbox.Now);
        var q = s.Requests.Last(r => r.Path.EndsWith("/inbox"));
        Assert.Equal("GET", q.Method);
    }

    [Fact]
    public async Task Certify_sends_paise_deductions_and_returns_the_version()
    {
        var (api, s) = await Make(new FakeServer().On("POST api/aqc/v1/bills/b1/certify", HttpStatusCode.OK, """{"certified":true,"version":2}"""));
        var cert = new Certification(RetentionPaise: 50000, GstPaise: 180000, TdsPaise: 20000);
        var r = await api.CertifyAsync("b1", cert, "hash-12345678", "11111111-1111-1111-1111-111111111111");
        Assert.True(r.Certified); Assert.Equal(2, r.Version);
        var body = JsonNode.Parse(s.Requests.Last(x => x.Path.EndsWith("/certify")).Body)!;
        Assert.Equal(50000, body["retentionPaise"]!.GetValue<long>());
        Assert.Equal("hash-12345678", body["contentHash"]!.GetValue<string>());
        Assert.Equal(1000000 + 180000 - 50000 - 20000, cert.NetPaise(1000000));
    }

    [Fact]
    public async Task Certify_twice_is_already_certified()
    {
        var (api, _) = await Make(new FakeServer().On("POST api/aqc/v1/bills/b1/certify", HttpStatusCode.Conflict, """{"error":{"code":"already_certified","message":"done"}}"""));
        var e = await Assert.ThrowsAsync<ConnectException>(() => api.CertifyAsync("b1", new Certification(0)));
        Assert.Equal("already_certified", e.Code);
        Assert.Equal(409, e.Status);
    }

    [Fact]
    public async Task Negative_deductions_are_refused_locally()
    {
        var (api, s) = await Make(new FakeServer());
        await Assert.ThrowsAsync<ArgumentException>(() => api.CertifyAsync("b1", new Certification(-1)));
        Assert.DoesNotContain(s.Requests, r => r.Path.EndsWith("/certify"));
    }

    [Fact]
    public async Task Backup_download_uses_the_signed_url_without_the_bearer()
    {
        var (api, s) = await Make(new FakeServer().On("GET api/aqc/v1/files/bill/b1", HttpStatusCode.OK, """{"url":"https://storage.example/signed?t=1","expiresInSeconds":300}""").On("GET signed", HttpStatusCode.OK, "PDFBYTES"));
        var bytes = await api.DownloadBackupAsync("b1");
        Assert.Equal("PDFBYTES", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Null(s.Requests.Last(r => r.Path == "signed").Auth);
    }
}
