# Branch handoff (2026-10-11)

All remote branches were reviewed and merged into `main`. Nothing was deleted; the branches stay as history.

| Branch | State | What it holds |
|---|---|---|
| `claude/aqc-connect-a1` | Merged (two commits, A1 + A2) | `Aorms.Connect` library (sign-in, single session, token refresh, restore), `ProjectMapper` (.bbsproj <-> rows, `_rid` minting), `ProjectsApi` (list, seed, push online, pull), smoke app and 24 xUnit tests (`dotnet test BBSDesktop/Aorms.Connect.Tests`: 24 passed). |
| `feat/aorms-bridge-smoke` | Merged; `main`'s versions kept | Older `Aorms.Bridge.Smoke` and spike doc. `main` already had newer versions (licence activation, hub port 4000), so conflicts in `Program.cs` and `docs/AORMS-BRIDGE-SPIKE.md` were resolved to `main`. Nothing unique was lost. |
| `claude/ai-entry-points-kgcmip` | Already in `main` | AI entry points, DPAPI-encrypted API key. |
| `claude/roadmap-continuation-l8vv8e` | Already in `main` | Roadmap phases up to row edit/delete via the assistant. |

## Not verified
- The WinUI app and C++ engine were not rebuilt after this merge; only `Aorms.Connect.Tests` was run.
- `Aorms.Connect` is not yet referenced by `BBSApp`; wiring it into the shells is the next step.

## Next
1. Wire `Aorms.Connect` into the Estimation/BBS/PM shells.
2. Build the hcwCAD-KIT importer ("Import from CAD") per `hcwCAD-KIT/docs/bridge/AQC-IMPORTER-SPEC.md`; take-off happens in CAD.
3. Then retire `BBSApp/Controls/TakeoffCanvas.cs` if the owner agrees.
