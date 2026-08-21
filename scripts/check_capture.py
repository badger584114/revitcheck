#!/usr/bin/env python3
"""Run the Revit checks against a captured model, off a Revit machine.

This is the other half of the Capture button — the bit that makes the
two-machine workflow tolerable. Capture a real project at work, bring
the JSON here, and iterate on rules with a normal edit/run loop:

    python scripts/check_capture.py samples/revit/BR06.capture.json
    python scripts/check_capture.py capture.json --json out/issues.json
    python scripts/check_capture.py capture.json --bcf out/bcf
    python scripts/check_capture.py capture.json --all-views

Nothing in this path imports the Revit API — see
`extensions/RevitCheck.extension/lib/revitcheck/__init__.py`.
"""

from __future__ import annotations

import argparse
import os
import sys

_LIB = os.path.abspath(
    os.path.join(
        os.path.dirname(__file__), "..", "extensions", "RevitCheck.extension", "lib"
    )
)
if _LIB not in sys.path:
    sys.path.insert(0, _LIB)

import revitcheck.checks  # noqa: E402,F401 - registers the rules
from revitcheck import RuleConfig, capture, run_checks  # noqa: E402
from revitcheck.bcf import DEFAULT_MAX_ISSUES_PER_FILE  # noqa: E402
from revitcheck.checks.dimensions import drafted_views  # noqa: E402
from revitcheck.report import summarize, to_bcf, to_json, to_markdown  # noqa: E402


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", help="a .capture.json written by the Capture Model button")
    parser.add_argument("--json", dest="json_out", help="write the full issue list here")
    parser.add_argument(
        "--bcf",
        dest="bcf_out_dir",
        help="write BCF 2.1 .bcf file(s) here, one file per "
        "%(max)s issues (Forma's import cap)" % {"max": DEFAULT_MAX_ISSUES_PER_FILE},
    )
    parser.add_argument(
        "--all-views",
        action="store_true",
        help="include views not placed on a sheet (off by default: unplaced "
        "views are not issued to anyone, and flagging work in progress is noise)",
    )
    parser.add_argument("--rule", action="append", help="run only this rule id (repeatable)")
    args = parser.parse_args(argv)

    model = capture.load(args.capture)
    config = RuleConfig(
        enabled_rule_ids=set(args.rule) if args.rule else None,
        sheeted_views_only=not args.all_views,
    )

    issues = run_checks(model, config)

    print("Model: {0}".format(model.doc_title or "(untitled)"))
    print(
        "  {0} sheets, {1} views, {2} dimensions"
        "".format(len(model.sheets), len(model.views), len(model.dimensions))
    )
    if model.captured_at:
        print("  captured {0} (Revit {1})".format(model.captured_at, model.revit_version))
    print()
    print(to_markdown(issues, model_title=model.doc_title))

    fully_drafted = drafted_views(model, config)
    if fully_drafted:
        print()
        print("Views to verify against the model ({0}):".format(len(fully_drafted)))
        for view in fully_drafted:
            print(
                "  - {0} [{1}]{2}".format(
                    view.name,
                    view.view_type,
                    " sheet {0}".format(view.sheet_no) if view.sheet_no else "",
                )
            )

    if args.json_out:
        directory = os.path.dirname(os.path.abspath(args.json_out))
        if directory:
            os.makedirs(directory, exist_ok=True)
        with open(args.json_out, "w") as handle:
            handle.write(to_json(issues, model_title=model.doc_title))
        print()
        print("Wrote {0}".format(args.json_out))

    if args.bcf_out_dir:
        os.makedirs(args.bcf_out_dir, exist_ok=True)
        bcf_files = to_bcf(issues, model_title=model.doc_title)
        print()
        if not bcf_files:
            print("No issues to export — nothing written.")
        for filename, data in bcf_files:
            path = os.path.join(args.bcf_out_dir, filename)
            with open(path, "wb") as handle:
                handle.write(data)
            print("Wrote {0} ({1} bytes)".format(path, len(data)))

    counts = summarize(issues)
    # Non-zero only for findings that need action, so this can gate a
    # batch run without a low-severity coverage note failing it.
    return 1 if counts["high"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
