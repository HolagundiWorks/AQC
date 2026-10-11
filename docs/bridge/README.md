# hcw-aqc-bridge, version 1

The file hcwCAD-KIT writes for AQC (and reads back, in the other direction). One JSON document per drawing: `<drawing>.aqcbridge.json`.

* The schema is [hcw-aqc-bridge.schema.json](hcw-aqc-bridge.schema.json); two sample files are in [fixtures](fixtures): `levels-only.json` (project details and floors) and `small-house.json` (every section).
* **Millimetres on the wire** (`"units": "mm"`; areas in square metres). The plugin converts from the drawing's units; AQC's element rows are already millimetres.
* **Every section is optional**, so a first import can carry only the project and the levels.
* **Levels** are the floors above the plinth, in order, with `height_mm` (floor to floor), `slab_thickness_mm` and `beam_depth_mm`. AQC computes a column's clear height as `height - slab - beam depth`, so the beam depth matters. AQC matches levels by `name`, then by position, to `Lvl1`, `Lvl2` ...; `Lvl0` (plinth) is AQC's and an import does not change it.
* **Every row has a `ref`** (the plugin's wall id, or a block handle) and the file has a `drawing_id` kept in the drawing. AQC stores them as `source` and `source_ref`, so importing again updates rows and does not duplicate them.
* **The plugin sends measured facts, not derived quantities.** No plaster, paint, skirting, shuttering or rebar: AQC derives the first four from the masonry and RCC rows and owns the bar bending schedules.
* **Standard opening marks:** D1 800, D2 900, D3 1200; W1 600, W2 900, W3 1200, W4 1500, W5 2000; V1 600 (a ventilator is a window). A size not in the table is D4 and up, W6 and up.
* **Wall thickness:** 230 is a 230 mm brick wall; 114.3 (4.5 in) is read by AQC as its 110 mm wall (anything 120 mm or under).
* A change to a field name, a required field or a unit is a new `version`.

## How hcwCAD-KIT fills it (`HCWBRIDGE`, written 2026-10-07)

| Section | From |
|---|---|
| `source` | the drawing file name, a GUID kept in the drawing, the AutoCAD version, the export time (UTC) |
| `project` | project details: `name` project title, `client_name` owner, `company_name` consulting architect, `address`, `pid`, `site_area_m2` (the number in the site area text), `plot_use` |
| `levels`, `beam_depths_mm` | the floors of the project (`HCWFLOORS`): floor to floor, slab, the floor's beam depth, ceiling, lintel bottom; the standard beam depths |
| `walls` | wall objects: centre line length, ceiling height, thickness, layer; `ref` is the wall id |
| `openings`, `opening_schedule` | doors and windows made by the tools; `ref` is the block handle, `wall_ref` the nearest wall within 1.5 wall thicknesses |
| `columns`, `lintels`, `rooms` | closed rectangles and column blocks on the column layers; the lintel lines `HCWLINTEL` makes; closed outlines on the room layers (each outline once) |
| `slabs` | not sent yet |

## Reading a file back (`HCWBRIDGEIMPORT`)

The plugin reads the same format in the other direction. From a file written by AQC it takes only the `project` details, the `levels` (above the plinth; AQC's `Lvl0` is never imported) and `beam_depths_mm`; it matches floors by name, then by position, shows what would change (old and new) and applies it only when the owner says Yes. It never changes walls, openings or other drawing geometry. Values absent from the file leave the drawing's values alone. The importer on the AQC side is specified in [AQC-IMPORTER-SPEC.md](AQC-IMPORTER-SPEC.md).

Status: draft, written before AQC has an importer. See [../AQC-BRIDGE-PLAN.md](../AQC-BRIDGE-PLAN.md) and [../AQC-FINDINGS.md](../AQC-FINDINGS.md).
