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

a.SignOut();
Check("sign out clears the store", storeA.Load() is null && !a.IsConnected);
Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
