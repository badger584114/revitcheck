# Backend Review — pre-frontend readiness

> **Historical.** This reviews `src/pdfchecker/`, the PDF/DWG/IFC
> pipeline, which was parked the day after this review was written — see
> `ARCHIVE-pdf-dwg.md`; the code is at `git checkout pdf-dwg-final`. It
> is kept because it is part of why the pivot happened: §8's finding that
> the API/DB/queue/frontend build was "the majority of the work" still
> ahead is one of the things that made moving the checks into Revit the
> cheaper path. Two fixes it produced *did* carry over into
> `extensions/RevitCheck.extension/` — run-time rule-id resolution
> instead of a construction-time snapshot, and per-rule error isolation.

**Reviewed:** 2026-08-17 · **Commit:** `614ce07` (main) · **Scope:** everything under `src/pdfchecker/`, `scripts/`, `tests/`, packaging and environment.

**Method:** read the full source tree, then executed against the real BR06 sample — CLI entry points, the full 230-test pytest suite, the session-config loader, and per-rule timing. Findings below marked *(verified)* were reproduced by running the code, not inferred from reading it.

---

## Verdict

The **domain core is genuinely strong** and is not what should worry you. Extraction, the IR, and the check rules are careful, well-calibrated against real drawings, and unusually honest about their own limits — the "skip rather than guess" discipline and the coverage-indicator-instead-of-silence convention are consistently applied, and the docstrings record *why* decisions were made, not just what. That is the expensive part of this project and it is in good shape.

What should worry you is that **there is no backend in the sense a frontend needs one.** Today the project is a Python library plus three `argparse` scripts. There is no API, no job queue, no session lifecycle, no storage, no auth. A React app has nothing to call.

Three things are also **broken on `main` right now** — including one that would make a headline frontend control silently do nothing. All three are cheap to fix, and all three are invisible because there is no CI.

**Recommendation: do not start the frontend yet.** Roughly 1–2 weeks of backend work (§1, §2, §3 below) turns this from a library into something a UI can be built against. Starting the UI first means building it against an interface that does not exist yet, and you will design it twice.

---

## 1. Broken on `main` right now

### 1.1 `PyYAML` is missing from the venv — both CLIs and the entire test suite fail to start *(verified)*

`requirements.txt` pins `PyYAML==6.0.1`, but it is not installed in `.venv`. Because `checks/__init__.py` imports `session_config` unconditionally, and that module does a top-level `import yaml`, *everything* that touches the checks package dies at import:

```
$ python scripts/check.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf
ModuleNotFoundError: No module named 'yaml'

$ python -m pytest tests/
ImportError while loading conftest 'tests/conftest.py'
```

Introduced by `47b7635` (the session-config loader), which added the dependency to `requirements.txt` without reinstalling. Nothing caught it because the same failure takes down the test suite that would have caught it.

**Fix:** `pip install -r requirements.txt`. *(Applied during this review, to allow the rest of the assessment to proceed.)*

### 1.2 `tests/test_dxf_markup.py` cannot be collected on Python 3.9 — and it takes the whole suite with it *(verified)*

```
tests/test_dxf_markup.py:42: in <module>
    def _sheet(page_index: int, dxf_path: str | None, ...) -> Sheet:
E   TypeError: unsupported operand type(s) for |: 'type' and 'NoneType'
```

`str | None` in a signature requires either Python 3.10+ or `from __future__ import annotations`. This file is the only one of the 13 test modules that uses the syntax without the import. It shipped in the most recent commit, `4c5ce4c`.

Two consequences worth separating:

- **A single collection error aborts the entire pytest run** (`Interrupted: 1 error during collection`) — so this one file has been suppressing *all* test feedback, not just its own.
- **The DXF redline export's tests have therefore never run in this environment**, despite `CLAUDE.md` recording that work as "Confirmed end-to-end against sheet 2871051's real DXF: opened, redlined, re-saved, re-read." That claim may well be true from a manual check, but it is not currently reproducible from the suite.

