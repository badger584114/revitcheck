# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.

## Project

Automated review of civil engineering drawings — bridges, retaining
walls, and similar structures — run **inside Revit**. The checks
themselves (`extensions/RevitCheck.extension/lib/revitcheck/`) are what
matters here and are host-independent; the pyRevit toolbar in that same
extension is today's *host* for them, not a permanent architectural
choice.

> **PLANNING.md §12 (2026-08-21, updated 2026-08-22):** pyRevit's
> CPython bridge failed deployment the same way on two different
> machines — three environment-coupling causes, not code bugs,
> documented in `extensions/RevitCheck.extension/README.md`'s "Three
> CPython gotchas". Production is moving to a compiled native Revit
> add-in. The pyRevit extension's job was development plus proving the
> Revit → BCF → Forma → Revit round trip — **that proof succeeded
> 2026-08-22**, confirmed by the user as the entire scope pyRevit
> needed to cover. No further pyRevit feature work is planned; see §12
> for the full reasoning, including the decision on what happens to
> the Python rule engine (kept — see below) and what the add-in needs
> to build next (dimension-vs-model verification, not more triage).
>
> **PLANNING.md §13 (2026-08-24):** the native add-in's first real
> feature — metadata reconciliation — is now built, deployed, calibrated
> against real data, and merged. §14 is the current plan for what's next:
> the dimension/sheet/view adapter, then dimension-vs-model verification.
> **§14 update (2026-08-25):** the adapter half (Track A) is now built and
> validated against a real cloud-worksharing model the same day — 59
> sheets/833 views/538 dimensions, 0 extraction errors, run via the
> updated Capture Model button. `native/tools/RevitCheck.CheckRunner`
> (new) then ran both dimension checks against that capture off-Revit and
> found 125 real issues, including two override-consistency findings —
> both triaged by the user as correct, expected flags, not bugs (sheet
> 2871008 is diagrammatic; sheet 2871071's is a pipe-clearance call-out)
> — see PLANNING.md §14. Track B (dimension-vs-model verification):
> comparison logic is still unbuilt. `InspectDimensionGeometry.pushbutton`
> was run seven times (2026-08-25) — runs 1-4 fixed real code bugs,
> runs 5-7 found real facts about the drawings, culminating in run 7:
> a whole real pile-layout view swept, zero `CUT_EDGE` references
> anywhere. **Pile setout on this project is drafted tag-to-tag against
> a schedule, not tag-to-geometry — confirmed by the user to be
> PLANNING.md §5b's `geometry.setout_reconstruction` all over again,
> same real sheet number (2873041) as the old PDF/DWG pipeline's BR08
> sample.** Two real dimensioning conventions now confirmed on this
> project: piles (setout table + bearing/dimension-chain reconstruction,
> the §5b technique) and deck/abutments (direct-to-geometry — what
> runs 1-5's `Face.Project` work was actually for). A third real
> wrinkle: the pile schedule is populated by a Dynamo script that isn't
> always rerun after the model changes, so **two pile checks are
> wanted, not one** — drawing-vs-schedule (the direct §5b port) and
> model-vs-schedule (comparing each `Pile`'s own current position to
> the schedule directly — cheaper if `ProjectLocation.
> GetProjectPosition` already gives real coordinates for this model,
> unconfirmed). `native/diagnostics/InspectPileSetout.pushbutton`
> (new) was built to answer the remaining real unknowns (schedule
> field structure, origin/pile parameter names, whether
> `GetProjectPosition` works here) before writing either port — not
> yet run. **This project already has real history bearing directly on
> the coordinate question** — `native/`'s current model is the literal
> same file as the old PDF/DWG pipeline's BR08 sample (both
> `T2DPAA-T2D-C3S-BR-M3D-100304`), which found a real, twice-confirmed
> Survey Point convention (~278,000mE/6,129,000mN) for this client, and
> separately a cautionary case from a *different* client (Flinders'
> "Massachusetts problem" — an unconfigured Revit template default
> masquerading as real geodata). See PLANNING.md §14 for the full
> detail.
>
> **PLANNING.md §14 update (2026-08-26):** `InspectPileSetout.pushbutton`
> was run for real, and the model-vs-schedule pile check is now fully
> unblocked — no bearing/chain reconstruction needed for it at all.
> `ProjectLocation.GetProjectPosition(Pile.Location.Point)`, the pile's
> own `XYZ_Easting`/`XYZ_Northing` parameters, and the schedule's
> `EASTING`/`NORTHING` row (joined via `DIT_SiteID` = the schedule's
> `SITE ID`) **agree to sub-millimetre precision** on all 4 real piles
> sampled — this model's Survey Point is genuinely configured, not a
> Massachusetts-style default. **Correction to the read above:**
> `DIT_StartEasting`/`DIT_StartNorthing` being identical across piles
> is not staleness — confirmed by the user to be a deliberate client
> convention giving the *bridge's centre* location (matching the sheet
> title-blocks' own lat/long), not a per-pile position; `XYZ_Easting`/
> `XYZ_Northing` are the real per-pile parameters. A same-day re-run of
> `InspectDimensionGeometry.pushbutton` across the whole pile layout
> view (46 dimensions) turned run 7's qualitative tag-to-tag finding
> into hard numbers: 87/92 references resolve to `AnnotationSymbol`,
> 5/92 to `Grid`, **zero** to any model geometry — confirms
> drawing-vs-schedule genuinely needs proximity/tag-matching, not
> `Face.Project`. Next: build the model-vs-schedule pile check as its
> own ribbon button ([[track-b-per-element-type-buttons]]). See
> PLANNING.md §14 for the full detail, including the exact per-pile
> numbers.
>
> **PLANNING.md §14 correction (2026-08-26, later the same day):** the
> paragraph above is misleading on one point — `XYZ_Easting`/
> `XYZ_Northing` must NOT be used as the check's comparison input.
> Confirmed by the user: those parameters are themselves written by the
> same Dynamo script that (re)writes the schedule, from the insertion
> point at the time it last ran, so comparing one against the other
> compares the same stale value to itself — a pile moved without a
> Dynamo rerun would show as clean, silently, forever. The sub-mm
> agreement above only proves nobody had moved a pile since the last
> Dynamo run; it was never three independent confirmations, only one
> (`GetProjectPosition`, computed from live geometry) agreeing with a
> self-consistent pair. Fixed: `ElementMetadata.ProjectPositionEastingMm`/
> `NorthingMm` (new Core IR field, populated only by a live
> `GetProjectPosition` call — genuinely new Addin-side work, still
> unbuilt) is what the check reads now, not any parameter. See
> PLANNING.md §14 for the full correction and the regression test that
> proves it.
>
> **PLANNING.md §14 update (2026-08-26, later still): the dimension
> buttons' BCF export was proof-of-concept plumbing, not the intended
> steady-state output — confirmed by the user.** A real run produces
> ~250 triage candidates, not confirmed problems, and shipping all of
> them to Forma isn't the goal — that BCF wiring only ever existed to
> prove the round trip (§12). Fixed: `DimensionProvenanceCommand`/
> `DimensionOverrideConsistencyCommand` now pass `writeBcf: false` to
> `IssueOutput.WriteNextToModel` (new parameter, defaults true so
> `MetadataReconciliationCommand` — already verdicts, not triage — is
> unaffected); JSON/CSV still write. Real BCF export now belongs to a
> later reconciliation stage (not built) that prunes triage against
> investigation-check verdicts first. Also extended the same day:
> `InspectDimensionGeometry.pushbutton` gained pile-proximity matching
> (`_collect_piles`/`_nearest_pile`, 2D/X-Y only — real data shows a tag
> reference's Z sitting ~180m from a real pile's) so a pile dimension's
> own stated value can be checked directly against the measured distance
> between its two nearest real piles, no schedule needed — not yet run.
> `_collect_piles` is scoped to the active view (`FilteredElementCollector(doc,
> view.Id)`), not document-wide — the user's own suggestion, avoiding
> false matches against foundation instances from unrelated structures.
>
> **PLANNING.md §14 update (2026-08-26, same day, stage 3 design
> started):** `Core/Reporting/InvestigationReconciliation.cs` (new,
> tested, not yet wired to any command) prunes dimension triage against a
> per-dimension investigation check's verdicts — the real join needed the
> investigated-scope to be a parameter separate from the issues list
> (absence-of-issue is ambiguous between "checked, clean" and "never
> examined," and treating it as clean would violate "report a coverage
> indicator, never fail silently"), and most real triage volume needed
> "un-rolling" first (`DimensionProvenanceCheck`'s view-rollup issue now
> carries `drafted_dimension_ids` in its `SuggestedFix`, a small additive
> fix). `revitcheck.pile_model_schedule_consistency` deliberately doesn't
> participate — it's keyed on Pile ElementIds, never a Dimension's, so it
> naturally never matches; it should export to BCF directly once wired to
> its own command, not route through this. Blocked on the one
> investigation check that would actually feed it (the pile-proximity
> match above) not existing as a real Core check yet.
>
> **PLANNING.md §14 refinement (2026-08-26, same day, per the user's own
> direction): three outcomes, not two.** `Reconcile` now returns a
> `ReconciliationResult` (`ConfirmedProblems`/`NeedsManualReview`/
> `StillOpenTriage`), not one flat list — some dimensions genuinely need
> drawing interpretation a script can't do, and a future investigation
> check marks those with `Category = InvestigationReconciliation.
> ManualReviewCategory` rather than being forced to guess "clean" or
> "problem". Only `ConfirmedProblems` is meant for automatic BCF export.
> See PLANNING.md §14 for the full detail.
>
> **PLANNING.md §14 update (2026-08-26, later still): the tag-to-pile
> approach is now decisively validated on real data, and a third pile
> check is built.** A view-scoping fix to `InspectDimensionGeometry
> .pushbutton`'s pile collection (281 "piles" document-wide → 43,
> matching the real ~47) unblocked a real re-run: 31 of 32 matched
> dimensions agree with real pile geometry to sub-millimetre precision,
> and the one real outlier turned out to be dimensioned to a setout-point
> marker, not a pile — confirmed directly by the user, not a drafting
> bug. Reconstructing each chain's bearing from live geometry and
> comparing to the printed bearing call matched all 4 real chains to
> within a third of an arcsecond. Per the user directly — the results are
> clear enough that a manual-review fallback isn't needed here, build the
> automatic comparison. Built: `revitcheck.pile_chain_bearing_consistency`
> (`Checks/PileChainReconstruction.cs`/`BearingText.cs`/`BearingMath.cs`),
> new IR (`ReferenceInfo.LocalPoint`/`ElementMetadata.LocalPoint`/
> `Ir/TextNoteInfo.cs`), 32 new tests including one built from the literal
> real pile/note numbers — 289 Core tests passing. Not wired to a command
> yet. See PLANNING.md §14 for the full numbers.
>
> **PLANNING.md §15 (2026-08-25):** a real cloud-model run of Metadata
> Reconciliation crashed — `Document.PathName` for a Revit Cloud
> Worksharing model isn't a filesystem path, and the code didn't guard
> against that, only against an empty path. Fixed (`DocumentPaths.cs`);
> full diagnosis and the .NET Framework-vs-.NET-8 `Path` behaviour gap
> that made it not reproduce on this Mac are in §15.
>
> **PLANNING.md §16 (2026-08-26): the interactive checking workflow tying
> triage/investigation/reconciliation together is designed, not built.**
> Full plan saved at `~/.claude/plans/an-idea-of-how-floating-peacock.md`
> — read it before starting this work, don't re-derive it. Headline: a
> real correctness bug (`PileChainBearingConsistencyCheck`'s issues are
> pile-keyed, `Reconcile` joins on dimension ids — feeding it unmodified
> would silently reconcile a flagged chain's dimensions as clean) was
> found and designed around before any code was written, via a new
> `ExpandByElementIdList` helper. First feature needing cross-command
> session state, a custom code-behind-only WPF window (no XAML/SDK
> change), and the `ExternalEvent` pattern (all new to this codebase).
> Manual sheet-resolution (for diagrammatic sheets no check will ever
> resolve, e.g. construction-sequence drawings) deliberately cannot
> override a real confirmed-problem finding. Staged: Stage 1 is pure
> Core, testable off-Revit today; Stage 2 builds the two pile checks'
> first real Addin commands (needed regardless of this feature); Stage 3
> is the window/session/combined-triage-command wiring; Stage 4 is real
> Revit-machine validation.
>
> **PLANNING.md §16 update (2026-08-28): Stage 1 built.**
> `InvestigationReconciliation.ExpandByElementIdList` plus
> `Core/Reporting/CheckingSession.cs`/`CheckingSessionSerializer.cs` are
> built and tested — 313 Core tests passing, `dotnet build` clean across
> the whole solution. The regression Stage 1 exists to prevent is directly
> tested end to end.
>
> **PLANNING.md §16 update (2026-08-28, same day): Stage 2 built too, not
> yet run on the Revit machine.** `PileModelScheduleConsistencyCommand`/
> `PileChainBearingConsistencyCommand` are real ribbon buttons now. The
> Addin-side geometry work both checks were blocked on is built:
> `RevitMetadataElementSource.Collect` gained an opt-in
> `populateLivePosition` flag (live `GetProjectPosition` + `Location.Point`
> per element, off by default for API cost); `RevitDimensionSource` now
> populates `ReferenceInfo.LocalPoint` and collects `TextNote`s; a new
> `Adapters/RevitScheduleSource.cs` reads every `ViewSchedule`, skipping
> the real two-row header artifact (PLANNING.md line 695) by matching each
> row against its own schedule's resolved headers rather than a hardcoded
> row count. `dotnet build` clean including the net48 Addin — compile-only
> verification; **validate both buttons for real before starting Stage
> 3**, matching this project's own pattern that every real correction so
> far came from an actual machine run.
>
> **PLANNING.md §16 correction (2026-08-28, real machine run, same day):
> both commands were whole-document, and shouldn't have been.** The user
> ran both: 281 piles, 0 captured schedules, 62 extraction errors (Pile
> Model/Schedule); 281 piles, 1297 dimensions, 3790 text notes (Pile Chain
> Bearing) — and confirmed directly these tools are meant to run on the
> active view, not the whole drawing set. 281 is the exact same
> document-wide over-collection number `InspectDimensionGeometry
> .pushbutton` already fixed once this session (real count ~43-47) — a
> real miss carrying that lesson forward, not a plan ambiguity. Fixed same
> day: `RevitMetadataElementSource.Collect`/`RevitDimensionSource.Collect`
> both gained a `scopeView` parameter (the live `View`, not a name
> re-lookup); both pile commands now pass `ActiveView`. Schedule collection
> stays whole-document deliberately — not "in" a view the way a pile
> element is. Separately: 0 schedules with 2 real ones expected, and 62
> extraction errors nobody could read (no error *text* was ever surfaced,
> only a count) — new `Commands/ExtractionErrorSample.cs` fixes the
> visibility gap. **Same-day follow-up, per the user's own suggestion:**
> `RevitScheduleSource.Collect` now only reads a schedule's expensive body
> cells when its headers already resolve all three of
> `PileModelScheduleConsistencyCheck`'s own id/Easting/Northing candidates
> (the identical filter that check already applies downstream, just
> hoisted earlier) — not a proven diagnosis of the 62 errors, but a
> well-justified fix for the most likely cause.
>
> **PLANNING.md §16 update (2026-08-28, real machine re-run, screenshots):
> Pile Chain Bearing is fully validated; Pile Model/Schedule's real root
> cause is now diagnosed and fixed.** Pile Chain Bearing: 43 piles, 46
> dimensions (the exact figure already on record from the manual
> diagnostic), 31 text notes, 0 issues — **done, no further work needed.**
> Pile Model/Schedule: the header filter worked exactly as designed (62
> errors → 2, both on the two real named schedules) and the real error
> text answered the question the previous update could only guess at:
> `Illegal attempt to modify document. Reason: Changes are disabled for
> the active document!` — `ViewSchedule.GetTableData()`/`GetCellText` can
> internally need document-modify permission even though it's conceptually
> a read, and `TransactionMode.ReadOnly` blocked that. Fixed:
> `TransactionMode.Manual` with the schedule read wrapped in a `Transaction`
> that's always `RollBack()`'d, never committed. Not yet confirmed — needs
> one more real run.
>
> **PLANNING.md §16 update (2026-08-28, later the same day): the
> transaction fix worked, but every pile (43/43) now fails its schedule
> match.** Real issue descriptions confirmed a flat zero-match join for
> every pile, not "ambiguous," not a numeric mismatch — a systematic bug
> (4 real piles already confirmed sub-mm agreement, so 100% failure can't
> be real drift). `PileModelScheduleConsistencyCommand` gained a permanent
> `ScheduleDiagnostics` summary (the check's own `candidateSchedules`
> filter, made visible): each candidate schedule's name, real row count,
> and its first row's literal id value — enough to tell "row-skip heuristic
> ate every row" from "ids don't textually match" without dumping real
> coordinates. Needs one more real run.
>
> **PLANNING.md §16 update (2026-08-28, later still): schedule rows are now
> read off the schedule's own backing elements, not rendered table text.**
> The user asked whether a schedule keeps a link back to its real elements —
> yes, via `FilteredElementCollector(doc, schedule.Id)`, a genuine Revit API
> pattern. Real fix, not another patch: resolve each candidate column's
> real bound parameter (`ScheduleField.ParameterId`) and read it directly
> off each backing element, sidestepping `GetCellText`'s format-fragile
> rendered text entirely. Every Revit API member used was verified against
> the real `RevitAPI.dll` (via `System.Reflection.MetadataLoadContext`, no
> Revit machine needed) before writing any code. `dotnet build` clean.
> Needs one more real run.
>
> **PLANNING.md §16 update (2026-08-28, later still): the element-based
> read worked, but the join still failed 43/43 - real diagnostics found
> the exact cause.** Rows were genuinely captured correctly this time
> (19+24=43, matching pile count, a correctly-formatted first-row id
> `PIL232126` identical to an already-failing pile's own key). Piles read
> their key via `AsString()`; the schedule reader read every column,
> including id, via `AsValueString()` first — `ScheduleInfo.RowsForKey`'s
> `Ordinal` join silently breaks on any divergence there. Fixed:
> `ReadParameterText` now branches on `Parameter.StorageType` — `AsString()`
> first for `String` (matching piles exactly), `AsValueString()` first for
> numeric. A `CharacterCheck` diagnostic (hex code points for one matched
> pair) was added alongside the fix so a wrong hypothesis would still show
> why. `dotnet build` clean. Needs one more real run.
>
> **PLANNING.md §16 update (2026-08-28, later still): the id-join fix
> worked — `CharacterCheck` confirms an exact byte-for-byte match — but the
> issue count is still exactly 43.** Since the join is now provably
> correct, this can't be the same "no matching row" bug. `CharacterCheck`
> never verified Easting/Northing. Added `PositionCheck` for the same
> matched pair: raw captured Easting/Northing text, whether it parses as a
> bare number (`AsValueString()` may apply the project's display unit/
> suffix, which `TryParseMetresToMm` doesn't expect), and the pile's own
> live position. `dotnet build` clean. Needs one more real run — also
> asked the user for real issue descriptions from this run's own output,
> no rebuild needed.
>
> **PLANNING.md §16 update (2026-08-28, later still): the CSV alone found
> the exact bug — no machine run needed.** Every one of 43 issues showed
> the schedule's parsed Easting/Northing at exactly 1000× the live value.
> `AsValueString()` applies the *parameter's own* display unit — confirmed
> millimetres for this project's `XYZ_Easting`/`XYZ_Northing`, not the
> metres the schedule column's heading implies. Fixed: read the raw
> internal value via `AsDouble()` instead (always decimal feet for a real
> Length spec), convert to mm directly, divide by
> `RuleConfig.ScheduleMetresToMm` before handing it back as row text, so
> the check's existing, unchanged `TryParseMetresToMm` recovers the
> correct value instead of scaling it twice. `dotnet build` clean, no Core
> changes. Needs one more real run — the fourth real bug found and fixed
> in this one check's schedule-reading path in a single day. See
> PLANNING.md §16 for the full detail.

Two categories of check:

1. **Drafting** — standards and convention compliance, annotation and
   dimension completeness, cross-sheet consistency, spelling, revision
   consistency, plus project-specific rules.
2. **Geometry** — dimensional consistency within and across views, and
   whether what a drawing states matches what the model actually says.

Scope is **internal projects only**, confirmed by the user, so a Revit
model is always available. Models are **cloud-workshared in Autodesk
Forma**, which is the firm's approved and secure store for project
information — so persistent state living off the machine is legitimate,
and the tool may keep memory between runs.

> This **corrects** the earlier "nothing leaves the machine" position
> stated here until 2026-08-18. That was never a client requirement; it
> was inherited from PLANNING.md §10's air-gap design for the parked
> web stack. See §10 for the correction and why the underlying
> confidentiality concern is still answered.

**Read `PLANNING.md` before making structural changes** — it holds the
reasoning, not just the "what". §5c records why the checks moved into
Revit and §5d the open reporting question; §5 (all of it) is the domain
knowledge about what actually goes wrong on real drawing sets, and
remains correct regardless of where the checks run. §10 is **withdrawn**
— read its superseded note before treating anything in it as a rule.

### There is a large archive, and you should know what is in it

This project spent weeks building `src/pdfchecker/`, a working pipeline
that checked PDF/DWG/IFC *exports* instead. It was parked on 2026-08-18.

- **`ARCHIVE-pdf-dwg.md`** — what it did, what real drawing sets look
  like, and which assumptions broke when a second client's files
  arrived. Genuinely worth reading before assuming anything about
  drafting conventions.
- **`git checkout pdf-dwg-final`** — the code itself, its 276-test
  suite, and the real BR06/BR08 sample sets.
- **`BACKEND_REVIEW.md`** — a review of that backend taken just before
  the pivot.
- **`git show frontend-plan-final:FRONTEND_PLAN.md`** — the frontend
  implementation plan, never merged. §5c took the web stack off the
  path; tagged rather than deleted because it was the only copy.

Both tags are the only surviving reference to their branches: every
merged branch was deleted on 2026-08-18 and `main` is now the sole
branch, so nothing is recoverable by branch name any more.

The one lesson from it that governs new work: **logic built on domain
invariants survived a second client; logic built on client conventions
broke.** The Revit API is an invariant. That is the whole argument for
this direction — see PLANNING.md §5c.

## Layout

```
extensions/RevitCheck.extension/     # the pyRevit extension
  lib/revitcheck/                    # the package — here, not under src/,
                                     # because pyRevit puts <extension>/lib on
                                     # sys.path automatically. ONE copy: the
                                     # files the buttons import are the files
                                     # the tests import
    ir.py                            # plain dataclasses, raw facts, millimetres
    issue.py                         # Issue + derived issue_id + sort_issues
    catalog.py                       # @register, RuleConfig, run_checks
    capture.py                       # RevitModel <-> JSON (the dev loop)
    report.py                        # summarize / to_json / to_markdown / to_bcf
    bcf.py                           # Issues -> BCF 2.1 (.bcf), split at
                                     #   100/file for Forma's import cap
    en_gb_variants.py                # curated en-GB spelling variants — data
                                     # landed ahead of its rule, rescued from
                                     # the parked tree (see its docstring)
    adapters/revit_source.py         # the ONLY module importing the Revit API
    checks/dimensions.py             # revit.dimension_provenance +
                                     #   revit.dimension_override_consistency
    checks/coverage.py               # revit.capture_coverage
  RevitCheck.tab/Checks.panel/       # the buttons — thin by design
config/                              # firm_glossary.json, project_glossary.json
scripts/check_capture.py             # run the checks against a captured model
tests/revit/                         # 151 tests, ~0.1s, no Revit needed
```

`extensions/RevitCheck.extension/README.md` covers installing the
extension in pyRevit and what each button does.

## The layering rule, which is the one that matters

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

- **The adapter extracts facts and judges nothing** — no classification,
  no tolerances, no filtering. `ReferenceInfo` records that an element is
  view-specific; deciding that this means "drafted" happens in
  `checks/dimensions.py`. So retuning a classification never invalidates
  a capture and never requires a trip back to a Revit machine.
- **Anything that grows inside a `script.py` is logic only debuggable
  inside Revit.** Buttons stay thin.

Units: **every length in the IR is millimetres.** Revit's internal unit
is decimal feet regardless of project settings, so the adapter multiplies
by `304.8` — no `UnitUtils` call, therefore no `UnitTypeId` vs
`DisplayUnitType` version branching, which is the usual source of Revit
version breakage.

## Development setup, and why it looks like this

Development happens on a Mac with **no Revit**. Revit runs on a
locked-down Windows work machine where the only LLM access is Copilot.
`capture.py` is what makes that workable, and it is the workflow rather
than a convenience feature:

```
# on the Revit machine, once per project
Capture Model  ->  BR06.capture.json

# anywhere, as often as you like
python scripts/check_capture.py BR06.capture.json
python -m pytest tests/ -q          # 151 tests, ~0.1s, no dependencies
```

No install step, no virtualenv needed for the tests: `revitcheck` is
**stdlib-only** (plus the Revit API inside the adapter), and
`tests/revit/conftest.py` puts `extensions/RevitCheck.extension/lib` on
`sys.path` exactly as pyRevit does. `pytest` is the sole dev dependency.
There is no `[project]` table in `pyproject.toml` and that is deliberate
— see the comment in it.

**CI** (`.github/workflows/ci.yml`) is one job across Python 3.9 / 3.12 /
3.13 — the code has to run on whatever CPython pyRevit ships (3.8/3.9 on
pyRevit 4.8, 3.12 on pyRevit 5), which is not a version this repo picks.
The `compileall` step is the important one: `adapters/revit_source.py`
and the button scripts cannot be imported without Revit, so no test will
ever touch them, and byte-compiling is the only automated check they get.

**One real capture is committed**, deliberately:
`samples/T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json`. All tests still
use the synthetic IR builders in `tests/revit/conftest.py` — the real
capture isn't test fixture data, it's kept as the C#-port test fixture
PLANNING.md §12 names (real client geometry, sheet numbers and view names,
so treat committing one the way this project treats uploaded drawings and
check before it lands in git). Its per-view dimension attribution predates
the `OwnerViewId` fix below and should not be trusted until it's replaced.
A handful of other real-capture variants and unrelated debug artifacts from
the same 2026-08-19/21 sessions were cleaned out of `samples/` on
2026-08-23 (stale, superseded, or already fully written up elsewhere) —
this file is the one still worth keeping.

## Built state

| Rule | What it does |
| --- | --- |
| `revit.dimension_provenance` | For each dimension, do its references resolve to model geometry, a datum, or view-specific linework? Four-way classification, rolled up per view. |
| `revit.dimension_override_consistency` | Where a drafter typed over the measured value, is the difference explainable as rounding to a sensible grid? A stated limit (`500 MIN.`) is checked against the limit instead. Always reports how much was checkable. |
| `revit.capture_coverage` | Turns the adapter's per-element extraction failures into a visible Issue, plus a separate low-severity note for any workset excluded from the capture by user choice. |
| `revitcheck.metadata_reconciliation` | Native add-in only (no Python equivalent) — joins captured model elements to an external reference CSV via a per-run-chosen mapping file, flags missing/mismatched fields. Built, wired to a real ribbon button, deployed, and calibrated against two real reference tables on a real model — see PLANNING.md §13. |
| `revitcheck.pile_model_schedule_consistency` | Native add-in, Core-side built and tested 2026-08-26; ribbon button (`PileModelScheduleConsistencyCommand`) built 2026-08-28, view-scoped and schedule-read-narrowed the same day; a real re-run diagnosed the remaining error as a `TransactionMode.ReadOnly` conflict with `ViewSchedule.GetCellText` (fixed via `TransactionMode.Manual` + a rolled-back `Transaction`) — not yet confirmed clean on a re-run. Compares each pile's own LIVE position (`ElementMetadata.ProjectPositionEastingMm`/`NorthingMm`, populated by a `GetProjectPosition` call — `RevitMetadataElementSource`'s opt-in `populateLivePosition` flag) against its live pile schedule row (joined via `DIT_SiteID`, read by the new `Adapters/RevitScheduleSource.cs`) — the "model-vs-schedule" half of the two pile checks named in PLANNING.md §14, catching a pile moved in the model without the schedule's Dynamo script being rerun. Deliberately does NOT compare against `XYZ_Easting`/`XYZ_Northing` — those are Dynamo-written from the same insertion point the schedule reads, so they're the value being audited, not an independent check on it (a real design bug caught and fixed same-day, see PLANNING.md §14). See PLANNING.md §16 and native/README.md. |
| `revitcheck.pile_chain_bearing_consistency` | Native add-in, Core-side built and tested 2026-08-26; ribbon button (`PileChainBearingConsistencyCommand`) built 2026-08-28, view-scoped the same day. **Fully validated on a real machine run, 2026-08-28**: 43 piles, 46 dimensions (the exact figure already on record from a manual diagnostic run on this same view), 31 text notes, 0 issues. Reconstructs each real pile chain's own bearing from live model geometry (`Checks/PileChainReconstruction.cs`, tag-to-pile proximity matching) and compares it against the drafted bearing call nearest to it — validated end-to-end against real data: all 4 real chains reconstructed from a real pile-layout view matched their real printed bearing call to within a third of an arcsecond. A simpler, stronger mechanism than the originally-planned drawing-vs-schedule §5b DXF-chain-walk port — no dimension-chain traversal or witness-point matching needed. See PLANNING.md §16 and native/README.md. |

**Export:** `bcf.py` writes the full issue list as BCF 2.1 (`.bcf`),
split at 100 issues per file for Forma's import cap, exposed as
`report.to_bcf` alongside `to_json`/`to_markdown` and as the **Export
BCF** button. The Revit → BCF → Forma round trip **is proven**
(PLANNING.md §12, 2026-08-22) — real Forma import, after fixing four
real rejections in a row (extension, `project.bcfp`/camera, a
Viewpoint on every Topic, and the actual `<Viewpoint>` XML shape being
a child element, not the attribute form this module invented). Every
finding anchors to its **sheet** (`SheetInfo.unique_id`, denormalized
onto `ViewInfo.sheet_unique_id`), not the dimension/view element
itself — changed after Forma warned that some issues "may not match
the current model," on the theory that a Dimension/View has no 3D
placement for a model viewer to resolve where a sheet is exactly what
a document-coordination platform navigates to directly. `unique_id`
still flows adapter → `ir.py` → `Issue` as the fallback anchor when a
finding has no sheet to anchor to.

Notes worth not rediscovering:

- **Grids, levels and reference planes are datums, not risks.**
  Dimensioning to a grid is good practice and must not be lumped in with
  detail linework. `ImportInstance` **is** a risk despite not being
  view-specific — an imported DWG is a static snapshot of someone else's
  file.
- **A wholly-drafted view is one finding, not twenty.** It is a larger
  and different finding, and it is the unit the follow-up tool operates
  on — `drafted_views()` returns exactly that list.
- **Drafting views get different wording and severity.** A section could
  have been live and someone chose otherwise; a drafting view never had
  a model behind it.
- The provenance check reports **triage, not verdicts** — that the file
  cannot answer whether a dimension is right, not that it is wrong. Per
  the user's standing position: *assume nothing is trustworthy or you
  will be caught out.* An override goes stale exactly as a witness line
  does.
- **`Dimension.OwnerViewId` is not trustworthy read document-wide.**
  Found 2026-08-22: a real capture attributed 430 dimensions to one
  view that had roughly a dozen, confirmed by Select-by-ID in Revit
  (view-specific elements can't be selected unless their real owning
  view is active; several of the "extra" ones couldn't be, while the
  blamed view was). The adapter now collects dimensions **per view**
  (`FilteredElementCollector(doc, view.Id)`) instead of once
  document-wide — `view_id` comes from the loop, never read back off
  the element. This also scopes collection to views placed on a sheet
  by default (confirmed as the right call for this project: a heavy
  template leaves thousands of unplaced premade views, each of which
  would otherwise cost its own collector call for nothing). The
  committed sample capture predates this fix and its per-view
  attribution should not be trusted until it's replaced.

## Working conventions

- **Any change to the IR** affects every rule — update PLANNING.md §3
  alongside the change.
- **New rules go in the catalog** (`@register`), not hardcoded into a
  button or into pipeline logic, so a project-specific check is a config
  change rather than a code change.
- **Tolerances must be configurable** (`RuleConfig`), never hardcoded
  constants — and say in a comment whether a figure is calibrated
  against real data or inherited as a placeholder.
- **Report a coverage indicator; never fail silently.** A rule that
  found nothing because nothing was in scope must not look like a rule
  that found nothing because the model is clean. This has already bitten
  this project twice: a check scope that ran zero rules and looked
  clean, and a dimension rule that was structurally inert on a whole
  client's drawings and reported 0 issues.
- **Skip rather than guess.** An override that isn't a clean number, a
  reference that won't resolve, a convention not yet seen — record it as
  unchecked, don't infer.
- **Wait for real data before writing convention-specific logic.** Every
  extractor in this project's history that guessed ahead of a sample had
  to be rewritten when one arrived.
- **Rules must be auditable** — an Issue says how it reached its
  conclusion (which reference, which segment, which view). This is a
  compliance/review tool, not a black box.

## Next

**The actual current frontier: the interactive checking workflow tying triage → investigation → reconciliation → BCF export together, designed 2026-08-26, not yet built.** Full plan at `~/.claude/plans/an-idea-of-how-floating-peacock.md` — read it before starting, don't re-derive it; §16 above has the headline points. Start with Stage 1 (pure Core, testable off-Revit today) and Stage 2 (the two pile checks' first real Addin commands — they don't exist as buttons yet, and are worth building regardless of the rest of this feature).

**The native add-in's metadata-reconciliation path is done, deployed,
and calibrated against real data (2026-08-24, PLANNING.md §13) — this is
no longer "next", it's built.** `revitcheck.metadata_reconciliation` +
`Capture Model` are real ribbon buttons, merged to main. Don't propose
re-scoping or re-validating that path without a specific real-data reason
to — see PLANNING.md §13 for what was found and fixed.

**The dimension/sheet/view adapter (phase 6) is built and its collection
step validated on the Revit machine (2026-08-25).** `~/.claude/plans/crispy-hopping-key.md`
(written 2026-08-24) covers both tracks — Track A executed 2026-08-25, read
it before re-deriving Track B from scratch:

1. **Port the adapter** (`revit_source.py`'s
   `_collect_dimensions`/`_collect_sheets_and_views` → C#) and wire up
   `DimensionProvenanceCommand`/`DimensionOverrideConsistencyCommand` —
   **done**: `Adapters/RevitDimensionSource.cs`, both commands, both ribbon
   buttons (order: Capture Model → Dimension Provenance → Dimension
   Overrides → Metadata Reconciliation), `CaptureModelCommand` extended to
   capture sheets/views/dimensions too. `dotnet build` clean, all 218 tests
   still pass. **Collection now confirmed against a real document**: the
   user ran Capture Model against the real cloud-worksharing model the same
   day and uploaded the result — 59 sheets, 833 views, 538 dimensions, 0
   extraction errors. Running both dimension checks against that capture
   (via the new `native/tools/RevitCheck.CheckRunner`, off-Revit) found 125
   real issues — see PLANNING.md §14. Still open: the *check-producing*
   ribbon commands (Dimension Provenance/Overrides) haven't themselves been
   run on the Revit machine yet, so the §15 second-pass save-dialog fix is
   still unconfirmed there specifically — this run used Capture Model,
   which already had a working dialog before that fix.
2. **Verify drafted dimensions against the model** — the harder half.
   Comparison logic is still entirely unbuilt; the required first step,
   the diagnostic below, is now built and has been run (2026-08-25/26,
   see below for both diagnostics' real results).
   `revit.dimension_provenance` and `revit.dimension_override_consistency`
   currently report *triage* — a dimension is drafted/overridden — never
   *verdicts* — whether that drafted/overridden value has actually
   drifted from the model. Bridge curves and the need for "clean" issued
   drawings mean some dimensions will always be drafted or overridden,
   permanently, not a defect any amount of filtering removes — a
   drafted dimension is only a real problem if it disagrees with the
   model. Same problem the parked PDF/DWG pipeline's
   `geometry.ifc_setout_consistency` solved for piles (ARCHIVE-pdf-dwg.md;
   real IFC comparison, 0 false positives on 24 real piles matched
   within 10mm) — didn't survive the pivot into Revit, because "the
   model is already the source of truth" never got followed through to
   actually comparing against it. One simplification versus that old
   approach: no IFC intermediary needed — the model is one API call
   away. `revit.dimension_provenance`'s `drafted_views()` is the
   existing scope this consumes. **Zero existing code in `native/`
   touches Revit's geometry API** (`Curve`/`XYZ`/`Options`/
   `GeometryElement`/`Solid`/`BoundingBoxXYZ` — confirmed via grep,
   2026-08-24) — this is genuinely new, so the plan's first step is a
   throwaway diagnostic against real drafted views, not writing
   comparison logic blind. That diagnostic —
   `native/diagnostics/InspectDimensionGeometry.pushbutton/`, mirroring
   `InspectElements.pushbutton`'s role and disposal discipline — is now
   built and run seven times against real data (2026-08-25) — runs 1-4
   each found and fixed a real code bug, runs 5-7 found real facts
   about the drawings, run 7 (a real pile-layout view swept, zero
   `CUT_EDGE` references anywhere) being the one that mattered:
   **confirmed by the user to be PLANNING.md §5b's
   `geometry.setout_reconstruction` all over again** — pile setout on
   this project is drafted tag-to-tag against a live schedule, on the
   same real sheet number (2873041) the old PDF/DWG pipeline already
   solved this for. Two real dimensioning conventions confirmed:
   piles (chain+bearing reconstruction) and deck/abutments
   (direct-to-geometry — what the `Face.Project` work in runs 1-5 was
   actually for). The pile schedule is Dynamo-populated and can go
   stale after a model move, so two pile checks are wanted: drawing-
   vs-schedule (the direct §5b port) and model-vs-schedule (cheaper if
   `ProjectLocation.GetProjectPosition` already gives real coordinates
   for this model). `native/diagnostics/InspectPileSetout.pushbutton`
   was built to answer the remaining real unknowns, and **was run for
   real 2026-08-26**: `GetProjectPosition`, the pile's own `XYZ_Easting`/
   `XYZ_Northing` parameters, and the schedule's `EASTING`/`NORTHING`
   row agree to sub-mm precision on all 4 sampled piles — **the
   model-vs-schedule pile check is now fully unblocked, no bearing/chain
   reconstruction needed for it at all.** (Correction to the paragraph
   above: `DIT_StartEasting`/`DIT_StartNorthing` isn't a stale-position
   signal — it's a deliberate client convention giving the bridge's
   *centre* location, not the piles' own.) **Second correction, same
   day: `XYZ_Easting`/`XYZ_Northing` are NOT the pile's live position
   either — they're written by the same Dynamo script that (re)writes
   the schedule, so comparing one against the other compares the same
   stale value to itself and would miss a genuinely moved pile.** The
   check's actual comparison is `GetProjectPosition(Pile.Location.Point)`
   (a live call, populating the new `ElementMetadata.
   ProjectPositionEastingMm`/`NorthingMm` — real Addin-side geometry-API
   work, still unbuilt) against the schedule directly — the one thing
   "no bearing/chain reconstruction needed" was correctly saying is that
   this is still far cheaper than the §5b port, not that it needs no
   geometry API at all. Drawing-vs-schedule was expected to remain the
   harder, unbuilt half — a same-day `InspectDimensionGeometry.pushbutton`
   re-run across the whole pile layout view (46 dimensions) confirmed it
   numerically: 87/92 references resolve to `AnnotationSymbol`, 5/92 to
   `Grid`, zero to model geometry, so there's no `CUT_EDGE`/model
   reference for the old §5b DXF-chain-walk approach's mechanics to use.
   **Superseded 2026-08-26 by a stronger, simpler mechanism: reconstruct
   each pile chain's own bearing directly from live model geometry
   (tag-to-pile proximity matching) and compare it against the drafted
   bearing call — no dimension-chain traversal or witness-point matching
   needed at all.** Validated decisively against real data (31 of 32
   matched dimensions agree with real pile geometry to sub-millimetre
   precision; all 4 real reconstructed chains matched their real printed
   bearing call within a third of an arcsecond) and built the same day as
   `revitcheck.pile_chain_bearing_consistency` — see PLANNING.md §14 for
   the full numbers and the built state table above.

**Export findings as BCF — built and proven, 2026-08-22.** `bcf.py` +
`unique_id` + the sheet anchor are done (see Built state above), and a
real Forma import succeeded after fixing four real rejections in a row
— see PLANNING.md §12 for the full sequence of exact error text ->
diagnosis -> fix. The reasoning that got BCF chosen over six other
candidates is unchanged (PLANNING.md §5d): it is the only off-machine
format that keeps the element anchor, rather than degrading a finding
to a number someone retypes into Select by ID.

**Native-side port done too (2026-08-25).** `native/src/RevitCheck.Core/Reporting/IssueBcfWriter.cs`
is a line-for-line port of `bcf.py`, wired into all three native
check-producing commands so every run writes JSON, CSV, *and* BCF side by
side — see native/README.md's "Next" for the detail, including the
hand-rolled UUIDv5 (`System.Guid` has no built-in equivalent to
`uuid.uuid5`) verified against real Python output. Genuinely no Revit
machine needed to build or test this half — only confirming the output
still imports into Forma does.

One thing not to re-litigate: **the Forma Issues API cannot create
element-pinned issues** (`linkedDocuments` is not writable on creation),
which is what ruled it out as the primary sink.

**Confirmed by the user, 2026-08-22: this was the entire scope of what
pyRevit needed to do.** The Revit -> BCF -> Forma -> Revit round trip
is proven; no further pyRevit feature work is planned. §12's decided
direction (native add-in for production) starts next.

Also open, carried over from PLANNING.md:

- **Multi-hop survey-tolerance scaling** (§5's `base + per_hop×√hops`) —
  still live, awaiting a retaining-wall sample. Bridge chains are short;
  retaining walls are not.
- **Abutment beam placement**, specifically real-world height/elevation
  (bearing seat / soffit level) — this tool's responsibility, not the
  roads team's, and awaiting a real sample. Deck geometry is confirmed
  **out of scope** (roads team's).
- **Porting the drafting checks** — glossaries, en-GB variants and the
  precise check definitions carry over as data and semantics; the
  extraction layer does not. See ARCHIVE-pdf-dwg.md.

## Environment quirks worth knowing

- **`.venv` is Python 3.13 and holds only `pytest`** (26MB). Rebuilt
  from scratch on 2026-08-18: a virtualenv hardcodes its own path and
  does not survive the folder rename. The one it replaced was 359MB of
  PyMuPDF/pdfplumber/ezdxf/ifcopenshell, all of it for the parked
  pipeline.
- 3.13 rather than the 3.9.5 this project used to run on — that is an
  **x86_64 build under Rosetta on an arm64 Mac** and correspondingly
  slow. CI still gates 3.9, so the floor is covered without paying for
  it locally. Worth knowing before reading anything into an interpreter
  benchmark taken here: a local 3.9-vs-3.13 comparison mostly measures
  Rosetta. On CI, same runner both sides, the real gap was ~13%.
- **The project folder is `~/projects/revitcheck`**, renamed from
  `pdf checker` on 2026-08-18 along with the repo. Claude Code keys its
  per-project state on the path, so
  `~/.claude/projects/-Users-petergriggs-projects-pdf-checker` was moved
  to `...-revitcheck` at the same time — memory included. Anything still
  naming the old path is stale.
- **Git remote** is `https://github.com/badger584114/revitcheck.git`
  (private, renamed from `pdf-dwg-checker` on 2026-08-18 — GitHub keeps
  a redirect, so an old clone's remote still works). Two failure modes seen on this machine: (1) a GitHub HTTP/2
  push bug (`RPC failed; HTTP 400`) — fixed by `git config http.version
  HTTP/1.1` plus a larger `http.postBuffer`, already set in this repo's
  `.git/config`, so a fresh clone needs it re-applied; (2) GitHub accepts
  only a Personal Access Token as the HTTPS password, and a stale
  Keychain credential produces a similar-looking HTTP 400 — clear it with
  `git credential-osxkeychain erase`.
- The **`gh` CLI is installed and authenticated** (`badger584114`) — use
  it for PRs (`gh pr create`, `gh pr merge`) rather than the REST API.
- `samples/Flinders/` (201MB, a third client's real drawings) was never
  tracked and was deleted from disk 2026-08-23 — its findings were already
  fully written up in ARCHIVE-pdf-dwg.md, so nothing was lost. If it
  reappears on disk from a future export, it must stay untracked; do not
  `git add` it.
