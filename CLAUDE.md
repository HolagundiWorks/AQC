# AQC: notes for any Claude agent in this repo

**Quantity take-off from drawings is done in the CAD program, not in AQC.** Today that is hcwCAD-KIT (<https://github.com/HolagundiWorks/hcwCAD-KIT>, an AutoCAD plugin); the toolkit is moving into **AADT** (<https://github.com/HolagundiWorks/AADT>, the owner's own CAD program, in development; the owner will share it here when it is ready). The CAD program measures walls, openings, columns, lintels, rooms and floors and hands them to AQC in a JSON file (`hcw-aqc-bridge` v1, millimetres). AQC imports those measured facts and does everything after: derived quantities, rates, estimates, bar bending schedules.

Read **[docs/AADT.md](docs/AADT.md)** before touching imports, levels, take-off or the bridge. In short:

- The importer exists (`BBSDesktop/AQC.CadImport`, File > Import from CAD). Change it through its tests (`dotnet test BBSDesktop/AQC.CadImport.Tests`).
- Import idempotently: every imported row gets `source = "hcwcad:<drawing_id>"` and `source_ref = <ref>`. Keep the `hcwcad:` prefix: it is stored in user projects.
- `Lvl0` (plinth) is AQC's; an import never changes it. Match levels by name, then by position.
- Never import plaster, painting, skirting, shuttering or rebar: AQC derives them.
- Preview first, one undo step, never delete a row that has gone from the file.
- Do not guess AADT's commands, files or export code; nothing here has been checked against it yet. Do not copy code between AQC (AGPL) and the CAD repos (MIT); share only the file contract. Do not edit the hcwCAD-KIT or AADT repos from here.
- Say only what you have run. No "works" or "tested" without the check and the commit.
- The owner decides file formats, names, defaults and scope.