**Fix:** add `from __future__ import annotations` to the file. *(Applied during this review.)* **Superseded 2026-08-17** — this test file was deleted outright along with the DXF markup feature it covered, once the user confirmed the drafting team works in Revit and never opens AutoCAD. The underlying lesson stands and is unaffected: a single uncollectable test module silently disabled the entire suite, which is what §6.1's CI item exists to catch.

### 1.4 With 1.1 and 1.2 fixed, everything passes *(verified)*

```
230 passed, 12 warnings in 770.39s (0:12:50)
```

Worth stating plainly: **the codebase underneath these two breakages is healthy.** Nothing in §1.1 or §1.2 indicates a deeper problem — they are an un-reinstalled dependency and a one-line syntax slip, and both were invisible only because they disabled the mechanism that would have reported them. The 12m50s runtime is itself a CI design constraint, though — see §6.1.

### 1.3 `check_scope: drafting_and_geometry` silently runs **zero** geometry rules *(verified)*

This is the most important finding in this report, because it is the one that would ship into the UI.

`checks/__init__.py` imports four rule modules for their `@register` side effects:

```python
from pdfchecker.checks import cross_sheet, revisions, spelling, title_block
```

`geometry` is not among them. So unless something *else* separately imports `pdfchecker.checks.geometry`, the catalog contains only the six drafting rules:

```
$ python -c "from pdfchecker.checks import all_rule_ids; print(all_rule_ids())"
['cross_sheet.reference_resolves', 'revision.cloud_matches_schedule',
 'revision.schedule_matches_title_block', 'revision.sequential_numbering',
 'spelling.en_gb', 'title_block.required_fields_present']
```

Loading the project's own example session config — which sets `check_scope: "drafting_and_geometry"` — produces this:

```
check_scope: drafting_and_geometry
geometry rules that will actually run: []
warnings: (nothing about geometry)
```

The scope selector is one of exactly two controls `CLAUDE.md` specifies for the upload screen. Built against today's backend, it would be a no-op with no error, no warning, and no geometry issues in the output — indistinguishable from "geometry checks ran and found nothing."

The test suite does not catch this because `tests/test_geometry.py` imports `checks.geometry` directly, which registers the rules as a side effect for the rest of the session.

**There is a second-order trap here too.** `RuleConfig.enabled_rule_ids` defaults to `set(all_rule_ids())` evaluated *at construction time*. So even after adding the missing import, behaviour still depends on import order: a `RuleConfig()` built before `geometry` is imported permanently excludes geometry rules. Worth removing that footgun while you are in there — e.g. resolve the default lazily, or have `run_checks` treat "no explicit selection" as "everything currently registered."

**Fix — applied 2026-08-17.** Three changes, because the import was only the surface of it:

1. `geometry` added to `checks/__init__.py`'s side-effect import list. The catalog now holds all 10 rules.
2. `RuleConfig.enabled_rule_ids` changed from a `set(all_rule_ids())` field default to a `None` sentinel, resolved at *run* time by a new `resolved_rule_ids()`. This removes the construction-time snapshot described above — import order no longer decides behaviour. An explicit set still means exactly what it says.
3. `session_config.py` now warns if `drafting_and_geometry` is requested against a catalog containing no `geometry.*` rules — unreachable after (1), but this is precisely the class of thing that should never fail quietly again.

`tests/test_session.py::TestRuleRegistration` guards all three, and deliberately imports only `pdfchecker.checks` — importing `checks.geometry` there would recreate the workaround that hid the bug in the first place.

