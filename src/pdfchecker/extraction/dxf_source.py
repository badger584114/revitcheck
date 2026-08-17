r"""DXF ingestion — PLANNING.md §5's geometry-check input. Reads an
already-converted DXF via `ezdxf`; DWG→DXF conversion is a separate step
(`convert_dwg_to_dxf`, below) since it shells out to the ODA File
Converter rather than parsing anything itself.

Calibrated against real converted DXF — all 31 real `.dwg` files in
`samples/dwg/`, converted via `/Applications/ODAFileConverter.app`
(confirmed 2026-08-10 to run fully headlessly, no GUI interaction, despite
being a Qt GUI app) and inspected with `ezdxf`. Two of PLANNING.md §5's
assumptions were checked directly against this real data, one confirmed
and one refined:

1. **Confirmed: `DIMENSION` entities carry what §5 needs, directly.** 470
   `DIMENSION` entities across 26 of the 31 real sheets (the other 5 are
   construction-staging diagrams with nothing to dimension — consistent,
   not a gap). All live in modelspace only (never paper space, on this
   sample) — confirmed by checking every layout, not assumed. Each has
   `get_measurement()` (the raw geometric distance) and `dxf.defpoint`/
   `defpoint2`/`defpoint3` (dimension-line location + both witness-line
   origins) directly — no proximity inference needed, exactly as §5's
   "Mechanics of (a)" assumed. `$INSUNITS` (DXF header) resolves the
   real-world unit — meters (code 6) on this sample.
2. **Confirmed, not just assumed: manual dimension-text overrides are
   genuinely common.** 252 of 470 dimensions (54%) carry an explicit
   override — this validates PLANNING.md §5's rounding-grid tolerance
   design as addressing a real, frequent case, not a hypothetical one.
   One gotcha worth knowing before parsing override text as a number: at
   least one real override was `'150‎'` — a trailing Unicode
   LEFT-TO-RIGHT MARK (U+200E) that AutoCAD/Revit inserts and that looks
   invisible in a terminal/editor. `stated_text` is stored exactly as
   `ezdxf` reports it (after only `.strip()`, which does NOT remove
   U+200E since it's not whitespace) — whoever writes the actual
   drawn-vs-stated numeric comparison needs to strip control/format
   characters before parsing, not just whitespace.
3. **Not yet built: title-block extraction, though the real geometry
   for it was inspected and confirmed feasible.** DXF block inserts on
   this sample carry zero `ATTRIB` attributes (corrects PLANNING.md §4's
   original assumption — see that section) — title-block field values
   are plain `MTEXT` on a `D-ENHA-TITLEBLOCK`-layer, in the paper-space
   layout. What makes this tractable without label text to anchor to
   (unlike PDF's `titleblock.py`): the title-block's block `INSERT` is
   placed at the same paper-space origin with the same scale/rotation on
   every sheet (confirmed: comparing sheet 101051 against 101112, 26 of
   27 title-block `MTEXT` entities sit at *exactly* the same paper-space
   coordinate, to 4 decimal places — the one sheet's own `sheet_no`
   value naturally differing by position, since that's the one field
   that must vary). So a fixed-position (not label-anchored) extraction
   would work, calibrated once against a reference sheet. Not built
   here because the first geometry check (single-sheet dimensional
   consistency, PLANNING.md §9 step 3) doesn't need it — only
   `dimensions` and `units` do; sheet identity for that check comes from
   the DWG/DXF filename's numeric suffix (PLANNING.md §8's already-
   confirmed join against the PDF's `sheet_no`), not DXF-internal fields.
4. **Not every `DIMENSION` measures a length, and not every override is
   a numeric rounding — confirmed on sheet 101151 (49 dimensions, all
   overridden).** 46 are `dim_type=0` (linear) but the override text is
   a bare letter ("A", "B", "C"...) keying into a separate bar-mark/
   schedule table elsewhere on the sheet — a legitimate drafting
   convention, not a value to compare numerically. `checks/geometry.py`
   (the actual drawn-vs-stated check) has to parse the override and skip
   silently when it isn't a clean number, same "skip rather than guess"
   principle as everywhere else in this codebase — see that module's
   docstring for the actual comparison logic and tolerance handling.

`extract_inserts`/`extract_texts` (`DxfSheet.inserts`/`.texts`) were added
2026-08-11 for §5b (extraction/setout_reconstruction.py) — modelspace
`INSERT`/`TEXT`/`MTEXT` entities, read the same way `extract_dimensions`
reads `DIMENSION`s. `MTEXT` content is read via `.plain_text()`, not raw
`.text` — confirmed real MTEXT carries `\P` paragraph-break codes
`plain_text()` resolves into real newlines that raw `.text` leaves as a
literal backslash-P, which breaks any multi-line regex parse downstream.
See extraction/setout_reconstruction.py's docstring for what these feed:
a real setout-point/bearing/dimension-chain convention confirmed on this
same sheet (101051).
"""

