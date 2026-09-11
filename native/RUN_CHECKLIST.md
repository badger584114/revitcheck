# Run checklist — Check Dimensions and the drafted-dimension check

Written 2026-09-11 for the first real-machine run of PLANNING.md §28's
work. Delete or rewrite it once that run is written up.

**Why this run matters more than most.** Per the user, 2026-09-11: if the
drafted-dimension check can't be made to work, *"the whole thing is kind of
redundant"*. Triage would then be raising dimensions nothing can ever
verify, on model-backed views, which is most of what it raises. The check's
1.44mm mean error rests on six elevation dimensions. The one true section
dimension sat at 7.16mm, and sections are still the unconfirmed case.

The order matters. Each step says what to look at and what "wrong" looks
like.

---

## 0. Before leaving the Mac

- [x] **Pile path in Check Dimensions fixed** (2026-09-11, unconfirmed on
      a machine). It now runs `pile_dimension_consistency`, and counts a
      pile dimension as investigated only when its stated value was
      compared. **Settle a pile view with Check Dimensions, not the
      standalone Pile Chain Bearing button**, which still reconciles on
      chain topology alone.
- [ ] `dotnet build` and `dotnet test` clean, CI green, and **write down
      the commit hash you deploy**. Results can't be tied to a build
      otherwise.
- [ ] Confirm the scratch copy of model 100302 **still has §23's 50mm
      plant on pile 5506399**. A planted error is only useful while it's
      still there to re-run against.

## 1. Deploy

