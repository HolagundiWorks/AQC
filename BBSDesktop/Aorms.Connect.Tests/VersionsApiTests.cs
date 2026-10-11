// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aorms.Connect;
using Xunit;

namespace Aorms.Connect.Tests;

public class PaiseTests
{
    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(0.005, 1)]
    [InlineData(-0.005, -1)]
    [InlineData(1234567.891, 123456789)]
    [InlineData(0.0, 0)]
    public void Rupees_round_half_away_from_zero(double rupees, long paise) => Assert.Equal(paise, Paise.FromRupees(rupees));

    [Fact] public void Decimal_overload_agrees() => Assert.Equal(1L, Paise.FromRupees(0.005m));
}

public class VersionsApiTests
{
    const string Cfg = """{"contract":"2026-10-aqc-1","supabaseUrl":"https://hub.example","supabaseAnonKey":"k","limits":{"rowBatch":500,"maxRowsPerProject":20000,"maxFileBytes":26214400,"leaseSeconds":120}}""";
    const string Login = """{"accessToken":"a","refreshToken":"r","expiresAt":4000000000,"userId":"u1","email":"a@x.in"}""";
    const string Sess = """{"contract":"2026-10-aqc-1","sessionId":"s1","account":{"id":"u1","email":"a@x.in"},"studio":{"firmId":"f1","name":"A","publicId":"S1"},"role":"OWNER","capabilities":{"write":true,"fees:manage":true,"cost:approve":false},"entitlement":{"connected":true,"plan":"PROFESSIONAL","expiresAt":null}}""";

    static async Task<(VersionsApi api, FakeServer s, SessionInfo session)> Make(FakeServer s)
    {
        s.On("GET api/aqc/v1/config", HttpStatusCode.OK, Cfg).On("POST api/aqc/v1/auth/login", HttpStatusCode.OK, Login).On("POST api/aqc/v1/session", HttpStatusCode.OK, Sess);
        var c = new ConnectClient(new Uri("https://aorms.example/"), new InMemoryTokenStore(), s);
        var session = await c.SignInAsync("a@x.in", "pw", "t");
        return (new VersionsApi(c), s, session);
    }

    [Fact]
    public void Kinds_map_to_the_capability_the_server_enforces()
    {
        Assert.Equal("fees:manage", VersionKinds.RequiredCapability("estimate"));
        Assert.Equal("cost:approve", VersionKinds.RequiredCapability("ipc"));
        Assert.Equal("write", VersionKinds.RequiredCapability("bbs"));
        Assert.Throws<ArgumentOutOfRangeException>(() => VersionKinds.RequiredCapability("nope"));
    }

    [Fact]
    public async Task Owner_session_can_add_estimates_but_not_ipcs()
    {
        var (_, _, session) = await Make(new FakeServer());
        Assert.True(VersionKinds.CanAdd(session, "estimate"));
        Assert.False(VersionKinds.CanAdd(session, "ipc"));
    }

    [Fact]
    public async Task PublishFile_runs_the_three_steps_with_the_sha_as_content_hash()
    {
        var bytes = Encoding.UTF8.GetBytes("%PDF-1.4 fake");
        var sha = VersionsApi.Sha256Hex(bytes);
        var (api, s, _) = await Make(new FakeServer()
            .On("POST api/aqc/v1/projects/q1/files", HttpStatusCode.Created, """{"storageKey":"f1/q1/estimate/KEY.pdf","uploadUrl":"https://storage.example/upload?token=t"}""")
            .On("PUT upload", HttpStatusCode.OK, "{}")
            .On("POST api/aqc/v1/projects/q1/versions", HttpStatusCode.Created, """{"version":"v1"}"""));
        var v = await api.PublishFileAsync("q1", "estimate", bytes, "application/pdf", VersionsApi.EstimateSummary(1234567.891, "Rev A"));
        Assert.Equal("v1", v);

        var init = JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/files")).Body)!;
        Assert.Equal(sha, init["sha256"]!.GetValue<string>());
        Assert.Equal(bytes.Length, init["sizeBytes"]!.GetValue<int>());
        var put = s.Requests.Single(r => r.Method == "PUT");
        Assert.Null(put.Auth); // the signed URL is the credential — no AORMS bearer leaks to storage
        var add = JsonNode.Parse(s.Requests.Single(r => r.Path.EndsWith("/versions")).Body)!;
        Assert.Equal(sha, add["contentHash"]!.GetValue<string>());
        Assert.Equal("f1/q1/estimate/KEY.pdf", add["storageKey"]!.GetValue<string>());
        Assert.Equal(123456789, add["summary"]!["grandTotalPaise"]!.GetValue<long>());
        Assert.Equal(3, s.Requests.Count(r => r.Path.Contains("projects/q1") || r.Method == "PUT"));
    }