from __future__ import annotations

import re
import subprocess
from pathlib import Path

import ezdxf

from pdfchecker.ir import (
    DimensionEntity,
    DxfInsert,
    DxfSheet,
    DxfText,
    Point3D,
    Project,
    ViewportEntity,
)

# AutoCAD $INSUNITS codes actually seen in practice on civil drawings —
# not the full DXF spec table, just what's worth resolving to a readable
# string. Falls back to the raw numeric code (as a string) for anything
# else rather than guessing.
_INSUNITS = {
    0: "unitless",
    1: "in",
    2: "ft",
    4: "mm",
    5: "cm",
    6: "m",
    7: "km",
}

_DEFAULT_ODA_PATH = "/Applications/ODAFileConverter.app/Contents/MacOS/ODAFileConverter"


def convert_dwg_to_dxf(
    in_dir: str,
    out_dir: str,
    oda_path: str = _DEFAULT_ODA_PATH,
    out_version: str = "ACAD2018",
) -> None:
    """Batch-converts every DWG in `in_dir` to DXF in `out_dir`, via ODA
    File Converter as a local subprocess (PLANNING.md §10: "ODA File
    Converter invoked as a local subprocess" — never a hosted conversion
    API). Confirmed 2026-08-10 to run fully headlessly on macOS with no
    GUI window or interaction needed, despite being a Qt GUI application
    — invoked with all arguments given, it converts and exits cleanly.

    ODA operates on directories, not single files (hence `in_dir`, not a
    file path) — stage exactly the file(s) you want converted into their
    own directory if you don't want a whole folder processed.

    Raises `FileNotFoundError` with a clear message if the converter
    isn't installed, rather than letting `subprocess.run` fail with an
    opaque OS-level error — this is genuinely a separate manual install
    step (see PLANNING.md §10 / CLAUDE.md), not a `pip`/`requirements.txt`
    dependency, so a clear pointer matters here more than most missing
    dependencies in this codebase.

    **This direction only.** A `convert_dxf_to_dwg` counterpart existed
    briefly (2026-08-15) for §8's DXF/DWG redline export, and was removed
    2026-08-17 along with that whole export path: the user confirmed all
    drafting is done in Revit and the drafting team never opens AutoCAD,
    so there is nobody to hand a `.dwg` redline back to. DWG here is a
    Revit *export*, purely an input to the geometry checks — it is never
    an editable deliverable this tool produces. See PLANNING.md §8.
    """

    if not Path(oda_path).exists():
        raise FileNotFoundError(
            f"ODA File Converter not found at {oda_path!r}. Install it from "
            "https://www.opendesign.com/guestfiles/oda_file_converter (see "
            "PLANNING.md §10 / CLAUDE.md) — it's a separate manual install, "
            "not a pip dependency."
        )
    Path(out_dir).mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [oda_path, in_dir, out_dir, out_version, "DXF", "0", "1"],
        check=True,
        capture_output=True,
    )


