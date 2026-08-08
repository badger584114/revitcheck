# PDF Checker — Architecture & Planning

A web app that ingests PDF or DWG sets of civil engineering drawings (bridges, retaining walls) and runs two categories of automated review: a **drafting check** (standards, annotation, cross-sheet, spelling/revisions) and a **geometry check** (dimensional consistency, full structure reconstruction from setout data, cross-checking against setout tables). A third input, an uploaded **client specification document**, can auto-generate project-specific rules that feed both engines (§6).

## 1. Recommended stack

**Backend: Python (FastAPI)**

Python has the strongest ecosystem for the two hardest problems here — CAD/PDF parsing and geometric validation:

- `ezdxf` — reads/writes DXF, gives structured access to lines, polylines, arcs, circles, text, dimensions, blocks, layers. **Deferred/conditional** — see the DWG handling note below; don't build this until PDF-only is actually proven insufficient.
- `PyMuPDF` (fitz) — extracts vector paths, text (with position), and images from PDF
- `pdfplumber` — table extraction (setout tables, revision blocks, title blocks) from PDF
- `python-docx` — text/table extraction from Word-format client specs
- `shapely` / `numpy` — geometric operations, tolerance checks, polygon closure, coordinate math
- `opencv-python` + `pytesseract` (or `paddleocr`) — OCR for scanned/raster sheets
- `pyspellchecker` / LanguageTool (self-hosted) — spelling, base dictionary locale **en-GB** (British spelling — "specialised", "colour", "kerb", not the US variants), layered with a custom engineering/abbreviation dictionary
- Self-hosted LLM (local Llama/Mistral-class model via vLLM or Ollama) — client spec requirement extraction (§6); no cloud LLM API, per the zero-outbound-internet constraint (§9)

