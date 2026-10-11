# hcwCAD-KIT and AQC: who measures, who estimates

Instructions for anyone (person or agent) working on AQC. Written 2026-10-11 on the owner's instruction.

## The rule

**Quantity take-off from a drawing happens in hcwCAD-KIT, inside the CAD program.** hcwCAD-KIT is the AutoCAD plugin (BricsCAD and ZWCAD builds share its source) at <https://github.com/HolagundiWorks/hcwCAD-KIT>. It measures walls, openings, columns, lintels, rooms and floors from the real geometry, and hands them to AQC in a file. **AQC does not re-measure them.** AQC takes the measured facts and does everything after that: derived quantities, rates, estimates, bar bending schedules, billing, the rest of the suite.

| hcwCAD-KIT (CAD) | AQC |
|---|---|
| Measures lengths, heights, thicknesses, areas, counts and sizes from the drawing, in millimetres | Imports them as rows and keeps them as the take-off |
| Walls, doors and windows (with standard marks), columns, lintels, rooms, floors, project details, beam depths | Derives plaster, painting, skirting, shuttering and the rest through its link rules |
| Never computes finishes, rates or rebar | Owns the C++ engine, rates, BOQ, estimates and every number that carries money (the engine stays the single source of truth) |

AQC's own PDF take-off (`TakeoffPage`) is for drawings that have no CAD source. **Open owner question:** whether to retire it, or keep it for those drawings. Do not remove it without the owner's say-so.

## The bridge file

The two programs meet only through a JSON file, never through shared code. The format is `hcw-aqc-bridge`, version 1, one file per drawing (`<drawing>.aqcbridge.json`). The contract is kept in the hcwCAD-KIT repo (MIT licence, so the schema and sample files may be copied here):

* README and rules: `docs/bridge/README.md`
* JSON Schema: `docs/bridge/hcw-aqc-bridge.schema.json`
* Sample files: `docs/bridge/fixtures/levels-only.json`, `docs/bridge/fixtures/small-house.json`
* The row-by-row specification for the AQC importer, with the tests it needs: `docs/bridge/AQC-IMPORTER-SPEC.md`

Read the spec before writing any code. The rules that matter most:

1. **Millimetres.** The file says `"units": "mm"` and AQC's element rows are already millimetres, so no conversion is needed. Refuse a file with another `units` or `version`.
2. **Every row has a `ref`**, and the file has a `drawing_id`. Store them on each imported row as `source = "hcwcad:<drawing_id>"` and `source_ref = <ref>`. Importing again updates by that pair and never duplicates (`JointMeasurementPull` is the pattern to follow: idempotent, marks, a `source`, then `ProjectStore.Current.Notify()`).
3. **Levels.** The file's levels are the floors above the plinth. Match them to AQC levels by `name`, then by position, to `Lvl1`, `Lvl2` ...; **`Lvl0` (plinth) is AQC's and an import never changes it.** The file's `height_mm` is floor to floor, which is the nearest thing to AQC's slab-top to slab-top height (note the finish difference in the preview). `beam_depth_mm` matters: AQC computes a column's clear height as `height - slab - beam depth`.
4. **Columns carry no height.** Let AQC compute it from the level.
5. **Never import** plaster, painting, skirting, shuttering, rebar, PCC, earthwork or waterproofing. They are not in the file. AQC derives the first four and owns the bar schedules.
6. **Never touch** a row whose `source` is not this drawing's, or a row AQC derives (`source` starting `auto_`). A row in AQC that has this `source` but is no longer in the file is **not deleted**: list it for the user to decide.
7. **Wall thickness:** a thickness of 120 mm or less is measured by AQC as its 110 mm wall, so the plugin's 4.5 in partition (114.3 mm) lands there. Do not change that rule for imports.
8. **Marks.** New rows get AQC's own `NextMark` marks (`MW-Lvl1-001`). Door and window marks are the plugin's standard marks (D1 800, D2 900, D3 1200; W1 600, W2 900, W3 1200, W4 1500, W5 2000; V1 600, a ventilator being a window).
9. **The user sees a preview first** (adds, updates, unchanged, skipped, with reasons), and the whole import is one undo step. Nothing is written before the user accepts.

## What to build in AQC

1. **A small plain `net8.0` class library** (no WinUI) that reads the file and returns an import plan, with xUnit tests that use the two sample files (the seven tests are listed in the spec). AQC has no C# tests today; this library can be built and tested without the WinUI workload.
2. **One menu item, "Import from CAD"**, in `BBSApp`, that calls the library, shows the preview and applies the plan.
3. **"Export for CAD"**: AQC writes `levels`, `project` (name, client, company, address) and `beam_depths_mm` in the same v1 format, so the plugin's `HCWBRIDGEIMPORT` can read them. The plugin applies them only after the user says Yes, and never changes drawn geometry.
4. Keep the contract authority in the hcwCAD-KIT repo. If the importer needs a field the contract lacks, ask the owner: a new field or a changed unit is a new `version`, decided by the owner, in both repos together.

## What hcwCAD-KIT already does (so you do not rebuild it)

* `HCWBRIDGE` writes the file next to the saved drawing (floors with beam depth, wall objects, doors and windows with standard marks and their schedule, columns, lintels, rooms). Slabs are not sent yet.
* `HCWBRIDGEIMPORT` reads a v1 file and applies floors, project details and beam depths on a Yes.
* Tested so far: the file contents and the import comparison have unit tests; `HCWBRIDGE` and `HCWBRIDGEIMPORT` pass headless tests in AutoCAD 2022's core console. Not tested: interactively in AutoCAD, in BricsCAD or ZWCAD, or against a running AQC (a round trip). That is your first check once the importer exists.

## Building AQC (what worked on 2026-10-09 with AQC `3667630`)

* **C++ engine and tests:** needs MSVC. With Visual Studio 2022 Build Tools (C++ workload), CMake and Ninja: run `vcvars64.bat`, then `cmake -S BBSDesktop -B <build dir> -G Ninja -DCMAKE_BUILD_TYPE=Release` and `cmake --build <build dir>`. Build outside the repo folder. `bbs_tests.exe` reported 0 failures (40 checks).
* **.NET projects** (`Aorms.Bridge`, `Aorms.Bridge.Smoke`, `AQC.BBS`, `AQC.Estimation`, `AQC.PM`, `BBSApp`): `dotnet build <project> -c Release -p:Platform=x64` with the .NET 8 SDK works, with no Visual Studio WinUI workload.
* **Known portability gap, for the engine's owner:** the engine does not compile with clang and libc++ (MinGW): `src/api/bbs_c_api.cpp` uses `std::malloc` and `std::free` without `<cstdlib>`, and `src/core/Project.cpp` opens files with a wide-character path, which only MSVC accepts. Fixing both would let the engine build without Visual Studio.
* The app itself has been built but **not yet started**.

## Rules for working across the two repos

* **Do not copy code between the repos.** AQC is AGPL-3.0 or commercial; hcwCAD-KIT is MIT. Only the file contract (schema, sample files, spec text) is shared.
* **Do not edit the hcwCAD-KIT repo from here.** Two agents work there (a local one that builds and tests, a cloud one that develops) under its own rules (`docs/AGENTS.md` in that repo). Ask the owner for anything you need from it.
* **Say only what you have run.** Never write "works" or "tested" for the importer without the check, the commit and, for anything involving a CAD program, a result from a real run.
* The owner decides names, defaults, file formats and what is in version 1.