def _point(vec) -> Point3D:
    return Point3D(x=vec.x, y=vec.y, z=vec.z)


def extract_units(doc) -> str:
    code = doc.header.get("$INSUNITS", 0)
    return _INSUNITS.get(code, str(code))


def extract_dimensions(doc) -> list[DimensionEntity]:
    """`DIMENSION` entities live in modelspace only on the real sample
    (confirmed by checking every layout, not assumed) — but this walks
    every layout regardless, in case a future sample puts an annotative
    dimension directly in paper space; cheap to not assume the same
    convention holds everywhere."""

    dimensions = []
    for layout in doc.layouts:
        for e in layout:
            if e.dxftype() != "DIMENSION":
                continue
            text = e.dxf.text.strip() if e.dxf.text else ""
            # "<>" is AutoCAD's literal placeholder for "show the computed
            # measurement" — not a real override, same as no text at all.
            stated_text = None if text in ("", "<>") else text
            dimensions.append(
                DimensionEntity(
                    measurement=e.get_measurement(),
                    stated_text=stated_text,
                    dim_line_point=_point(e.dxf.defpoint),
                    ext_line1_origin=_point(e.dxf.defpoint2),
                    ext_line2_origin=_point(e.dxf.defpoint3),
                    dimstyle=e.dxf.dimstyle,
                    layer=e.dxf.layer,
                    dim_type=e.dimtype,
                )
            )
    return dimensions


def extract_viewports(doc) -> list[ViewportEntity]:
    """Paper-space `VIEWPORT` entities only (§5's PDF-markup-transform
    consumer, PLANNING.md §8, needs these per paper-space layout, not
    modelspace, which has none)."""

    viewports = []
    for layout in doc.layouts:
        if not layout.is_any_paperspace:
            continue
        for e in layout:
            if e.dxftype() != "VIEWPORT":
                continue
            viewports.append(
                ViewportEntity(
                    id=e.dxf.id,
                    ps_center=_point(e.dxf.center),
                    ps_width=e.dxf.width,
                    ps_height=e.dxf.height,
                    view_center_point=_point(e.dxf.view_center_point),
                    view_height=e.dxf.view_height,
                    view_twist_angle=e.dxf.get("view_twist_angle", 0.0),
                )
            )
    return viewports


def extract_inserts(doc) -> list[DxfInsert]:
    """`INSERT` (block reference) entities, modelspace only — paper-space
    inserts are the title-block/viewport furniture, not the model
    geometry §5b's setout reconstruction needs (piles, setout-point
    symbols). See `DxfInsert`'s docstring (ir.py) for why matching is by
    block-name substring: this firm's export carries no `ATTRIB`
    attributes to key off directly."""

    return [
        DxfInsert(name=e.dxf.name, insert=_point(e.dxf.insert), layer=e.dxf.layer)
        for e in doc.modelspace()
        if e.dxftype() == "INSERT"
    ]


def extract_texts(doc) -> list[DxfText]:
    """`TEXT`/`MTEXT` entities, modelspace only (same paper-space
    exclusion reasoning as `extract_inserts`). `MTEXT.plain_text()` is
    used, not raw `.text` — see this module's docstring for why (the
    `\\P` paragraph-break formatting code)."""

    texts = []
    for e in doc.modelspace():
        if e.dxftype() == "MTEXT":
            texts.append(DxfText(text=e.plain_text(), insert=_point(e.dxf.insert), layer=e.dxf.layer))
        elif e.dxftype() == "TEXT":
            texts.append(DxfText(text=e.dxf.text, insert=_point(e.dxf.insert), layer=e.dxf.layer))
    return texts


def ingest_dxf(path: str) -> DxfSheet:
    doc = ezdxf.readfile(path)
    return DxfSheet(
        source_path=path,
        dimensions=extract_dimensions(doc),
        viewports=extract_viewports(doc),
        units=extract_units(doc),
        inserts=extract_inserts(doc),
        texts=extract_texts(doc),
    )


