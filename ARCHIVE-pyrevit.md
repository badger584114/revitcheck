# ARCHIVE — the pyRevit extension

**This is history, not instructions.** Everything below describes
`extensions/RevitCheck.extension/`, a working, tested pyRevit toolbar
that ran the checks inside Revit, plus its 172-test suite
(`tests/revit/`), `scripts/check_capture.py` and `pyproject.toml`. It
was removed from the tree on 2026-09-07.

**The code is not gone. Get it back with:**

```
git checkout pyrevit-final          # the extension, its buttons, its tests
git show pyrevit-final:extensions/RevitCheck.extension/lib/revitcheck/bcf.py
```

That tag is the only surviving reference to it — `main` is the sole
branch, as it has been since 2026-08-18.

## Why it was archived

**It finished its job.** This host existed to do two things, and did
both:

1. **Develop the checks** off a Revit machine, via the adapter/IR/checks
   split and JSON capture (PLANNING.md §5c). That layering is the single
   most valuable thing this project produced, and it survived intact into
   the C# port — the strongest possible evidence the cut was in the right
   place.
2. **Prove the Revit → BCF → Forma → Revit round trip.** It did,
   2026-08-22, after fixing four real Forma import rejections in a row
   (PLANNING.md §12). **The user confirmed at the time that this was the
   entire scope of what pyRevit needed to cover**, and no further feature
   work was planned from that day.

**Production had already moved.** pyRevit's CPython bridge failed
deployment the same way on two different machines — three
environment-coupling causes, not code bugs (PLANNING.md §12). The native
C# add-in has been the production host since 2026-08-24, and by
2026-09-02 it had six real ribbon buttons, seven registered checks and a
full interactive checking workflow, all validated on a real Revit
machine.

**What actually forced the archive: it was still being maintained.** On
2026-09-02 the dimension-override fix (PLANNING.md §17) was written into
*both* engines. That is the cost of an ambiguous status — a host declared
complete on 2026-08-22 was still taking changes eleven days later, in a
Python implementation that is a strict subset of the C# one. Every rule
`revitcheck` carried exists in `native/`:

| Python | C# equivalent |
| --- | --- |
| `checks/dimensions.py` | `Checks/DimensionProvenanceCheck.cs`, `Checks/DimensionOverrideConsistencyCheck.cs` |
| `checks/coverage.py` | `Checks/CaptureCoverageCheck.cs` |
| `ir.py` | `Ir/*.cs` |
| `issue.py` | `Issues/Issue.cs` |
| `catalog.py` | `Catalog/Catalog.cs`, `Catalog/CheckRegistry.cs` |
| `capture.py` | `Capture/CaptureSerializer.cs` |
| `report.py` | `Reporting/Issue{Json,Csv,Bcf}Writer.cs` |
| `bcf.py` | `Reporting/IssueBcfWriter.cs` (a line-for-line port, its hand-rolled UUIDv5 verified against real Python output) |
| `adapters/revit_source.py` | `Adapters/RevitDimensionSource.cs`, `Adapters/RevitMetadataElementSource.cs` |
| `scripts/check_capture.py` | `native/tools/RevitCheck.CheckRunner` |

The C# side additionally has metadata reconciliation, both pile checks,
the Spot Elevation check, the schedule adapter and the whole checking
session/checklist workflow, none of which ever existed in Python.

## What survived into the live tree, and why

**`config/en_gb_variants.json`** — 563 curated British/American
spelling-variant pairs, converted from `en_gb_variants.py` to plain JSON
so it is no longer tied to a Python implementation. This data has now
outlived two archived trees: it was rescued from `pdf-dwg-final` into the
pyRevit extension for exactly the same reason it is rescued again here.
The content is the expensive part — every pair was hand-curated or added
in response to a real false positive on real issued drawings — and it is
not format-specific or language-specific. Nothing imports it yet; the
en-GB spelling rule it exists for is still unbuilt (CLAUDE.md's "Next").

> **A real bug was found and fixed during that conversion.** The Python
> built `catalogue`/`dialogue`/`analogue` by stripping three characters
> from the `-ogue` form (`word[:-3]`), one too many, producing
> `catalo`/`dialo`/`analo`. The source comment stated the intended
> `"catalogue" -> "catalog"` that the code did not produce, and none of
> the four tests covered it. The consequence would have been real in both
> directions: the correct American spellings `catalog`/`dialog`/`analog`
> would never have been recognised, so the rule would have failed to flag
> exactly the words it exists to catch. The JSON carries the corrected
> values.

**`config/firm_glossary.json` / `config/project_glossary.json`** — the
same argument, and these never lived inside the extension anyway.

**`samples/T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json`** — kept as
the C#-port test fixture PLANNING.md §12 named. Real client geometry,
sheet numbers and view names, so treat it the way this project treats
uploaded drawings.

## What did not survive, deliberately

- **The buttons** (`RevitCheck.tab/`) — every one has a real, validated
  C# ribbon equivalent.
- **`extensions/test.extension/`** — a scratch extension used to diagnose
  the CPython bridge failures. Its findings are written up in PLANNING.md
  §12's "Three CPython gotchas"; the code itself proved nothing on its
  own.
- **The Python CI workflow** — it byte-compiled and tested a package that
  no longer exists in the tree. Replaced the same day with a .NET
  workflow that builds the whole solution *including the net48 Addin*,
  which is the half no test can reach, for exactly the reason the Python
  workflow gave for its own `compileall` step.
- **`pyproject.toml`** — existed only to configure pytest for that suite.

## What to read instead

- **PLANNING.md §12** — why pyRevit was a proof-of-concept host, the
  three CPython deployment failures, and the BCF/Forma round trip in
  full, including the exact sequence of four real import rejections.
- **CLAUDE.md** — current state. It no longer describes the pyRevit
  layout, but the **layering rule** it states (adapter → IR → pure
  checks, nothing below the adapter knows Revit exists) was invented here
  and is unchanged.
- **ARCHIVE-pdf-dwg.md** — the tree archived before this one, and still
  the best source on what real drawing sets look like.