**Verified on the real BR06 set:** a `drafting_and_geometry` run now reports **10 rules run** and produces **4 geometry issues** that were previously unreachable — all four low-severity coverage indicators from `geometry.setout_reconstruction` (schedule points that couldn't be anchored or matched in the DXF), which is the documented expected behaviour for that sheet, not a new defect.

---

## 2. There is no service layer

None of the intended stack beyond parsing exists. Confirmed absent from both `requirements.txt` and `pyproject.toml`: `fastapi`, `uvicorn`, `celery`, `redis`, `sqlalchemy`, `psycopg`, `minio`, `boto3`. There is no `.github/` either — no CI of any kind.

### 2.1 No single entry point for a full run

`ingest_pdf(path)` handles PDF only. A geometry-inclusive run currently requires the caller to hand-sequence eight steps:

```
ingest_pdf → convert_dwg_to_dxf → ingest_dxf (×N) → attach_dxf_sheets
           → ingest_ifc → attach_ifc_model
           → import checks.geometry   ← the undocumented step from §1.3
           → run_checks → render_markup
```

That sequence exists in exactly one place: `tests/test_geometry.py`. `CLAUDE.md` acknowledges this ("no CLI wrapper for Stage 3 yet").

**This is the highest-value next piece of backend work.** One function — call it `run_session(pdf, dxf_paths, ifc_path, config) -> SessionResult` — is the natural thing for an API to wrap, the natural place to fix §1.3 properly, the natural owner of the scratch-directory lifecycle (§4.3), and the natural boundary for per-stage progress reporting the UI will need. Build this before the API, and the API becomes thin.

**Built 2026-08-17** — `src/pdfchecker/session.py`. It owns the four things no previous caller did:

- **The ingestion half of `check_scope`.** The selection existed in `session_config.py`, but nothing downstream acted on it; geometry inputs are now only ingested when the scope asks for them.
- **The scratch lifecycle.** `SessionResult` is a context manager; `SessionWorkspace.purge()` only ever deletes a directory it created itself via `mkdtemp`, never a caller-supplied path — the delete path is deliberately incapable of reaching user files, since §2.2 is a confidentiality rule.
- **Coverage warnings for the quiet-failure cases.** A geometry run where no DXF joined to any sheet, or where no geometry input was supplied at all, previously produced the same empty result as a genuinely clean set. Both now warn.
- **Per-stage progress and timing**, so a worker has somewhere to report from and a UI can show more than a spinner.

`scripts/run_session.py` drives it — the first CLI that can run Stage 3. Verified end to end on the real BR06 set (PDF + 2 DXF + IFC): 37 sheets, 221 issues, 10 rules, marked-up PDF plus 2 DXF redline files, scratch purged on exit.

Two things it deliberately does **not** fix, both documented in its docstring: per-page/per-rule error isolation (§4.1 — that belongs in `ingest_pdf`/`run_checks`, where a failure needs to become a coverage Issue rather than an exception) and the process-global reconstruction cache (§4.2 — that belongs in `checks/geometry.py`).

### 2.2 Statelessness is a hard constraint with no implementation

`CLAUDE.md` states it as a client-confidentiality decision, not just an architectural one: uploaded files, extracted IR, issues and markups are all cleared after report delivery. Nothing in the codebase creates, scopes, or purges a session's working files today. ODA conversion in particular writes DXF into a caller-chosen directory that nothing cleans up.

Retrofitting deletion semantics after a frontend exists is how confidentiality guarantees get quietly broken. This belongs in §2.1's orchestrator from the start.

---

## 3. Data-model gaps the UI will hit immediately

### 3.1 `Issue` has no stable identity — and §8's core flow depends on one — **fixed 2026-08-17**

PLANNING §8 step 2 is *"Engineer selects which issues to include"*. On a stateless server with no run persistence, that means the client must send the selected subset back. But:

- `Issue` has no id, and is not hashable.
- `assign_tags` (in `markup/pdf_markup.py`) numbers by **position after sorting**, so `#014` identifies an issue only relative to one exact list. Send back a subset and every tag shifts — the tags in the marked-up PDF would no longer match the tags in the report.
- `Issue.to_dict()` exists; there is no `from_dict`. Nothing can round-trip.

**Fix — applied 2026-08-17.** `Issue.issue_id` is a derived property (sha256 of rule/sheet/page/bbox/description, 16 hex chars), not a stored field, so it cannot go stale. Page coordinates are rounded to 0.01pt before hashing — bboxes come out of a transform chain and the IFC work in this same branch moved some by ~1e-9m while being deliberately equivalent, which must not re-identify every geometry finding. Severity and `suggested_fix` are excluded: a config change that re-tiers a rule, or a reworded fix, shouldn't turn one finding into a different one.

`assign_tags` now ends its sort on `issue_id`, which matters more than it sounds — the old key (page, severity, rule_id) left genuine ties (a sheet with twenty spelling issues has twenty identical keys), and Python's stable sort broke them by *input list order*, so tags depended on the order the caller accumulated issues in. `Issue.from_dict` and `checks.issue.select_by_id` complete the round-trip; `from_dict` deliberately ignores any client-supplied `issue_id` and recomputes it.

Verified on the real BR06 run: 0 collisions across the issue set, ids identical across runs, and tags unchanged across 5 shuffles of the input order.

### 3.2 No report exporter — **fixed 2026-08-17**

§8 specifies the markup set and the full report are downloaded together. `MarkupReportEntry.to_dict()` exists, but nothing writes a report artifact — no JSON, CSV, or PDF. The UI needs a defined report format to render *and* to offer as a download.

**Fix — applied 2026-08-17.** `markup/report.py`'s `build_report(session_result) -> CheckReport`, with `to_json()`, `to_csv()` and `write(stem)`. JSON is the machine-readable artifact (API response, the frontend's filterable issue list, and — because every entry carries `issue_id` — the input §7 names for a stateless cross-run diff if that capability ever returns). CSV covers §7's "Excel report" with no new dependency. Tags come from the same `assign_tags` the markup uses, so `#014` on a sheet and `#014` in the report are the same finding; `marked_up` joins on `issue_id`, not tag, since a caller may render markup for a whole run and report a selected subset. `scripts/run_session.py --report <stem>` drives it. A PDF report is deliberately not built — see the module docstring.