- [ ] Add-in: follow `native/README.md`, "Deploying to a Revit machine".
      `RevitCheck.addin` goes in `Addins\2024\`; the rest of
      `bin/Debug/net48/` goes in `Addins\2024\RevitCheck\`.
- [ ] Probe: copy `native/diagnostics/InspectSectionCutGeometry.pushbutton`
      into the scratch pyRevit extension. **Restart Revit rather than using
      pyRevit Reload**, because Reload breaks CPython for the rest of the
      session.
- [ ] Launch Revit. The RevitCheck tab should show three panels. **A
      Check Dimensions button proves the new build is loaded.** Without it,
      stop, because everything below would test the old DLL.
- [ ] **Rule Config**: note anything it says is pinned. None of the four
      `DrawnDimension*` settings should be pinned; they're new, so a pin
      means someone chose it.

## 2. Fresh capture, before planting anything new

- [ ] **Capture Model → the whole document.** Note how long it takes
      (CLAUDE.md watch item; if it's unreasonable, the lever is "active
      view", not narrowing categories).
- [ ] Keep the `.revitcheck.json` written next to it.

What this gives you off-machine: `pile_dimension_consistency`, Pile Chain
Bearing and Spot Elevation replayed through the CheckRunner, plus the pile
positions the Sep 9 captures lacked. The drafted-dimension check **cannot**
be replayed from any capture, because its witness search runs only inside
Check Dimensions.

## 3. Pick the views

Reuse views already probed, so the results can be compared with the dumps
in `samples/`. Then add at least one of each kind nobody has looked at yet,
so this isn't only calibration data being read a second time.

- [ ] Elevations (100302): `DRG-2871116 - ELEVATION - BARRIER PPT234105`,
      `DRG-2871072 - ABUTMENT B CONCRETE ELEVATION`. The 1.44mm figure
      comes from these.
- [ ] Sections (100304): `DRG - 2873174 - SECTION 1`,
      `DRG - 2873175 - SECTION 3`. This is the unconfirmed case.
- [ ] One more elevation and one more section, never probed.
- [ ] An abutment view with Spot Elevations (for §5 below).
- [ ] The pile layout view on the scratch 100302 (for §4 below).

## 4. Standalone passes. Do these BEFORE pressing Dimension Triage

Once Dimension Triage has run in this Revit session, Check Dimensions
records into the session and **writes no results file**, and its coverage
notes show only as a count. If a session is already running, restart Revit.

For each view:

- [ ] Run **Inspect Section Cut Geometry** first. It writes
      `Desktop\<document title>.inspect_section_cut_geometry.json`, **named
      per document, so the next view overwrites it**. Rename it with the
      view name immediately.
- [ ] Run **Check Dimensions** and save as `<view>_check_dimensions`
      (JSON, CSV and BCF).
- [ ] Screenshot the dialog.

Reading the dialog:

- **"By dimension type"** will call detail-linework dimensions
  "unreachable". That's a known stale label (`DimensionResolution` predates
  the check), not a result.
- **Mismatches**: each one is stated vs model, with the delta in mm.
- **Coverage count**: the notes themselves are in the JSON. Expect some for
  filled regions, for both ends on one element, and for no edge-on face
  within 1500mm.

**Stop and send it back** if you see any of these:

- `No dimension had a witness-geometry search run`: the build is stale, or
  the adapter isn't running the search.
- `in a view with no recorded direction`: `ViewDirection` isn't being read.
- Every compared dimension flagged, or deltas in the tens to hundreds of
  mm: the in-plane measurement isn't behaving on this view. Put the probe's
  `delta_in_plane_vs_measured_mm` next to it.
- Any extraction errors (the dialog shows a sample).

**For every finding:** select the dimension by ID and decide whether the
drawing or the model is right. Write the verdict down. That's the first
real evidence of whether a finding means anything.

Pile layout view: also run **Check Dimensions** there. It now reports
each pile dimension's stated distance against the real spacing, which is
this check's first real run.

## 5. Negative controls, on the scratch or detached copy only

Everything in §4 can only show agreement. A check that returns nothing on
real data has proved nothing (§23). Log every plant: element ID,
direction, amount.

**Drafted-dimension check.** Pick two dimensions on an elevation that the
probe shows within about 3mm (`delta_in_plane_vs_measured_mm`).

- [ ] **A. Model changed, drawing didn't.** This is the real failure mode.
      Move one of the model elements the first dimension measures against
      (the probe's `witness_probes[].faces` names them) **50mm along the
      dimension's direction**, i.e. perpendicular to the face it's
      dimensioned to. Leave the detail component where it is. Expect about
      50mm on that dimension and on any other dimension measuring that
      element, and nothing new elsewhere. A move parallel to the face
      should change nothing, and that is correct behaviour.
- [ ] **B. The stated-value path.** On the second dimension, type a numeric
      override 50mm more than its measured value. Expect one finding of
      about 50mm.

**Pile checks.** Plant **30mm, not 50mm**. `PileTagMatchToleranceMm` is
50mm, so a 50mm move can stop a tag matching its pile at all. The pile then
drops out of every chain and is flagged for nothing. That may be exactly
why 5506399 is in no chain. Avoid 5506399, 6491094 and 6492138, which are
in no chain.

- [ ] **C1. Bearing.** Move a pile in the middle of a run 30mm
      **perpendicular** to the run. Scaling CLAUDE.md's 50mm figure
      (0.3°–0.8°), that's about 0.2°–0.5°: well above real scatter (306″,
      about 0.085°) and around the 0.2° collinearity tolerance. So expect a
      "changes direction at pile …" manual-review item, or a run bearing
      mismatch. Either one counts as detection. Pile Model/Schedule should show about
      30mm. `pile_dimension_consistency` should stay silent, because the
      distances change by under 1mm.
- [ ] **C2. Pile dimensions.** On a different chain, move a mid-run pile
      30mm **along** its run. Expect the two dimensions touching it to be
      out by about 30mm each, in `pile_dimension_consistency` (Check
      Dimensions runs it; §6's capture lets it be re-checked off-machine).
      Bearing should stay silent.
- [ ] **If C2 flags nothing**, first check whether the tag moved with the
      pile. If it did, tag-to-tag dimensions track the model by
      construction, and only an override can drift. That's worth knowing
      in its own right.

**Spot Elevation** (optional, only if there's time):

- [ ] **D.** Pick a spot triage calls drafted, meaning it sits on
      linework. Raise the model shelf under it by 50mm. Expect about 50mm.

Keep the plants in place afterwards. They're what makes any fix
re-checkable.

## 6. After planting

- [ ] Run **Check Dimensions** again on each planted view, standalone, and
      save the results.
- [ ] **Capture Model → active view** on the pile layout view, with the
      plants in place. This is what lets C2 be evaluated off-machine, and
      what finally answers whether 5506399's tag is 50mm from its pile.

## 7. The session path

- [ ] **Dimension Triage → Start Fresh**, not Resume.
- [ ] From the checklist window, **Open View** on one elevation, then run
      **Check Dimensions**. Screenshot the row before and after. Dimensions
      the check compared should move from manual review to resolved, and a
      drafted-dimension finding should show as a confirmed problem. The row
      may already read "manual review" before you run anything; that's the
      stale label again.
- [ ] Abutment view: run the standalone **Spot Elevation** button with the
      session active. **This closes §18's last unconfirmed item.**
      Confirmed-clean spots should resolve, with no phantom confirmed
      problem.
- [ ] **Export Reconciled BCF.** Confirmed problems should contain only
      real findings, never a coverage note.

## 8. Watch for (invisible if they fail)

- **`TextNote.BaseDirection` coming back empty.** Bearing-call matching
  silently falls back to distance only. The only symptom is a bearing
  finding that pairs a run with the wrong call.
- **Capture duration** (§2).

## 9. Bring back, via Forma as `04.zip`

- [ ] Every Check Dimensions results set, per view, before and after
      planting.
- [ ] Every probe JSON, renamed per view.
- [ ] Both captures (§2 whole-document, §6 active-view) and the
      `.revitcheck.json`.
- [ ] Dialog screenshots, plus the checklist window before and after.
- [ ] The reconciled BCF export.
- [ ] A short note: the deployed commit hash, every plant (ID, direction,
      amount), and your verdict on each finding.

Same real-client caution as every earlier zip.

## What the results decide

- **Whether the drafted-dimension check is real.** Does the in-plane
  agreement hold on views nobody calibrated against and on sections, and
  did A and B get caught? If so, route those dimensions to it in
  `DimensionResolution`: the deferred suppress-or-surface decision, which
  is then "surface". Also calibrate `DrawnDimensionSearchRadiusMm` (still a
  placeholder) from the probe numbers.
- **If it fails,** face the user's conclusion rather than iterating around
  it.
- **Whether the bearing path can detect at all** (C1), which is the first
  item under CLAUDE.md "Next".
- **Why 5506399 is in no chain**, from §6's capture.