    [Fact]
    public async Task Version_answer_may_be_a_number_as_the_server_sends_it()
    {
        var (api, _, _) = await Make(new FakeServer().On("POST api/aqc/v1/projects/q1/versions", HttpStatusCode.Created, """{"version":3}"""));
        Assert.Equal("3", await api.AddAsync("q1", "boq", "hash-12345678", new JsonObject()));
    }

    [Fact]
    public async Task Bad_files_are_refused_before_any_request()
    {
        var (api, s, _) = await Make(new FakeServer());
        await Assert.ThrowsAsync<ArgumentException>(() => api.UploadAsync("q1", "estimate", new byte[] { 1 }, "text/html"));
        await Assert.ThrowsAsync<ArgumentException>(() => api.UploadAsync("q1", "estimate", Array.Empty<byte>(), "application/pdf"));
        await Assert.ThrowsAsync<ArgumentException>(() => api.UploadAsync("q1", "estimate", new byte[VersionsApi.MaxFileBytes + 1], "application/pdf"));
        Assert.DoesNotContain(s.Requests, r => r.Path.EndsWith("/files"));
    }

    [Fact]
    public async Task Storage_unavailable_surfaces_as_a_retryable_error()
    {
        var (api, _, _) = await Make(new FakeServer().On("POST api/aqc/v1/projects/q1/files", HttpStatusCode.ServiceUnavailable, """{"error":{"code":"storage_unavailable","message":"later"}}"""));
        var e = await Assert.ThrowsAsync<ConnectException>(() => api.UploadAsync("q1", "estimate", new byte[] { 1, 2 }, "application/pdf"));
        Assert.Equal("storage_unavailable", e.Code);
    }

    [Fact]
    public async Task Failed_put_is_an_upload_error_not_a_version()
    {
        var (api, s, _) = await Make(new FakeServer()
            .On("POST api/aqc/v1/projects/q1/files", HttpStatusCode.Created, """{"storageKey":"k","uploadUrl":"https://storage.example/upload"}""")
            .On("PUT upload", HttpStatusCode.Forbidden, "{}"));
        var e = await Assert.ThrowsAsync<ConnectException>(() => api.PublishFileAsync("q1", "boq", new byte[] { 1 }, "application/pdf", new JsonObject()));
        Assert.Equal("upload_failed", e.Code);
        Assert.DoesNotContain(s.Requests, r => r.Path.EndsWith("/versions"));
    }

    [Fact]
    public void Summaries_use_paise_and_the_keys_aorms_reads()
    {
        var ipc = VersionsApi.IpcSummary("bill-1", 1000.5, 900.25);
        Assert.Equal("bill-1", ipc["billId"]!.GetValue<string>());
        Assert.Equal(100050, ipc["grossPaise"]!.GetValue<long>());
        Assert.Equal(90025, ipc["netPaise"]!.GetValue<long>());
        Assert.Equal(500000, VersionsApi.FinalAccountSummary(5000)["finalValuePaise"]!.GetValue<long>());
        Assert.Null(VersionsApi.EstimateSummary(1)["label"]);
    }
}
