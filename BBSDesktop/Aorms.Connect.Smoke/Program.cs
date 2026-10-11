// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

// Live walk-through of AQC connected mode against an AORMS deployment (docs/esti/AQC-CLIENT-SPEC.md §2).
//   AORMS_URL=https://aorms.in AORMS_EMAIL=... AORMS_PASSWORD=... dotnet run --project Aorms.Connect.Smoke
// Optional: AORMS_VIEWER_EMAIL / AORMS_VIEWER_PASSWORD to also check a read-only role.
using Aorms.Connect;

string url = Environment.GetEnvironmentVariable("AORMS_URL") ?? "http://localhost:3000";
string email = Environment.GetEnvironmentVariable("AORMS_EMAIL") ?? "";
string password = Environment.GetEnvironmentVariable("AORMS_PASSWORD") ?? "";
if (email == "" || password == "") { Console.WriteLine("Set AORMS_EMAIL and AORMS_PASSWORD."); return 2; }

int failures = 0;
void Check(string name, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name}{(detail == "" ? "" : " — " + detail)}"); if (!ok) failures++; }

// Computer A signs in.
var storeA = new InMemoryTokenStore();
using var a = new ConnectClient(new Uri(url), storeA);
var s1 = await a.SignInAsync(email, password, "Smoke A");
Check("sign in (A)", a.IsConnected, $"{s1.Studio.Name} · {s1.Role} · plan {s1.Entitlement.Plan} · write={s1.CanWrite}");
Check("refresh token stored", storeA.Load()?.RefreshToken is { Length: > 10 });
var health = await a.RefreshSessionAsync();
Check("session health check", health.SessionId == s1.SessionId);

// Restore on a fresh process with the stored credentials.
using var a2 = new ConnectClient(new Uri(url), storeA);
Check("restore from stored sign-in", await a2.RestoreAsync() == RestoreOutcome.Restored, a2.Session?.Account.Email ?? "");

// Computer B signs in as the same user: A's session is replaced.
var storeB = new InMemoryTokenStore();
using var b = new ConnectClient(new Uri(url), storeB);
var s2 = await b.SignInAsync(email, password, "Smoke B");
Check("second computer gets a new session", s2.SessionId != s1.SessionId);
bool replacedRaised = false;
a.SessionReplaced += () => replacedRaised = true;
try { await a.RefreshSessionAsync(); Check("old session is rejected", false, "call succeeded"); }
catch (ConnectException e) { Check("old session is rejected", e.IsSessionReplaced && replacedRaised, e.Code); }
Check("restore never takes the session over", await a2.RestoreAsync() == RestoreOutcome.SessionReplaced);
var s3 = await a.TakeOverSessionAsync("Smoke A (took over)");
Check("'Sign in here' takes it back", a.IsConnected && s3.SessionId != s2.SessionId);

