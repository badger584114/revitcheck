# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.

## Project

A web app that ingests PDF or DWG sets of civil engineering drawings — bridges, retaining walls, and similar structures — and runs automated review in two categories:

1. **Drafting check** — standards/convention compliance, annotation and dimension completeness, cross-sheet consistency (callouts resolve to real sheets), spelling, revision table/cloud consistency, and project-specific custom checks defined per project.
2. **Geometry check** — dimensional consistency within and across views, and full structure reconstruction from setout/coordinate data, cross-checked against setout tables printed on the sheets.

A project can also have an uploaded **client specification document** (PDF/Word). It's not a third engine — it auto-extracts requirements (narrative and numeric) and feeds them into checks 1 and 2 above as project-specific rules, tagged and traceable back to the source clause. See PLANNING.md §6.

Full architecture, rationale, and open questions: see `PLANNING.md` in the repo root. Read it before making structural changes — it captures the reasoning behind the stack and pipeline design, not just the "what."

**Implementation status: Stage 1 (PDF ingestion) and Stage 2 (drafting checks) are done; Stage 3 (geometry checks, PLANNING.md §9 step 3) is deferred for now — PDF/drafting work continues instead (per-user decision, 2026-08-09).** Title block completeness, revision consistency, spelling, and cross-sheet reference resolution (§4's "Cross-sheet consistency" row — symbol-based section/detail callouts only, general free-text note references still deferred per §4's scoping note) are implemented, merged to `main`, and running against the real sample. A real Stage 3 build attempt (single-sheet dimensional consistency) confirmed PDF-only dimension-line reconstruction is unreliable — see PLANNING.md §1/§5 — so geometry checks now require DXF input; that DXF ingestion work hasn't been built yet (no `ezdxf` code, no DXF sample in `samples/` yet either — check with the user before assuming one exists). Not yet built: the cloud/triangle → revision-schedule-row check (§4's third revision cross-check — needs new geometric detection of revision clouds; **paused as of 2026-08-09** until the user adds sample drawings that actually contain revision clouds to `samples/` — the current sample has none to calibrate detection against, same "build against real geometry, not the planning doc's description" approach the reference graph used), general-note cross-sheet references (§4's deferred second half of the reference graph), geometry checks (§5), the full project-config YAML schema (§4's consolidated schema — a small ad-hoc `RuleConfig` stands in for now), API/DB/queue/frontend. See "Development setup" below for the actual layout and commands.

## Development setup

```
pdfchecker/
  src/pdfchecker/
    ir.py                        # IR dataclasses (§3): Project, Sheet, TitleBlock,
                                  # RevisionEntry, Table, TextWord, PathEntity, BBox, Reference
    extraction/
      pdf_source.py               # PyMuPDF: word-level text + vector paths per page
      titleblock.py                # label-anchored title-block field extraction
                                    # (project-extensible field list, §4)
      tables.py                    # pdfplumber ruled-table extraction + classification,
                                    # plus word-clustering revision-schedule extraction
      references.py                # cross-sheet reference graph (§3/§4) — whole-project pass,
                                    # symbol-based (section/detail marker) resolution only;
                                    # see its docstring for the real marker convention found
                                    # on the sample (text-adjacency, not vector shape)
      pipeline.py                  # ties the above into ingest_pdf(path) -> Project
    checks/                        # Stage 2: the drafting check engine (§4)
      issue.py                     # the Issue schema — location + suggested_fix from the first rule (§8)
      catalog.py                   # @register decorator + RuleConfig + run_checks(project, config)
      title_block.py               # required-field-presence rule
      revisions.py                 # schedule<->title-block cross-check + sequential numbering
                                    # (NOT the cloud/triangle check yet — needs revision-cloud detection)
      cross_sheet.py                # unresolved-reference rule — thin wrapper over
                                    # extraction/references.py's already-built graph
      spelling.py                  # en-GB spellcheck + glossary (see en_gb_variants.py's docstring
                                    # for why: pyspellchecker + a curated variant list, not a real
                                    # en-GB dictionary — LanguageTool needs a JVM not set up here)
      en_gb_variants.py            # curated British<->American spelling-variant data
      glossary.py                  # loads firm-wide/project glossary JSON files (§4)
  config/
    firm_glossary.json             # firm-wide glossary seed (§4) — real terms found flagged
                                    # against the sample: qualifications, product trade names
    project_glossary.json          # project-scoped glossary seed
  scripts/
    ingest.py                      # CLI: dumps a Stage-1 summary + sample IR for a given PDF
    check.py                       # CLI: runs Stage 2 checks, prints an issue summary
  tests/
    conftest.py                    # session-scoped `project`/`real_issues` fixtures — ingesting
                                    # the 37-page sample takes ~90s, share one run across test files
    test_ingest_sample.py          # Stage 1, against the real sample (incl. the reference graph)
    test_references.py             # extraction/references.py's resolution algorithm, synthetic
                                    # Sheets exercising each branch (self-marker, note-reference
                                    # false positives, nonexistent-sheet vs. no-matching-tag)
    test_checks.py                 # Stage 2: real-sample assertions (this set is clean, so these
                                    # cover the "correctly finds nothing" path) + synthetic minimal
                                    # IR objects for the "fires when it should" path
  requirements.txt                 # frozen from .venv — see the Python 3.9 note below
```

