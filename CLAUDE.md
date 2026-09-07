# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.

**This file is current state.** The dated reasoning behind everything
here lives in PLANNING.md, which is the audit trail and is written
append-only. When the two disagree, this file is right about *what is
true now* and PLANNING.md is right about *how it got that way*.

## Project

Automated review of civil engineering drawings — bridges, retaining
walls, and similar structures — run **inside Revit**, as a compiled
native C# add-in (`native/`).

Two categories of check:

1. **Drafting** — standards and convention compliance, annotation and
   dimension completeness, cross-sheet consistency, spelling, revision
   consistency, plus project-specific rules. **Nothing here is built in
   the Revit era** — see "Next".
2. **Geometry** — dimensional consistency within and across views, and
   whether what a drawing states matches what the model actually says.
   This is where all the work so far has gone.

Scope is **internal projects only**, confirmed by the user, so a Revit
model is always available. Models are **cloud-workshared in Autodesk
Forma**, which is the firm's approved and secure store for project
information — so persistent state living off the machine is legitimate,
and the tool may keep memory between runs. (This corrects an earlier
"nothing leaves the machine" position that was never a client
requirement — PLANNING.md §10, which is otherwise **withdrawn**; read its
superseded note before treating anything in it as a rule.)

**Read PLANNING.md before making structural changes.** §5 is the domain
knowledge about what actually goes wrong on real drawing sets and remains
correct regardless of where the checks run. §5c records why the checks
moved into Revit. §12 onward is the native add-in's history, most
usefully §19 (the most recent corrections).

## Two archives, and you should know what is in them

Both are tags, and each tag is the only surviving reference to its code:
`main` is the sole branch and every merged branch was deleted on
2026-08-18.

- **`ARCHIVE-pdf-dwg.md`** / `git checkout pdf-dwg-final` — a working
  pipeline that checked PDF/DWG/IFC *exports* instead, parked 2026-08-18.
  Its 276-test suite and the real BR06/BR08 sample sets are in the tag.
  Genuinely worth reading before assuming anything about drafting
  conventions. `BACKEND_REVIEW.md` reviews it; `git show
  frontend-plan-final:FRONTEND_PLAN.md` is the never-merged frontend plan.
- **`ARCHIVE-pyrevit.md`** / `git checkout pyrevit-final` — the pyRevit
  extension, archived 2026-09-07. It proved the adapter/IR/checks split
  and the Revit → BCF → Forma → Revit round trip, then was superseded by
  `native/`. Every rule it carried has a C# equivalent (the archive doc
  has the mapping).

**The one lesson from the first archive that governs new work: logic
built on domain invariants survived a second client; logic built on
client conventions broke.** The Revit API is an invariant. That is the
whole argument for this direction — PLANNING.md §5c.

That lesson has since been sharpened twice, one level down, inside the
Revit API surface itself:

