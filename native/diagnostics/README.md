# Diagnostics

One-off scripts for answering questions that need ground truth from a real
Revit model, not description in words. **Not part of the frozen
`extensions/RevitCheck.extension`** (PLANNING.md §12: pyRevit stays "no new
buttons, no growing surface") — these are throwaway aids for building the
native add-in, run once and discarded.

## `InspectElements.pushbutton`

Dumps the current selection's full identity (category, family, type,
whether it's a nested sub-component and of what) and complete parameter set
(instance *and* type, kept separate) to JSON.

**How to run it:** copy this `.pushbutton` folder into any pyRevit
extension's `.tab/*.panel/` directory on the Revit machine — a scratch
local extension, not `extensions/RevitCheck.extension` — reload pyRevit,
select representative elements first (**include at least one nested-
component case**, e.g. a fixing bracket nested in a panel — its
sub-components get walked automatically, you only need to select the
host), then click the button.

**What it answers:**
1. The real Revit parameter name used as the cross-tool join key (readable
   directly off the dumped parameter list).
2. How a nested sub-component actually appears via the API — whether it
   has its own `ElementId`/`UniqueId` and independently editable
   parameters (`has_sub_components` / `host_element_id` in the output), or
   something else.
3. Which categories/classes actually carry the tracked parameters, so the
   native adapter's collection sweep can be scoped correctly.

**Handle the output like a capture** (PLANNING.md §2): it contains real
parameter values from a real project. Send it back for review, then delete
it — don't commit it to this repo, and delete the scratch extension copy
off the Revit machine once you're done with it.

## `InspectDimensionGeometry.pushbutton`

Dumps a dimension's witness-point geometry, its view's cut plane/direction,
and nearby model geometry to JSON — the Track B diagnostic PLANNING.md §14
names as the required first step before writing any dimension-vs-model
comparison logic (this project's own repeated lesson: every extractor that
guessed ahead of a real sample had to be rewritten).

**How to run it:** same copy-into-a-scratch-extension pattern as
`InspectElements.pushbutton` above. First identify drafted views worth
looking at — run the real Dimension Provenance button, or
`native/tools/RevitCheck.CheckRunner` against a capture, and read either
one's "views to verify against the model" list
(`DimensionProvenanceCheck.DraftedViews()`). Open one of those views in
Revit, then either select some/all of its dimensions and run this button,
or run it with nothing selected — it falls back to every dimension in the
active view. **Scope to real setout-critical dimensions first** (piles,
abutments, foundations — matching `geometry.ifc_setout_consistency`'s
original scope, ARCHIVE-pdf-dwg.md) rather than sweeping the whole model;
a handful of representative dimensions answers the diagnostic's questions
as well as thousands would, with a much smaller file to review.

**What it answers** (PLANNING.md §14 Track B, questions 1-3):
1. Whether a dimension's witness point resolves to a real 3D position
   (`Reference.GlobalPoint`, with a resolved element's own `Location` as a
   fallback) — and for which reference types that does or doesn't work.
2. What a view's cut plane/direction actually looks like via the API
   (`SketchPlane`, `ViewDirection`, `Origin`) across the view types
   drafted dimensions actually live in.
3. What model geometry is actually nearby a witness point (a bounding-box
   collector search) — whether there's a real geometric signal to compare
   against at all.

Same handling as `InspectElements.pushbutton`'s output: real client
geometry and element identities, treat it like a capture (PLANNING.md
§2), send it back for review, then delete it and the scratch extension
copy — don't commit it.

**First real run (2026-08-25) already found a real bug in the script
itself** — `Reference.GlobalPoint`/the `Location` fallback were both
unreliable for real model geometry (see PLANNING.md §14 for the full
findings), which the script has since been fixed to work around
(`DimensionSegment.Origin` as the witness-point anchor instead). If
you're running this for the first time, you already have the fixed
version; if re-running to confirm the fix, that's expected and is the
point.
