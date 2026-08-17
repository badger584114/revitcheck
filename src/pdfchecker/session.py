"""One check run, end to end — PLANNING.md §2's pipeline as a single
callable, rather than a sequence every caller has to reassemble by hand.

Before this existed, a geometry-inclusive run meant hand-sequencing eight
steps (`ingest_pdf` -> `convert_dwg_to_dxf` -> `ingest_dxf` xN ->
`attach_dxf_sheets` -> `ingest_ifc` -> `attach_ifc_model` -> importing
`checks.geometry` for its registration side-effect -> `run_checks`), and
that sequence existed in exactly one place: `tests/test_geometry.py`.
CLAUDE.md acknowledged the gap ("no CLI wrapper for Stage 3 yet"); the
2026-08-17 backend review named it as the thing to build before an API,
since this is the natural seam for one to wrap.

Four things this owns that no single previous caller did:

1. **Scope actually means something.** `check_scope` (§2) decides whether
   geometry inputs are ingested at all and whether `geometry.*` rules run
   — see `_apply_scope`. Previously the selection existed in
   `session_config.py` but nothing downstream acted on the *ingestion*
   half of it.
2. **The scratch directory's whole lifecycle**, per §2's "stateless by
   design" hard constraint: DWG->DXF conversion has to write converted
   files somewhere, and something has to delete them afterwards.
   `SessionResult` is a context manager for exactly that — see
   `SessionWorkspace` for the deliberate narrowness of what `purge()`
   will delete.
3. **Coverage reporting instead of silence.** A geometry run where no DXF
   joined to any sheet, or where the IFC model was never supplied,
   produces the same empty geometry-issue list as a genuinely clean set.
   Those go into `SessionResult.warnings` — the same "report a coverage
   indicator, don't fail silently" convention the check rules themselves
   already follow.
4. **Per-stage progress and timing.** Ingestion is the slow part (~125s
   for a real 37-page set, plus ~56s of spelling), so a UI needs
   something better than an indeterminate spinner, and a worker needs
   somewhere to report from. `progress` is called before each stage with
   `(stage_name, index, total)`.

**Deliberately NOT in scope here**, both flagged by the same review and
both real:

- *No per-rule/per-page error isolation.* `ingest_pdf` and `run_checks`
  still fail the whole run if one page or one rule raises. That's a
  change to those functions' own semantics (a failure needs to become a
  coverage Issue, not an exception), not something to paper over from
  out here.
- *`checks/geometry.py`'s reconstruction cache is still process-global*,
  keyed partly on `id(config)` with no weakref guard on the config side.
  Making it run-scoped belongs in that module.
"""

from __future__ import annotations

import shutil
import tempfile
import time
from dataclasses import dataclass, field, replace
from pathlib import Path
from typing import Callable, Optional, Sequence

from pdfchecker.checks.catalog import RuleConfig, run_checks
from pdfchecker.checks.issue import Issue
from pdfchecker.checks.session_config import VALID_CHECK_SCOPES
from pdfchecker.extraction.dxf_source import attach_dxf_sheets, convert_dwg_to_dxf, ingest_dxf
from pdfchecker.extraction.ifc_source import attach_ifc_model, ingest_ifc
from pdfchecker.extraction.pipeline import ingest_pdf
from pdfchecker.ir import Project

ProgressFn = Callable[[str, int, int], None]

_GEOMETRY_PREFIX = "geometry."


class SessionWorkspace:
    """A session's scratch directory — created by this class, owned by
    this class, deleted by this class.

    `purge()` deliberately only ever removes a directory this object
    itself created via `tempfile.mkdtemp`. It never touches a caller-
    supplied input path, and `parent_dir` is only where the temp
    directory gets made, never something that gets deleted. That
    narrowness is the point: §2's stateless-by-design rule is a client-
    confidentiality decision, so the delete path has to be obviously
    incapable of reaching a user's own files.
    """

    def __init__(self, parent_dir: Optional[str] = None) -> None:
        if parent_dir is not None:
            Path(parent_dir).mkdir(parents=True, exist_ok=True)
        self.dir: Optional[str] = tempfile.mkdtemp(prefix="pdfchecker-session-", dir=parent_dir)

    def subdir(self, name: str) -> str:
        if self.dir is None:
            raise RuntimeError("workspace already purged")
        path = Path(self.dir) / name
        path.mkdir(parents=True, exist_ok=True)
        return str(path)

    def purge(self) -> None:
        """Deletes the scratch directory and everything in it. Idempotent.

        Note for callers: converted DXF files live here, and
        `Sheet.dxf_sheet.source_path` points at them — so anything that
        re-reads a sheet's source DXF must run *before* this, not after.
        """

        if self.dir is not None:
            shutil.rmtree(self.dir, ignore_errors=True)
            self.dir = None


@dataclass
class SessionStage:
    """One pipeline stage's real cost and outcome — what a progress UI
    renders and what a worker logs. `detail` is a short human-readable
    result ("37 sheets", "26 of 31 DXF files joined to a sheet"), not a
    status code; the machine-readable version of the same facts is in
    `SessionResult.warnings` where it matters."""

    name: str
    seconds: float
    detail: str

    def to_dict(self) -> dict:
        return {"name": self.name, "seconds": round(self.seconds, 3), "detail": self.detail}


