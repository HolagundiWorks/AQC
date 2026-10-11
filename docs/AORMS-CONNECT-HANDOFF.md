# AORMS Connect — handoff for local testing

Branch: `claude/aqc-connect-a1` · contract `2026-10-aqc-1` · written 2026-10-11 by a cloud session (Linux, no Windows/WinUI).
Server side + full spec live in the AORMS repo (`HolagundiWorks/aorms`): `docs/esti/AQC-CONNECT-PLAN.md` (decisions) and
`docs/esti/AQC-CLIENT-SPEC.md` (the wire contract and build order A1–A7). Read the spec first; this file only says what exists and what to verify.

## What is built (platform-neutral, `BBSDesktop/Aorms.Connect`, net8.0)

| Step | Files | What |
|---|---|---|
| A1 | `ConnectClient.cs`, `Models.cs`, `ConnectException.cs`, `TokenStore.cs` | config, sign-in, single session, bearer + `x-aqc-session`, token refresh (401 → refresh once), `RestoreAsync` (never steals a session), take-over, events (SessionReplaced / NotConnected / SignedOut) |
| A2 | `ProjectMapper.cs`, `ProjectsApi.cs` | `.bbsproj` v17 ⇄ rows + settings skeleton, `_rid` minting; list online projects, seed, push online, pull (paged) |
| A4 | `VersionsApi.cs` | version kinds + capability check, rupees→paise (half away from zero), signed-URL file upload (3 steps, SHA-256 as content hash), summaries for estimate / IPC / final account |
| A5 | `InboxApi.cs` | contractor inbox (bills with lines, submissions, `since`), bill backup download, certify (paise deductions, optional IPC version), net-payable helper |
| A3 | `ProjectSync.cs` | edit lease (renew 60 s / retry 30 s), persistent `Outbox`, 2 s debounce, 500-row batches, 423 re-acquire, 400 row isolation, offline back-off, `session_replaced` stop, catch-up that asks for review when local changes exist |

Tests: `BBSDesktop/Aorms.Connect.Tests` (54 xunit, fake HTTP). Live walkthrough: `BBSDesktop/Aorms.Connect.Smoke`.

## NOT built / NOT verified (this is the local agent's job)

1. **No WinUI wiring.** `BBSApp` still uses the old `Aorms.Bridge` (targets the removed hub). Nothing references `Aorms.Connect` yet.
2. **`IConnectTokenStore` for Windows Credential Manager** is not written (only `InMemoryTokenStore`). Refresh token + sessionId must never go in `firm.db` or a file.
3. **`IOutboxStore` file-backed implementation** not written (only in-memory). Needed so unsent edits survive restarts.
4. **Home screen** (sign-in first, *Continue without signing in* → Community; Online projects list, then Local; open from seed; *Push online*). Spec §2, §3, §9.
5. **ProjectStore integration**: call `Outbox.Upsert/Delete` + `NoteChange()` from `ProjectStore.Notify()`; drive `ProjectSync.TickAsync()` from a ~1 s UI timer; mint `_rid` on take-off rows (use `ProjectMapper.ToSync`).
6. **A6–A7 not started**: shared rate books, hardening (offline for days, lease expiry mid-edit, 10k-row project).
7. Old `Aorms.Bridge`, `Aorms.Bridge.Smoke`, and the Connect/licence code are still in the tree; remove once nothing references them.

## Run the tests (no AORMS needed)

```
cd BBSDesktop
dotnet test Aorms.Connect.Tests          # expect 54 passed
```

## Run the live walkthrough

Needs an AORMS deployment whose studio is on a connected plan (STUDIO/PROFESSIONAL/ENTERPRISE, not expired). Demo studio `AORMS-S-R0QV`
should be PROFESSIONAL; demo password `DemoAORMS2026!`.

```
export AORMS_URL=https://aorms.in
export AORMS_EMAIL=aditi.rao@aorms.in          # OWNER (writes)
export AORMS_PASSWORD='DemoAORMS2026!'
export AORMS_VIEWER_EMAIL=demo@aorms.in        # VIEWER (read-only)
export AORMS_VIEWER_PASSWORD='DemoAORMS2026!'
export AORMS_PILOT=<path>/pilot-sample.bbsproj  # from aorms: web/tests/fixtures/aqc/pilot-sample.bbsproj (optional; enables push/edit/delta)
dotnet run --project BBSDesktop/Aorms.Connect.Smoke
```

Expected: every line `OK`, ending `All checks passed.` It covers sign-in, health, restore, second-computer takeover (`session_replaced`),
restore-doesn't-steal, take-back, project list, seed, push 43 rows, pull identical, bind-once (400), lease, one-row edit, second client sees
the delta, viewer can't lease, wrong password, sign-out.

Caveats: pushing binds an unbound demo project **once** (re-runs skip the push with `SKIP push — every project is already bound`; unbind by
resetting demo data on the AORMS side). Against the **deployed** site this was NOT run — only against a local dev server with a temporary
plan override. First deployed run must confirm: Platform licence lookup (entitlement), and Identity-password sign-in (`not_linked` for an
unlinked account).

## Things only a real Windows run can settle

- Credential Manager round trip (store/load/clear) and that sign-out clears it.
- Token refresh across a real >1 h session; clock skew behaviour (60 s skew in `ConnectClient`).
- Lease contention: two **writer** accounts on one project (only one writer account existed in cloud tests) — second must open read-only with "Edited by <name>", retry every 30 s.
- Lease expiry mid-edit (let it lapse > 120 s offline, then edit): expect 423 → re-acquire or read-only with rows kept.
- Offline for days: edits accumulate in the outbox, restart the app, reconnect, replay in order.
- **File upload (A4) was never run end to end**: signed upload URLs could not be exercised in cloud (placeholder storage keys; the smoke prints `SKIP file upload`). On the deployed site, publish a PDF estimate and confirm it appears in AORMS `/aqc` with its total, and that the PUT to the signed URL succeeds with a raw body + Content-Type (Supabase storage may want multipart — if it 4xxs, that is the first thing to adjust in `ConnectClient.PutSignedAsync`).
- `POST /versions` answers `{ version: <number> }` (a version number, not a GUID).

- **Certify was not run live** by the smoke (it changes data): set `AORMS_CERTIFY_BILL=<bill id>` with a `cost:approve` account on a throwaway DRAFT bill to exercise certify + the 409 on a second try. Bill backup download is also unexercised (needs storage).

## Known server notes

- Row `fields` accept nested JSON (e.g. schedule `links[]`), ≤ 64 KB/row; scalar-only was a bug fixed in AORMS before this branch.
- Error codes: invalid_credentials, not_linked, pending, rate_limited (retryAfterSeconds), not_connected (reason), session_replaced (409), lease_required (423), forbidden (403).
- Money on the wire is integer paise; AQC rupee doubles convert with round-half-away-from-zero (spec §6).