**DWG/DXF handling — conditional, not committed.** DWG is a proprietary format with no reliable open-source parser; if it's ever needed, the standard approach is converting server-side via the free, redistributable **ODA File Converter** then parsing with `ezdxf`. But this whole path was added as a hedge against uncertainty about whether PDF vector extraction alone carries enough information (layers, block/symbol structure, precise dimension geometry) — not as a proven requirement. Given source drawings are Revit-authored PDF exports (§5's tolerance discussion already established this), the honest position is: most of what the two engines need — vector geometry, text, tables, dimension witness-line geometry — is retrievable from a true vector PDF (`PyMuPDF` reads real coordinate paths, not a raster), so PDF-only may well be sufficient. What PDF genuinely lacks relative to DXF is block/insert structure (no native "this is an instance of symbol X with these attributes" — see §4's cross-sheet reference graph, where this already shows up as PDF markers needing geometric detection instead of a block-name lookup) and layer names surviving reliably (PDF *can* carry them as Optional Content Groups if the exporter preserves them, but it's not guaranteed the way DXF's native layer table is).

Don't build DXF parsing until that gap is actually demonstrated. Concretely: run the extraction pipeline against a real sample in `samples/` and check (a) whether it's a true vector PDF or a flattened raster, (b) whether layers survive as named OCGs, (c) whether the geometric-detection approach to cross-sheet markers (§4) produces good enough match confidence without block attributes. Only invest in ODA/`ezdxf` if one of those turns out to be a real, not hypothetical, blocker — this sharpens §9 step 1's existing "defer DWG/ODA conversion" into "conditional on proof of need," not just "later."

**Frontend: React + TypeScript (Next.js)**

- PDF.js for PDF rendering, a custom DXF→SVG/Canvas renderer (or convert DXF to PDF server-side via ODA/`ezdxf`+`matplotlib` for a unified viewer)
- Canvas/SVG overlay layer to highlight flagged issues directly on the drawing, with click-to-jump between issue list and drawing location

**Data & infra**

- PostgreSQL + PostGIS — structured storage of extracted entities and geometry, with native geometric query support
- Redis + Celery (or RQ) — async job queue; parsing, OCR, and geometry reconstruction are CPU-heavy and too slow for a request/response cycle
- S3-compatible object storage — original files, converted DXF/DWG, rendered overlays
- Docker Compose for local dev; containers for API, worker, DB, Redis, MinIO (local S3)

This stack is a recommendation, not a constraint — flag if there's an existing internal stack (e.g. .NET shop, Azure-only infra) this needs to fit into instead.

## 2. Processing pipeline

```
Upload (PDF/DWG) 
  -> File type detection & DWG->DXF conversion (ODA)
  -> Extraction (per sheet):
       - Vector entities (lines, polylines, arcs, circles, dimensions, blocks, layers)
       - Text entities (native) + OCR text (for raster content)
       - Tables (setout/coordinate tables, revision blocks, title blocks)
  -> Normalization into common Intermediate Representation (IR)
  -> Drafting Check Engine   \
  -> Geometry Check Engine    > run against IR, produce Issues
  -> Report generation (in-app + exportable PDF/Excel)
```

Each stage runs as an async worker job; job status is polled/streamed to the frontend. A client spec upload (§6) runs a parallel extraction pipeline that feeds rules into the two check engines before they run against the IR.

**Check scope selection.** Extraction/IR-normalization always runs (both engines depend on it), but the two check engines execute independently against the IR, so a `CheckRun` records which scope was requested:

- **Drafting only** — runs §4 against the IR, skips §5 entirely. Fast, no reconstruction cost; useful for early-stage/incomplete sheet sets where geometry reconstruction isn't meaningful yet (e.g. setout data not finalized), or when an engineer just wants a quick drafting-convention pass.
- **Drafting + geometry** — runs both engines.

There's no "geometry only" mode — geometry checks (§5) still consume drafting-extracted entities/text (title block scale, units) as reconstruction inputs, so drafting extraction is a prerequisite even when its *rule* output isn't wanted; scope selection only controls which engines' Issues get generated and reported, not which extraction steps run. The scope choice is per check run (not fixed per project), defaulting to whatever the project's last run used, so an engineer can re-run drafting-only after a quick fix without paying for a full geometry reconstruction each time. Client-spec-derived rules (§6) respect the same scope split: narrative/presence rules only fire in a drafting-inclusive run, numeric/threshold overrides only apply when geometry runs.

**Upload handling for large files.** Full drawing packs are routinely 100MB+ as a single PDF (many sheets in one file), so there's no hard cap dictated by the architecture, but every layer the upload passes through defaults to limits far below that and needs explicit, coordinated configuration rather than discovering a silent 413 in production:

- **Don't proxy the raw file through the API app process.** Have the browser upload directly to object storage (MinIO/S3-compatible) via a presigned multipart upload URL issued by the API, then have the browser notify the API once the upload completes so a processing job can be queued. This keeps a 100MB+ transfer off the API server's request memory/threads entirely, decoupling upload reliability from API server config — the API is only ever handling small JSON requests, never streaming the file itself.
- **Reverse proxy / ASGI limits** (nginx `client_max_body_size`, any load balancer body-size cap, Uvicorn's own limits) still need raising if anything sits in front of the presigned-upload path (e.g. the presign-request endpoint itself, or a dev setup without direct-to-storage upload yet) — these default low (often 1MB) and silently reject otherwise-fine uploads.
- **Chunked/resumable upload** on the frontend (e.g. multipart PUT in parts, or a resumable protocol) rather than a single request — on a 100MB+ file over an office network, a single-shot upload that fails at 95% and restarts from zero is a real usability problem, not a hypothetical one.
- **Size limit is a configurable ceiling, not hardcoded** — same "config, never a hardcoded constant" principle as tolerances elsewhere in this doc. Start generous (e.g. default 500MB/file) and adjust per deployment once real pack sizes are known, rather than picking a number that turns out to clip a real project.
- **Processing time is already decoupled from upload** — extraction/OCR/geometry reconstruction run as async worker jobs (per the pipeline above), so a large pack taking minutes to process doesn't block anything; the actual resource risk for large packs is OCR over many raster/scanned sheets (CPU/memory-heavy), not file size or vector/text extraction (`PyMuPDF` opens and reads pages lazily, not the whole file into memory at once) — worth sizing OCR worker resource limits independently from upload size limits.

**Processing at scale (200–250 sheet packs on large structures).** At that page count, a single serial job over the whole PDF is the wrong shape — fan out per-sheet extraction as independent parallel worker tasks (a Celery group/chord) instead of one big job for the whole file:

- **Parallel extraction, per sheet.** Each sheet's vector/text/table extraction is independent of every other sheet, so it parallelizes cleanly across the worker pool — wall-clock time for a 250-sheet pack scales with worker concurrency, not sheet count processed serially.
- **Granular progress, not a spinner.** Job status should report sheet-level progress (e.g. "142/250 sheets extracted"), not just started/done — a multi-minute job on a large pack needs visible progress, and per-sheet granularity falls out naturally once extraction is fanned out per sheet.
- **Failure isolation per sheet.** One corrupt or unusual sheet (bad embedded image, malformed vector data) shouldn't abort the other 249 — report per-sheet extraction status (`ok` / `failed: <reason>`) the same way §5b already reports per-point reconstruction status, and run the check engines against whatever extracted successfully rather than failing the whole run over one bad sheet.
- **Rule scheduling splits by scope.** Most drafting rules (§4) and single-sheet geometry checks (§5a) only need their own sheet's IR, so they can run the moment that sheet's extraction completes — no need to wait on the other 249. Cross-sheet rules (§4's cross-sheet consistency, §5's cross-view matching, §5b's multi-sheet reconstruction) genuinely need the full extraction fan-in — a join after every sheet's extraction task completes — since they depend on entities/tables from sheets other than their own. Worth tagging each rule in the catalog with a `scope: single_sheet | cross_sheet` attribute so the scheduler knows which tier to run it in.
- **Incremental review as a UX consequence, not just a backend optimization.** Since single-sheet results can be ready well before all 250 sheets finish, the issue list can populate sheet-by-sheet rather than the engineer staring at a blank screen until the entire pack completes — worth designing the frontend's job-status/issue-list UI to stream results in rather than reveal them all at once, given pack sizes at this scale.

## 3. Intermediate Representation (IR)

A common schema both engines operate on, regardless of whether the source was PDF or DXF:

- **Project** → **Sheet** (one per drawing page/tab)
- **Sheet** metadata: title block fields (drawing number, title, scale, revision, date, discipline), units, coordinate origin
- **Entities**: lines, polylines, arcs, circles, text (with position/rotation/style), dimensions (with measured value + witness lines), blocks/inserts (symbols, north arrows, detail bubbles, section markers), hatches
- **Tables**: parsed rows/columns for setout tables (point ID, northing/easting/elevation or station/offset/level), revision tables, schedules
- **Layers**: name, color, linetype, per project layer-naming convention
- **References**: cross-sheet edges connecting a source marker entity (section cut, detail bubble, match line, or a general-note text match) to a target view/sheet, each with a `type` (section/detail/match_line/general_note), a resolution state (`resolved`/`unresolved`), and a confidence score. Built as its own pass across the whole sheet set (needs every sheet's entities/text extracted first — see §2 "Processing at scale"), consumed by both engines: drafting's cross-sheet consistency rule (§4) and geometry's cross-view dimensional matching (§5a). See §4 for the full extraction/resolution mechanics.

Extraction from PDF vs DXF converges on this same schema so both check engines are format-agnostic.

## 4. Drafting check engine

Rule-based, with rules grouped by category and configurable per project/standard (a project can select "AASHTO-style", "internal firm standard", state DOT standard, etc., plus add custom rules).

| Category | Examples |
|---|---|
| Standards/conventions | Layer naming matches convention, linetypes/weights correct, text style/font consistent, scale stated matches actual, north arrow present, title block fields complete |
| Annotation/dimension completeness | Every drawn feature requiring a dimension has one, section cut markers have a corresponding section sheet, detail bubbles have a corresponding detail sheet, all callouts resolve to something that exists |
| Cross-sheet consistency | Build a reference graph (sheet A section marker → sheet B section view); verify every reference resolves and labels match; detail/section titles match their callouts |
| Spelling | Run extracted text through a spellchecker against an **en-GB (British English)** base dictionary — not en-US — so correct British spellings (`specialised`, `colour`, `kerb`, `programme`, ...) never flag, plus a custom glossary (engineering abbreviations, material names, project-specific terms) to suppress domain false positives |
| Revisions | Revision clouds/triangles on a sheet must have a matching row in that sheet's revision table; revision numbering must be sequential and consistent across the sheet set — see mechanics below |
| Project-specific | Rules engine accepts a per-project rule set (JSON/YAML: regex text checks, required-element presence/absence, custom tolerance overrides) defined by the project admin — no code change needed to add a project-specific check. Client-spec-derived rules (§6) populate this same rule set automatically. |

**Rule engine design:** each rule is a small function `(IR, config) -> [Issue]`. Rules are registered in a catalog; a project's active rule set is just a list of rule IDs + parameters. This makes "ask for project-specific things to be checked" a config-time operation, not a dev-time one.

**Project-specific rule primitives.** The "Project-specific" row above already covers three: regex text checks, required-element presence/absence, and tolerance overrides on existing checks — but those are all *internal*-consistency primitives (does a field exist, match a pattern, agree with another drawn value). A field-value-correctness check — e.g. a client-mandated lat/long parameter in the title block, which needs to be verified as actually correct, not just present — needs a fourth primitive plus one extraction fix:

- **Extraction gap first.** §3's title-block field list (drawing number, title, scale, revision, date, discipline) is fixed; a project-specific field like lat/long isn't in it, so nothing can check it until extraction knows to pull it out. The title-block field list needs to be project-extensible — a config entry naming the field (by label text or a fixed title-block position/region to read from) so an arbitrary firm/client-specific field becomes part of that sheet's IR metadata before any rule runs against it.
- **Fourth primitive: value-correctness against a reference.** Compares an extracted field to an **expected value**, which can be either a literal configured once at project setup (simplest — an admin enters the project's correct site lat/long once, every sheet's title-block value is checked against it) or a value derived from other project data (e.g. the project's site origin/coordinate system definition transformed to lat/long) — start with the literal-value form for MVP, same "structured/simple case first" sequencing used everywhere else in this doc, and treat the computed/derived form as a later enhancement.
- **Tolerance as real-world distance, not a naive value delta.** For lat/long specifically, `abs(stated − expected)` in degrees is misleading — a degree of longitude covers a very different real distance depending on latitude. Compute an actual geodetic distance (haversine or similar, e.g. via `pyproj`/`geopy`) between stated and expected coordinates and compare that against a distance tolerance (e.g. "within 10m"), not a raw coordinate delta. This is the general lesson, not lat/long-specific: any project-specific numeric correctness check should use a tolerance in the value's own real-world unit, never assume a flat delta on the raw stored representation is meaningful.
- **Sheet-to-sheet consistency is a separate, cheaper check worth having too.** Independent of "is it correct," every sheet in the set should show the *same* lat/long — a same-extracted-field-across-sheets equality check, reusing the same extraction but no reference value or geodetic math needed. Worth registering as its own lightweight rule rather than folding into the correctness check, since it can catch a class of error (one sheet's field silently diverging from the rest) even before anyone's confirmed what the "correct" value is.

**Custom dictionary / glossary management.** A term flagged as misspelled must be dismissible permanently, not just per-run, otherwise recurring engineering terms (`abutment`, `chainage`, product/material trade names, client/project codenames) create standing noise that trains engineers to ignore the spelling check entirely. Two tiers, both stored as data (not code) and both feeding the same en-GB-based spellchecker:
- **Firm-wide glossary** — terms approved once, apply to every project (engineering abbreviations, standard material/product names, common trade terms).
- **Project glossary** — terms scoped to one project (site names, client-specific codenames, one-off product names) — doesn't pollute the firm-wide list with names that won't recur.

Adding a term is one click from the issue list ("Add to dictionary" on a spelling Issue — choose firm-wide or project scope), so the flagged-word list itself doubles as the intake path rather than requiring a separate admin screen. Re-running a check after an addition must not re-flag that term. This is the same "config change, not code change" principle as the rest of §4's rule catalog, applied to the spelling dictionary specifically.

**Cross-sheet reference graph — mechanics.** Referenced by name in the Cross-sheet consistency row above and reused by the geometry engine's cross-view matching (§5a), but it's shared infrastructure worth specifying once rather than each caller re-deriving it. It's built as its own pass, not folded into either engine.

*Reference types* (a project-configurable list, since drafting conventions vary by firm/discipline):
- **Section marker → section view** (a cut-line symbol on a plan/elevation tagged e.g. `A`, pointing to a view titled `SECTION A-A` on some sheet)
- **Detail bubble → detail view** (a circled tag e.g. `4/S-201`, pointing to a view titled `DETAIL 4` on sheet `S-201`)
- **Match line → adjoining sheet** (a line across a large plan tagged `MATCH LINE — SEE SHEET S-103`, connecting two sheets at a specific edge rather than pointing at a titled view)
- **General note reference** (free text, e.g. "see Detail 4 on Dwg S-201", inside notes/specs text rather than a graphic symbol)

*Extraction, for the PDF-only build this is actually being built against (§1 — DXF is conditional/not committed):* no block concept survives a PDF export; markers have to be detected geometrically — a circle/hexagon/pentagon vector shape containing text split by a divider line, near (not overlapping) a section cut line or view boundary — then proximity-matched to nearby text. This is the near-term implementation; build and tune it directly against real sheets in `samples/`.

*If DXF support is ever added* (§1), section/detail markers there are usually block inserts (a firm's standard symbol family) with attribute text for the tag/sheet number — a much more direct lookup than PDF's geometric detection, requiring only a per-project/firm block-naming convention (config, same pattern as the layer-naming convention in §4's Standards/conventions row). Worth keeping in mind as the reason a future DXF path would raise cross-sheet-reference confidence, but not worth building ahead of proven need.
- **General note references** are free text in both formats — extract via pattern matching (`SEE (DETAIL|SECTION) <tag>(?: ON)? (?:DWG|SHEET) <sheet-no>` and known variants), inherently less reliable than a graphic symbol and scoped as a later addition (see scoping note below).

*Resolution algorithm:*
1. **Build a target index first, across the whole sheet set** — every view has a title block/label of its own (`DETAIL 4`, `SECTION A-A`, plus its scale), so scan every sheet for these and index by `(tag, sheet_number)`, since this pass only makes sense after the cross-sheet extraction fan-in (§2 "Processing at scale") — it's a `cross_sheet`-scope operation by definition, same tier as the cross-sheet rules that consume it.
2. **For each source marker**, parse its tag/sheet-number text, normalize (case-fold, strip whitespace/punctuation — `"4 / S-201"`, `"4/S-201"`, `"DETAIL 4 — S201"` should all normalize the same), and look up an exact match in the target index.
3. **Exact match** → resolved edge, high confidence.
4. **No exact match** → fall back to fuzzy string matching (edit distance) against the index *only* to produce a lower-confidence candidate for review, never to silently auto-resolve — same "confidence, not silent" principle used for geometry reconstruction (§5b) and spec extraction (§6). If nothing plausible is found at all (referenced sheet number doesn't exist anywhere in the set, or exists but has no matching tag), it's unresolved.
5. **Unresolved → Issue**, located at the source marker, using the markup label already defined in §8: `No match: <ref>` (e.g. `No match: Detail 4/S-402`).

*Reuse by the geometry engine (§5a):* cross-view dimensional comparison only runs between two views connected by a **resolved** edge in this graph (skip low-confidence or unresolved pairs rather than guess-matching by proximity/value alone, per §5a's existing "skip rather than guess" rule) — this graph is the actual mechanism behind that reuse, not just a conceptual link.

*Scoping.* Build symbol-based resolution (section markers, detail bubbles, match lines — deterministic, geometric, highest value) first; free-text general-note references are open-ended text parsing with a materially higher false-match rate, so defer them the same way §6 defers narrative spec extraction until the structured/numeric case is proven — sequence general note references after the graphic-symbol resolution is working against real sheets in `samples/`.

**Revision consistency — mechanics.** Three distinct pieces of information on a typical sheet have to agree, not just the single cloud↔table link the table above implies:

- **Revision schedule** (bottom-left convention) — the full history table: one row per revision (rev ID, date, description, by/initials). Already covered by the IR's `Table` construct (§3); column mapping is project-configurable since firms vary in column order/naming, same pattern as the layer-/block-naming conventions used elsewhere in §4.
- **Current-revision parameter** (bottom-right convention) — a single title-block field showing the sheet's current revision, already part of Sheet metadata in §3 ("title block fields... revision..."). Neither of these needs an IR change — this rule builds directly on existing IR constructs, unlike the cross-sheet reference graph above which needed a new one.
- **Revision clouds/triangles** — drawn on the sheet body: a closed scalloped-curve entity (the cloud) paired with a small triangle/delta symbol carrying the revision tag. Detected the same way as this section's cross-sheet reference markers: geometric heuristic in PDF (a cloud's characteristic many-small-arcs closed curve is a distinctive enough shape to detect directly; the triangle+text pairing mirrors the detail-bubble detection already designed above), block/attribute lookup if DXF support is ever added (§1).

**Three-way cross-check:**
1. **Schedule ↔ current-rev parameter.** The schedule's latest (highest/most recent) row must match the current-revision title-block field. A mismatch — schedule updated but the title-block parameter forgotten, or vice versa — is one of the more common real drafting errors, and is exactly the kind of thing this check exists to catch. Report both values: `Rev: title block shows C, schedule latest is D`.
2. **Cloud/triangle → schedule row.** Every cloud's revision tag must resolve to a matching schedule row — same resolution algorithm as the cross-sheet reference graph above (normalize, exact match, fuzzy fallback only as a lower-confidence candidate, never silent). Unresolved → the `Rev: cloud has no table row` Issue already defined in §8's markup table.
3. **Sequential numbering.** Schedule rows themselves must be sequential and non-duplicated — no skipped or repeated rev IDs.
4. **Schedule row with no matching cloud — informational, not a hard fail.** Not every revision necessarily has a localized cloud (a general reissue or sheet renumbering can bump the revision without marking a specific area), so flag this at lower severity, and make whether it's enforced at all a project-configurable option — firms differ on how strictly they expect every row to have a corresponding cloud.
5. **Cross-sheet consistency, precisely defined.** Not "every sheet must be at the same revision" — sheets don't all change on every issue, so that would be a false-positive machine. The actual check: sheets whose schedules carry a row with the same *date* should carry the same *revision ID* for that date, so a shared transmittal/issue is internally consistent across the set, without demanding sheets that weren't touched bump their revision anyway.

**Project rule configuration — consolidated schema.** Every check above was individually specified as "project-configurable," but nothing so far shows what a project admin actually edits. Pulling it into one schema is worth doing explicitly, because it exposes a layering question that's been implicit throughout: not everything configurable lives at the same scope, and conflating them would make the schema unmanageable.

*Four layers, broadest to narrowest — only the middle two are part of this schema:*
1. **Deployment config** (out of scope here) — upload size limits (§2), which OCR/LLM backend is running (§10). Ops-level, not rule-related.
2. **Firm-level config** — shared across every project at the firm:
   - The firm-wide glossary (§4).
   - Named standard bundles (`AASHTO-style`, `internal firm standard`, a state DOT standard) — each bundle is itself a reusable set of rule IDs + default params (layer-naming pattern, default `rounding_grid`, etc.) that a project selects as its starting point rather than authoring from scratch.
3. **Project-level config** — this document's actual subject. Layers on top of a selected firm standard: overrides, additions, and everything genuinely specific to one project (site reference values, this project's title-block field extensions, its glossary additions). This is the file a project admin edits.
4. **Rule catalog (code, not config)** — deliberately *not* in this schema: a rule's `scope` tag (`single_sheet`/`cross_sheet`, §2), its note template and `suggested_fix` generator (§8), and its diffing identity-key extractor (§7) are properties of the rule's implementation, not something a project admin sets. Keeping this boundary explicit matters — "config, not code" (CLAUDE.md's own rule) is about *parameters*, not about the matching/extraction logic itself; that stays in code even though it's driven by config.

*A representative project config file, showing several of the pieces designed above coexisting:*
```yaml
project_id: "T2DPAA"
config_version: 7          # bumped on every save; CheckRuns record which version they ran
                            # against, so §7's diffing can tell "resolved: drawing_change"
                            # from "resolved: rule_config_change"
extends_standard: "internal_firm_standard_v3"   # firm-level bundle this project starts from

glossary:
  project_additions: ["chainage", "T2DPAA", "Woronora"]   # layered on top of the firm-wide glossary

default_check_scope: "drafting_and_geometry"   # §2 — per-run override still allowed

title_block:
  custom_fields:
    - name: "site_lat_long"
      label_match: "LAT/LONG"       # how to find it on the sheet
  reference_values:
    - field: "site_lat_long"
      expected: "-33.8688, 151.2093"
      tolerance: {distance: 10, unit: "m"}   # §4 — geodetic distance, not a raw coordinate delta

revision_schedule:
  column_mapping: {rev_id: "REV", date: "DATE", description: "DESCRIPTION", by: "BY"}
  require_cloud_for_every_row: false   # §4 — lenient by default, per-firm convention

tolerances:
  rounding_grid:
    default: "5mm"
    setout_critical: "1mm"           # confirm real figure against samples/, per §5
  measurement_epsilon: "0.5mm"
  setout_critical_overrides:          # manual promotions beyond the automatic graph-edge rule (§5)
    - layer: "C-BEARING"
  survey_tolerance:
    base_tolerance: "10mm"
    per_hop_allowance: "3mm"          # §5 — allowance = base + per_hop × √hops

rules:
  - id: "spelling.en_gb"
    enabled: true
  - id: "regex_text_check"            # one of §4's four project-specific primitives
    enabled: true
    params: {pattern: "Grade 60", required_in: "rebar_notes"}
    source: "manual"
  - id: "value_correctness_check"     # another primitive — the lat/long example itself
    enabled: true
    params: {field: "site_lat_long"}
    source: "manual"
  - id: "spec.cover_min_50mm"         # auto-registered by §6's spec extraction, not hand-authored
    enabled: true
    source: "spec_extraction"
    spec_clause_ref: "§03 30 00 3.2"
    confidence: 0.91
```

The `source: manual | spec_extraction` field on each rule entry is what lets §6's review screen filter to just the auto-extracted rules for correction, without needing a separate list — spec-derived rules are ordinary entries in the same `rules` array, just tagged with their provenance.

This is the deepest part of the system. Two sub-goals:

**a) Dimensional/geometric consistency**
- Verify drawn geometry matches its own stated dimensions (a rectangle's drawn edge length matches its dimension string)
- Verify a feature's geometry is consistent across plan, elevation, and section views of the same sheet set
- Verify shapes close properly (no gaps in a wall outline, footing polygon, deck edge)
- Configurable tolerance per check (drafting tolerance vs. survey tolerance); client-spec-derived numeric limits (§6) supply the expected value for some of these checks instead of internal cross-referencing

**Mechanics of (a):**
- **Dimension-to-geometry association:** every DIMENSION entity has witness lines whose endpoints should snap (within a small tolerance, via `shapely` nearest-point queries) to the endpoints of the drawn entity it measures — that snap is how the engine knows *which* edge a dimension string belongs to, not just that a number exists on the sheet.
- **Drawn-vs-stated comparison:** take the drawn distance between those snapped points, convert plot-space units to real-world units using the sheet's stated scale/units from the IR's title block metadata, and compare against the dimension string parsed to a normalized value (handles `3200`, `3.2m`, `10'-6"`, bearings in DMS, etc.). Mismatch beyond drafting tolerance → Issue.
- **Cross-view matching:** reuse the cross-sheet reference graph already built for callout resolution (§4) to match the same physical feature across plan/elevation/section by label first; only fall back to proximity/value-based matching when there's no explicit callout, and skip the comparison (rather than guess) when match confidence is low — a wrong match is worse than no check.
- **Closure checks:** for anything that should be a closed polygon (footing outline, wall cross-section), sum edge vectors around the loop; a non-zero closing residual beyond tolerance is the Issue, reported with the residual vector so the engineer sees which edge is likely wrong, not just "doesn't close."

**b) Structure reconstruction from setout data + cross-check**
- Extract setout/coordinate data: point tables (northing/easting/elevation, or station/offset/level), alignment/chainage geometry, dimension chains
- Reconstruct the structure's geometry (bridge deck/piers/abutments, retaining wall stem/footing) as a 2D (and where elevation data allows, 3D) model, by walking dimension chains and combining with any explicit coordinate points
- Independently compute coordinates for key points from the *drawn* geometry (e.g., wall corner derived by walking a dimension chain from a known origin) and compare against the *stated* coordinates in the setout table
- Flag discrepancies beyond tolerance, with the specific point/dimension and both values (drawn vs. tabulated) so an engineer can see exactly what disagrees
- Where full reconstruction isn't achievable (missing reference point, ambiguous dimension chain), report partial reconstruction with a confidence/coverage indicator rather than failing silently

**Mechanics of (b) — this is a survey-traverse problem, not a CAD-geometry problem, and the key design call is: don't behave like a survey least-squares adjustment that smooths disagreement away. Surfacing disagreement between independent paths *is* the point of this check, so no averaging/best-fit reconciliation.**
1. **Build a graph.** Setout table rows become nodes (named points with stated N/E/Z or station/offset/level). Dimension entities become edges, each needing a length + direction — sourced from an explicit bearing/distance callout (`N45°30'E 12.450`) where present, otherwise inferred from the drawn vector's own orientation once entity geometry is available.
2. **Pick reference point(s).** Typically a stated coordinate origin, a benchmark, or the first setout row carrying an absolute coordinate.
3. **Traverse the graph** (shortest-path / all-paths, e.g. via `networkx`) from each reference to every other setout point, accumulating dx/dy (and dz where levels exist) along the dimension chain — this produces an independently *drawn-derived* coordinate per point, separate from whatever the table states.
4. **Cross-check against the table.** `delta = derived_coordinate − tabulated_coordinate` per point; flag beyond the configured survey tolerance, always reporting both values plus the delta, never just pass/fail.
5. **Redundant paths are a QC signal, not something to reconcile.** If a point is reachable by two different dimension chains and they disagree, that disagreement is itself flagged as an issue (ambiguous/conflicting dimensioning) — the engine never quietly averages multiple chains into one "best" value the way a real survey adjustment would.
6. **Partial reconstruction is an expected outcome, not a failure state.** Report each point's status (`reconstructed` / `unreachable` / `conflicting`) plus an overall coverage percentage for the sheet, rather than failing the whole run when one point can't be resolved.
7. **Provenance on every derived value.** Store the exact chain used to reach each reconstructed point (e.g. `origin BM100 → dim '12.450' brg 090° → dim '3.200' brg 000°`) so an engineer can verify the path rather than trust an opaque number — this is the specific form CLAUDE.md's "report *how* you reached a value" requirement takes here.

**Tolerance configuration — drafting vs. survey are different *kinds* of tolerance, not just different numbers.** Source drawings for this project are Revit-authored (DWG/PDF export). Revit models routinely need dimension text manually overridden to round the true modeled length to a buildable value — especially on angled/skewed elements, where the raw geometric length rarely lands on a clean number. That's expected, correct drafting behavior, not noise, and it shapes both tolerances differently.

The rounding grid itself **isn't a single project-level constant** — it's tiered by element type, tighter for setout-critical dimensions (feed directly into structural positioning/the setout table) than for general-arrangement ones. So config is a lookup by category with a fallback default, not one number:
```
rounding_grid:
  default: 5mm
  setout_critical: 1mm   # placeholder — confirm the real figure against samples/
```
Classification into `setout_critical` should be two-source, not purely a manual tag: (1) a project config mapping by layer/dimension-style/element-category, for the general case, and (2) automatic promotion to `setout_critical` for any dimension that's actually an edge in the §5b reconstruction graph (i.e., it directly derives a setout table point) — regardless of whether it was tagged, because getting that specific number wrong has downstream reconstruction consequences even if a drafter wouldn't otherwise think of it as "setout-critical." Reusing the graph classification here means the two engines don't need separately-maintained lists of which dimensions matter most.

- **Drafting tolerance (§5a, drawn vs. stated dimension):** a flat numeric delta handles routine rounding badly — loose enough to swallow legitimate rounding and it also swallows real typos/stale overrides; tight enough to catch real errors and it false-positives on every rounded dimension. Model it as a rounding-grid tolerance instead, using the dimension's resolved tier (`setout_critical` or `default`):
  - `rounding_grid(dimension)` + a small `measurement_epsilon` (~0.5–1mm, covers export/plot noise). Pass if `abs(stated − raw_drawn) ≤ rounding_grid(dimension)/2 + epsilon`.
  - Secondary signal (confidence/severity, not a hard gate): check whether `stated` itself is close to a multiple of its tier's grid. A value that's off *and* isn't a clean grid multiple is more likely a genuine typo or stale override (left over from a prior model revision) than one that lands cleanly on the grid — surface that distinction on the Issue so engineers can triage.
  - Orientation-agnostic by construction — "raw" length is the Euclidean distance between the witness-line-snapped endpoints (§5a mechanics), so angled/skewed elements aren't a special case as long as the dimension-to-entity association itself is correct.
- **Survey tolerance (§5b, reconstructed vs. tabulated setout value):** driven by construction/survey achievability standards, not drafting rounding convention — but the rounding-override behavior still leaks in, because reconstruction walks a *chain* of individually-rounded dimensions, and every edge in that chain is by definition `setout_critical` (point 2 above), so it's the tight tier's grid that compounds here, not the general default. Each hop can carry up to `rounding_grid(setout_critical)/2` of legitimate rounding error, so a point reached via a long chain can accumulate real, non-erroneous deviation well beyond what a single-hop point would show. A flat survey tolerance over-flags long chains or under-flags short ones. Scale the allowance with path length instead — same shape as a survey traverse misclosure formula, e.g. `allowance = base_tolerance + per_hop_allowance × √hops` as a starting form, calibrated against real chains in `samples/` rather than picked blind. Where multiple paths reach the same point, prefer the shortest as primary (least compounding) — a genuine conflict between paths is still always its own Issue per point 5 above, never silently resolved by picking the "better" path.

Both stay project-configurable per CLAUDE.md's "tolerances must be configurable, never hardcoded" rule — the grid values here are this project's drafting convention, not universal constants, and should be confirmed (both the `default`/`setout_critical` split and their actual figures) against real Revit-exported sheets in `samples/` before locking in.

**Output:** an overlay showing the reconstructed structure on top of the original drawing, with mismatches highlighted at the specific location, plus a data table of every setout point: drawn value, tabulated value, delta, pass/fail.

This is genuinely hard in the general case (ambiguous dimension chains, missing datums, drafting shorthand). Recommend scoping the MVP to a well-defined structure type (e.g., a single retaining wall run, or a simple bridge abutment) with clear setout table conventions, then generalizing once that reconstruction logic is proven.

## 6. Client specification check

Client-supplied specification documents (project specs, not just drawings) encode requirements that drawings must satisfy — minimum concrete cover, factor-of-safety values, bar spacing, mandated terminology, material grades — and firms currently check these by hand. This feature extracts those requirements and auto-generates rules that feed the two engines above (§4, §5), rather than being a separate third engine with its own IR.

**Input:** client spec as PDF or Word (DOCX). Real specs are a mix of narrative clauses (CSI-style sections, prose requirements) and embedded tables/schedules of numeric limits — the extractor has to handle either, not assume one format.

**Extraction pipeline**
```
Spec upload (PDF/DOCX)
  -> Text + table extraction (PyMuPDF/pdfplumber for PDF, python-docx for Word)
  -> Clause segmentation (section/clause numbering, headings)
  -> Requirement extraction (self-hosted LLM, see §9) -> structured SpecRequirement records
  -> Auto-registered as project rules, flagged as spec-derived
```

Each `SpecRequirement`: clause reference (e.g. `§03 30 00 3.2`), verbatim source text, extracted parameter/operator/value/unit where numeric (e.g. `cover >= 50mm`), applicability (element type/discipline the clause governs), and an extraction confidence score. These live alongside `Project`, not `Sheet` — a spec applies to the whole project, not one drawing.

**Two requirement kinds, feeding the two existing engines:**
- Narrative/presence requirements (e.g. "drawings shall note rebar as Grade 60", terminology the spec mandates) become drafting rule-catalog entries — same `(IR, config) -> [Issue]` shape as any hand-written rule in §4, just auto-generated.
- Numeric/threshold requirements (cover, spacing, factor of safety, dimensional limits) become geometry-engine parameter/tolerance overrides (§5) — checked against drawn or tabulated values the same way setout deltas are, except the "expected" value comes from the spec instead of a setout table.

**Auto-apply with flagging.** Extracted requirements go live in the project's active rule set immediately — no manual approval gate before they can flag issues on a check run — but every Issue raised from a spec-derived rule is visibly tagged (`Spec:` label prefix, e.g. `Spec: cover 42mm < 50mm required`) and carries its clause reference and extraction confidence, so an engineer can immediately see which flags came from automated extraction and prioritize checking the low-confidence or unusual ones. A spec review screen lists every extracted requirement (source clause, generated rule, confidence) so a misextracted rule can be corrected or disabled after the fact — this is correction, not a pre-check gate.

**Markup/report integration:** spec-derived issues use a `Spec: <what's wrong>` note in §7's markup table and carry the clause reference as their report backlink, the same role a rule ID plays for other issues.

**Scoping note.** Free-form narrative extraction is open-ended NLP; recommend scoping first to the numeric/threshold case (structured schedules, clearly stated single-value limits) since it's highest-value and least ambiguous, then extending to narrative/presence requirements once extraction quality is proven against a real client spec sample (see `samples/` conventions in CLAUDE.md — the same "check real documents before assuming a format" principle applies here).

## 7. Reporting

- In-app: issue list (filterable by category/severity), click an issue to jump to its location on the drawing viewer, overlay markup on the drawing itself
- Export: PDF and/or Excel report — issue reference tag, full description, severity, sheet, location, rule reference (or spec clause reference, §6), drawn vs. expected value where applicable. This is the detailed companion to the minimal on-sheet markup (§8) — download together as one package
- Each check run is versioned so re-running after a drawing revision shows what's newly resolved/introduced — see mechanics below

**Revision diffing — mechanics.** An Issue's location/entity handle isn't stable across runs — every extraction pass reassigns entity handles fresh, and even genuinely-unchanged content can shift slightly (OCR jitter, a minor re-plot). Naively comparing Issue objects run-to-run would call most things both "new" and "resolved" simultaneously, which is worse than no diffing at all — it would train engineers to distrust the feature on day one.

**Matching needs a per-rule identity key, not one generic formula.** What makes two issues "the same issue, still open" differs by category, and several rule categories already have a genuinely stable natural identifier available rather than needing an approximation:
- **Setout mismatch** — the setout table's own point ID is a stable, human-assigned name that persists across revisions unchanged; use it directly. Highest-confidence key available anywhere in the system.
- **Cross-sheet reference** — the callout's own tag + sheet + target reference string (already extracted by §4's reference graph).
- **Spelling** — the misspelled word + its sheet (+ surrounding text context if the word appears more than once on the sheet, to disambiguate).
- **Revision/cloud** — revision tag + sheet, though these are somewhat different in kind: a resolved revision issue often means the sheet was genuinely revised again (the cloud/tag itself changed), not that the same flagged content was fixed in place.
- **Everything else** (most drafting/geometry issues tied to a bounding box with no natural name) — generic fallback: same sheet + same rule ID + location within a small spatial tolerance.

This is the same generalization §8 already applies to note templates and `suggested_fix`: every rule in the catalog defines its own identity-key extractor alongside its check logic, so match quality is rule-specific instead of one global heuristic straining to cover every category.

**Matching algorithm, run N → run N+1, per sheet:**
1. Compute each Issue's identity key (rule-defined, or the generic fallback).
2. Exact key match between the two runs' issue sets → same issue, carries forward as **open**.
3. No exact match → fuzzy/spatial-proximity fallback within tolerance, surfaced as a **lower-confidence candidate match** for engineer confirmation rather than silently asserted — the same "confidence, not silent" principle used throughout this doc (§4's reference graph, §5b's reconstruction).
4. Present in run N, no match at all in run N+1 → **resolved**.
5. Present in run N+1, no match in run N → **new**.

**Two edge cases worth handling explicitly, not letting the algorithm silently misreport:**
- **Sheet added/removed/renumbered.** If a sheet's drawing number itself changes, the sheet-identity anchor breaks — don't auto-mark everything on it "resolved" (it wasn't fixed, the sheet just moved); flag as `needs_confirmation` instead of asserting resolved or new.
- **Rule config changed between runs, not the drawing.** If a project admin loosens a rule (raises a `rounding_grid`, disables a check) between run N and N+1, issues disappear from the report — but not because anything was fixed on the drawing. Diffing needs to distinguish `resolved: drawing_change` from `resolved: rule_config_change`, so each check-run record needs to carry which rule-set version it ran against, not just a run number. Without this, a project admin could silently make findings vanish by loosening config — which would undercut the audit trail CLAUDE.md requires of this tool everywhere else.

**Identity is a lineage, not a per-run field.** An Issue's cross-run identity ("open since run 2, still open in run 5") is a separate, stable concept from its per-run reference tag (`#014` in §8 — scoped to one run's report only). Track an `issue_lineage_id` that persists across matched runs, with each run contributing one snapshot (its own description/location/values at that point) to the lineage — this is what makes §8's "detect which flagged items were addressed vs. still open" claim concrete rather than aspirational.

## 8. Markup & redline export

Yes — this is a natural extension of the existing Issue data, and worth building once the check engines are producing reliable results. Goal: engineer reviews and selects flagged issues, app burns them onto the sheets as redlines, drafting team gets a marked-up set with no manual markup step.

**Flow**
1. Check run completes → issue list (as in §7), each already tied to a sheet and a location.
2. Engineer selects which issues to include (default: all; filterable by severity/category/sheet — e.g. exclude a low-severity spelling flag they've decided to ignore).
3. "Generate markup set" renders each selected issue directly onto its sheet, at the issue's recorded location.
4. Output is a downloadable marked-up set — combined PDF and/or per-sheet DXF/DWG — ready to send straight to drafting.

**Keep markups minimal.** A sheet with twenty issues is unreadable if every one gets a full-sentence description. Each markup is two elements only:
- A **box** drawn tight around the flagged content's bounding box (not a cloud — clouds are for freehand engineer markup, not something worth simulating; a precise rectangle is faster to read and cheap to generate from the Issue's stored bounding box). Use a bounding-box-less marker (small circle at the point) only when the issue is an *absence* — nothing exists yet to box, e.g. a missing dimension.
- A **leader to a short note**, one line, `Label: payload` — a fixed short label per rule category plus the minimum information needed to act, no restated description:

| Category | Note format | Example |
|---|---|---|
| Spelling | `Spelling: <correct word>` | `Spelling: concrete` |
| Missing dimension/annotation | `Missing: <what>` | `Missing: dimension` |
| Unresolved callout/cross-sheet ref | `No match: <ref>` | `No match: Detail 4/S-402` |
| Revision inconsistency | `Rev: <what's wrong>` | `Rev: cloud has no table row` |
| Standards/convention | `<field>: <expected>` | `Layer: should be C-WALL` |
| Dimensional consistency | `Drawn <a> ≠ dim <b>` | `Drawn 3.42 ≠ dim 3.40` |
| Geometry/setout mismatch | `Setout Δ<value>` | `Setout Δ0.015` |
| Client spec mismatch (§6) | `Spec: <what's wrong>` | `Spec: cover 42mm < 50mm` |

Every rule in the catalog defines its own note template alongside its check logic (not a generic description field), so the label is always predictable and the payload is always the smallest thing a drafter needs to make the fix.

Markups can stay this terse because they're not the only output: each issue gets a short reference tag (e.g. `#014`) printed after its label, and the full report (§7, exported alongside the marked-up set) has the matching entry with the complete description, rule (or spec clause) reference, sheet, and both drawn/tabulated values. The sheet answers "what to fix"; the report is there for "why" if the drafter or engineer needs it — most issues won't require anyone to open it.

**Format-specific rendering**
- PDF: draw markup as native PDF annotations (`PyMuPDF` — FreeText, Circle/Cloud, Line/Leader), not a flattened raster overlay. Native annotations stay vector-crisp at any zoom and let the drafting team toggle, reply to, or resolve them in Acrobat or Bluebeam, matching how AEC firms already work with redlines.
- DXF/DWG: add markup as entities on a dedicated layer (e.g. `Z-QC-REDLINE`, red/ACI color 1) using `ezdxf` — `MULTILEADER` + `MTEXT` for the note, a polyline/spline for the cloud. This lets drafters open and edit the markup natively in AutoCAD/Civil 3D. Convert back through ODA if the original was DWG.

**Suggested fix**
Not every rule can generate one automatically, but many can, and it's worth having the rule catalog populate this from the start rather than bolting it on later. The full-sentence description (shown in the in-app issue list per §7) and the terse markup note (above) are two different renderings of the same underlying fix data — a rule that can compute "concrete" as the correction, or "3.42 vs 3.40" as the delta, feeds both.

This means the `Issue` object (produced by every rule, `(IR, config) -> [Issue]`) needs: a precise location (point, bounding box, or entity handle, not just "sheet N"), a full description for the issue list, and — where derivable — a structured `suggested_fix` (e.g. `{corrected: "concrete"}` or `{drawn: 3.42, expected: 3.40}`) that the markup renderer turns into the short note. Rules without an obvious auto-fix carry a description but no `suggested_fix`, and get boxed with just their label (no payload). Build this into the Issue schema from the first rule written, even before the markup renderer exists, so it isn't a retrofit.

**Traceability**
Tag each generated markup with its source Issue's `issue_lineage_id` (§7's revision-diffing mechanics) and the check-run version it came from, not just a per-run reference tag. A later re-check on the drafting team's revised sheets can then automatically detect which flagged items were addressed vs. still open by following that lineage across runs, turning this into a closed-loop QC cycle rather than a one-shot export.

## 9. Suggested MVP scope

1. PDF ingestion only — DXF/DWG support (§1) is conditional, not deferred-but-planned: build it only if PDF vector extraction against real samples proves insufficient (missing layer data, block/symbol structure needed for §4's reference graph, etc.), not by default
2. Drafting checks: title block completeness, spelling, revision table consistency (fastest to build, clearest value)
3. Geometry checks: dimensional consistency within a single sheet (start here) before attempting full multi-sheet structure reconstruction
4. One structure type end-to-end (e.g., retaining wall) before generalizing to bridges
5. Basic project-specific rule config (JSON/YAML rules file, schema in §4's "Project rule configuration" subsection) before building a full rules UI
6. Client spec upload + numeric-threshold extraction (§6), scoped to structured schedules first — this is an automated way to populate the same rule set from step 5, so sequence it once that config mechanism works
7. Narrative/presence requirement extraction from free-form spec prose, once numeric extraction (step 6) is proven on a real client spec
8. Markup export (§8) once the check engines are reliable enough that auto-generated redlines are trustworthy to send to drafting unreviewed-in-detail — this is a trust-dependent feature, sequence it after the checks it depends on are proven

## 10. Self-contained / offline-capable deployment

This should run with **zero outbound internet access at runtime**, deployable on an internal network or fully air-gapped machine. Worth locking in as a hard constraint given client-confidentiality/data-residency concerns (see §11) — engineering drawing sets are exactly the kind of data firms don't want touching an external service.

Every component in the recommended stack (§1) is self-hostable; a few need a specific choice to keep it that way rather than defaulting to a hosted service:

| Component | Self-contained choice | Avoid |
|---|---|---|
| OCR | `pytesseract` (local Tesseract binary), or PaddleOCR with model files baked into the Docker image at build time | Cloud OCR APIs, or letting PaddleOCR fetch models on first run |
| Spelling | Self-hosted LanguageTool container configured for **en-GB**, or local-dictionary `pyspellchecker` with an en-GB word list | Cloud grammar/spelling APIs, or an en-US default dictionary |
| Client spec extraction (§6) | Self-hosted LLM (local Llama/Mistral-class model via vLLM or Ollama, weights baked into the image or mounted from internal storage), with deterministic regex/table parsing as a non-LLM fallback for structured numeric schedules | Cloud LLM APIs |
| Object storage | MinIO (self-hosted, S3-compatible) | Actual AWS S3 |
| Frontend assets | PDF.js and all JS dependencies bundled at build time, served from the app's own server | `<script src>` references to a CDN at runtime |
| Auth | Local accounts, or on-prem LDAP/AD/SSO | External-only identity providers |
| Telemetry/error reporting | Self-hosted (e.g. self-hosted Sentry) or omitted entirely | SDKs that phone home by default |
| DWG conversion | ODA File Converter invoked as a local subprocess | Any hosted conversion API |

With these, the whole system is a Docker Compose (or Kubernetes) stack — api, worker, db, redis, MinIO, LanguageTool, local LLM runtime — with no required egress while running. Building the images the first time still needs internet (base images, `pip`/`npm` packages, LLM weights); for a genuinely air-gapped target, build on a connected machine and transfer the images in, or use an internal package mirror.

Security posture this buys: no attack surface from outbound calls, drawing and spec data never leaves the deployment environment, and it satisfies most "data must stay on our network" procurement requirements out of the box rather than needing a bespoke compliance story later.

## 11. Open questions to resolve before/while building

- Is there an existing internal tech stack this must fit (cloud provider, auth system, existing DB)?
- What's the expected drawing volume/sheet count per project, and typical file size — affects whether sync processing is viable for small jobs vs. always-async
- ~~Are DWG files true CAD data or will most uploads be PDF exports?~~ Resolved for now: uploads are Revit-authored PDF exports, and DXF/DWG parsing is deliberately not being built until PDF-only is proven insufficient (§1). Re-open this if a project shows up needing native DWG input.
- What existing setout table formats/conventions should the parser target first (survey firm templates vary widely)?
- What do this firm's client specs actually look like — get a real sample. §6 assumes a narrative/table mix, but the extraction priority (numeric schedules first) depends on confirming that's really how specs arrive
- Is a self-hosted LLM already available/approved for this environment (§10), or does spec extraction need to ship with a non-LLM (regex/pattern-based) fallback for the numeric case as the real MVP path?
- Any regulatory/compliance handling needed for drawing data beyond what §10's offline-capable deployment already covers?