@dataclass
class SessionResult:
    """Everything one check run produced. A context manager, so the
    scratch directory (§2's stateless-by-design constraint) has an
    obvious end:

        with run_session(pdf, dwg_paths=dwgs, check_scope="drafting_and_geometry") as result:
            render_markup(result.project, result.issues, "out.pdf")
        # scratch purged here

    Using it without `with` is fine too — call `purge()` yourself once
    the markup/report artifacts are written."""

    project: Project
    issues: list[Issue]
    check_scope: str
    config: RuleConfig
    rules_run: list[str]
    stages: list[SessionStage] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    workspace: Optional[SessionWorkspace] = None

    @property
    def seconds(self) -> float:
        return sum(s.seconds for s in self.stages)

    def purge(self) -> None:
        if self.workspace is not None:
            self.workspace.purge()

    def __enter__(self) -> "SessionResult":
        return self

    def __exit__(self, *exc) -> None:
        self.purge()

    def to_dict(self) -> dict:
        """Run metadata only — deliberately *not* the extracted IR. A
        real `Project` carries every word and vector path on every sheet
        (hundreds of thousands of objects), which is neither what an API
        response wants nor something §2 wants leaving the server. Issues
        and stage timings are the run's actual product."""

        return {
            "check_scope": self.check_scope,
            "source_path": self.project.source_path,
            "sheet_count": len(self.project.sheets),
            "rules_run": sorted(self.rules_run),
            "issues": [i.to_dict() for i in self.issues],
            "issue_count": len(self.issues),
            "stages": [s.to_dict() for s in self.stages],
            "warnings": list(self.warnings),
            "seconds": round(self.seconds, 3),
        }


def _apply_scope(config: RuleConfig, check_scope: str) -> RuleConfig:
    """Returns a copy of `config` with `geometry.*` stripped for a
    drafting-only run. A copy, not a mutation — a caller's `RuleConfig`
    is theirs, and the same object may well be reused across runs with
    different scopes.

    Idempotent against `session_config.py`, which already strips geometry
    ids for `drafting_only`; re-applying here means a hand-built
    `RuleConfig` (the tests, `scripts/`) gets the same treatment as a
    file-loaded one, rather than scope only working down one of the two
    paths."""

    if check_scope != "drafting_only":
        return config
    kept = {r for r in config.resolved_rule_ids() if not r.startswith(_GEOMETRY_PREFIX)}
    return replace(config, enabled_rule_ids=kept)


def _collect_dxf_paths(
    dwg_paths: Sequence[str],
    dxf_paths: Sequence[str],
    workspace: SessionWorkspace,
    oda_path: Optional[str],
) -> tuple[list[str], str]:
    """Already-converted DXF plus freshly-converted DWG, as one list.

    ODA File Converter works on whole directories rather than single
    files (`extraction/dxf_source.py`), so the requested DWGs are staged
    into a scratch input directory first — copied, not symlinked, since
    ODA's handling of symlinked input isn't something this codebase has
    confirmed against real data. Returns the paths plus a short detail
    string for the stage record."""

    collected = [str(p) for p in dxf_paths]
    if not dwg_paths:
        return collected, f"{len(collected)} DXF supplied"

    staged = workspace.subdir("dwg_in")
    for src in dwg_paths:
        shutil.copy2(src, Path(staged) / Path(src).name)
    out_dir = workspace.subdir("dxf_out")

    kwargs = {"oda_path": oda_path} if oda_path else {}
    convert_dwg_to_dxf(staged, out_dir, **kwargs)

    converted = sorted(str(p) for p in Path(out_dir).glob("*.dxf"))
    collected.extend(converted)
    return collected, f"{len(converted)} of {len(dwg_paths)} DWG converted, {len(dxf_paths)} DXF supplied"