# How a DXF filename carries its sheet identifier. Two real conventions
# confirmed so far, tried most-specific first:
#
#   T2DPAA  "...-DRG-101032_0.dxf"  -> 101032   (digits before a _revision suffix)
#   CS1     "359944.dxf"            -> 359944   (the whole filename)
#
# The third pattern is a genuine fallback: the last digit run in the stem,
# for a naming scheme neither of the above anticipates. It only produces a
# *candidate*; whether it becomes a match is decided by the join below,
# which refuses anything ambiguous.
_DXF_NAME_PATTERNS = (
    re.compile(r"-(\d+)_\d+$"),   # T2DPAA: trailing _<revision>
    re.compile(r"^(\d+)$"),       # CS1: bare number
    re.compile(r"(\d+)$"),        # fallback: trailing digit run
)


def filename_sheet_digits(path: str) -> str | None:
    """The sheet-identifying digit run in a DXF filename, or `None`."""

    stem = Path(path).stem
    for pattern in _DXF_NAME_PATTERNS:
        m = pattern.search(stem)
        if m:
            return m.group(1)
    return None


def attach_dxf_sheets(project: Project, dxf_sheets: list[DxfSheet]) -> int:
    """Matches each `DxfSheet` to its counterpart `Sheet` and sets
    `Sheet.dxf_sheet`. Returns the number matched, so a caller can tell
    "ran cleanly, nothing matched" apart from silently doing nothing —
    `session.run_session` turns that into a coverage warning.

    Filename-based rather than DXF-internal-data-based, deliberately: it
    works for sheets with zero dimensions, and doesn't depend on
    title-block extraction from DXF, which isn't built (and is confirmed
    out of scope — see ir.py's `DxfSheet`).

    **Two matching strengths, strongest first.** A filename's digits are
    compared against each sheet's own `sheet_no` digits:

    1. *Exact* — the whole identifier matches. This is the CS1/Flinders
       case, where `359944.dxf` belongs to the sheet printing `359944`.
    2. *Last four digits* — required for T2DPAA, where the two genuinely
       differ everywhere else: DWG `...-101051_0.dxf` belongs to the sheet
       printing `2871051`, agreeing only on `1051`.

    **Ambiguity is refused, not guessed.** Last-four matching is lossy, and
    an earlier version keyed a plain dict on it, so two sheets sharing
    their last four digits silently overwrote each other and one DXF got
    attached to whichever sheet happened to be indexed last. With 116
    sheets in a set (Flinders) that is a real birthday-problem risk rather
    than a theoretical one — it happens not to collide on the sets to hand,
    which is luck, not a guarantee. A key matching more than one sheet now
    attaches nothing, so the DXF is reported as unmatched rather than
    attached to the wrong sheet — the same "skip rather than guess" rule
    used throughout this codebase, and the safer failure here by a wide
    margin: a wrong attachment silently checks one sheet's geometry
    against another sheet's drawing."""

    exact: dict[str, list] = {}
    last4: dict[str, list] = {}
    for sheet in project.sheets:
        digits = re.sub(r"\D", "", sheet.sheet_no or "")
        if not digits:
            continue
        exact.setdefault(digits, []).append(sheet)
        if len(digits) >= 4:
            last4.setdefault(digits[-4:], []).append(sheet)

    matched = 0
    for dxf_sheet in dxf_sheets:
        digits = filename_sheet_digits(dxf_sheet.source_path)
        if not digits:
            continue
        candidates = exact.get(digits) or (last4.get(digits[-4:]) if len(digits) >= 4 else None)
        if not candidates or len(candidates) > 1:
            continue  # unmatched, or ambiguous — either way, don't guess
        candidates[0].dxf_sheet = dxf_sheet
        matched += 1
    return matched