// Online projects: list, open from seed, push the pilot sample (optional AORMS_PILOT=path to a .bbsproj), pull it back.
var api = new ProjectsApi(a);
var online = await api.ListAsync();
Check("online project list", online.Count > 0, $"{online.Count} projects, {online.Count(p => p.IsBound)} bound");
var unbound = online.FirstOrDefault(p => !p.IsBound);
if (unbound is not null)
{
    var seed = await api.GetSeedAsync(unbound.ProjectOfficeId);
    Check("seed fills title block and parties", seed["project"]?["name"]?.GetValue<string>() is { Length: > 0 } && seed["parties"]?["pm"] is not null, unbound.Title);
    if (Environment.GetEnvironmentVariable("AORMS_PILOT") is { Length: > 0 } pilot && s3.CanWrite)
    {
        var doc = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pilot))!;
        var pushed = await api.PushOnlineAsync(unbound.ProjectOfficeId, doc);
        Check("push local project online", pushed.Rows >= 20, $"{pushed.Rows} rows, head {pushed.HeadSeq}");
        var pulled = await api.PullAsync(pushed.AqcProjectId);
        var rebuilt = ProjectMapper.FromSync(pulled.Settings, pulled.Rows);
        Check("pull rebuilds the same take-off rows", System.Text.Json.Nodes.JsonNode.DeepEquals(doc["masonry"], rebuilt["masonry"]) && System.Text.Json.Nodes.JsonNode.DeepEquals(doc["columns"], rebuilt["columns"]));
        try { await api.PushOnlineAsync(unbound.ProjectOfficeId, doc); Check("a project binds only once", false); }
        catch (ConnectException e) { Check("a project binds only once", e.Status == 400, e.Message); }

        // A3: lease, edit one row, flush, and a second user sees just that change.
        var sync = new ProjectSync(a, pushed.AqcProjectId, pushed.HeadSeq, new Outbox());
        Check("owner takes the edit lease", await sync.AcquireLeaseAsync());
        var mas = (System.Text.Json.Nodes.JsonObject)((System.Text.Json.Nodes.JsonArray)doc["masonry"]!)[0]!.DeepClone();
        mas["length"] = "19000";
        sync.Outbox.Upsert("masonry", mas["_rid"]!.GetValue<string>(), mas); sync.NoteChange();
        var flushed = await sync.FlushAsync();
        Check("edit flushed", flushed.Sent == 1 && sync.State == SyncState.Synced && sync.HeadSeq == pushed.HeadSeq + 1, $"head {sync.HeadSeq}");
        // A4: publish an estimate version (idempotent), then try a file upload.
        var versions = new VersionsApi(a);
        if (VersionKinds.CanAdd(s3, "estimate"))
        {
            var summary = VersionsApi.EstimateSummary(1234567.891, "Smoke");
            var hash = "smoke-" + Guid.NewGuid().ToString("N");
            var v1 = await versions.AddAsync(pushed.AqcProjectId, "estimate", hash, summary);
            var v2 = await versions.AddAsync(pushed.AqcProjectId, "estimate", hash, summary);
            Check("estimate version published, same hash is a no-op", v1 == v2, v1);
            try
            {
                var f = await versions.PublishFileAsync(pushed.AqcProjectId, "estimate", System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 smoke " + hash), "application/pdf", summary);
                Check("estimate PDF uploaded and published", f.Length > 0);
            }
            catch (ConnectException e) when (e.Code == "storage_unavailable") { Console.WriteLine("SKIP file upload — storage not configured on this server (" + e.Code + ")"); }
        }
        string? vE = Environment.GetEnvironmentVariable("AORMS_VIEWER_EMAIL"), vP = Environment.GetEnvironmentVariable("AORMS_VIEWER_PASSWORD");
        if (vE is not null && vP is not null)
        {
            using var other = new ConnectClient(new Uri(url), new InMemoryTokenStore());
            await other.SignInAsync(vE, vP, "Smoke second client");
            var theirs = new ProjectSync(other, pushed.AqcProjectId, pushed.HeadSeq, new Outbox());
            var delta = await theirs.CatchUpAsync();
            Check("second client sees only the delta", delta.Rows.Count == 1 && delta.Rows[0].Section == "masonry" && delta.Rows[0].Fields["length"]?.ToString() == "19000" && theirs.HeadSeq == sync.HeadSeq);
            Check("viewer cannot take the lease", !await theirs.AcquireLeaseAsync() && theirs.State == SyncState.ReadOnlyRole);
        }
    }
}
else Console.WriteLine("SKIP push — every project is already bound");

