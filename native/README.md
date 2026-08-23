# RevitCheck native add-in

The C# native Revit add-in. `extensions/RevitCheck.extension` (pyRevit) is
frozen — no further feature work there (PLANNING.md §12) — this is where new
work happens.

See the plan the metadata reconciliation feature was built from for the full
design reasoning: `~/.claude/plans/the-first-button-we-abundant-platypus.md`
(or ask for it to be copied into this repo if that path won't be around
later).

## Layout

```
native/
  RevitCheck.sln
  Directory.Build.props           # shared LangVersion/Nullable across every project
  src/
    RevitCheck.Core/               # netstandard2.0 - NO Revit API reference, no
                                    #   runtime dependency. Everything here is
                                    #   plain, pure, and testable with zero Revit.
      Ir/                          # RevitModel + everything it carries:
                                    #   ElementMetadata/ParameterValue (metadata
                                    #   reconciliation), SheetInfo/ViewInfo/
                                    #   DimensionInfo/DimensionSegmentInfo/
                                    #   ReferenceInfo/Provenance (dimension checks,
                                    #   ported from ir.py). One capture, growing.
      Capture/                     # JSON save/load - mirrors capture.py
      Issues/                      # Issue + IssueSorting - mirrors issue.py
      Catalog/                     # Catalog + CheckRegistry - mirrors catalog.py
      Mapping/                     # ParameterMapping ("the export file taken
                                    #   once and saved") + its serializer
      Csv/                         # CSV-only reference-data reader (CsvHelper)
      Checks/
        RuleConfig.cs                        # dimension-check tolerances/scoping,
        DimensionProvenanceOptions.cs         #   ported from catalog.py's RuleConfig
        DimensionClassification.cs           # classify_reference/classify_dimension
        ViewScoping.cs                        # views_in_scope and its helpers
        DimensionDescriptions.cs             # shared wording helpers
        DimensionProvenanceCheck.cs           # revit.dimension_provenance
        DimensionOverrideConsistencyCheck.cs  # revit.dimension_override_consistency
        MetadataReconciliationCheck.cs        # revitcheck.metadata_reconciliation
        ReconciliationConfig.cs
        CaptureCoverageCheck.cs               # revitcheck.capture_coverage
      Polyfills/                   # IsExternalInit / required-member shims
                                    #   netstandard2.0 needs for modern C#
    RevitCheck.Addin/               # net48 (Revit 2024) - the ONLY project that
                                    #   references the Revit API. Compiles cleanly
                                    #   on this Mac (Nice3point.Revit.Api.* NuGet
                                    #   packages), but has no real adapter code
                                    #   yet - see "What's not done" below.
  tools/
    RevitCheck.MappingBuilder/      # net8.0 console app, no Revit refs. Builds a
                                    #   starter mapping file from a real capture +
                                    #   a real CSV's headers - off-machine, the
                                    #   analogue of scripts/check_capture.py.
  diagnostics/
    InspectElements.pushbutton/     # ONE-OFF Revit-machine diagnostic (not part
                                    #   of the frozen extension) - dumps a
                                    #   selection's full identity/parameter set to
                                    #   JSON. See diagnostics/README.md.
  tests/
    RevitCheck.Core.Tests/          # xUnit, 168 tests
      Fixtures/                     #   synthetic-IR builders + the real-capture
                                     #   parity fixture (see below)
    RevitCheck.MappingBuilder.Tests/ # xUnit, 6 tests
```

## Building and testing

Requires the .NET 8 SDK. If it's not on PATH:

```
export PATH="$HOME/.dotnet:$PATH"   # if installed via dotnet-install.sh to ~/.dotnet
```

```
cd native
dotnet build          # whole solution, including the net48 Addin (RevitAPI
                       # comes from the Nice3point.Revit.Api.* NuGet packages -
                       # no Revit install needed to compile, only to run)
dotnet test           # 168 + 6 = 174 tests, zero Revit involved
```

Both are expected to be clean (0 warnings, 0 errors; all tests passing) at all
times - this mirrors the Python side's `pytest` loop running in a tenth of a
second with no Revit present.

### Running the mapping-build tool

```
dotnet run --project tools/RevitCheck.MappingBuilder -- \
    <capture.json> <reference.csv> <key-parameter-name> <output-mapping.json>
```

Writes a starter `ParameterMapping` JSON file: exact (case-insensitive)
column/parameter-name matches are auto-filled; anything else is left out of
`fields` and printed to the console instead, along with the real parameter
names present per distinct (category, family) pair in the capture - so a
human resolves the hard, family-varying cases against a curated shortlist,
not a blind guess. **The tool never writes an `overrides` entry itself.**
Every auto-matched numeric field is deliberately left with no `tolerance_mm`,
which means `ParameterMappingSerializer.Load` will refuse to load the file
until a human has actually reviewed it and filled that in - a real gate, not
just a comment asking nicely.