**Setup:**
```
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
```
**Run ingestion / checks against the sample:**
```
python scripts/ingest.py "samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
python scripts/check.py "samples/T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
```
**Run tests** (a full spelling pass over 37 pages is slow — `sc.correction()` does an edit-distance search per unknown word — genuinely takes a couple of minutes; prefer running in the background over waiting on it inline):
```
python -m pytest tests/ -v
```

**Spelling check limitation worth knowing:** the en-GB dictionary is `pyspellchecker`'s default (US-oriented) dictionary layered with a curated British/American variant list (`checks/en_gb_variants.py`) covering common cases — not an exhaustive or authoritative en-GB dictionary. Running it against the real sample surfaced two categories of noise, both expected rather than bugs: (1) missing inflected forms in the variant list (fixed as found, e.g. "millimetres" being flagged with "millimeters" suggested — the variant list only had base forms until this was caught), and (2) genuine domain vocabulary a general dictionary can't know (engineering qualifications, product trade names, construction terms) — this is exactly what the firm/project glossary files exist to absorb, and `config/firm_glossary.json` is seeded with real examples found this way, not meant to be exhaustive. Swapping to a proper en-GB dictionary (self-hosted LanguageTool, PLANNING.md §10's actual recommendation) is real follow-up work, not something this list tries to replace.

**Environment quirk worth knowing:** the system Python here is 3.9.5, an x86_64 build running under Rosetta on an arm64 Mac (no Homebrew/pyenv available). `pip install pdfplumber` pulls in `cryptography` transitively (via `pdfminer.six`), and recent `cryptography` releases have no prebuilt wheel for this old interpreter/arch combo — building from source fails without a working Rust/OpenSSL toolchain. Fix: `pip install "cryptography<43" --only-binary=cryptography` before installing `pdfplumber`, which is what `requirements.txt` already pins. Don't `pip install --upgrade` blindly in this environment without re-checking that constraint.

**Git remote:** `origin` is `https://github.com/badger584114/pdf-dwg-checker.git` (private). Two things worth knowing if a push fails here: (1) this machine hits a GitHub HTTP/2 push bug (`RPC failed; HTTP 400`, `unexpected disconnect while reading sideband packet`) even with valid credentials — fixed locally via `git config http.version HTTP/1.1` + a larger `http.postBuffer`, already set in this repo's `.git/config`, so a fresh clone would need it re-applied; (2) GitHub only accepts a Personal Access Token as the HTTPS password, not an account password — a stale cached credential in macOS Keychain can produce a similar-looking HTTP 400 and needs `git credential-osxkeychain erase` (protocol=https, host=github.com) to force a fresh prompt. The `gh` CLI isn't installed here — PR creation/merge goes through the GitHub REST API with the Keychain-cached token instead.

**What's calibrated against the real sample so far** (all title-block/table extraction is label-anchored / column-name-matched, not fixed-position — see the docstrings in `extraction/titleblock.py` and `extraction/tables.py` for the specific layout quirks this had to work around, e.g. large-font values overlapping their own label's bounding box, and a full-page border/grid fooling pdfplumber's default table detector into merging the whole sheet into one bogus table): title block fields (`drawing_no`, `sheet_no`, `amend_no`, `designed_by`, `drafted_by`, `accepted_by`, `sheet_latitude`, `sheet_longitude`), the bottom-left revision schedule (§4 "Revision consistency — mechanics"), generic ruled tables (pile/setout schedules), and the cross-sheet reference graph (§4 "Cross-sheet reference graph — mechanics" — see `extraction/references.py`'s docstring for the real section/detail marker convention this sample uses, which differs from what PLANNING.md originally anticipated in two ways worth reading before touching that module).

