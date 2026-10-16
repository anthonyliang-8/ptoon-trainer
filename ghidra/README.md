# Ghidra project note

The analysis database `proj/platoon.rep/idata/00/~00000000.db/` (db.1.gbf, db.2.gbf, ~170 MB each)
is NOT in git because it exceeds GitHub's 100 MB file limit.

The rest of the project (platoon.gpr, project.prp, scripts) is committed, but Ghidra cannot open the
project without that DB. To regenerate it: re-import the pla_toon binary into a new project and
re-run auto-analysis (the DB is purely a cached analysis result; OFFSETS.md holds the findings).
