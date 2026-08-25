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
> comparison logic is still unbuilt. Its required first step — the
> `InspectDimensionGeometry.pushbutton` diagnostic — has been run
> against real data **seven times** (2026-08-25); runs 1-4 each found
> and fixed a real code bug (`GlobalPoint`/`Location` unusable →
> `DimensionSegment.Origin` anchor → document-wide search noise →
> `Face` geometry needing its own resolution → imprecise bbox-midpoint
> needing `Face.Project` instead) and runs 5-6 found the specific test
> dimensions weren't representative (dragged dimension text; a
> road-label dimension with no model reference), not code bugs. **Run 7
> is the one that matters: swept a real pile-layout plan view (46
> dimensions, 92 references, real un-overridden pile-spacing values)
> and found zero `CUT_EDGE` references anywhere — every one is
> `AnnotationSymbol`-to-`AnnotationSymbol` or `AnnotationSymbol`-to-
> `Grid`.** Pile setout on this project is drafted **tag-to-tag**, not
> tag-to-geometry — a materially different case than the Wall/Floor
> `CUT_EDGE` dimensions runs 1-5 were built around. `Face.Project`
> still hasn't had a clean test and may never get one from a
> tag-to-tag dimension; verifying these instead likely needs
> **proximity matching** against real `Pile` elements (the
> `geometry.ifc_setout_consistency` algorithm shape the plan already
> named as the template to reuse) — confirmed necessary by real data,
> not assumed. **The diagnostic loop is paused here** (seven real
> Revit-machine round trips today) in favour of writing up Track B's
> actual design — two dimensioning conventions now confirmed to
> coexist (direct-to-geometry and tag-to-tag), so it needs two matching
> strategies, not one. See PLANNING.md §14 for the full run-by-run
> detail.
>
> **PLANNING.md §15 (2026-08-25):** a real cloud-model run of Metadata
> Reconciliation crashed — `Document.PathName` for a Revit Cloud
> Worksharing model isn't a filesystem path, and the code didn't guard
> against that, only against an empty path. Fixed (`DocumentPaths.cs`);
> full diagnosis and the .NET Framework-vs-.NET-8 `Path` behaviour gap
> that made it not reproduce on this Mac are in §15.

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
   the diagnostic below, is now built (2026-08-25) but not yet run.
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
   built and **run seven times against real data** (2026-08-25).
   Runs 1-4 each found and fixed a real code bug in turn — unusable
   `GlobalPoint`/misleading `Location` → an anchor real but not close
   to anything (`DimensionSegment.Origin`, the value-text position,
   plus unscoped search noise) → `Face` geometry needing its own
   resolution (`Evaluate` has the wrong signature for it) → a `Face`
   bbox-midpoint too imprecise, needing `Face.Project(candidate_point)`
   instead. Runs 5-6 found the specific test dimensions weren't
   representative (dragged dimension text 527m from its own witness
   lines; a road-label dimension with no model reference at all), not
   code bugs. **Run 7 swept a whole real pile-layout plan view (46
   dimensions, 92 references, real un-overridden pile-spacing values)
   and found zero `CUT_EDGE` references anywhere** — pile setout on
   this project is drafted tag-to-tag (`AnnotationSymbol`-to-
   `AnnotationSymbol`/`Grid`), not tag-to-geometry, a materially
   different case than the Wall/Floor dimensions runs 1-5 were built
   around. `Face.Project` still hasn't had a clean test and may never
   get one from a tag-to-tag dimension; verifying these instead likely
   needs proximity matching against real `Pile` elements — the
   `geometry.ifc_setout_consistency` algorithm shape already named as
   the template to reuse, now confirmed necessary by real data rather
   than assumed. **The diagnostic loop is paused here** in favour of
   writing up Track B's actual design, which now needs two matching
   strategies (direct-to-geometry and tag-to-tag), not one — see
   PLANNING.md §14 for the full run-by-run detail.

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