## Sample drawings

`samples/` holds real PDF/DWG sheet sets (bridges, retaining walls) for reference — actual title block layouts, setout table formats, callout/revision conventions, and drafting quirks. Check here before making assumptions about how any of this is actually laid out on real sheets; it's a better source of truth than the generic descriptions in this file and PLANNING.md. When extraction or check logic is built, use these as the first real test fixtures.

## Intended stack (see PLANNING.md §1 for rationale)

- Backend: Python + FastAPI
- CAD/PDF parsing: `PyMuPDF` (PDF vectors/text), `pdfplumber` (tables), `python-docx` (Word specs) for drafting checks — confirmed sufficient against the real sample. `ezdxf` (DXF) + ODA File Converter for DWG→DXF are **committed, but scoped to geometry checks only** — PDF-only dimension-line reconstruction was tried and confirmed unreliable (repeated/stacked dimensions defeat spatial-proximity heuristics); DXF's native DIMENSION entity removes that ambiguity. Drafting checks stay on PDF. See PLANNING.md §1
- Geometry: `shapely`, `numpy`
- OCR: `pytesseract` or `paddleocr` (scanned/raster sheets)
- Spelling: `pyspellchecker` or self-hosted LanguageTool, **en-GB base dictionary** (British spelling, e.g. "specialised" not "specialized" — never en-US), layered with a firm-wide + project-level custom glossary that engineers can add technical terms to directly from a flagged issue so they stop recurring
- Client spec extraction: self-hosted LLM (local Llama/Mistral-class model via vLLM or Ollama) for narrative clauses, deterministic parsing for structured numeric schedules — see PLANNING.md §6
- DB: PostgreSQL + PostGIS
- Queue: Redis + Celery (parsing/OCR/reconstruction are async worker jobs, not request/response)
- Storage: S3-compatible object storage
- Frontend: React + TypeScript (Next.js), PDF.js + custom DXF renderer, canvas/SVG overlay for issue markup
- Dev/deploy: Docker Compose locally; containerized services (api, worker, db, redis, object storage)

Treat this as a starting recommendation. If the user specifies an existing internal stack, defer to that instead of the above.

**Hard constraint: zero outbound internet access at runtime.** The whole stack must be deployable self-contained (Docker Compose/Kubernetes, even air-gapped) — no cloud OCR/spellcheck APIs, no cloud LLM APIs, no CDN-loaded frontend assets, no external identity providers by default. See PLANNING.md §10 for the specific self-hosted choice for each component (e.g. self-hosted LanguageTool not a cloud grammar API, MinIO not AWS S3, bundled PDF.js not a CDN reference, a local LLM not a cloud one for spec extraction).

## Core data model: the Intermediate Representation (IR)

Both check engines operate on a common schema, regardless of whether the source file was PDF or DXF (PLANNING.md §3):

- `Project` → `Sheet` (title block metadata, units, scale, coordinate origin)
- `Entity`: lines, polylines, arcs, circles, text, dimensions, blocks/inserts, hatches, layers
- `Table`: parsed setout/coordinate tables, revision tables, schedules
- `Reference`: cross-sheet edges (§4's reference graph, built) — a whole-project pass run after per-sheet extraction, not part of it (needs every sheet's view titles indexed first); symbol-based (section/detail marker) resolution only so far, general free-text note references still deferred

Any new extractor (PDF or DXF) must normalize into this IR — don't build format-specific downstream logic in the check engines.

## Check engines

- Each check run has a **scope**: drafting-only, or drafting + geometry — selected per run, not fixed per project. IR extraction always runs (geometry depends on drafting-extracted entities as reconstruction input), but scope controls which engine(s) produce Issues. There is no geometry-only mode. See PLANNING.md §2.
- **Drafting checks** are rule functions with signature `(IR, config) -> [Issue]`, registered in a catalog. A project's active rule set is a config (rule IDs + parameters), so adding a project-specific check should be a config change, not a code change. See PLANNING.md §4 for the rule categories.
- **Geometry checks** reconstruct structure geometry from setout data and dimension chains, then cross-check computed coordinates against tabulated setout values, flagging discrepancies beyond tolerance. This is the highest-risk/highest-effort part of the system — see PLANNING.md §5 before extending it. Partial reconstruction should report a confidence/coverage indicator rather than failing silently.
- Every rule's `Issue` output must carry a precise location (point, bounding box, or entity handle — not just a sheet number) and an optional `suggested_fix`. This isn't optional polish: the markup export feature (PLANNING.md §8) depends on it, so build it into the Issue schema from the first rule written.

