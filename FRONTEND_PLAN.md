# Frontend (+ minimal API) plan

Companion to `PLANNING.md`. This document plans the frontend/API layer referenced in `PLANNING.md` §1/§2/§7/§8 and flagged in `CLAUDE.md`'s 2026-08-15 roadmap review as "the actual goal — a real multi-user web app... the large, mostly-unstarted piece of the original plan." It does not change anything about the existing `pdfchecker` Python library, check catalog, or markup export — it plans a new `frontend/` and `api/` layer that wraps them as-is.

## Context

`pdfchecker` is currently a Python library/CLI only (`src/pdfchecker/`, driven by `scripts/check.py` / `scripts/markup.py`). Stage 1-3 (PDF ingestion, drafting checks, geometry checks) and PDF markup export are built and calibrated against real samples. Confirmed by exploration: **no `frontend/`, `api/`, or `web/` directory exists yet; no FastAPI app; no `package.json`.** This is fully greenfield.

The plan below designs a new `frontend/` (React+TS/Next.js) and a new `api/` (FastAPI) that wraps the existing `pdfchecker` library — it reuses every existing Python contract as-is (`Issue`, `RuleConfig`, `LoadedSessionConfig`, `run_checks`, `render_markup`, `assign_tags`), rather than redesigning any of them.

Two constraints from `PLANNING.md` are non-retrofittable and must be designed in from the start, not bolted on later:

- **Stateless by design** (decided 2026-08-10): no persistent project/session history; only firm-level config (glossary, rule bundles) survives a login. No server-stored per-user drawing/session data ever touches Postgres.
- **Zero outbound internet access at runtime**: all frontend assets (incl. PDF.js) vendored/bundled at build time, no CDN references, no cloud APIs.

Client spec upload (`PLANNING.md` §6) is out of scope per the user's 2026-08-15 "probably not needed" call. DXF/DWG redline markup UI is out of scope per the same-day "PDF is the only real deliverable" call — PDF markup is the only markup export the frontend exposes; `markup/dxf_markup.py` stays in the codebase but is never surfaced.

## Architecture

```
frontend/ (Next.js, React+TS, PDF.js self-hosted, no CDN)
   |  same-origin HTTP/JSON + multipart upload
   v
api/ (FastAPI) -- orchestration/pipeline.py --> src/pdfchecker
   |                (ingest_pdf, dxf_source.*, ifc_source.*,
   |                 catalog.run_checks, pdf_markup.render_markup)
   |
   +-- Storage interface   (local-disk now -> MinIO/S3 later, same interface)
   +-- JobStore interface  (in-process/BackgroundTasks now -> Celery+Redis later, same interface)
   +-- Postgres            (auth + firm-level glossary/rule-bundle config ONLY)
```

`api/` is an orchestration/serialization layer, not new logic: it imports `Issue`, `RuleConfig`, `LoadedSessionConfig`, `run_checks`, `render_markup`, `assign_tags` and calls them; it does not reimplement any check or extraction logic.

Early phases (0-3) run the check pipeline **synchronously in-process** (FastAPI `BackgroundTasks` + an in-memory job dict) so the frontend, viewer, and issue-selection UX can be built and demoed against `samples/BR06` without standing up Redis/Celery/MinIO. The job-status/issue-list API shape is identical in sync and async modes, so moving to Celery (Phase 4) is additive infra, not an API rewrite. `Storage`/`JobStore` are defined as interfaces from Phase 0 specifically so that swap is a new adapter, not a redesign.

## Directory layout

