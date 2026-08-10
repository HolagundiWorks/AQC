# AQC Core · project sync (Connect + hub)

**Status:** Bind · Flush · Publish live in BBSApp (2026-08-10)  
**Canon:** esti [`PORTAL-SYNC-BRIDGE.md`](https://github.com/HolagundiWorks/aorms/blob/main/docs/esti/PORTAL-SYNC-BRIDGE.md) · [`AORMS-CONNECT.md`](https://github.com/HolagundiWorks/aorms/blob/main/docs/esti/AORMS-CONNECT.md) · [`AQC-JM-SYNC.md`](https://github.com/HolagundiWorks/aorms/blob/main/docs/esti/AQC-JM-SYNC.md)

## What syncs

| Plane | Behaviour |
| --- | --- |
| Work (`.bbsproj`) | Stays local — BOQ / MB / BBS drafts |
| Identity | `HubProjectId` on project info = Connect catalog UUID |
| Meta outbox | `projectStatus` · `estimateTotals` → Flush → hub `/api/sync/meta` |
| Pull | Outputs › **Pull JM** (requires bind) |

## UX

| Surface | Actions |
| --- | --- |
| ActionDock | **Bind** · **Flush** |
| File menu | Bind from Connect · Flush · Publish status · Publish estimate totals |
| Outputs ribbon | Bind project · Flush · Publish · Pull JM |
| Settings › Project | Catalog combo + Hub project id + sync status line |
| Top bar chip | Sync ready / bind / pending outbox |

## Prerequisites

1. **AORMS Connect** Activate (writes `%LocalAppData%\AORMS-Connect\session.json` + `catalog.json`).
2. Bind the open `.bbsproj` to a catalog project (shared UUID).
3. Optional env overrides: `ESTI_HUB_URL`, `ESTI_LICENSE_API_URL`, `INSTALL_ID`.

Firm DB: `%LocalAppData%\AQCCore\firm.db` (tokens + outbox). Bridge: `BBSDesktop/Aorms.Bridge`.