## Client specification check (see PLANNING.md §6)

A project can have an uploaded client spec (PDF/DOCX) alongside its drawings. Real specs mix narrative clauses and numeric schedules/tables — don't assume one format. Extraction pipeline: text/table extraction → clause segmentation → requirement extraction (self-hosted LLM for narrative clauses, deterministic parsing for structured numeric limits) → `SpecRequirement` records (`Project`-level, not `Sheet`-level) → auto-registered as rules in the same rule catalog used by drafting/geometry checks.

- Narrative/presence requirements become drafting rule-catalog entries; numeric/threshold requirements (cover, spacing, FS, dimensional limits) become geometry-engine parameter/tolerance overrides. No new engine or IR — spec extraction is a rule *source*, not a third check type.
- **Auto-apply with flagging**: extracted rules go live immediately (no approval gate blocking a check run), but every spec-derived `Issue` is tagged `Spec: <what's wrong>`, carries its source clause reference and an extraction confidence score, and appears on a review screen so a misextracted rule can be corrected/disabled after the fact.
- MVP: scope to numeric/threshold extraction from structured schedules first (least ambiguous), defer narrative clause extraction until that's proven — see PLANNING.md §9 build order.

## Markup & redline export (see PLANNING.md §8)

Engineers select which flagged issues to include, and the app burns them onto the sheets as redlines — output as a marked-up PDF and/or DXF/DWG ready to hand to the drafting team. Keep markups minimal: a tight box around the flagged content's bounding box (a point marker if the issue is an absence, e.g. a missing dimension), plus a leader to a single-line note in `Label: payload` form — no restated descriptions on the sheet. Each rule category has a fixed label and minimal payload, e.g. `Spelling: concrete`, `Missing: dimension`, `Setout Δ0.015`, `Spec: cover 42mm < 50mm` — see PLANNING.md §8 for the full table. Each note also carries a short reference tag (e.g. `#014`) matching an entry in the full exported report, so a drafter can look up complete detail if the terse note isn't enough — the markup set and the report are downloaded together. PDF markup uses native PDF annotations (PyMuPDF), not flattened rasters, so the drafting team can toggle/reply in Acrobat or Bluebeam. DXF/DWG markup goes on a dedicated redline layer so drafters can edit it natively in CAD. Sequence this after the check engines are reliable — it's a trust-dependent feature.

## Build order (see PLANNING.md §9)

1. PDF ingestion first, for drafting checks (confirmed sufficient); DWG/ODA + `ezdxf` conversion is committed work for geometry checks specifically, once PDF-only dimension reconstruction was confirmed unreliable (PLANNING.md §1)
2. Drafting checks: title block completeness, spelling, revision consistency (build first — clearest value, fastest to ship)
3. Geometry checks: single-sheet dimensional consistency before multi-sheet structure reconstruction
4. Prove the pipeline on one structure type (e.g., a single retaining wall run) before generalizing to bridges
5. JSON-based project rule config before a full rules-authoring UI
6. Client spec upload + numeric-threshold extraction (§6), scoped to structured schedules first — this automates step 5's rule config, so build it once step 5 works
7. Narrative/presence requirement extraction from free-form spec prose, once numeric extraction (step 6) is proven
8. Markup export once the check engines are trustworthy enough to send their output to drafting with minimal review

## Working conventions

- Any change to the IR schema affects both check engines — update PLANNING.md §3 alongside the change.
- New drafting rules go in the rule catalog, not hardcoded into pipeline logic.
- Geometry reconstruction logic should always report *how* it reached a value (which dimension chain, which table row) so results are auditable by an engineer — this is a compliance/review tool, not a black box.
- Tolerances (drafting vs. survey) must be configurable, never hardcoded constants.
- Spec-derived rules must always carry their source clause text and an extraction confidence score through to the Issue — same auditability principle as geometry reconstruction, applied to LLM-based extraction instead of dimension-chain math.
