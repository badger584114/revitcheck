# RevitCheck — drawing checks that run inside Revit

A pyRevit extension. Same checks this project has been building against
PDF/DWG/IFC exports, moved to where the information actually lives.

## Why it moved

The PDF/DWG path stalled on one thing (PLANNING.md §5): **section and
elevation cutting planes are Revit-only knowledge and cannot be
recovered from an export.** Confirmed directly — no section-marker
blocks survive in any DXF inspected, and the 55 markers the reference
graph finds carry tag, position and target with no cut line, direction
or depth. The workaround being designed was a per-view human confirm
step, an hour or two per project.

Inside Revit the problem doesn't exist. A `ViewSection` carries its own
origin and direction, and a `Dimension` holds references that resolve to
real elements — so "which element is this section dimensioning?" is a
lookup rather than an inference. Several other hard-won pieces of the
export pipeline become single API calls: three title-block extraction
strategies collapse to `sheet.SheetNumber`, revision-cloud scallop-arc
vector clustering collapses to `FilteredElementCollector(doc).OfClass(RevisionCloud)`.

## Install

1. Clone this repo somewhere on the Revit machine.
2. pyRevit → **Settings** → **Custom Extension Directories** → add the
   `extensions/` folder (the parent of `RevitCheck.extension`, not the
   extension folder itself).
3. **Reload** pyRevit. A `RevitCheck` tab appears.

No other install step: pyRevit puts `RevitCheck.extension/lib` on
`sys.path` automatically, which is where the `revitcheck` package lives.
The scripts declare `#! python3`, so they run on pyRevit's CPython
engine.

## Buttons

| Button | What it does |
| --- | --- |
| **Dimension Provenance** | Flags dimensions measuring detail linework instead of model geometry, and lists views with no model-derived dimensions at all. |
| **Capture Model** | Writes the extracted data to JSON so checks can be developed off a Revit machine. |

## The development loop

Revit is Windows-only and the machine it runs on is locked down, while
the code gets drafted elsewhere. **Capture Model** is what makes that
workable:

```
# on the Revit machine, once per project
Capture Model  ->  BR06.capture.json

# anywhere, as often as you like
python scripts/check_capture.py BR06.capture.json
python -m pytest tests/revit/
```

It is the same loop the PDF and DXF work already used with committed
samples, and it is why `tests/revit/` runs in a tenth of a second with
no Revit present.

A capture contains sheet numbers, view names and coordinates. Treat it
as client data (PLANNING.md §2) — check before it leaves a machine or
lands in git.

## Layout, and the one rule that matters

```
adapters/revit_source.py   the ONLY module that imports the Revit API
    |                      (reads the open document -> RevitModel)
    v
ir.py                      plain dataclasses, raw facts, millimetres
    |
    v
checks/*.py                pure (RevitModel, RuleConfig) -> [Issue]
```

**Nothing below the adapter knows Revit exists.** Two consequences that
are easy to erode and worth defending in review:

- The adapter **extracts facts and judges nothing** — no
  classification, no tolerances, no filtering. `ReferenceInfo` records
  that an element is view-specific; deciding that this means "drafted"
  happens in `checks/dimensions.py`. So retuning a classification never
  requires a fresh capture, and never requires a Revit machine.
- Anything that grows inside a `script.py` is logic that can only be
  debugged in Revit. Buttons stay thin.

## What the provenance check is actually for

Some drafting teams don't use live sections — they draw setout details
as static 2D linework to save time, and those drift as the model
changes. That is the live problem (PLANNING.md §5).

Curved bridge geometry makes sections hard to cut perpendicular, so
model-derived dimensions land a few mm out. Drafters resolve that two
ways, with very different checkability:

1. **Overwrite the dimension text** — the model's measurement survives
   alongside the override, so the discrepancy is visible in the file.
2. **Draw witness lines and dimension to those** — the dimension agrees
   perfectly with the line it measures, so the file is *internally
   consistent while collectively stale*. Nothing in it reveals the drift.

Case 2 was undetectable from a DXF export except by proxy (the CAD layer
nearest each witness point — BR06 44/60 on `D-BDGE`, Flinders 50/52 on
`A-DETL`). Here it is a direct property lookup.

The check reports **triage, not verdicts**. It says the file cannot
answer whether a dimension is right, not that it is wrong — per the
standing position in PLANNING.md §5: *assume nothing is trustworthy or
you will be caught out.* An override goes stale exactly as a witness
line does.

## Next

The follow-up tool — verifying drafted dimensions against the model —
consumes this one's output directly. `drafted_views()` returns the views
with no model-derived dimensions, which is the scope that tool operates
on, and the reason the roll-up reports a wholly-drafted view as one
finding rather than twenty.
