#!/usr/bin/env python3
"""Run one full check session — the first CLI that can drive Stage 3.

`scripts/check.py` and `scripts/markup.py` are drafting-only (PDF in,
drafting issues out). This one wraps `pdfchecker.session.run_session`, so
it covers the whole pipeline: PDF + DWG/DXF + IFC in, issues and
optionally a marked-up PDF out, with the session's scratch directory
purged on the way out (PLANNING.md §2's stateless-by-design constraint).

DWG/DXF here is strictly a geometry-check *input* (a Revit export) — the
only markup this produces is the PDF (PLANNING.md §8).

Usage:
  # drafting-only, same result as scripts/check.py
  python scripts/run_session.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf \\
      --scope drafting_only

  # + geometry, from already-converted DXF and the IFC model
  python scripts/run_session.py samples/BR06/T2DPAA-T2D-C3S-BR-DRG-101000.pdf \\
      --dxf "samples/BR06/dxf/*.dxf" \\
      --ifc samples/BR06/T2DPAA-T2D-C3S-BR-M3D-000001.ifc

  # + geometry from raw DWG (needs ODA File Converter installed)
  python scripts/run_session.py <pdf> --dwg "samples/BR06/dwg/*.dwg" --markup out/
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from glob import glob
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "src"))

from pdfchecker.checks import RuleConfig, load_session_config  # noqa: E402
from pdfchecker.session import run_session  # noqa: E402


def _expand(patterns: list[str] | None) -> list[str]:
    """Each argument may be a literal path or a glob. Globbed here rather
    than relying on the shell so the documented `--dxf "dir/*.dxf"`
    (quoted, to survive shells that would otherwise expand it into
    separate argv entries) behaves the same everywhere."""

    out: list[str] = []
    for pattern in patterns or []:
        matches = sorted(glob(pattern))
        out.extend(matches or ([pattern] if Path(pattern).exists() else []))
        if not matches and not Path(pattern).exists():
            print(f"[warning] no files matched {pattern!r}")
    return out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("pdf_path")
    parser.add_argument("--dwg", action="append", metavar="PATH_OR_GLOB", help="DWG sheet file(s); converted via ODA")
    parser.add_argument("--dxf", action="append", metavar="PATH_OR_GLOB", help="already-converted DXF sheet file(s)")
    parser.add_argument("--ifc", metavar="PATH", help="IFC 3D model export for this project")
    parser.add_argument("--config", metavar="PATH", help="session rules file (YAML/JSON) — see config/session_example.yaml")
    parser.add_argument(
        "--scope",
        choices=["drafting_only", "drafting_and_geometry"],
        help="overrides check_scope from --config; defaults to drafting_and_geometry",
    )
    parser.add_argument("--markup", metavar="DIR", help="also write a marked-up PDF here")
    parser.add_argument("--json", metavar="PATH", help="write the full run record as JSON")
    parser.add_argument(
        "--report",
        metavar="STEM",
        help="write PLANNING.md §7's report as <STEM>.json and <STEM>.csv "
        "(the detailed companion to --markup; download them together)",
    )
    args = parser.parse_args()

    if args.config:
        loaded = load_session_config(args.config)
        for warning in loaded.warnings:
            print(f"[config warning] {warning}")
        config, scope = loaded.rule_config, loaded.check_scope
    else:
        config = RuleConfig(
            firm_glossary_path="config/firm_glossary.json",
            project_glossary_path="config/project_glossary.json",
        )
        scope = "drafting_and_geometry"
    scope = args.scope or scope

    def show_progress(stage: str, index: int, total: int) -> None:
        print(f"[{index}/{total}] {stage} ...", flush=True)

    # `with` matters here: the scratch directory holding any ODA-converted
    # DXF is purged on exit, and anything reading `Sheet.dxf_sheet.
    # source_path` has to run before that.
    with run_session(
        args.pdf_path,
        dwg_paths=_expand(args.dwg),
        dxf_paths=_expand(args.dxf),
        ifc_path=args.ifc,
        config=config,
        check_scope=scope,
        progress=show_progress,
    ) as result:
        print(f"\n{args.pdf_path}: {len(result.project.sheets)} sheets, {len(result.issues)} issues "
              f"({result.check_scope}, {result.seconds:.1f}s)\n")

        for stage in result.stages:
            print(f"  {stage.name:<12} {stage.seconds:7.1f}s  {stage.detail}")

        if result.warnings:
            print("\n--- coverage warnings ---")
            for warning in result.warnings:
                print(f"  ! {warning}")

        print("\n--- issues by category ---")
        for category, count in Counter(i.category for i in result.issues).most_common():
            print(f"  {category}: {count}")

        by_severity = Counter(i.severity for i in result.issues)
        print("  (" + ", ".join(f"{sev}: {by_severity[sev]}" for sev in ("high", "medium", "low") if by_severity[sev]) + ")")

        for issue in [i for i in result.issues if i.severity == "high"][:20]:
            print(f"\n  [{issue.severity}] {issue.sheet_no}: {issue.description}")

        if args.json:
            Path(args.json).write_text(json.dumps(result.to_dict(), indent=2))
            print(f"\nRun record -> {args.json}")

        markup_entries = None
        if args.markup:
            from pdfchecker.markup.pdf_markup import render_markup

            out_dir = Path(args.markup)
            out_dir.mkdir(parents=True, exist_ok=True)
            pdf_out = out_dir / (Path(args.pdf_path).stem + "_markup.pdf")
            markup_entries = render_markup(result.project, result.issues, str(pdf_out))
            drawn = sum(1 for r in markup_entries if r.rendered)
            print(f"\nMarked-up PDF -> {pdf_out}  ({drawn} of {len(markup_entries)} drawn)")

        if args.report:
            from pdfchecker.markup.report import build_report

            # markup_entries is passed when --markup also ran, so the
            # report's `marked_up` column reflects what actually got drawn
            # rather than guessing from whether a bbox exists.
            written = build_report(result, markup_entries=markup_entries).write(args.report)
            for path in written:
                print(f"Report        -> {path}")


if __name__ == "__main__":
    main()
