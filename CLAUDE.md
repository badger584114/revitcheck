# CLAUDE.md

Guidance for Claude Code (and other agents) working in this repository.

## Project

A web app that ingests PDF or DWG sets of civil engineering drawings — bridges, retaining walls, and similar structures — and runs automated review in two categories:

1. **Drafting check** — standards/convention compliance, annotation and dimension completeness, cross-sheet consistency (callouts resolve to real sheets), spelling, revision table/cloud consistency, and project-specific custom checks defined per project.
2. **Geometry check** — dimensional consistency within and across views, and full structure reconstruction from setout/coordinate data, cross-checked against setout tables printed on the sheets.

A project can also have an uploaded **client specification document** (PDF/Word). It's not a third engine — it auto-extracts requirements (narrative and numeric) and feeds them into checks 1 and 2 above as project-specific rules, tagged and traceable back to the source clause. See PLANNING.md §6.

Full architecture, rationale, and open questions: see `PLANNING.md` in the repo root. Read it before making structural changes — it captures the reasoning behind the stack and pipeline design, not just the "what."

**Implementation status: Stage 1 (PDF ingestion) and Stage 2 (drafting checks) are done. Stage 3 (geometry checks) has three rules: §5a's single-sheet dimensional consistency (2026-08-10), §5b's structure reconstruction (2026-08-11, `geometry.setout_reconstruction` — bearing + dimension-chain pile setout reconstruction; extended 2026-08-14 to the multi-sheet case, where the schedule table and the DXF dimension chain live on two different sheets, linked by a printed cross-reference note — the multi-branch-graph form of §5b is still not built), and a first slice of §5's proposed third geometry source (2026-08-12, `geometry.ifc_setout_consistency` — cross-checks each reconstructed setout point against the project's IFC model).** Title block completeness, revision consistency (all three cross-checks — schedule↔title-block, sequential numbering, and cloud/triangle↔schedule row), spelling, and cross-sheet reference resolution (§4's "Cross-sheet consistency" row — symbol-based section/detail callouts, plus general free-text note references as of 2026-08-14: `"<REFER|SEE> [TO] SHEET [No.] <digits>[ TO <digits>]"`, scoped to same-drawing-package citations only — a note explicitly naming a different `DRAWING <number>` is a real, common convention for citing another discipline's own package and is deliberately excluded rather than flagged, since one ingested `Project` is one drawing set) are implemented, merged to `main`, and running against the real sample. A real Stage 3 build attempt (single-sheet dimensional consistency) confirmed PDF-only dimension-line reconstruction is unreliable — see PLANNING.md §1/§5 — so geometry checks require DXF input. **DXF ingestion is built and calibrated against real data:** `ezdxf` is installed (`requirements.txt`), ODA File Converter is installed locally (`/Applications/ODAFileConverter.app`, invoked as a local subprocess by `extraction/dxf_source.py`'s `convert_dwg_to_dxf` — confirmed to run fully headlessly, no GUI interaction), and `extraction/dxf_source.py` extracts `DIMENSION`/`VIEWPORT` entities into new DXF-only IR constructs (`DxfSheet`, `DimensionEntity`, `ViewportEntity`, `Point3D` in `ir.py`) — calibrated against all 31 real `.dwg` files in `samples/dwg/`, with two committed as pre-converted DXF in `samples/dxf/` so tests don't need ODA installed. Confirms PLANNING.md §5's core premise (`DIMENSION` entities carry measurement + witness points directly — 470 across 26/31 sheets, manual text overrides genuinely common at 54%) but **corrects two other assumptions** — see PLANNING.md §4 (DXF block inserts carry no `ATTRIB` attributes on this firm's Revit export — title-block/marker extraction needs the same text-position approach as PDF, not attribute lookup; not built yet, not needed by this check) and §8 (a sheet's paper-space layout has multiple `VIEWPORT`s, each its own DXF↔PDF transform, not one per sheet). **`checks/geometry.py`'s `geometry.dimension_consistency` rule is built**, consuming `Sheet.dxf_sheet` (attached via `extraction.dxf_source.attach_dxf_sheets`'s numeric-suffix join, §8) — scoped to linear (`dim_type=0`) dimensions with a numeric override, since a real sheet (101151) showed not every dimension is linear and not every override is numeric (some are letter tags into a separate bar-mark/schedule table, a legitimate convention this check skips rather than misreads). Issues from this rule carry no page-space `bbox` yet — the DXF→PDF transform (§8) isn't built — the DXF-space location is still reported via `suggested_fix`. The cloud/triangle → revision-schedule-row check (§4's third revision cross-check) was unblocked 2026-08-10 once `samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf` (a second, later-revision export of the same drawing set, also added 2026-08-10) gave real revision-cloud geometry to calibrate against — see `extraction/revision_clouds.py`'s docstring for the real convention found (clouds are dozens of separate small red scallop-arc path objects, not one closed curve; paired with a same-colored 3-line triangle carrying the tag digit). **`extraction/setout_reconstruction.py` + `checks/geometry.py`'s `geometry.setout_reconstruction` rule are built (2026-08-11)** — §5b's first real slice: rather than comparing DXF model geometry to the pile schedule directly (tried and rejected — the schedule's Easting/Northing columns aren't live/parametric, they're generated by a separate script from the same bearing + dimension inputs a drafter types onto the sheet, so the check needs to catch *both* manual-entry mistakes in those inputs *and* the schedule going stale when a dimension changes but the script isn't rerun), it independently walks the sheet's own printed setout-point origin (known real Easting/Northing) + printed bearing (manually-entered DMS text) + chained `DIMENSION` spacing (shared witness points, a real convention confirmed on the sample — see that module's docstring) to derive each pile's position and compares it to the schedule row. Calibrated against sheet 2871051/`samples/dxf/101051`: all 24 real piles across both abutments reconstruct within ~7mm of their schedule row (well inside the 10mm default tolerance); a real ambiguity also surfaced and got fixed — a neighboring, undimensioned structure's pile labels (`OFF STRUCTURE BARRIER` piles) can sit geometrically *closer* to an abutment chain's end node than that abutment's own (slightly offset) label, so pile-ID matching is scoped by the schedule's own `LOCATION` column (matched to nearby `"ABUTMENT A"`/`"ABUTMENT B"` DXF text) before falling back to plain nearest-distance, not nearest-distance alone. Originally single-sheet scoped (the schedule and DXF geometry on the same sheet). **Confirmed 2026-08-11 by running the rule against the full real BR06 sample** (all 31 DWGs converted via ODA and attached to the 37-page PDF, not just the two committed `samples/BR06/dxf/` files): sheet 2871051 was the *only* sheet in that sample set that carried a setout/coordinate schedule at all — every other sheet is an elevation, section, detail, or reinforcement drawing with no `SITE ID`/`EASTING`/`NORTHING`-style table.

**Extended 2026-08-14 to the multi-sheet case, using `samples/BR08/`** (a second, real, complete bridge drawing set with more/different setout schedules than BR06 — the "no real data to push on this" gap above is closed). BR08's sheet 2873042 carries a real pile schedule with no DXF geometry of its own; a printed note on the sheet says a different sheet, 2873041, carries the authoritative setout dimensions. `reconstruct_sheet` gained an optional `geometry_sheet` parameter, and `checks/geometry.py` a `_resolve_geometry_sheet` helper, to resolve and use that cross-referenced sheet's DXF instead — via a narrowly-calibrated regex over the schedule sheet's own `raw_text` (`find_geometry_sheet_no`) plus the existing `Project.sheet_by_no`. Real result: the sheet's two main 14-pile groups (`ABUTMENT A`/`B`, 28 piles) reconstruct to well under 2mm, cross-sheet-sourced — proof the mechanism itself works. A real, separate limitation also surfaced, not introduced by this work (confirmed present either way): the sheet's three smaller 5-pile sub-groups (`ABUTMENT A1`/`B1`/`B2`) didn't reconstruct accurately. A location-based one-to-one origin/bearing-selection fix (mirroring the existing pile-matching pattern) was tried and reverted the same session — it made a previously-correct group *worse*, not better — left as an honestly-reported open gap.

**Re-investigated and resolved 2026-08-14, later session:** origin/bearing selection was never actually the problem (confirmed by brute-forcing every candidate pairing against the schedule — the "nearest text" picks for `A1`/`B1`/`B2` were already correct). The real root cause was a sign/direction bug in `walk_chain`: its "positive walking direction" is derived from a chain's own two farthest local nodes, a real-world-arbitrary choice that happened to land correctly on BR06's abutments and BR08's two 14-pile main chains, but backwards on all three of BR08's short, branch-less sub-chains (confirmed: flipping the bearing 180° turned each sub-group's error from a *growing* 1.4m-16m into a *constant* ~1.40m). Fixed via sign inheritance, not schedule-checking: `_chain_has_branch` identifies the one real, checkable-without-the-schedule structural fact separating the reliably-correct chains from the reliably-wrong ones (a real fork/branch where the setout point's leader attaches, present on every correct chain seen so far and absent on every wrong one); a branch-less chain now borrows its already-walked, colinear neighbor's real-world-oriented direction (`_oriented_span_from_walk` + `_find_reference_span`) instead of guessing from its own small node set. Real result: `A1`/`B1`/`B2` now reconstruct to a constant ~1.40m, not a growing 1.4m-16m spread — and that remaining ~1.40m is real, confirmed directly by the user (who has access to the actual drawings): `A1`/`B1`/`B2` were added to this bridge later than the main structure, as part of a separate retaining-wall design package for the same large infrastructure project, and use that package's own setout point rather than the bridge sheet's — a staged-design interface condition specific to this project, not a reconstruction error. `checks/geometry.py`'s `geometry.setout_reconstruction` correctly surfacing these as a high-severity Issue is the right behavior; the fix only makes the reported magnitude honest instead of misleading. See `extraction/setout_reconstruction.py`'s docstring for the full account.

Multi-hop survey-tolerance scaling (PLANNING.md §5's `base + per_hop×√hops`) and the multi-*branch-graph* form of §5b (a chain that genuinely forks, not a single anchored path) are still not built — no real multi-hop-from-different-origins or branching-chain case has turned up yet to calibrate either against. **IFC ingestion + a first real check — PLANNING.md §5's proposed third geometry source — is built (2026-08-12):** `ifcopenshell` installed cleanly (prebuilt wheel, no repeat of the `cryptography` build-from-source issue below) and `extraction/ifc_source.py`'s `ingest_ifc(path) -> IfcModel` (`ir.py`'s new `IfcElement`/`IfcModel`) is calibrated against two real `.ifc` files, `samples/BR06/` and `samples/BR08/` — confirmed the same client project (same `IfcSite` reference point on both), which the user flagged upfront as a reason NOT to treat either file's metadata conventions as a general IFC template. Extraction is deliberately schema-general only: element class + `PredefinedType` + `GlobalId` (all IFC4-standard) and world-space geometry (confirmed `ifcopenshell.geom` always normalizes to real metres regardless of a file's declared unit) — Revit `Name`/property-set data is real but confirmed firm-specific (`T2D_`/`DIT_`/`WGA22_` prefixes), kept only as a non-authoritative display label. Real gaps confirmed: neither file carries IFC4's own georeferencing (`IfcMapConversion`/`IfcProjectedCRS`) — both instead bake real Easting/Northing directly into `IfcSite`'s raw placement, a firm-specific convention this module refuses to assume holds elsewhere; and a DXF sheet export and IFC world space are confirmed NOT directly comparable (a DWG/DXF export is a sheet's paper-space view, not model space — there's no single fixed transform to look for, confirmed by the user, not a gap to fill in later). **`checks/geometry.py`'s `geometry.ifc_setout_consistency` rule is built** on that basis — it never compares DXF geometry to IFC directly; it compares each sheet's already-*reconstructed* real-world point (`extraction/setout_reconstruction.py`'s `reconstruct_sheet`) against the nearest pile-shaped IFC element, identified via a schema-general bounding-box heuristic (small footprint, tall) rather than this firm's Revit naming — confirmed on real data to find exactly the same 28 real piles a Name-text search would, with zero mismatches (an earlier claim that BR06's IFC model had no piles at all was wrong — caught by the user, traced to an exploration pass that only sampled 8 of 45 real elements). Run end-to-end against the real sample: all 24 reconstructable piles matched their nearest IFC pile within the 10mm default tolerance, 0 issues. See PLANNING.md §5's IFC subsection, `extraction/ifc_source.py`'s docstring, and `checks/geometry.py`'s docstring for the full account. **PR #8 code review (2026-08-12) found and fixed real bugs, all applied on the same branch, not yet re-pushed/re-reviewed:** (1) `check_ifc_setout_consistency` now does one-to-one nearest-available matching across the whole project instead of independently matching every point to the same global "nearest" element, which let two points double-claim one IFC pile — fixed via greedy assignment sorted by distance, with a new real-data test (`test_one_to_one_assignment_no_double_claiming`) proving it; the underlying "wrong structure's pile" ambiguity itself isn't fully solved (no confirmed per-IFC-element location signal like the DXF-side schedule `LOCATION` scoping has), documented as a known gap in the rule's docstring, not silently ignored. (2) A model with zero pile-shaped elements no longer returns a silent `[]` — now reports one low-severity "nothing to cross-check against" Issue per affected sheet, per CLAUDE.md's own "report a coverage indicator, don't fail silently" rule. (3) `extraction/ifc_source.py`'s `_dms_to_decimal` had a real sign bug (read sign from the degrees component alone, wrong when degrees is 0 but minutes/seconds are negative) — fixed to check all components, with a regression test. (4) `geometry.setout_reconstruction` and `geometry.ifc_setout_consistency` were each independently calling `reconstruct_sheet` per sheet — added `_cached_reconstruct_sheet` (id-keyed, weakref-cleaned since `Sheet` is unhashable) so a normal run with both rules enabled doesn't recompute the whole schedule-parse + dimension-chain-walk pipeline twice; caught and fixed a real bug in the cache fix itself during testing (a bare `weakref.ref(sheet, callback)` with nothing holding the returned `ref` object never fires its callback — needed a second dict just to keep the ref alive). (5) `PLANNING.md` §3 and `ir.py`'s `IfcElement`/`IfcModel` docstring were updated — §3 now lists the new IR constructs (CLAUDE.md's own rule: any IR schema change updates §3 alongside it), and the docstring's stale "no check consumes them yet" claim (contradicted by the same diff that added `Project.ifc_model` and the check) is fixed. Verified: `tests/test_geometry.py` (25/25) and the targeted `tests/test_ifc_source.py` run all pass, plus a real end-to-end re-run against BR06's full IFC model still gives 0 issues (unchanged from before the fix, confirming the one-to-one rewrite didn't regress the real 24-pile match). **Done 2026-08-14:** the full `python -m pytest tests/` regression pass was confirmed clean (111 passed), the fix commit was pushed to `stage3-ifc-geometry-check`, and PR #8 was reviewed and merged to `main`. Not yet built: general-note cross-sheet references (§4's deferred second half of the reference graph), the multi-branch-graph form of §5b (see the setout-reconstruction status above for what *is* now built), title-block extraction from DXF, the DXF→PDF coordinate transform, an IFC-based check for non-pile superstructure (deck, abutment beams — no setout table and no shape heuristic built for them yet), the full project-config YAML schema (§4's consolidated schema — a small ad-hoc `RuleConfig` stands in for now), API/DB/queue/frontend. See "Development setup" below for the actual layout and commands.

## Development setup

```
pdfchecker/
  src/pdfchecker/
    ir.py                        # IR dataclasses (§3): Project, Sheet, TitleBlock,
                                  # RevisionEntry, RevisionCloud, Table, TextWord, PathEntity,
                                  # BBox, Reference; plus Stage 3's DXF-only constructs
                                  # (Point3D, DimensionEntity, ViewportEntity, DxfSheet) —
                                  # deliberately separate from Project/Sheet, not merged yet
    extraction/
      pdf_source.py               # PyMuPDF: word-level text + vector paths per page
                                    # (paths now carry stroke color + curve-vs-line shape too,
                                    # added for revision_clouds.py below)
      titleblock.py                # label-anchored title-block field extraction
                                    # (project-extensible field list, §4)
      tables.py                    # pdfplumber ruled-table extraction + classification,
                                    # plus word-clustering revision-schedule extraction
      references.py                # cross-sheet reference graph (§3/§4) — whole-project pass,
                                    # symbol-based (section/detail marker) resolution only;
                                    # see its docstring for the real marker convention found
                                    # on the sample (text-adjacency, not vector shape)
      revision_clouds.py           # per-sheet revision-cloud/triangle detection (§4) — see its
                                    # docstring for the real vector convention found on
                                    # samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf (a cloud is
                                    # dozens of separate small same-color scallop-arc path
                                    # objects, not one closed curve; the triangle tag is a
                                    # separate same-color 3-line cluster)
      pipeline.py                  # ties the above into ingest_pdf(path) -> Project
      dxf_source.py                 # Stage 3 (§5): convert_dwg_to_dxf (ODA File Converter,
                                    # local subprocess) + ingest_dxf(path) -> DxfSheet (DIMENSION/
                                    # VIEWPORT/INSERT/TEXT/MTEXT) — see its docstring for what real
                                    # converted DXF confirmed vs. corrected from PLANNING.md's
                                    # original assumptions
      setout_reconstruction.py      # Stage 3 (§5b): bearing + dimension-chain pile setout
                                    # reconstruction — parses the pile schedule, finds setout-point
                                    # origins/bearings/chained DIMENSIONs off DxfSheet.inserts/texts,
                                    # walks each chain to an independently-derived position per pile.
                                    # See its docstring for the real convention this is calibrated
                                    # against and why it doesn't just compare DXF model geometry to
                                    # the schedule
      ifc_source.py                 # Stage 3 (§5, proposed third source): ingest_ifc(path) ->
                                    # IfcModel, via ifcopenshell — schema-general element typing
                                    # (IfcElement subtype + PredefinedType + GlobalId) and
                                    # world-space geometry only; deliberately does NOT read Name/
                                    # ObjectType/property-set data as anything but a display label
                                    # — see its docstring for the real BR06/BR08 findings this is
                                    # built around, and why "same client project" (confirmed by the
                                    # user) means those two files' shared metadata conventions must
                                    # NOT be treated as a general-IFC template
    checks/                        # Stage 2: the drafting check engine (§4)
      issue.py                     # the Issue schema — location + suggested_fix from the first rule (§8)
      catalog.py                   # @register decorator + RuleConfig + run_checks(project, config)
      title_block.py               # required-field-presence rule
      revisions.py                 # all three revision cross-checks: schedule<->title-block,
                                    # sequential numbering, and cloud/triangle<->schedule row
      cross_sheet.py                # unresolved-reference rule — thin wrapper over
                                    # extraction/references.py's already-built graph
      spelling.py                  # en-GB spellcheck + glossary (see en_gb_variants.py's docstring
                                    # for why: pyspellchecker + a curated variant list, not a real
                                    # en-GB dictionary — LanguageTool needs a JVM not set up here)
      en_gb_variants.py            # curated British<->American spelling-variant data
      glossary.py                  # loads firm-wide/project glossary JSON files (§4)
      geometry.py                   # Stage 3: geometry.dimension_consistency (§5a, drawn-vs-stated,
                                    # scoped to linear dimensions with a numeric override),
                                    # geometry.setout_reconstruction (§5b, thin wrapper over
                                    # extraction/setout_reconstruction.py), and
                                    # geometry.ifc_setout_consistency (§5, cross-checks each
                                    # reconstructed setout point against the nearest pile-shaped
                                    # IFC element, via a schema-general shape heuristic — see
                                    # their docstrings)
  config/
    firm_glossary.json             # firm-wide glossary seed (§4) — real terms found flagged
                                    # against the sample: qualifications, product trade names
    project_glossary.json          # project-scoped glossary seed
  scripts/
    ingest.py                      # CLI: dumps a Stage-1 summary + sample IR for a given PDF
    check.py                       # CLI: runs Stage 2 checks, prints an issue summary
  tests/
    conftest.py                    # session-scoped `project`/`real_issues` fixtures (clean
                                    # sample, ~90s to ingest) plus `amended_project` (the second,
                                    # later-revision sample with real clouds, also ~90s) —
                                    # each shared across the test files that need it
    test_ingest_sample.py          # Stage 1, against the real sample (incl. the reference graph)
    test_references.py             # extraction/references.py's resolution algorithm, synthetic
                                    # Sheets exercising each branch (self-marker, note-reference
                                    # false positives, nonexistent-sheet vs. no-matching-tag)
    test_revision_clouds.py        # extraction/revision_clouds.py's clustering/tag-resolution,
                                    # synthetic PathEntity/TextWord objects + amended_project spot-checks
    test_dxf_source.py             # extraction/dxf_source.py, against the two real DXF files
                                    # committed in samples/dxf/ — no ODA install needed to run this;
                                    # convert_dwg_to_dxf's real-conversion test skips itself if
                                    # ODA isn't installed at its default path
    test_geometry.py               # Stage 3: synthetic tolerance-logic branches for both rules +
                                    # real-sample assertions via samples/dxf/101051 (attached
                                    # through the real attach_dxf_sheets join for §5a, built
                                    # directly for §5b — see test_setout_reconstruction.py for why)
    test_setout_reconstruction.py  # Stage 3 (§5b): extraction/setout_reconstruction.py's
                                    # mechanics — dimension-chain grouping, bearing DMS parsing,
                                    # signed bidirectional chain walking, one-to-one pile matching
                                    # — plus real-sample assertions: all 24 real piles on
                                    # samples/BR06/dxf/101051 (single-sheet case), and BR08's
                                    # cross-sheet schedule/geometry pair (2873042/2873041)
    test_ifc_source.py             # Stage 3 (§5, proposed third source): extraction/ifc_source.py
                                    # against the two real .ifc files (samples/BR06, samples/BR08)
                                    # — element counts/class mixes, schema-vs-project-specific
                                    # findings (no IfcMapConversion on either, shared IfcSite
                                    # reference between the two same-project files). Genuinely slow
                                    # — see its docstring, prefer running in the background
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
python scripts/ingest.py "samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
python scripts/check.py "samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf"
```
No CLI wrapper for Stage 3 yet — `ingest.py`/`check.py` are PDF/drafting-only. Driving DXF ingestion + the geometry checks today means calling `extraction.dxf_source.{convert_dwg_to_dxf,ingest_dxf,attach_dxf_sheets}`, `extraction.ifc_source.{ingest_ifc,attach_ifc_model}`, and `checks.geometry.{check_dimension_consistency,check_setout_reconstruction,check_ifc_setout_consistency}` directly (see `tests/test_geometry.py` for a working end-to-end example).
**Run tests** (a full spelling pass over 37 pages is slow — `sc.correction()` does an edit-distance search per unknown word — genuinely takes a couple of minutes; the suite also now ingests a second 37-page sample (`amended_project`, ~90s) for revision-cloud tests, on top of the original `project` fixture; prefer running in the background over waiting on it inline):
```
python -m pytest tests/ -v
```

**Spelling check limitation worth knowing:** the en-GB dictionary is `pyspellchecker`'s default (US-oriented) dictionary layered with a curated British/American variant list (`checks/en_gb_variants.py`) covering common cases — not an exhaustive or authoritative en-GB dictionary. Running it against the real sample surfaced two categories of noise, both expected rather than bugs: (1) missing inflected forms in the variant list (fixed as found, e.g. "millimetres" being flagged with "millimeters" suggested — the variant list only had base forms until this was caught), and (2) genuine domain vocabulary a general dictionary can't know (engineering qualifications, product trade names, construction terms) — this is exactly what the firm/project glossary files exist to absorb, and `config/firm_glossary.json` is seeded with real examples found this way, not meant to be exhaustive. Swapping to a proper en-GB dictionary (self-hosted LanguageTool, PLANNING.md §10's actual recommendation) is real follow-up work, not something this list tries to replace.

**Environment quirk worth knowing:** the system Python here is 3.9.5, an x86_64 build running under Rosetta on an arm64 Mac (no Homebrew/pyenv available). `pip install pdfplumber` pulls in `cryptography` transitively (via `pdfminer.six`), and recent `cryptography` releases have no prebuilt wheel for this old interpreter/arch combo — building from source fails without a working Rust/OpenSSL toolchain. Fix: `pip install "cryptography<43" --only-binary=cryptography` before installing `pdfplumber`, which is what `requirements.txt` already pins. Don't `pip install --upgrade` blindly in this environment without re-checking that constraint.

**Git remote:** `origin` is `https://github.com/badger584114/pdf-dwg-checker.git` (private). Two things worth knowing if a push fails here: (1) this machine hits a GitHub HTTP/2 push bug (`RPC failed; HTTP 400`, `unexpected disconnect while reading sideband packet`) even with valid credentials — fixed locally via `git config http.version HTTP/1.1` + a larger `http.postBuffer`, already set in this repo's `.git/config`, so a fresh clone would need it re-applied; (2) GitHub only accepts a Personal Access Token as the HTTPS password, not an account password — a stale cached credential in macOS Keychain can produce a similar-looking HTTP 400 and needs `git credential-osxkeychain erase` (protocol=https, host=github.com) to force a fresh prompt. The `gh` CLI is installed and authenticated here (confirmed 2026-08-11, `gh auth status` — logged in as `badger584114`, keyring-backed token) — use it directly for PR creation/merge (`gh pr create`, `gh pr merge`) rather than the raw GitHub REST API. (Earlier PRs #1-6 predate this and went through the REST API with the Keychain-cached token instead — that workaround is no longer necessary, just historical.)

**What's calibrated against the real sample so far** (all title-block/table extraction is label-anchored / column-name-matched, not fixed-position — see the docstrings in `extraction/titleblock.py` and `extraction/tables.py` for the specific layout quirks this had to work around, e.g. large-font values overlapping their own label's bounding box, and a full-page border/grid fooling pdfplumber's default table detector into merging the whole sheet into one bogus table): title block fields (`drawing_no`, `sheet_no`, `amend_no`, `designed_by`, `drafted_by`, `accepted_by`, `sheet_latitude`, `sheet_longitude`), the bottom-left revision schedule (§4 "Revision consistency — mechanics"), generic ruled tables (pile/setout schedules), and the cross-sheet reference graph (§4 "Cross-sheet reference graph — mechanics" — see `extraction/references.py`'s docstring for the real section/detail marker convention this sample uses, which differs from what PLANNING.md originally anticipated in two ways worth reading before touching that module). Revision-cloud/triangle detection (§4's third revision cross-check) is calibrated against `samples/T2DPAA-T2D-C3S-BR-DRG-101000_1.pdf` specifically — see `extraction/revision_clouds.py`'s docstring for the real vector convention (many separate small same-color scallop-arc objects, not one closed path; a same-color 3-line triangle for the tag, distinguished from unrelated 4-line highlight boxes found on the same sheets). DXF `DIMENSION`/`VIEWPORT` extraction (§5, Stage 3) is calibrated against all 31 real files in `samples/dwg/`, converted via ODA File Converter — see `extraction/dxf_source.py`'s docstring for what was confirmed (measurement + witness points present directly, no proximity inference needed; manual overrides genuinely common) vs. corrected (no `ATTRIB` block attributes on this firm's export; multiple `VIEWPORT`s per sheet, not one) from PLANNING.md's original assumptions. §5b's bearing + dimension-chain pile setout reconstruction is calibrated against sheet 2871051/`samples/BR06/dxf/101051` (single-sheet case) and, since 2026-08-14, BR08's sheet-2873042-schedule/sheet-2873041-geometry pair (the multi-sheet case, `samples/BR08/dxf/103041_0.dxf`) — see `extraction/setout_reconstruction.py`'s docstring for the real setout-point/bearing/chained-dimension convention found on both, the real matching ambiguity (a neighboring undimensioned structure's pile labels sitting closer to the wrong chain's node than the right pile's own label) that one-to-one assignment + schedule-`LOCATION` scoping had to resolve, and BR08's real `walk_chain` sign/direction bug (fixed 2026-08-14 via branch-detection + sign inheritance from a colinear already-walked neighbor chain) uncovered while investigating what first looked like an origin/bearing-selection limitation for closely-packed sub-groups. IFC ingestion and `geometry.ifc_setout_consistency` (§5's proposed third geometry source) are calibrated against both real `.ifc` files, `samples/BR06/` and `samples/BR08/` — see `extraction/ifc_source.py`'s docstring for the real schema-general-vs-firm-specific findings (element typing and world-space geometry generalize; Revit naming, property sets, and the raw-placement-as-real-coordinates convention confirmed on these two files do not) and `checks/geometry.py`'s docstring for the pile-shape heuristic and the real end-to-end result (all 24 reconstructable BR06 piles match their nearest IFC pile within 10mm).

## Sample drawings

`samples/` holds real PDF/DWG sheet sets (bridges, retaining walls) for reference — actual title block layouts, setout table formats, callout/revision conventions, and drafting quirks. Check here before making assumptions about how any of this is actually laid out on real sheets; it's a better source of truth than the generic descriptions in this file and PLANNING.md. When extraction or check logic is built, use these as the first real test fixtures.

Samples are organized per-project under `samples/<project>/` (e.g. `samples/BR06/`, `samples/BR08/`). `samples/BR06/dwg/` holds the real DWG set (31 files, one per sheet — a different structure from the PDF's single 37-page file). `samples/BR06/dxf/` holds two of those, pre-converted to DXF via ODA File Converter and committed directly so tests don't need ODA installed — regenerate/expand via `extraction.dxf_source.convert_dwg_to_dxf`, or manually: `ODAFileConverter <in_dir> <out_dir> ACAD2018 DXF 0 1`. `samples/BR08/` is a second, larger, multi-discipline bridge project (133 DWGs across `-BR-` and `-DU-` sheet series) — added but not yet used to calibrate anything. Both `BR06` and `BR08` now also carry a `.ifc` 3D-model export (`T2DPAA-T2D-C3S-BR-M3D-*.ifc`) — see PLANNING.md §5's IFC proposal, not yet built against.

## Intended stack (see PLANNING.md §1 for rationale)

- Backend: Python + FastAPI
- CAD/PDF parsing: `PyMuPDF` (PDF vectors/text), `pdfplumber` (tables), `python-docx` (Word specs) for drafting checks — confirmed sufficient against the real sample. `ezdxf` (DXF) + ODA File Converter for DWG→DXF are **committed, but scoped to geometry checks only** — PDF-only dimension-line reconstruction was tried and confirmed unreliable (repeated/stacked dimensions defeat spatial-proximity heuristics); DXF's native DIMENSION entity removes that ambiguity. Drafting checks stay on PDF. See PLANNING.md §1
- Geometry: `shapely`, `numpy`
- OCR: `pytesseract` or `paddleocr` (scanned/raster sheets)
- Spelling: `pyspellchecker` or self-hosted LanguageTool, **en-GB base dictionary** (British spelling, e.g. "specialised" not "specialized" — never en-US), layered with a firm-wide + project-level custom glossary that engineers can add technical terms to directly from a flagged issue so they stop recurring
- Client spec extraction: self-hosted LLM (local Llama/Mistral-class model via vLLM or Ollama) for narrative clauses, deterministic parsing for structured numeric schedules — see PLANNING.md §6
- DB: PostgreSQL — narrow scope (see the stateless-by-design constraint below): auth accounts + firm-level config (glossary, standard rule bundles) only, not client drawing data
- Queue: Redis + Celery (parsing/OCR/reconstruction are async worker jobs, not request/response)
- Storage: S3-compatible object storage — scratch space for one session's processing, purged after report delivery, not long-term drawing storage
- Frontend: React + TypeScript (Next.js), PDF.js + custom DXF renderer, canvas/SVG overlay for issue markup. Upload UI: a workflow-scope selector (drafting-only vs. drafting + geometry) plus two dropzones — PDF (always shown) and CAD/DXF/DWG (appears only once "+ geometry" is selected)
- Dev/deploy: Docker Compose locally; containerized services (api, worker, db, redis, object storage)

Treat this as a starting recommendation. If the user specifies an existing internal stack, defer to that instead of the above.

**Hard constraint: zero outbound internet access at runtime.** The whole stack must be deployable self-contained (Docker Compose/Kubernetes, even air-gapped) — no cloud OCR/spellcheck APIs, no cloud LLM APIs, no CDN-loaded frontend assets, no external identity providers by default. See PLANNING.md §10 for the specific self-hosted choice for each component (e.g. self-hosted LanguageTool not a cloud grammar API, MinIO not AWS S3, bundled PDF.js not a CDN reference, a local LLM not a cloud one for spec extraction).

**Hard constraint: stateless by design, no persistent project/session data (decided 2026-08-10).** This is not a project-management tool with saved history — login, pick a workflow scope, upload drawings (+ optional spec), run checks, download the report/markup set, and the server clears that session's uploaded files, extracted IR, generated Issues, and rendered markups. The next login starts fully fresh; there's no list of past projects/runs. Only firm-level config (glossary, standard rule bundles) persists. This is a client-confidentiality decision, not just an architectural one — see PLANNING.md §2 "Stateless by design" and §10. Consequence worth knowing before extending anything: cross-run revision diffing (comparing this run to a previous one) was in the original plan but was **dropped**, not adapted, because it depended on server-stored run history that no longer exists — see PLANNING.md §7/§8 for what that section originally specified and the stateless-diff redesign sketched as the path back in if it's ever wanted.

## Core data model: the Intermediate Representation (IR)

Both check engines operate on a common schema, regardless of whether the source file was PDF or DXF (PLANNING.md §3):

- `Project` → `Sheet` (title block metadata, units, scale, coordinate origin)
- `Entity`: lines, polylines, arcs, circles, text, dimensions, blocks/inserts, hatches, layers
- `Table`: parsed setout/coordinate tables, revision tables, schedules
- `Reference`: cross-sheet edges (§4's reference graph, built) — a whole-project pass run after per-sheet extraction, not part of it (needs every sheet's view titles indexed first); symbol-based (section/detail marker) resolution, plus general free-text note references (`ref_type == "note"`, 2026-08-14) — same-drawing-package citations only, see `extraction/references.py`'s docstring. Match-line references (§3's fourth type) still not built

Any new extractor (PDF or DXF) must normalize into this IR — don't build format-specific downstream logic in the check engines.

## Check engines

- Each check run has a **scope**: drafting-only, or drafting + geometry — selected per run, not fixed per project. IR extraction always runs (geometry depends on drafting-extracted entities as reconstruction input), but scope controls which engine(s) produce Issues. There is no geometry-only mode. See PLANNING.md §2.
- **Drafting checks** are rule functions with signature `(IR, config) -> [Issue]`, registered in a catalog. A project's active rule set is a config (rule IDs + parameters), so adding a project-specific check should be a config change, not a code change. See PLANNING.md §4 for the rule categories.
- **Geometry checks** have two parts (PLANNING.md §5): **(a) drawn-vs-stated dimensional consistency** — compare a DXF `DIMENSION`'s raw measurement against its manually-overridden stated text, within a rounding-grid tolerance; **built**, `checks/geometry.py`'s `geometry.dimension_consistency`. **(b) full structure reconstruction** from setout data and dimension chains, cross-checking computed coordinates against tabulated setout values — **not built**, the highest-risk/highest-effort part of the system, see PLANNING.md §5 before starting it. Partial reconstruction should report a confidence/coverage indicator rather than failing silently.
- Every rule's `Issue` output must carry a precise location (point, bounding box, or entity handle — not just a sheet number) and an optional `suggested_fix`. This isn't optional polish: the markup export feature (PLANNING.md §8) depends on it, so build it into the Issue schema from the first rule written.

## Client specification check (see PLANNING.md §6)

A project can have an uploaded client spec (PDF/DOCX) alongside its drawings. Real specs mix narrative clauses and numeric schedules/tables — don't assume one format. Extraction pipeline: text/table extraction → clause segmentation → requirement extraction (self-hosted LLM for narrative clauses, deterministic parsing for structured numeric limits) → `SpecRequirement` records (`Project`-level, not `Sheet`-level) → auto-registered as rules in the same rule catalog used by drafting/geometry checks.

- Narrative/presence requirements become drafting rule-catalog entries; numeric/threshold requirements (cover, spacing, FS, dimensional limits) become geometry-engine parameter/tolerance overrides. No new engine or IR — spec extraction is a rule *source*, not a third check type.
- **Auto-apply with flagging**: extracted rules go live immediately (no approval gate blocking a check run), but every spec-derived `Issue` is tagged `Spec: <what's wrong>`, carries its source clause reference and an extraction confidence score, and appears on a review screen so a misextracted rule can be corrected/disabled after the fact.
- MVP: scope to numeric/threshold extraction from structured schedules first (least ambiguous), defer narrative clause extraction until that's proven — see PLANNING.md §9 build order.

## Markup & redline export (see PLANNING.md §8)

Engineers select which flagged issues to include, and the app burns them onto the sheets as redlines — output as a marked-up PDF and/or DXF/DWG ready to hand to the drafting team. Keep markups minimal: a tight box around the flagged content's bounding box (a point marker if the issue is an absence, e.g. a missing dimension), plus a leader to a single-line note in `Label: payload` form — no restated descriptions on the sheet. Each rule category has a fixed label and minimal payload, e.g. `Spelling: concrete`, `Missing: dimension`, `Setout Δ0.015`, `Spec: cover 42mm < 50mm` — see PLANNING.md §8 for the full table. Each note also carries a short reference tag (e.g. `#014`) matching an entry in the full exported report, so a drafter can look up complete detail if the terse note isn't enough — the markup set and the report are downloaded together. PDF markup uses native PDF annotations (PyMuPDF), not flattened rasters, so the drafting team can toggle/reply in Acrobat or Bluebeam. DXF/DWG markup goes on a dedicated redline layer so drafters can edit it natively in CAD. **PDF is the primary markup target for every Issue, including geometry-check ones (decided 2026-08-10)** — opening a marked-up PDF beats opening each sheet's DWG in CAD just to see what's flagged; the DXF/DWG redline layer stays as a secondary CAD-native option. This needs a sheet-correspondence + DXF-space→PDF-page-space transform between the two formats — see PLANNING.md §8 for the mechanics and why it's low-risk here (this firm's Revit workflow exports both PDF and DWG from the same sheet view). Sequence this after the check engines are reliable — it's a trust-dependent feature.

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
