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
