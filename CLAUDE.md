# AQC: notes for any Claude agent in this repo

**Quantity take-off from drawings is done in hcwCAD-KIT, inside the CAD program, not in AQC.** hcwCAD-KIT (<https://github.com/HolagundiWorks/hcwCAD-KIT>) measures walls, openings, columns, lintels, rooms and floors and hands them to AQC in a JSON file (`hcw-aqc-bridge` v1, millimetres). AQC imports those measured facts and does everything after: derived quantities, rates, estimates, bar bending schedules.

Read **[docs/HCWCAD-KIT.md](docs/HCWCAD-KIT.md)** before touching imports, levels, take-off or the bridge. In short:

- Import idempotently: every imported row gets `source = "hcwcad:<drawing_id>"` and `source_ref = <ref>`; follow the pattern of `JointMeasurementPull`.
- `Lvl0` (plinth) is AQC's; an import never changes it. Match levels by name, then by position.
- Never import plaster, painting, skirting, shuttering or rebar: AQC derives them.
- Preview first, one undo step, never delete a row that has gone from the file.
- Do not copy code between AQC (AGPL) and hcwCAD-KIT (MIT); share only the file contract. Do not edit the hcwCAD-KIT repo from here.
- Say only what you have run. No "works" or "tested" without the check and the commit.
- The owner decides file formats, names, defaults and scope.