// A7: chunked push of a big synthetic project (AORMS_BIG=<rows>, e.g. 1300). Binds one more unbound project.
if (int.TryParse(Environment.GetEnvironmentVariable("AORMS_BIG"), out var bigN) && bigN > 0 && s3.CanWrite)
{
    var target = (await api.ListAsync()).FirstOrDefault(p => !p.IsBound);
    if (target is null) Console.WriteLine("SKIP big push — every project is already bound");
    else
    {
        var bigRows = new System.Text.Json.Nodes.JsonArray();
        for (var i = 0; i < bigN; i++) bigRows.Add(new System.Text.Json.Nodes.JsonObject { ["n"] = "C" + i, ["len"] = i });
        var big = new System.Text.Json.Nodes.JsonObject { ["project"] = new System.Text.Json.Nodes.JsonObject { ["name"] = "Smoke big" }, ["columns"] = bigRows };
        var res = await api.PushOnlineAsync(target.ProjectOfficeId, big);
        Check("big project pushed in batches", res.Rows == bigN && res.Pending.Count == 0, $"{res.Rows} rows, head {res.HeadSeq}");
        var back = await api.PullAsync(res.AqcProjectId);
        Check("big project pulls back complete", back.Rows.Count == bigN, $"{back.Rows.Count} rows");
    }
}

if (int.TryParse(Environment.GetEnvironmentVariable("AORMS_BIG"), out var bookN) && bookN > 0 && s3.Can("fees:manage"))
{
    var bigItems = Enumerable.Range(0, bookN).Select(i => new RateItem("BIG-" + i, "Smoke", "Item " + i, "nos", i + 0.25)).ToArray();
    var bp = await new RateBooksApi(a).PublishAsync("smoke-big-book", "Smoke big book", "", bigItems, activate: false);
    var bg = await new RateBooksApi(a).GetAsync(bp.VersionId);
    Check("big rate book reads back complete", bg.Items.Count == bookN && bg.Items[^1].Code == "BIG-" + (bookN - 1), $"{bg.Items.Count} items");
}

// A5: contractor inbox (read-only). Set AORMS_CERTIFY_BILL=<bill id> to also certify that bill (changes data).
var inboxApi = new InboxApi(a);
var inbox = await inboxApi.GetAsync();
Check("contractor inbox read", inbox.Now.Length > 0, $"{inbox.Bills.Count} bills, {inbox.Submissions.Count} submissions");
var sample = inbox.Bills.FirstOrDefault(b => b.Lines.Count > 0);
if (sample is not null) Check("bill lines carry paise amounts", sample.Lines.All(l => l.AmountPaise >= 0) && sample.GrossPaise > 0, $"{sample.Ref} gross {sample.GrossPaise}");
if (Environment.GetEnvironmentVariable("AORMS_CERTIFY_BILL") is { Length: > 0 } billId && s3.Can("cost:approve"))
{
    var bill = inbox.Bills.First(b => b.Id == billId);
    var cert = new Certification((long)(bill.GrossPaise * bill.RetentionPct / 100), (long)(bill.GrossPaise * bill.GstPct / 100));
    var res = await inboxApi.CertifyAsync(billId, cert);
    Check("bill certified", res.Certified, $"net {cert.NetPaise(bill.GrossPaise)}");
    try { await inboxApi.CertifyAsync(billId, cert); Check("certify twice refused", false); }
    catch (ConnectException e) { Check("certify twice refused", e.Code == "already_certified", e.Code); }
}