## What's built

### Metadata reconciliation (Phases 0-6)

Joins captured model elements against an external CSV of reference data via a
key parameter and a persisted mapping file that resolves family-variant
parameter names. `MetadataReconciliationCheck` implements the full join/
compare logic: numeric-with-tolerance and case-insensitive-exact-string
comparisons, family/category-based parameter-name overrides, a nested
sub-component reconciled completely independently of its host, an element
with a key but no CSV match reported by default (the "missing item" signal),
a genuinely blank model value against CSV data reported as a first-class
mismatch (the "incorrectly filled" signal) rather than a coverage note, and
zero noise from a 1000+ row whole-of-project CSV against a 20-30 element
model.

### Dimension checks, ported from Python (`checks/dimensions.py`)

`revit.dimension_provenance` and `revit.dimension_override_consistency`,
ported rule-by-rule against the existing Python behaviour as the spec to
match (PLANNING.md §12's own instruction), including the IR they run on
(`SheetInfo`/`ViewInfo`/`DimensionInfo`/`ReferenceInfo`/`Provenance`) and
`RuleConfig`. All 81 Python test functions (`test_dimension_provenance.py` +
`test_dimension_overrides.py`) were ported test-by-test, not reinvented.

**Verified against real data, not just synthetic tests**: `RealCaptureParityTests`
runs both ported rules against the one real capture committed to the repo
(`samples/T2DPAA-T2D-C3S-BR-M3D-100304_Peter.capture.json`) and asserts the
result against a fixture (`Fixtures/real_capture_expected_issue_ids.txt`)
captured once from the actual Python engine's output on the same file
(`python3 scripts/check_capture.py <file> --json`) — **every one of 957
issue_ids matches exactly**, not just the aggregate counts. Getting there
found and fixed two real translation bugs neither synthetic tests nor a
human read-through caught:

- An em-dash (—) silently ported as a plain hyphen (-) in several
  description strings — same text to a human eye at a glance, different
  SHA-256 identity hash.
- A naive string-quoting substitute for Python's `repr()` in the unparsed-
  override-forms coverage message, which would have embedded a raw invisible
  Unicode character (a real override on this capture is a lone U+200E) into
  the description instead of escaping it visibly the way Python's repr does
  (`PythonRepr` in `DimensionOverrideConsistencyCheck.cs`).

This is exactly the translation risk PLANNING.md §12 named up front (float
formatting, iteration order, string handling) — real data caught what
review alone didn't.

### The Addin project (net48) genuinely compiles on this Mac

Against real `Autodesk.Revit.DB`/`.UI` types, with no Revit installed, via
the `Nice3point.Revit.Api.*` NuGet packages — confirmed with a throwaway
sanity file during the build, not just assumed.

## What's not done - needs the Revit machine

- **The metadata adapter** (`Adapters/RevitMetadataElementSource.cs`) - not
  written. Blocked on real answers only available at the Revit machine (see
  "Open questions" below) - use `diagnostics/InspectElements.pushbutton` to
  get them.
- **The dimension/sheet/view adapter** - the C# port of `revit_source.py`'s
  `_collect_dimensions`/`_collect_sheets_and_views` and the rest of its
  geometry-reading side. Less blind than the metadata adapter: there's a
  working Python implementation to match line-for-line, including the
  documented `OwnerViewId` per-view-collection fix - but still needs the
  Revit machine to build and debug against real element/API behaviour.
- **`IExternalCommand`s, ribbon wiring, the `.addin` manifest** - nothing is
  wired up to actually run inside Revit yet. This is the real precondition
  for archiving pyRevit, not just having the checking logic ported.

### Open questions that block the metadata adapter, not resolvable from this side

1. **The actual key-parameter name/identity** used across the team's
   metadata-input tools.
2. **How nested sub-components actually work in these families** - whether
   the items needing independent data (e.g. a fixing bracket nested in a
   panel) are "shared" nested families with their own `ElementId` and
   independently editable instance parameters (which would make
   `FamilyInstance.GetSubComponentIds()`/`SuperComponent` the right API
   surface), or something the existing metadata-input tools already have a
   working convention for.
3. Element category/class scoping for the adapter's collection sweep -
   which categories actually carry the 30-40 tracked parameters.
4. Where a real mapping file and a real project's CSV should live - not
   committed to this repo (client asset data), by the same caution
   `capture.py` already applies to a real model capture.

Run `diagnostics/InspectElements.pushbutton` (see `diagnostics/README.md`)
against representative elements — including at least one nested-component
case — to answer 1-3 from ground truth rather than description.