### 3.3 Two rules emit issues with no location

`revision.schedule_matches_title_block` and `revision.sequential_numbering` both construct `Issue(...)` with no `bbox`. This contradicts `CLAUDE.md`'s own hard rule ("Every rule's `Issue` output must carry a precise location... build it into the Issue schema from the first rule written").

Practical effect: these are permanently `rendered=False` in both markup exporters, and in the UI they cannot be clicked to locate. Both are sheet-level findings about the revision schedule, whose bbox is already available on the sheet — so this is a small fix, worth doing before the UI makes the inconsistency visible to users.

---

## 4. Robustness

### 4.1 No error isolation anywhere

Neither of the two main loops has any exception handling:

- `ingest_pdf` — one malformed page raises, and the entire 37-page job is lost.
- `run_checks` — `issues.extend(_CATALOG[rule_id](project, config))` with no guard. One rule raising discards **every other rule's results**, including ones that already completed.

That is a reasonable trade for a CLI run against a known-good sample. It is not viable once users upload arbitrary PDFs and DWGs. Per-page and per-rule guards that record the failure as a coverage issue would match this codebase's own established convention ("report a coverage indicator, don't fail silently") rather than introducing a new one.

### 4.2 Process-global cache keyed on `id()`, with a gap on the config side

`checks/geometry.py` holds a module-level `_reconstruction_cache`, keyed `(id(sheet), id(geometry_sheet))` → `{id(config): result}`.

The sheet ids are properly protected by a `weakref` finalizer — that work was done carefully, including the subtle "keep the ref object alive" bug noted in the comments. **The config id is not.** And CPython reuses addresses immediately *(verified)*:

```
id reused after free: True 0x7f8080175fd0 0x7f8080175fd0
```

So: a `Sheet` that outlives a `RuleConfig`, plus a new `RuleConfig` allocated at the reused address, returns the *earlier* config's cached reconstruction — computed with different tolerances. Unreachable from today's one-shot CLI, where project and config are created and discarded together. Entirely reachable in a worker process that holds a `Project` across requests with per-session configs, which is precisely the architecture that is coming.

**Fix:** make the cache run-scoped rather than module-global — pass it through §2.1's orchestrator. That removes the shared-mutable-state problem and the id-reuse problem together.

### 4.3 No input validation or resource limits at what will become the upload boundary

All appropriate for a CLI; all need hardening before an HTTP endpoint:

- `ingest_pdf(path)` / `ingest_ifc(path)` open whatever they are given — no type, size, or page-count limits.
- `render_markup(..., output_path)` writes wherever it is told — path traversal if that ever derives from a request.
- `convert_dwg_to_dxf(..., oda_path=...)` executes a binary at a caller-supplied path. It uses a list-form `subprocess.run` with no shell, so there is no injection vector — but the path itself must never become user-controlled.

