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
      Reporting/                   # IssueJsonWriter - issues -> JSON, the one
                                    #   output shape a command needs today
                                    #   (not a full report.py port - see its
                                    #   docstring)
    RevitCheck.Addin/               # net48 (Revit 2024) - the ONLY project that
                                    #   references the Revit API. Compiles cleanly
                                    #   on this Mac (Nice3point.Revit.Api.* NuGet
                                    #   packages). Wired up as of 2026-08-24 -
                                    #   see "What's built" below.
      Adapters/
        RevitMetadataElementSource.cs  # the metadata adapter - reads
                                        #   category/family/parameters off a
                                        #   live Document, judges nothing
      Commands/
        MetadataReconciliationCommand.cs  # IExternalCommand: prompts for a
                                           #   mapping file + CSV (per run -
                                           #   see its docstring for why),
                                           #   runs the check, writes results
      RevitCheckApplication.cs      # IExternalApplication.OnStartup - creates
                                     #   the RevitCheck ribbon tab/panel
      RevitCheck.addin              # the manifest Revit actually loads
      Resources/Icons/               # ribbon icons (32/16px PNG + 256px
                                      #   masters under source/)
      Polyfills/                    # same shim pattern as Core's, needed
                                     #   again because it's a separate
                                     #   assembly (net48, not netstandard2.0)
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
    RevitCheck.Core.Tests/          # xUnit, 170 tests
      Fixtures/                     #   synthetic-IR builders + the real-capture
                                     #   parity fixture (see below)
    RevitCheck.MappingBuilder.Tests/ # xUnit, 7 tests
```

RevitCheck.Addin has no test project, deliberately - like `adapters/revit_source.py` on the
Python side, its Revit-API-calling code (`Adapters/`, `Commands/`,
`RevitCheckApplication.cs`) cannot be exercised without a real `Document`, so
no test will ever touch it. `dotnet build` (0 warnings/errors) is the only
automated check it gets; real verification happens on the Revit machine
(see "What's not done" below).

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
dotnet test           # 170 + 7 = 177 tests, zero Revit involved
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

## Wired up (2026-08-24) - compiles, not yet run inside Revit

The metadata-reconciliation path is now fully wired end to end:

- **`Adapters/RevitMetadataElementSource.cs`** - the metadata adapter.
  Category scope, key-parameter identity, and nested-sub-component shape
  (all three of the former "open questions" below) came from real data via
  `diagnostics/InspectElements.pushbutton`'s 71-element export, not a
  guess - see the class's own remarks for specifics.
- **`Commands/MetadataReconciliationCommand.cs`** - the `IExternalCommand`.
  Prompts for the mapping file and the reference CSV **on every run**
  (`Microsoft.Win32.OpenFileDialog`), confirmed with the user 2026-08-24:
  even within one client, individual models vary enough that a per-run
  picker is what actually works across all of them, rather than a path
  baked into config that quietly stops matching one project. Neither file
  is written back into this repo - both are client asset data (the old
  open question #4, below). Results are written as JSON
  (`Core/Reporting/IssueJsonWriter.cs`) next to the model file, plus a
  `TaskDialog` summary.
- **`RevitCheckApplication.cs`** - `IExternalApplication.OnStartup`,
  creates the "RevitCheck" ribbon tab/panel with one button (the icons
  under `Resources/Icons/`).
- **`RevitCheck.addin`** - the manifest. Deploys to the shared
  `Addins\2024\` folder itself, while the DLL and its dependencies go one
  level down in their own `RevitCheck\` subfolder - see "Next: prove it on
  the Revit machine" below for why that split, not everything in one
  place.

`dotnet build` is clean (0 warnings/errors) including all of the above,
against the real `Autodesk.Revit.DB`/`.UI` types via the Nice3point
packages. **Nothing here has run inside actual Revit yet** - that's the
real gate (see "Next: prove it on the Revit machine" below), not the
compile.

Deliberately scoped to *one* button: the dimension/sheet/view adapter (for
`revit.dimension_provenance` / `revit.dimension_override_consistency`) is a
separate, later phase - proving the wiring itself on the simpler case
first, rather than shipping every check's plumbing at once and debugging
all of it on the Revit machine simultaneously.

### Next: prove it on the Revit machine

Revit only scans `*.addin` files directly in
`%APPDATA%\Autodesk\Revit\Addins\2024\` (not subfolders), and that folder
already holds other add-ins' manifests - so the deploy is split in two,
confirmed with the user 2026-08-24:

1. Copy `RevitCheck.addin` itself directly into
   `%APPDATA%\Autodesk\Revit\Addins\2024\`, alongside whatever's already
   there.
2. Copy everything else from `bin/Debug/net48/` - `RevitCheck.Addin.dll`,
   `RevitCheck.Core.dll`, `CsvHelper.dll`, the `System.Text.Json` family,
   `Resources\Icons\` - into a new `RevitCheck\` subfolder one level under
   that (`...\Addins\2024\RevitCheck\`). Leave out the RevitAPI/RevitAPIUI
   assemblies if present; Revit supplies those itself. The manifest's
   `<Assembly>` path (`.\RevitCheck\RevitCheck.Addin.dll`) already points
   there.
3. Launch Revit, confirm the "RevitCheck" tab/"Checks" panel appears with
   the Metadata Reconciliation button showing its icon.
4. Run it against the same real project the Asset Classification /
   Location Referencing mappings came from; sanity-check the output
   against what's actually in the model and the CSV. The reference-CSV
   picker opening to wherever the user already keeps these files (their
   other tools' existing workflow, confirmed 2026-08-24) is expected -
   nothing to configure for that, it's just `OpenFileDialog`'s normal
   last-used-folder behaviour.
5. Only then: the dimension/sheet/view adapter, its two
   `IExternalCommand`s, and their ribbon buttons - a genuine line-for-line
   port of `revit_source.py`'s `_collect_dimensions`/
   `_collect_sheets_and_views`, including the documented `OwnerViewId`
   per-view-collection fix, but still needing the Revit machine to build
   and debug against real element/API behaviour.

### Former open questions - now answered from real data

1. ~~The actual key-parameter name/identity~~ - `ATM_Asset_Identifier`,
   confirmed via the real CSVs and the `InspectElements` export.
2. ~~How nested sub-components actually work~~ - `FamilyInstance.
   GetSubComponentIds()`, each with its own `ElementId`/`UniqueId` and
   independently-editable parameters (44 of 71 elements in the real export
   had one). `RevitMetadataElementSource` walks them exactly this way.
3. ~~Element category/class scoping~~ - Floors, Generic Models, Structural
   Connections, Structural Foundations, Structural Framing (the real
   spread in the export) - `RevitMetadataElementSource.DefaultCategories`.
4. ~~Where a real mapping file and CSV should live~~ - neither: both are
   picked per run (see "Wired up" above), never committed to this repo.