> **Rendered text is presentation; raw internal data is the invariant
> underneath it** (2026-08-28, the user's standing instruction). A
> schedule's `GetCellText`, `AsValueString()` or a column heading like
> "EASTING (m)" varies by project/client exactly the way a CAD layer name
> did; `AsDouble()`, `Location.Point`, `ElementId` do not.
> `RevitScheduleSource` learned this the hard way: a column heading
> claimed metres, the bound parameter's real display unit was
> millimetres, and trusting the heading silently double-converted every
> value.
>
> **The same holds for identity, more strongly** (2026-09-07, §19).
> Where the model already states a link, never reconstruct it from
> rendered text. Joining two things by matching their displayed strings
> needs both strings to exist, both to be recognised, and both to render
> identically — three ways to fail that an `ElementId` does not have, all
> three of which have now failed on real models.

## Layout

```
native/
  src/RevitCheck.Core/          # netstandard2.0 — no Revit dependency at all
    Ir/                         # plain records, raw facts, millimetres
    Issues/Issue.cs             # Issue + derived IssueId
    Catalog/                    # Catalog + CheckRegistry (explicit registration)
    Checks/                     # pure (RevitModel, RuleConfig) -> [Issue]
    Capture/CaptureSerializer   # RevitModel <-> JSON (the dev loop)
    Reporting/                  # JSON / CSV / BCF writers, CheckingSession,
                                #   InvestigationReconciliation
    Mapping/                    # ParameterMapping + its serializer
  src/RevitCheck.Addin/         # net48 — the ONLY project referencing the Revit API
    Adapters/                   # read the open document -> Core IR
    Commands/                   # the ribbon buttons — thin by design
    UI/                         # code-behind-only WPF, no XAML
  tools/RevitCheck.CheckRunner  # run checks against a capture, off-Revit
  tools/RevitCheck.MappingBuilder
  tests/                        # 359 Core + 7 MappingBuilder tests, ~1s, no Revit
  diagnostics/                  # throwaway pyRevit probes for answering real
                                #   unknowns before writing check logic
config/                         # firm_glossary.json, project_glossary.json,
                                #   en_gb_variants.json — data ahead of its rules
samples/                        # one real capture, kept as a test fixture
```

`native/README.md` covers building, deploying to a Revit machine, and
what each button does.

**`native/diagnostics/` is deliberately still pyRevit.** These are
throwaway probes, run once on a Revit machine to answer a real unknown
before any check logic is written — a workflow that has repeatedly paid
for itself (PLANNING.md §14, §18). They are not part of the product and
were not archived with the extension.

## The layering rule, which is the one that matters

```
Adapters/*.cs      the ONLY code that touches the Revit API
    |              (reads the open document -> RevitModel)
    v
Ir/*.cs            plain records, raw facts, millimetres
    |
    v
Checks/*.cs        pure (RevitModel, RuleConfig) -> [Issue]
```

**Nothing below the adapter knows Revit exists.** This survived a whole
host change (pyRevit → compiled C#) with the checks essentially intact,
which is the strongest available evidence the cut is in the right place.
Two consequences that are easy to erode and worth defending in review:

- **The adapter extracts facts and judges nothing** — no classification,
  no tolerances, no filtering. `ReferenceInfo` records that an element is
  view-specific; deciding that means "drafted" happens in `Checks/`. So
  retuning a classification never invalidates a capture and never
  requires a trip back to a Revit machine.
- **Anything that grows inside a Command is logic only debuggable inside
  Revit.** Buttons stay thin.

Units: **every length in the IR is millimetres.** Revit's internal unit
is decimal feet regardless of project settings, so the adapter multiplies
by `304.8` — no `UnitUtils` call, therefore no `UnitTypeId` vs
`DisplayUnitType` version branching, which is the usual source of Revit
version breakage.

## Development setup, and why it looks like this

Development happens on a Mac with **no Revit**. Revit runs on a
locked-down Windows work machine where the only LLM access is Copilot.
The capture file is what makes that workable, and it is the workflow
rather than a convenience feature:

```
# on the Revit machine, once per project
Capture Model  ->  BR08.capture.json   (+ a starter RuleConfig, see below)

# anywhere, as often as you like
dotnet test native/RevitCheck.sln                        # ~1s, no Revit needed
dotnet run --project native/tools/RevitCheck.CheckRunner -- BR08.capture.json
```

`RevitCheck.Core` is netstandard2.0 with no Revit dependency, so the
whole check suite runs on any machine. `RevitCheck.Addin` targets net48
and compiles against the Revit API, but needs neither Windows nor a Revit
install to *build* — the API comes from the Nice3point reference-assembly
NuGet packages.

**CI** (`.github/workflows/ci.yml`) builds the whole solution — including
the net48 Addin — and runs the tests. Building the Addin is the step that
matters most: it is the half no test can reach, since nothing in it can
be exercised without Revit running.

**One real capture is committed**, deliberately:
`samples/T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json`. All tests use
the synthetic IR builders in `tests/.../Fixtures/` — the real capture is
kept as the C#-port test fixture PLANNING.md §12 names. It contains real
client geometry, sheet numbers and view names, so treat committing one
the way this project treats uploaded drawings and check before it lands
in git. Its per-view dimension attribution predates the `OwnerViewId` fix
below and should not be trusted.

## Built state

Every rule is a real ribbon button in the native add-in unless noted.
Dated history for each is in PLANNING.md.

| Rule | What it does | Real-machine status |
| --- | --- | --- |
| `revit.dimension_provenance` | Per dimension, do its references resolve to model geometry, a datum, or view-specific linework? Four-way classification, rolled up per view. **Triage, not verdicts.** | Validated |
| `revit.dimension_override_consistency` | Where a drafter typed over the measured value, is the difference explainable as rounding to a sensible grid? A stated limit (`500 MIN.`) is checked against the limit instead. An override with no stated limit is not flagged (§17). Always reports how much was checkable. | Validated |
| `revit.capture_coverage` | Turns per-element extraction failures into a visible Issue, plus a low-severity note for any workset excluded by user choice. | Validated |
| `revitcheck.metadata_reconciliation` | Joins captured model elements to an external reference CSV via a per-run-chosen mapping file; flags missing/mismatched fields. | Validated, calibrated against two real reference tables (§13) |
| `revitcheck.pile_model_schedule_consistency` | Compares each pile's own **live** position (`GetProjectPosition`, never the Dynamo-written `XYZ_Easting`/`XYZ_Northing` — those are the value being audited) against its live pile schedule row. Joined on the row's own backing element (`ScheduleRow.ElementId`), so it needs no id column, no key parameter and no category match. Catches a pile moved without the schedule's Dynamo script being rerun. | Validated on one model (§16); **identity join (§19) not yet re-run** |
| `revitcheck.pile_chain_bearing_consistency` | Reconstructs each pile chain from live geometry by tag-to-pile proximity, splits it into geometrically straight runs, and compares each run's bearing against the drafted bearing call nearest it. A corner in a chain is reported for manual review, never averaged across. | Validated on one model (§16); **per-edge split (§19) not yet re-run** |
| `revitcheck.spot_elevation_consistency` | Compares a Spot Elevation's own drafted value (`DimensionInfo.Origin.Z` — `Value`/`ValueOverride` are unconditionally null for this family) against real horizontal `PlanarFace`s found near it via `Face.Project`, judged by 2D proximity, **never by Z agreement** (picking whichever face agrees would be circular). Deliberately not filtered by category anywhere. | Validated standalone (§18); session path fixed but **not re-confirmed** |

**The interactive checking workflow** (triage → per-view investigation →
reconciliation → manual per-dimension verdicts → reconciled BCF export)
is built and validated end to end (§16). `InvestigationReconciliation`
splits into three outcomes — `ConfirmedProblems` / `NeedsManualReview` /
`StillOpenTriage` — and only the first auto-exports to BCF. The design
exists precisely to stop "not yet checked" being silently promoted to
"confirmed clean".

**Export:** `Reporting/IssueBcfWriter.cs` writes BCF 2.1, split at 100
issues per file for Forma's import cap. The Revit → BCF → Forma → Revit
round trip **is proven** (§12, 2026-08-22, after fixing four real Forma
rejections in a row). Every finding anchors to its **sheet**, not the
dimension/view element — a Dimension has no 3D placement for a model
viewer to resolve, whereas a sheet is exactly what a document-coordination
platform navigates to. One thing not to re-litigate: **the Forma Issues
API cannot create element-pinned issues** (`linkedDocuments` is not
writable on creation), which is what ruled it out as the primary sink.

Notes worth not rediscovering:

- **Grids, levels and reference planes are datums, not risks.**
  Dimensioning to a grid is good practice and must not be lumped in with
  detail linework. `ImportInstance` **is** a risk despite not being
  view-specific — an imported DWG is a static snapshot of someone else's
  file.
- **A wholly-drafted view is one finding, not twenty.** It is a larger
  and different finding, and it is the unit the follow-up tool operates on.
- **Drafting views get different wording and severity.** A section could
  have been live and someone chose otherwise; a drafting view never had a
  model behind it.
- **Triage is not a verdict.** The provenance check says the file cannot
  answer whether a dimension is right, not that it is wrong. Per the
  user's standing position: *assume nothing is trustworthy or you will be
  caught out.* An override goes stale exactly as a witness line does.
- **`Dimension.OwnerViewId` is not trustworthy read document-wide.**
  Found 2026-08-22: a real capture attributed 430 dimensions to one view
  that had roughly a dozen, confirmed by Select-by-ID. Adapters collect
  dimensions **per view** (`FilteredElementCollector(doc, view.Id)`);
  `ViewId` comes from the loop, never read back off the element.
- **Collect view-scoped, not document-wide.** The same mistake has now
  been made three times (dimensions above; then piles, 281 document-wide
  against a real ~43 in view; then again in a diagnostic). A command's
  element sweep should pass `ActiveView` unless there is a stated reason
  not to. Schedules are the real exception — they are not "in" a view.

## Working conventions

- **Any change to the IR** affects every rule — update PLANNING.md §3
  alongside the change.
- **New rules go in the catalog** (`CheckRegistry`), not hardcoded into a
  command, so a project-specific check is a config change rather than a
  code change.
- **Config must be reachable without a rebuild.** `RuleConfig` is loaded
  per-model from a file (`RuleConfigSerializer`, written as a starter by
  Capture Model); a command resolves it via `RuleConfigSource`, never
  `new RuleConfig()`. Learned the hard way 2026-09-07 (§19): four
  commands constructed their own, so a category name could only be
  changed by rebuilding the add-in — and the same real correction ("these
  could be Generic Models") had to be made separately in each check
  instead of propagating once.
- **Where the model states a link, never reconstruct it from text** — see
  the archive lesson above.
- **Tolerances must be configurable**, never hardcoded constants — and
  say in a comment whether a figure is calibrated against real data or
  inherited as a placeholder. Most current ones are placeholders, and say
  so.
- **Report a coverage indicator; never fail silently.** A rule that found
  nothing because nothing was in scope must not look like a rule that
  found nothing because the model is clean. This has bitten the project
  three times, most recently when a category name matched no elements and
  a check returned having compared nothing.
- **Skip rather than guess.** An override that isn't a clean number, a
  reference that won't resolve, a convention not yet seen — record it as
  unchecked, don't infer.
- **Wait for real data before writing convention-specific logic.** Every
  extractor in this project's history that guessed ahead of a sample had
  to be rewritten when one arrived. The counterpart discipline that keeps
  paying: **run a throwaway diagnostic first** (`native/diagnostics/`).
  Spot Elevation is the only check that worked on its first real run, and
  it is the one that had the most diagnostic work behind it.
- **Verify Revit API members before using them.** They can be checked
  against the real `RevitAPI.dll` via `System.Reflection.MetadataLoadContext`
  with no Revit machine needed.
- **Rules must be auditable** — an Issue says how it reached its
  conclusion (which reference, which segment, which view). This is a
  compliance/review tool, not a black box.
- **Nothing is trusted until a real machine run confirms it.** Almost
  every correction in this project's history came from an actual run, not
  from review. When a fix is made, say plainly that it is unconfirmed.

## Next

**1. Validate §19's three fixes on the two models that failed.** Per-edge
bearings, the identity join, and the per-model config loop are all built
and tested but unrun on a Revit machine. This is the immediate frontier.

**2. Negative controls — the biggest evidence gap.** Every verification
check has only ever returned zero issues on real data. The tool is proven
to agree with a clean model, which is much weaker than proven to detect
drift. Deliberately breaking something in a scratch copy (move a pile
50mm without rerunning Dynamo; retype a Spot Elevation) and confirming
each check flags it at the right magnitude would convert three ambiguous
zeros into real evidence, and is the only thing that will exercise the
placeholder tolerances.

**3. A second real model.** Until 2026-09-07 every check had been
validated against exactly one model (BR08, the same file as the old
pipeline's sample). The first contact with two others broke all three
pile paths. That risk is now partly retired but not measured.

**4. The next dimension type.** The organizing axis is **dimension type
plus how its provenance resolves** — not element type, not view type
(corrected 2026-09-02, §18: piles read as "element-type" only because
piles' own conventions genuinely are pile-specific). Named and unbuilt:
- ordinary linear dimensions dimensioning to `DetailLine`s — 3 real ones
  found in the same abutment view Spot Elevation was validated against;
  needs a different mechanism (a measured distance between two witness
  points, not one point's Z against one face).
- a per-view dimension-type breakdown in the checklist ("3 linear, 3
  spot") so a reviewer knows which button to run without already knowing
  the answer.

**5. The drafting checks — the untouched half of the brief.** Glossaries,
`config/en_gb_variants.json` (563 curated pairs) and the precise check
definitions all carry over as data and semantics; only the extraction
layer does not. This is the half where the invariance argument is
*strongest* — Revit revisions, sheet parameters and view references are
API objects, not drafting conventions — and it is currently at zero.

**Also open:** multi-hop survey-tolerance scaling (§5's
`base + per_hop×√hops`), awaiting a retaining-wall sample. Bridge chains
are short; retaining walls are not, and may be curved, which the pile
chain work explicitly assumes away.

## Environment quirks worth knowing

- **The project folder is `~/projects/revitcheck`**, renamed from
  `pdf checker` on 2026-08-18 along with the repo. Claude Code keys its
  per-project state on the path, so the old
  `...-pdf-checker` state directory was moved at the same time — memory
  included. Anything still naming the old path is stale.
- **`.venv` is dead weight.** 45MB holding only `pytest`, for the test
  suite archived on 2026-09-07. Nothing in the tree runs Python any more
  except `native/diagnostics/`, which runs inside Revit's own interpreter.
- **Git remote** is `https://github.com/badger584114/revitcheck.git`
  (private, renamed from `pdf-dwg-checker` 2026-08-18 — GitHub keeps a
  redirect). Two failure modes seen on this machine: (1) a GitHub HTTP/2
  push bug (`RPC failed; HTTP 400`), fixed by `git config http.version
  HTTP/1.1` plus a larger `http.postBuffer`, already set in this repo's
  `.git/config` so a fresh clone needs it re-applied; (2) GitHub accepts
  only a Personal Access Token as the HTTPS password, and a stale Keychain
  credential produces a similar-looking HTTP 400 — clear it with
  `git credential-osxkeychain erase`.
- The **`gh` CLI is installed and authenticated** (`badger584114`) — use
  it for PRs rather than the REST API.
- `samples/Flinders/` (201MB, a third client's real drawings) was never
  tracked and was deleted from disk 2026-08-23 — its findings are fully
  written up in ARCHIVE-pdf-dwg.md. If it reappears from a future export
  it must stay untracked; do not `git add` it.
