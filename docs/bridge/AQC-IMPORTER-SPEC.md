# Spec: the AQC importer for hcw-aqc-bridge v1

For whoever builds the importer in the AQC repo (Phase 3 of [../AQC-BRIDGE-PLAN.md](../AQC-BRIDGE-PLAN.md)). **Written by Agent 2 (hcwCAD-KIT, MIT) on 2026-10-07 from the contract and from [../AQC-FINDINGS.md](../AQC-FINDINGS.md), which was read from AQC's source and never run.** It copies no AQC code. Nothing in it has been checked against a running AQC, so anything marked *check* must be confirmed when the importer is first run. Whether the AQC repo may be changed at all is an open owner question (QUEUE.md, question 3).

Inputs: the file in [README.md](README.md), the schema [hcw-aqc-bridge.schema.json](hcw-aqc-bridge.schema.json) and the two sample files in [fixtures](fixtures).

## 1. Shape of the work

* A small plain `net8.0` class library (no WinUI) that reads the file and returns an *import plan*: rows to add, rows to update, rows unchanged, rows skipped, each with a reason. Its own xUnit tests use the two sample files. `BBSApp` calls it from one menu item ("Import from CAD") and applies the plan.
* The importer is **idempotent**: every row it writes has `source = "hcwcad:<drawing_id>"` and `source_ref = <ref>`. A second import finds rows by that pair and updates them; it never creates a duplicate.
* The user sees the plan first (a preview of adds, updates, unchanged and skipped rows) and the whole import is one undo step. Nothing is written before the user accepts.
* A row the user edited in AQC after import is flagged in the preview and left alone unless the user chooses to overwrite it (*check* how AQC marks an edited row; it may need a "modified" flag).
* The importer never touches `Lvl0` (the plinth), rows with another `source`, or rows AQC derives (`source` starting `auto_`).

## 2. Levels and project

| File | AQC | Rule |
|---|---|---|
| `levels[i]` (above the plinth, in order) | `levels[]`, `Lvl1`, `Lvl2` ... | Match by `name` first, then by position. `height_mm` is floor to floor (the nearest thing to AQC's slab-top to slab-top; the small difference is the finish, flag it in the preview), `slab_thickness_mm`, `beam_depth_mm` as given. A level only in the file is added (ids stay positional, so add at the end). A level only in AQC is left alone. |
| `levels[i].ceiling_mm`, `lintel_bottom_mm` | none | Kept for display in the preview only. |
| `project.name`, `client_name`, `company_name`, `location`, `address` | `project` fields of the same names | Fill only the fields that are empty in AQC, or show a change in the preview and let the user accept it (owner question 2 decides the authority). |
| `project.pid`, `site_area_m2`, `plot_use` | none | Shown in the preview; not stored until AQC has fields for them. |
| `beam_depths_mm` | none | Informational. |

## 3. Rows (all lengths in millimetres, as AQC expects)

AQC rows are free-form string dictionaries; numbers may be written as strings or numbers. Marks follow AQC's own rule `{prefix}-{level}-{nnn}` for new rows (for example `MW-Lvl1-001`); the importer must use AQC's own `NextMark`, not invent marks. `level` is the AQC level id found by the level match above.

| File section | AQC category | Fields written |
|---|---|---|
| `walls[]` | `masonry` | `length` = `length_mm`, `height` = `height_mm`, `thickness` = `thickness_mm`, `unit_type` = the file's `unit_type` or `Brick`. A `thickness` of 120 or less is measured by AQC as its 110 mm wall; do not change it. `source_ref` = `ref`. |
| `openings[]` | `masonry_openings` | `wall_mark` = the mark of the masonry row whose `source_ref` equals `wall_ref`; `nos` = 1; `opening_l` = `width_mm`; `opening_h` = `height_mm`; `opening_kind` = `Door` or `Window`. An opening with no `wall_ref` is added with no wall and flagged. |
| `openings[]` (Door) | `doors` | `mark` = `mark` (D1, D2 ...), `width`, `height`, `door_type` = `type`, `wall_mark` as above, `deduct_from_wall` = yes. |
| `openings[]` (Window) | `windows` | `mark` (W1, V1 ...), `width`, `height`, `wall_mark`, `deduct_from_wall` = yes. A ventilator (V1) is a window. |
| `opening_schedule[]` | none | Used to check: the count of imported openings per mark equals `nos`. A mismatch is reported in the preview, not corrected. |
| `columns[]` | `columns` | `mark`, `width` = `width_mm`, `depth` = `depth_mm`. **Do not write `height`**: AQC computes the clear height from the level (`height - slab - beam depth`). A round column has equal width and depth. |
| `lintels[]` | `lintels` | `mark` (generate one when absent), `opening` = `opening_mm`, `bearing` = `bearing_mm` (default 150 when absent), `width` = `width_mm`, `depth` = `depth_mm`. |
| `rooms[]` | `flooring` | `length` = `length_mm`, `breadth` = `breadth_mm`; the room name in `notes`. When length and breadth are absent, use the area (*check* whether AQC has an area-based flooring row). |
| `slabs[]` | `slabs` | `thickness`; `span_x` and `span_y` from `length_mm` and `breadth_mm` when `rectangular` is true. A non-rectangular slab is added as an area-based row and flagged (*check* what AQC supports). |

**Never imported:** plaster, painting, skirting, shuttering, rebar, PCC, earthwork, waterproofing. AQC derives the first four from the masonry and RCC rows and owns the bar schedules. The file does not carry them.

## 4. Re-import rules

* Match on `source` + `source_ref`. Found: update the geometry fields (not marks, not user-entered fields such as finish or grade). Not found: add. Present in AQC with this `source` but not in the file: **do not delete**; list it in the preview as "no longer in the drawing" and let the user choose.
* Never change a row whose `source` is not `hcwcad:<drawing_id>`.
* A file with a different `drawing_id` than the project's last import is a different drawing: ask before importing into the same project.

## 5. Tests the importer library should have (use the fixtures)

1. `small-house.json` into an empty project: 3 masonry rows (the 114.3 wall flagged as the 110 build), 5 opening rows with `wall_mark` set, 2 doors rows and 2 windows rows by mark, 2 columns, 1 lintel, 2 flooring rows, 1 slab row.
2. The same file imported twice: the second plan has zero adds and no updates.
3. `levels-only.json` into a project with a plinth and one floor: `Lvl0` untouched, `Ground` matched by name, `First` added as `Lvl2`.
4. A wall whose `height_mm` changes between two imports: one update, no duplicate.
5. A row edited in AQC between imports is flagged and not overwritten by default.
6. The schedule counts equal the imported openings per mark.
7. A file with `version` 2, or `units` other than `mm`, is refused with a message.

## 6. What still needs a decision or a running AQC

Owner questions 2, 3, 4 and 6 in [../work/QUEUE.md](../work/QUEUE.md); *check* items above; and the round trip (export, import, change the drawing, export, import) needs AQC built, which has not been possible on the machines available so far.