---

## 5. Performance

Ingestion and spelling dominate, and together they determine whether the UI can use request/response or needs a job queue with progress. Measured on the real BR06 set (37 pages):

| Stage | Time | Output |
|---|---|---|
| `ingest_pdf` (37 sheets) | **125.3s** | — |
| `spelling.en_gb` | **55.9s** | 212 issues |
| `cross_sheet.reference_resolves` | <0.1s | 5 issues |
| all 3 revision rules | <0.1s | 0 issues |
| `title_block.required_fields_present` | <0.1s | 0 issues |
| all 4 `geometry.*` rules | <0.1s | 0 issues (no DXF/IFC attached in this run) |
| **Total, drafting-only** | **~181s** | 217 issues |

So a single drafting-only run on one sheet set takes **about three minutes**, and two stages account for essentially all of it. A geometry-inclusive run adds DWG→DXF conversion and IFC ingestion on top (§5.2).


### 5.1 `spelling.en_gb` re-derives corrections it has already computed

`checks/spelling.py` calls `sc.correction(lower)` for every unknown token *occurrence*, with no memoization. `pyspellchecker`'s `correction()` is an edit-distance search over the dictionary — expensive.

The real run quantifies the waste exactly *(verified)*:

```
--- spelling: 93 distinct flagged words, 212 occurrences ---
    37x  Possible misspelling: 'Autodesk'
    26x  Possible misspelling: 'CLSM'
     8x  Possible misspelling: 'CPST'
```

**212 searches for 93 distinct answers** — and the worst offender, `'Autodesk'`, triggers 37 identical edit-distance searches because it appears once per sheet. A `dict` cache keyed on the lowercased token is a few lines and should cut this stage by well over half.

This is the cheapest large win available, and it directly affects the frontend design decision.

Two adjacent observations from the same output, both cheap and both worth doing before a UI exposes them:

- `'Autodesk'` and `'CLSM'` alone are 63 of the 212 issues. Seeding the firm glossary with a first pass over real output would materially cut noise — the glossary mechanism exists and is exactly what this is for.
- The `cross_sheet` output contains **exact duplicate pairs** (`Reference 3/2871091` and `Reference 6/2871111` each appear twice, identical text, same sheet). These may be two genuine markers at different page positions — which is legitimate and would be distinguishable by `bbox` — or double-counted edges in the reference graph. Worth confirming either way, because a UI list will surface them as apparent duplicates and erode trust in the output.

### 5.2 IFC ingestion meshed every element just to get a bounding box — **fixed 2026-08-17**

`extraction/ifc_source.py:extract_elements` called `ifcopenshell.geom.create_shape` on every element with a representation, then reduced the result to an axis-aligned bbox — full BRep construction and triangulation to obtain six floats.

Profiling showed the cost was not spread across the model at all:

```
total create_shape time: 208.1s over 152 elements
 132.66s  63.8%  IfcBuildingElementPart  Concrete - Cast-in-Place Concrete
  73.14s  35.2%  IfcBuildingElementPart  Concrete - Cast-in-Place Concrete
            ...  median element: 3.7ms
```

**Two deck-pour elements were 99% of the runtime** — `IfcPolygonalFaceSet` geometry with 5,699 and 4,110 vertices across 10,508 and 7,566 polygonal faces. And their coordinates are a plain list in the file: `create_shape` was building a full BRep from those faces to produce a bounding box that `min`/`max` over the coordinate list gives directly.

`_faceset_bbox` now reads `Coordinates.CoordList` and applies the element's placement matrix (via `ifcopenshell.util.placement.get_mappeditem_transformation`, which handles IFC's mapped-item indirection correctly), falling back to `create_shape` for anything not purely tessellated. `IfcPolygonalFaceSet`/`IfcTriangulatedFaceSet` are IFC4-standard, so this stays schema-general.

**Verified equivalent, not merely faster** — the old algorithm was re-run verbatim and diffed against the new one:

```
OLD: 132 elements in 204.6s
NEW: 132 elements in   1.0s
same GlobalId set: True
max bbox difference across all 132 elements: 0.000001 mm
```