```
frontend/
  public/pdfjs/                      # vendored PDF.js worker/cmaps/fonts — no CDN
  src/
    app/
      login/page.tsx
      new-session/page.tsx           # workflow-scope + upload
      session/[sessionId]/processing/page.tsx
      session/[sessionId]/review/page.tsx      # issue list + viewer (centerpiece)
      session/[sessionId]/markup/page.tsx      # issue-selection-for-markup
      session/[sessionId]/download/page.tsx
    components/
      upload/{PdfDropzone,CadDropzone,IfcDropzone,SessionConfigDropzone,ScopeSelector,ChunkedUploader}.tsx
      progress/{SheetProgressBar,useJobStatus}.ts(x)
      issues/{IssueList,IssueFilters,IssueRow,AddToDictionaryAction}.tsx
      viewer/{DrawingViewer,IssueOverlay,PageNavigator}.tsx
      markup/{IssueSelectionTable,MarkupPreview}.tsx
      download/DownloadPanel.tsx
      config/SessionConfigWarnings.tsx
    lib/{api-client.ts,types.ts,state/}

api/
  pyproject.toml                     # depends on pdfchecker (editable install of repo root)
  app/
    main.py, deps.py
    routers/{auth,sessions,uploads,jobs,issues,markup,report,config}.py
    orchestration/
      pipeline.py     # NEW — ingest_pdf + convert_dwg_to_dxf/ingest_dxf/attach_dxf_sheets +
                       #       ingest_ifc/attach_ifc_model. This exact sequence is currently
                       #       demonstrated only in tests/test_geometry.py, not wired into any
                       #       CLI script — building it is real, necessary orchestration work.
      checks.py        # wraps load_session_config + run_checks
      markup.py         # wraps render_markup, issue_key <-> tag mapping (markup/tags.py)
      report_export.py # NEW — PDF/Excel export of the report entries (§7; today only CLI stdout)
      dxf_render.py     # NEW — DXF sheet -> raster/PDF for the viewer (Phase 2+)
    storage/{base,local,s3}.py
    jobs/{base,inprocess,celery_app,tasks}.py
    models/schemas.py   # Pydantic mirrors of Issue.to_dict()/LoadedSessionConfig — additive envelope only
    purge.py
  tests/                # integration tests against samples/BR06, samples/BR08
```

## Screens

| Screen | Responsibility |
|---|---|
| Login | Minimal auth gate (real accounts land in Phase 5). No profile, no history. |
| Workflow-scope + upload | `ScopeSelector` (drafting_only / drafting_and_geometry) controls dropzone visibility per PLANNING §2's literal UI spec: PDF dropzone always shown/required; CAD dropzone (multi-file .dwg/.dxf) appears only once geometry scope is picked; IFC dropzone optional/single-file/project-level regardless of scope; session-config dropzone optional YAML/JSON. |
| Processing/progress | Polls (Phase 0-3) or SSE-subscribes (Phase 4) to job status; renders `sheets_completed/sheets_total`, not a spinner; surfaces per-sheet failures without aborting the run; surfaces `LoadedSessionConfig.warnings` immediately. |
| Issue review (centerpiece) | Issue list streams in as it's computed, filterable by category/severity/sheet. `DrawingViewer` (PDF.js canvas) + `IssueOverlay` (SVG) draws each Issue's `bbox` — box for a region, small circle for a zero-area point-marker bbox (same convention `pdf_markup.py` already uses), nothing for `bbox=None` (still listed as report-only). Click an issue → viewer jumps to it; click a marker → list scrolls to it. |
| Issue selection for markup | Checkbox table, default all-selected, bulk toggle by severity/category/sheet. This is the step `PLANNING.md` repeatedly flags as "not yet built" — genuinely new UI work, not a wrapper over something existing. |
| Download | Marked-up PDF (`render_markup` output) + full report export (new PDF/Excel, §7). Confirming download fires the purge. No DXF/DWG redline download offered — out of scope per the 2026-08-15 call. |

The session-config screen is upload + warnings display only, not a rule-builder form — `PLANNING.md` treats the rules file as an upload, not a persisted/edited UI.

## API contract

