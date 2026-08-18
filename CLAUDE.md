# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.

## Project

Automated review of civil engineering drawings — bridges, retaining
walls, and similar structures — run **inside Revit** as a pyRevit
toolbar (`extensions/RevitCheck.extension/`). Two categories of check:

1. **Drafting** — standards and convention compliance, annotation and
   dimension completeness, cross-sheet consistency, spelling, revision
   consistency, plus project-specific rules.
2. **Geometry** — dimensional consistency within and across views, and
   whether what a drawing states matches what the model actually says.

Scope is **internal projects only**, confirmed by the user, so a Revit
model is always available. Nothing leaves the machine.

**Read `PLANNING.md` before making structural changes** — it holds the
reasoning, not just the "what". §5c records why the checks moved into
Revit; §5 (all of it) is the domain knowledge about what actually goes
wrong on real drawing sets, and remains correct regardless of where the
checks run.

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
    report.py                        # summarize / to_json / to_markdown
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
tests/revit/                         # 119 tests, ~0.1s, no Revit needed
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
python -m pytest tests/ -q          # 119 tests, ~0.1s, no dependencies
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

**No real capture is committed yet.** All tests use the synthetic IR
builders in `tests/revit/conftest.py`. Committing a real capture means
committing client geometry, sheet numbers and view names — treat one the
way this project treats uploaded drawings, and check before it lands in
git.

## Built state

| Rule | What it does |
| --- | --- |
| `revit.dimension_provenance` | For each dimension, do its references resolve to model geometry, a datum, or view-specific linework? Four-way classification, rolled up per view. |
| `revit.dimension_override_consistency` | Where a drafter typed over the measured value, is the difference explainable as rounding to a sensible grid? A stated limit (`500 MIN.`) is checked against the limit instead. Always reports how much was checkable. |
| `revit.capture_coverage` | Turns the adapter's per-element extraction failures into a visible Issue. |

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

**Verify drafted dimensions against the model** — the harder half,
deliberately sequenced after the easy wins so the workflow is
established first. `revit.dimension_provenance`'s `drafted_views()`
produces its input. It needs new adapter geometry (witness points in
model space, the view's own cut plane and direction, nearby model
elements), so it needs a Revit machine to debug and a real capture to
calibrate.

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
- `samples/Flinders/` may be present on disk and is **deliberately
  untracked** (201MB, a third client's real drawings). Its findings are
  fully written up in ARCHIVE-pdf-dwg.md; do not `git add` it.