BR08's 934-element model now ingests in **22.2s**. That matters beyond speed: BR08 had never been used to re-confirm the superstructure heuristics *because* it was too slow, and that blocker is now gone.

Worth noting what was rejected: both samples do carry real dimensional metadata — schema-standard `Qto_*` quantity sets, plus firm-specific `T2D_QTO` properties (`T2D_Diameter = 0.750`, `T2D_Length = 10.550` on a real pile). Quantities give **size but no position or orientation**, and `geometry.ifc_setout_consistency` matches on world-space centroid, so they cannot replace the bbox. The firm properties are also internally inconsistent about units on real data (`T2D_Height = 8050` beside `T2D_Length = 10.550`; `Pile_Length = 10500` beside `Pile_LengthOverall = 10550`), which is its own argument for trusting geometry over metadata.

### 5.2a Beam shape heuristic is miscalibrated (found in passing; pre-existing)

Verifying the above surfaced an unrelated real problem. `checks/geometry.py:_is_elongated_beam` is documented as calibrated on 4 of BR06's 68 `IfcBeam` elements, "all landing at footprint 10.51-13.32m, ratio 0.100-0.124", and explicitly claims that was "a same-population sample, not a search for a rare subtype."

Over the full population, that is backwards: roughly **60 of the 68 sit at footprint ~6.93m, ratio ~0.018** — inside the *deck* band (`<= 0.06`), not the beam band. Only ~2 fall in the documented range. The 4-element sample caught the rare subtype, repeating the exact under-sampling mistake its own docstring cites as the thing it avoided.

**No output changes today** — `geometry.ifc_superstructure_coverage` only reports when a category has zero matches, and both are non-empty either way (77 deck-shaped, 8 beam-shaped). So this is a "the heuristics don't mean what they claim" problem rather than a wrong-answer one. It becomes a real problem the moment that rule grows the magnitude/position cross-check §5 still has open. Recorded in the docstring and CLAUDE.md; recalibrating against the full population and against BR08 is the follow-up.

### 5.3 Consequence for the frontend

Even with §5.1 fixed, ingestion alone is tens of seconds per sheet set. Celery + Redis (already in the intended stack) is required, not optional — and the orchestrator in §2.1 should report per-stage progress so the UI can show something more useful than an indeterminate spinner.

---

## 6. Hygiene

### 6.1 No CI — the root cause of §1 — **fixed 2026-08-17**

Both §1.1 and §1.2 are the kind of failure a single `pytest` run on push catches immediately. With no automation, a test suite that cannot even *collect* went unnoticed into `main`.

This is the highest-leverage item in this report relative to effort. Add it before the frontend doubles the surface area.

The full suite takes **12m50s** *(verified)*, dominated by real-sample ingestion — too slow for every push. Suggested split:

- **Every push:** `pytest --collect-only` plus the synthetic/unit tests. This alone would have caught *both* §1.1 and §1.2 in seconds, since both are import-time failures.
- **Pre-merge or nightly:** the full suite including the real-sample fixtures.

The collect-only lane is worth adding even on its own — it is near-instant and catches the entire class of failure that produced §1.

**Built** — `.github/workflows/ci.yml`, four jobs:

| Job | Gating | What it does |
|---|---|---|
| `fast` | yes | Sparse checkout without `samples/`, `compileall` + `pytest --collect-only`. 276 tests collect in 0.8s locally. Catches §1.1 and §1.2 outright. |
| `packaging` | yes | `pip install .` from `pyproject.toml` alone, then imports every module from the installed distribution. Catches §6.2's manifest drift. |
| `tests` | yes | Full checkout, whole suite (~6 min). |
| `forward-compat` | no | Unpinned install on 3.13, collect-only. |

Two review findings were fixed to make CI meaningful rather than decorative: §6.2's stale `pyproject.toml` dependency list (it omitted `ezdxf`, `ifcopenshell` and `numpy`, so `pip install .` built a package that could not import itself — the `packaging` job would have failed on arrival otherwise), and §6.4's invalid `\P` escape, which the 3.13 run showed has already escalated from `DeprecationWarning` to `SyntaxWarning`.