Every response carrying issue data wraps `Issue.to_dict()` verbatim plus one added `issue_key` field (`Issue` has no stable identity field today, and one's needed for markup selection + incremental dedup — synthesized server-side, not a new dataclass field).

```
POST /api/auth/login                            {username, password} -> {token}
POST /api/sessions                               -> {session_id, expires_at}
PATCH /api/sessions/{sid}                         {check_scope} -> 409 if geometry requested with no CAD upload

POST /api/sessions/{sid}/uploads/pdf              multipart -> {file_id, filename, size}
POST /api/sessions/{sid}/uploads/cad              multipart, repeatable -> {file_id, ...}
POST /api/sessions/{sid}/uploads/ifc              multipart, single -> {file_id, ...}
POST /api/sessions/{sid}/uploads/config           multipart (yaml/json) -> {file_id, ...}
# Phase 4 adds, same upload model, presigned direct-to-storage:
POST /api/sessions/{sid}/uploads/presign          {filename, size, content_type, kind} -> {upload_id, parts:[...]}
POST /api/sessions/{sid}/uploads/{upload_id}/complete   {parts:[{part_number, etag}]} -> {file_id}

POST /api/sessions/{sid}/run                      {} -> {job_id}
GET  /api/jobs/{job_id}                           -> {
  status: "queued"|"running"|"done"|"failed",
  stage: "extraction"|"drafting_checks"|"geometry_checks"|"done",
  sheets_total, sheets_completed,
  sheets_failed: [{page_index, reason}],
  config_warnings: [str]
}
GET  /api/jobs/{job_id}/stream                    # Phase 4, SSE; polling above stays as fallback

GET  /api/sessions/{sid}/issues?since_sheet=N      -> { issues: [{issue_key, ...Issue.to_dict()}], complete: bool }

GET  /api/sessions/{sid}/pdf                       # same-origin byte stream for PDF.js
GET  /api/sessions/{sid}/dxf-sheets/{sheet_no}/raster?format=png|pdf   # Phase 2+

POST /api/sessions/{sid}/markup                    {issue_keys: [str]|"all"} -> {markup_job_id}
GET  /api/sessions/{sid}/markup/{markup_job_id}    -> {status, download_url, report: [...]}
GET  /api/sessions/{sid}/markup/{markup_job_id}/file

POST /api/sessions/{sid}/report                     {format: "pdf"|"xlsx", issue_keys} -> {report_job_id}
GET  /api/sessions/{sid}/report/{report_job_id}/file

POST /api/sessions/{sid}/complete                   # confirmed download -> triggers purge
DELETE /api/sessions/{sid}                           # explicit abandon

POST /api/firm-config/glossary                       {word, scope: "firm"|"project"}   # Phase 5 only
```

## State management

- **Server/job state** (session-lifetime only, never Postgres): session record, job status/progress, computed issues, markup/report artifacts — lives in `JobStore`/`Storage` (in-memory → Redis/MinIO).
- **Client in-memory** (React context/Zustand, no persistence middleware): `session_id`, scope selection, uploaded-file refs, active filters, selected-issues-for-markup set.
- **Client `sessionStorage`** (not `localStorage`, deliberately — clears on tab close): just `session_id`/`job_id`, so a refresh mid-flow resumes polling instead of restarting.
- Drawing bytes, issue descriptions, bboxes: always refetched from the API on mount, never cached beyond the tab's lifetime.

## Drawing viewer: PDF.js + server-side DXF conversion (not a client DXF renderer)

PDF pages render via `pdfjs-dist` onto canvas; `IssueOverlay` is an absolute-positioned SVG layer translating `Issue.bbox` (PDF page-space, per `ir.py`'s `BBox`) into current-zoom pixel coordinates.

DXF sheets are converted to raster/PDF **server-side** (`api/app/orchestration/dxf_render.py`, via `ezdxf`'s drawing add-on or the existing ODA plumbing), not rendered client-side, because:

- `extraction/dxf_pdf_transform.py` already puts every geometry Issue's `bbox` in PDF page-space (calibrated 2026-08-14 against real BR06/BR08 sheets) — so the overlay code path for a geometry Issue and a drafting Issue is identical; no separate DXF-space rendering is needed for the case `PLANNING.md` confirms is the overwhelming majority (a sheet with a PDF counterpart).
- It avoids reimplementing non-trivial, already-tested Python logic (model/paper-space viewport transforms, layer/color resolution) in TypeScript.
- It matches §8's own conclusion that DWG/DXF is informational-only and markups live on the PDF set — CAD parsing stays entirely server-side.
- Zero-egress stays trivially satisfied — no extra client CAD-rendering dependency.

Where the transform doesn't resolve (documented gap: BR08's cross-sheet case, `bbox=None`), the viewer shows a report-only row with no jump affordance, matching `pdf_markup.py`'s existing handling.

## Build order

**Phase 0 — Walking skeleton.** FastAPI scaffold with `Storage`/`JobStore` interfaces (local-disk/in-process impls); one synchronous chain: upload PDF → `ingest_pdf` → `run_checks` (default `RuleConfig`) → `render_markup` → download. Next.js scaffold, PDF.js vendored, single page (upload → flat issue list → download). No auth/DB/CAD/IFC/chunked upload.
*Verify*: run against `samples/BR06`'s PDF; diff issue counts/categories against `scripts/check.py`'s stdout for the same input; confirm zero outbound calls with egress blocked.

**Phase 1 — Core screens + viewer.** Real upload screen (scope selector, conditional CAD dropzone, optional IFC/config dropzones — accepted but not yet wired to checks), processing screen, `DrawingViewer`+`IssueOverlay` with click-to-jump, session-config upload wired to `load_session_config` with warnings surfaced.
*Verify*: run BR06 PDF + `config/session_example.yaml` through the UI; diff issue list/warnings against `scripts/check.py --config`; manually confirm overlay placement against known flagged words/regions.

**Phase 2 — Geometry scope + DXF/IFC orchestration.** Build `orchestration/pipeline.py` (the real current gap — only `tests/test_geometry.py` shows the DXF/IFC call sequence today, no CLI wires it). API-layer 409 gate for "geometry scope requires DXF present" (the library doesn't enforce this itself). Server-side DXF raster/PDF conversion for the viewer.
*Verify*: run BR06's DWG set + IFC through the full API; diff `geometry.*` issues/bboxes against `tests/test_geometry.py`'s expectations; confirm the 409 fires when geometry is requested with only a PDF uploaded.

**Phase 3 — Issue selection + markup + report export.** Build the issue-selection-for-markup screen; wire to `POST /markup` → `render_markup`; build PDF/Excel report export reusing the same tag assignment (`markup/tags.py`) so markup and report tags match.
*Verify*: confirm tag numbers match 1:1 between downloaded markup PDF and report export for the same run; confirm selecting a subset of issues produces a markup PDF with only those marks.

**Phase 4 — Async scale-out.** Redis+Celery (per-sheet group/chord fan-out per `PLANNING.md` §2), MinIO, presigned multipart upload + resumable frontend uploader, SSE/incremental issue streaming, purge policy (delete-on-download-confirm + inactivity TTL). Swap `Storage`'s local-disk impl for S3 — additive since the interface was fixed in Phase 0.
*Verify*: run BR08's 109-DWG set end to end; confirm granular "N/sheets" progress; confirm one bad sheet doesn't abort the run; confirm purge fires on both explicit confirm and simulated inactivity timeout.

**Phase 5 — Auth + firm-level config.** Postgres, minimal accounts/login, firm glossary + named standard-bundle CRUD (the only things allowed to persist), "Add to dictionary" wired from the issue list.
*Verify*: confirm no session/drawing/issue data ever lands in Postgres; confirm a glossary addition suppresses that word in a subsequent new session.

## Explicitly out of scope

- **Client spec upload UI** (§6) — "probably not needed," 2026-08-15. No dropzone, no review screen, no endpoint.
- **DXF/DWG redline markup UI** (§8) — deprioritized 2026-08-15. `markup/dxf_markup.py` stays in the Python codebase; the frontend never exposes it.
- **Cross-run diffing** (§7) — dropped 2026-08-10; not built in any phase.
- **Persistent "my projects"/session-history list** — would violate stateless-by-design. Every login goes straight to "start new session."

## Critical files to reuse (not redesign)

- `src/pdfchecker/checks/issue.py` — `Issue`/`to_dict()`, the wire-format contract
- `src/pdfchecker/checks/session_config.py` — `load_session_config`/`LoadedSessionConfig`
- `src/pdfchecker/checks/catalog.py` — `RuleConfig`, `run_checks`, `all_rule_ids`
- `src/pdfchecker/markup/pdf_markup.py`, `src/pdfchecker/markup/tags.py` — `render_markup`, `assign_tags`
- `src/pdfchecker/extraction/pipeline.py`, `dxf_source.py`, `ifc_source.py` — orchestration inputs for `api/app/orchestration/pipeline.py`