// A6: shared rate books. Publishes one small, fixed "smoke-book" (not activated); re-runs are unchanged no-ops.
var books = new RateBooksApi(a);
var onlineBooks = await books.ListAsync();
Check("rate books listed", true, $"{onlineBooks.Count} books, active: {onlineBooks.FirstOrDefault(b => b.IsActive)?.Name ?? "none"}");
if (s3.Can("fees:manage"))
{
    var items = new[] { new RateItem("SMK-1", "Smoke", "Smoke item", "nos", 1234.5), new RateItem("SMK-2", "Smoke", "Second", "cum", 99) };
    var pub1 = await books.PublishAsync("smoke-book", "Smoke book", "from Aorms.Connect.Smoke", items, activate: false);
    var pub2 = await books.PublishAsync("smoke-book", "Smoke book", "from Aorms.Connect.Smoke", items.Reverse().ToArray(), activate: false);
    Check("rate book publish is idempotent (order-independent)", !pub2.Changed && pub1.VersionId == pub2.VersionId, $"rev {pub2.Revision}");
    var got = await books.GetAsync(pub1.VersionId);
    Check("rate book round-trips rupee values", got.Items.Any(i => i.Code == "SMK-1" && i.Rate == 1234.5), $"{got.Items.Count} items");
    var plan = RateBooksApi.Plan(new[] { new LocalRateBook("smoke-book", "Smoke book", pub1.Revision) }, await books.ListAsync());
    Check("sign-in merge plan sees it in sync", plan.Any(p => p.ClientId == "smoke-book" && p.Action == RateBookAction.InSync));
}

// Wrong password.
using var bad = new ConnectClient(new Uri(url), new InMemoryTokenStore());
try { await bad.SignInAsync(email, "definitely-wrong", "x"); Check("wrong password refused", false); }
catch (ConnectException e) { Check("wrong password refused", e.Code is "invalid_credentials" or "unavailable", e.Code); }

// Read-only role.
string? vEmail = Environment.GetEnvironmentVariable("AORMS_VIEWER_EMAIL"), vPw = Environment.GetEnvironmentVariable("AORMS_VIEWER_PASSWORD");
if (vEmail is not null && vPw is not null)
{
    using var v = new ConnectClient(new Uri(url), new InMemoryTokenStore());
    var vs = await v.SignInAsync(vEmail, vPw, "Smoke viewer");
    Check("viewer is read-only", !vs.CanWrite, $"role {vs.Role}");
}

// Suite (runs last: signing in as app 'aqc' again replaces the first AQC session, by design): AAD and AQC side by side under one sign-in (shared store), each with its own session; CAD publishes a take-off, AQC lists it.
{
    var shared = new InMemorySharedSignInStore();
    using var aqcApp = new ConnectClient(new Uri(url), new InMemoryTokenStore(), shared: shared, app: "aqc");
    using var aadApp = new ConnectClient(new Uri(url), new InMemoryTokenStore(), shared: shared, app: "aad");
    var sq = await aqcApp.SignInAsync(email, password, "Smoke AQC");
    var sd = await aadApp.SignInAsync(email, password, "Smoke AAD");
    Check("AAD and AQC hold separate sessions", sq.SessionId != sd.SessionId && shared.Load() is not null);
    var okA = (await aqcApp.RefreshSessionAsync()).SessionId == sq.SessionId;
    var okB = (await aadApp.RefreshSessionAsync()).SessionId == sd.SessionId;
    Check("both stay connected (neither replaced the other)", okA && okB);
    var bound = (await new ProjectsApi(aadApp).ListAsync()).FirstOrDefault(p => p.IsBound);
    if (bound is not null && sd.CanWrite)
    {
        var bridge = new CadBridgeApi(aadApp);
        var hash = "smoke-cad-" + Guid.NewGuid().ToString("N");
        var vid = await new VersionsApi(aadApp).AddAsync(bound.Aqc!.Id, VersionKinds.CadBridge, hash, new System.Text.Json.Nodes.JsonObject { ["drawing_id"] = "smoke" });
        var listed = await new CadBridgeApi(aqcApp).ListAsync(bound.Aqc!.Id);
        Check("AQC lists the CAD take-off AAD published", listed.Any(v => v.Version.ToString() == vid), $"{listed.Count} versions");
        Check("cad_bridge without a file yields no download", await bridge.DownloadLatestAsync(bound.Aqc!.Id) is null || listed.Any(v => v.HasFile));
    }
    aqcApp.SignOut();
    Check("signing out of one app clears the shared login", shared.Load() is null);
}

a.SignOut();
Check("sign out clears the store", storeA.Load() is null && !a.IsConnected);
Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