def run_session(
    pdf_path: str,
    *,
    dwg_paths: Sequence[str] = (),
    dxf_paths: Sequence[str] = (),
    ifc_path: Optional[str] = None,
    config: Optional[RuleConfig] = None,
    check_scope: str = "drafting_and_geometry",
    scratch_parent_dir: Optional[str] = None,
    oda_path: Optional[str] = None,
    progress: Optional[ProgressFn] = None,
) -> SessionResult:
    """Ingests one drawing set and runs the enabled checks against it.

    `pdf_path` is always required — PLANNING.md §2: "IR extraction always
    runs (geometry depends on drafting-extracted entities as
    reconstruction input) ... There is no geometry-only mode."

    Geometry inputs (`dwg_paths`/`dxf_paths`/`ifc_path`) are only ingested
    when `check_scope` includes geometry; supplying them on a
    drafting-only run is reported as a warning rather than quietly
    honoured or quietly dropped, since it means the caller and the scope
    selection disagree about what this run is.

    Markup export is deliberately a separate step
    (`markup/pdf_markup.py`) — §8's flow puts engineer issue-selection
    between the check run and the markup, so this returns issues rather
    than rendering them.
    """

    if check_scope not in VALID_CHECK_SCOPES:
        raise ValueError(f"check_scope: {check_scope!r} must be one of {VALID_CHECK_SCOPES}")

    wants_geometry = check_scope != "drafting_only"
    has_geometry_input = bool(dwg_paths or dxf_paths or ifc_path)

    warnings: list[str] = []
    stages: list[SessionStage] = []
    workspace: Optional[SessionWorkspace] = None

    if not wants_geometry and has_geometry_input:
        warnings.append(
            "check_scope is 'drafting_only' but DWG/DXF/IFC inputs were supplied — they were not "
            "ingested and no geometry rule ran. Use 'drafting_and_geometry' if that wasn't intended."
        )
    if wants_geometry and not has_geometry_input:
        warnings.append(
            "check_scope is 'drafting_and_geometry' but no DWG/DXF/IFC input was supplied — geometry "
            "rules ran against nothing and produced no issues. That is not a clean geometry result."
        )

    # The plan is built up front so `progress` can report (i, total)
    # rather than an open-ended stream — a UI needs the denominator.
    plan = ["ingest_pdf"]
    if wants_geometry and (dwg_paths or dxf_paths):
        plan.append("ingest_dxf")
    if wants_geometry and ifc_path:
        plan.append("ingest_ifc")
    plan.append("run_checks")
    total = len(plan)

    def _stage(name: str):
        if progress is not None:
            progress(name, plan.index(name) + 1, total)
        return time.time()

    started = _stage("ingest_pdf")
    project = ingest_pdf(pdf_path)
    skipped = [s for s in project.sheets if not s.tables_scanned]
    stages.append(
        SessionStage(
            "ingest_pdf",
            time.time() - started,
            f"{len(project.sheets)} sheets"
            + (f"; ruled-table scan skipped on {len(skipped)}" if skipped else ""),
        )
    )
    if skipped:
        # Coverage, not silence: those sheets have `tables == []` because
        # nothing looked, not because they have no tables. Cheap to say so
        # here, and it's the difference between "no schedule on that sheet"
        # and "we never checked" if a geometry result later looks thin.
        warnings.append(
            f"Ruled-table extraction was skipped on {len(skipped)} of {len(project.sheets)} sheets "
            "whose text carries no Easting/Northing (extraction/tables.py's SETOUT_TABLE_KEYWORDS) — "
            "those sheets' `tables` are empty because they were not scanned, not because they hold no "
            "tables. Pass a wider `table_scan_keywords` to ingest_pdf() to widen or disable this."
        )

    if wants_geometry and (dwg_paths or dxf_paths):
        started = _stage("ingest_dxf")
        workspace = SessionWorkspace(scratch_parent_dir)
        collected, source_detail = _collect_dxf_paths(dwg_paths, dxf_paths, workspace, oda_path)
        dxf_sheets = [ingest_dxf(p) for p in collected]
        matched = attach_dxf_sheets(project, dxf_sheets)
        stages.append(
            SessionStage(
                "ingest_dxf",
                time.time() - started,
                f"{source_detail}; {matched} of {len(dxf_sheets)} joined to a sheet",
            )
        )
        # The numeric-suffix join (extraction/dxf_source.py) is the one
        # step here that can fail quietly and completely: every geometry
        # rule skips a sheet with no `dxf_sheet`, so a total join failure
        # looks exactly like a clean set.
        if dxf_sheets and matched == 0:
            warnings.append(
                f"None of the {len(dxf_sheets)} DXF file(s) matched a sheet in the PDF. Geometry rules "
                "that need DXF geometry produced nothing. Check the filename convention the "
                "numeric-suffix join expects (extraction/dxf_source.py's attach_dxf_sheets)."
            )
        elif matched < len(dxf_sheets):
            warnings.append(
                f"{len(dxf_sheets) - matched} of {len(dxf_sheets)} DXF file(s) did not match any sheet "
                "in the PDF and were ignored."
            )

    if wants_geometry and ifc_path:
        started = _stage("ingest_ifc")
        ifc_model = ingest_ifc(ifc_path)
        attach_ifc_model(project, ifc_model)
        stages.append(
            SessionStage(
                "ingest_ifc",
                time.time() - started,
                f"{len(ifc_model.elements)} elements ({ifc_model.schema})",
            )
        )
        if not ifc_model.elements:
            warnings.append(
                f"The IFC model at {ifc_path} yielded no elements with usable geometry — IFC-based "
                "rules produced nothing. That is not a confirmation that the model matches."
            )

    effective_config = _apply_scope(config if config is not None else RuleConfig(), check_scope)
    rules_run = sorted(effective_config.resolved_rule_ids())

    started = _stage("run_checks")
    issues = run_checks(project, effective_config)
    stages.append(SessionStage("run_checks", time.time() - started, f"{len(issues)} issues from {len(rules_run)} rules"))

    return SessionResult(
        project=project,
        issues=issues,
        check_scope=check_scope,
        config=effective_config,
        rules_run=rules_run,
        stages=stages,
        warnings=warnings,
        workspace=workspace,
    )