### 6.2 Packaging is stale and the package is not installed

`pyproject.toml` declares `requires-python = ">=3.9"` — which §1.2 violated — and lists only four dependencies, omitting `ezdxf`, `ifcopenshell`, `shapely`, and `numpy`, all of which are imported by `src/`. The package is also not installed into the venv, which is why every script and test module carries:

```python
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))
```

A single `pip install -e .` removes that boilerplate from 16 files and makes `pyproject.toml` the real dependency manifest instead of a second, drifting copy of `requirements.txt`.

### 6.3 Docstring drift — including one that is hiding a now-unblocked feature

This codebase leans on docstrings as its primary source of truth more than most, so drift costs more here than it normally would. Current examples:

- **`checks/catalog.py` and `checks/geometry.py:_tier`** both state that automatic promotion of reconstruction-chain dimensions to the tighter `setout_critical` tolerance "isn't possible yet (§5b isn't built)". **§5b has been built since 2026-08-11.** This is not merely a stale comment — it is a real feature whose stated blocker has been removed and which nobody has revisited. Setout-critical dimensions are currently classified by layer name alone.
- **`src/pdfchecker/__init__.py`** still says "Stage 1... No check engines yet — that's stage 2+." Four stages exist.
- `ir.py`'s `DxfSheet` docstring still frames the DXF↔PDF join and title-block extraction as unbuilt future work in places where the surrounding situation has moved on.

### 6.4 Invalid escape sequence in `extraction/dxf_source.py`

The module docstring contains a literal `\P` in a non-raw string, raising `DeprecationWarning: invalid escape sequence \P` on every import. This becomes a `SyntaxError` in a future Python. Fix: make the docstring raw (`r"""`), or double the backslash as line 275 already correctly does.

---

## 7. What is in good shape

Stated plainly, because it is the majority of the work and none of the above should obscure it:

- **The IR is well designed.** Clean separation between PDF-sourced, DXF-only, and IFC constructs; deliberate refusal to merge schemas ahead of a consumer; `to_dict()` throughout.
- **Calibration discipline is excellent.** Every extractor waited for real sample data before format-specific logic was written. The record of what was *confirmed* versus *corrected* against real files (DXF blocks carrying no `ATTRIB`s; multiple viewports per sheet) is exactly the institutional knowledge that is normally lost.
- **The check rules are honest.** "Skip rather than guess" is applied consistently — letter-tag dimension overrides, cross-package sheet citations, unscoped IFC matching. Coverage indicators are reported instead of silent success. This is what makes the tool trustworthy enough to eventually send output to a drafting team.
- **Hard problems were actually solved, not hand-waved.** The `walk_chain` sign-inheritance fix, the one-to-one IFC assignment, and the geographic LOCATION scoping are all real solutions with real validation behind them.
- **Test coverage over real samples is substantial** — 230 tests across 13 modules, exercising real PDFs, real DXFs, and real IFC models, not just synthetic fixtures. All green once §1 is fixed.

---

## 9. Postscript — a third client, 2026-08-17

After this review was written, a third real project (Flinders / `CS1-DRG-*`) was investigated. It is worth recording here because it tests the review's central assumption: that the codebase's careful calibration against real samples means it generalises.

It largely didn't. Six of eight title-block labels absent; a different DXF filename convention; the pile shape heuristic returning **4,966 matches (4,282 rebar) against 28 on BR06**; `IfcSite` coordinates pointing at Massachusetts. Two samples from one client turned out to validate much less than they appeared to.

The pattern is consistent and worth carrying forward: **logic built on domain invariants held; logic built on client conventions broke.** Easting/Northing in a setout table, dimension chains sharing witness points, the sheet identifier being the most prominent text — all survived unchanged. Label vocabulary, filename patterns, column names and override rates all needed work.

Three findings from that session bear on §5's performance items and §4's robustness items, and are recorded in full in `CLAUDE.md` and `PLANNING.md` §5:

- `geometry.dimension_consistency`'s override-only scope is not a defect — overriding dimension text and drawing witness lines are two drafting workarounds for the *same* problem (curved geometry giving sections that aren't perpendicular). The first is checkable from the DXF alone; the second is internally consistent while collectively stale, so only the model can catch it.
- Section and elevation cutting planes are Revit-only knowledge and cannot be recovered from the DXF export — confirmed directly against real markers.
- The remaining correspondence problem is solvable deterministically at a cost of an hour or two per project, amortised across every re-check, without the LLM that is unavailable under company policy.

**Project status: paused at the user's direction.** Not blocked technically; the open question is whether the value justifies the effort.

## 8. Suggested order of work

**Before any frontend work:**

1. ~~Fix §1.1, §1.2, §1.3 — the three live breakages.~~ **Done 2026-08-17.**
2. ~~**Add CI (§6.1).**~~ **Done 2026-08-17** — see §6.1.
3. ~~Build `run_session()` (§2.1) — the orchestrator.~~ **Done 2026-08-17.**

**Then, in parallel with early frontend work:**

4. Stable `Issue` ids + `from_dict` (§3.1) and the report exporter (§3.2) — the UI cannot do issue selection or downloads without these.
5. Error isolation (§4.1) and run-scoped caching (§4.2).
6. Spelling memoization (§5.1) — decides your progress-UI requirements.
7. FastAPI + Celery/Redis + session scratch lifecycle with purge (§2.2, §4.3).

**Lower priority, but do not lose:**

8. Bboxes for the two revision rules (§3.3).
9. ~~Packaging cleanup (§6.2)~~ and ~~the escape sequence (§6.4)~~ — **both done 2026-08-17**, as prerequisites for CI being meaningful. Docstring drift (§6.3) remains, especially the now-unblocked `setout_critical` promotion.

---

## Appendix: changes made

**During the review**, to unblock verification:

| Change | Reason |
|---|---|
| `pip install PyYAML==6.0.1` into `.venv` | Already pinned in `requirements.txt`; nothing ran without it (§1.1). Environment only — no file changed. |
| `tests/test_dxf_markup.py`: added `from __future__ import annotations` | One-line fix for the collection error blocking the whole suite (§1.2). Since deleted with the feature — see below. |

**Follow-up work**, fixing §1.3 and building §2.1:

| File | Change |
|---|---|
| `src/pdfchecker/checks/__init__.py` | `geometry` added to the registration import list (§1.3). |
| `src/pdfchecker/checks/catalog.py` | `enabled_rule_ids` defaults to `None` (resolved at run time) instead of a construction-time snapshot; new `resolved_rule_ids()`. |
| `src/pdfchecker/checks/session_config.py` | Warns when a geometry scope meets a catalog with no geometry rules. |
| `src/pdfchecker/session.py` | **New** — `run_session`, `SessionResult`, `SessionWorkspace` (§2.1, §2.2). |
| `scripts/run_session.py` | **New** — the first CLI that can drive Stage 3. |
| `tests/test_session.py` | **New** — 23 tests: registration regression guard, scope handling, scratch lifecycle, orchestration. |
| `CLAUDE.md` | Layout, commands, and status updated; the "no Stage 3 CLI yet" claim is no longer true. |

**DXF/DWG redline export removed (2026-08-17)**, after the user confirmed all drafting is done in Revit and the drafting team never opens AutoCAD — DWG/DXF is a geometry-check *input* (a Revit export), never an editable deliverable, so a CAD-native redline layer had no audience. Deleted: `markup/dxf_markup.py`, `tests/test_dxf_markup.py`, `extraction/dxf_source.py:convert_dxf_to_dwg`, and `extraction/dxf_pdf_transform.py:pdf_to_paper` (both had no remaining caller). `markup/tags.py` existed only so two formats would agree on tag numbering, so `assign_tags` folded back into `pdf_markup.py`. `convert_dwg_to_dxf` — the input direction — is untouched. PLANNING.md §8 and CLAUDE.md corrected: PDF is now the only markup target, which also means **the frontend has one download artifact rather than a format choice**.

Verification after all of the above: full suite green, and a real geometry run via the new CLI gives 37 sheets / 221 issues / 10 rules, with the marked-up PDF and DXF redlines written and the scratch directory purged on exit.
