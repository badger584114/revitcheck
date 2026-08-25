using RevitCheck.Core.Capture;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Issues;
using RevitCheck.Core.Reporting;

// Off-machine tool, no Revit involved - the C# counterpart of
// scripts/check_capture.py's role on the Python side, for the two rules
// that have no other way to run outside Revit: revit.dimension_provenance
// and revit.dimension_override_consistency (metadata reconciliation
// already needs its own reference CSV/mapping and isn't this tool's job).
// Loads a .capture.json written by Capture Model (or by a real check
// command's own JSON output, which is a superset), runs both rules against
// it, and prints the same kind of summary the ribbon buttons' TaskDialog
// does - plus optional JSON/CSV/BCF output, same shape as the buttons'
// IssueOutput.WriteNextToModel, so a capture pulled off the Revit machine
// can be iterated on with a normal edit/run loop instead of a trip back to
// Revit for every rule-tuning change.
if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: RevitCheck.CheckRunner <capture.json> [--json out.json] [--csv out.csv] " +
        "[--bcf out-dir] [--all-views] [--rule rule-id]...");
    Console.Error.WriteLine();
    Console.Error.WriteLine("  --all-views   include views not placed on a sheet (off by default - an ");
    Console.Error.WriteLine("                unplaced working view is never issued to anyone)");
    Console.Error.WriteLine("  --rule        run only this rule id (repeatable); default is both");
    Console.Error.WriteLine("                revit.dimension_provenance and revit.dimension_override_consistency");
    return args.Length < 1 ? 1 : 0;
}

var capturePath = args[0];
string? jsonOut = null;
string? csvOut = null;
string? bcfOutDir = null;
var allViews = false;
var ruleIds = new List<string>();

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json":
            jsonOut = args[++i];
            break;
        case "--csv":
            csvOut = args[++i];
            break;
        case "--bcf":
            bcfOutDir = args[++i];
            break;
        case "--all-views":
            allViews = true;
            break;
        case "--rule":
            ruleIds.Add(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"Unrecognised argument: {args[i]}");
            return 1;
    }
}

var model = CaptureSerializer.Load(capturePath);
var config = new RuleConfig { SheetedViewsOnly = !allViews };

var enabled = ruleIds.Count > 0
    ? new HashSet<string>(ruleIds)
    : new HashSet<string> { DimensionProvenanceCheck.RuleId, DimensionOverrideConsistencyCheck.RuleId };

var issues = new List<Issue>();
if (enabled.Contains(DimensionProvenanceCheck.RuleId))
{
    issues.AddRange(DimensionProvenanceCheck.Run(model, config));
}

if (enabled.Contains(DimensionOverrideConsistencyCheck.RuleId))
{
    issues.AddRange(DimensionOverrideConsistencyCheck.Run(model, config));
}

// Always included, same as the ribbon commands - an extraction failure
// dropped from a capture must never look indistinguishable from a clean run.
issues.AddRange(CaptureCoverageCheck.Run(model));

Console.WriteLine($"Model: {(string.IsNullOrEmpty(model.DocTitle) ? "(untitled)" : model.DocTitle)}");
Console.WriteLine($"  {model.Sheets.Count} sheets, {model.Views.Count} views, {model.Dimensions.Count} dimensions");
if (!string.IsNullOrEmpty(model.CapturedAt))
{
    Console.WriteLine($"  captured {model.CapturedAt} (Revit {model.RevitVersion})");
}

Console.WriteLine();
var bySeverity = issues.GroupBy(i => i.Severity).ToDictionary(g => g.Key, g => g.Count());
Console.WriteLine($"{issues.Count} issue(s): " +
    $"{bySeverity.GetValueOrDefault("high", 0)} high, " +
    $"{bySeverity.GetValueOrDefault("medium", 0)} medium, " +
    $"{bySeverity.GetValueOrDefault("low", 0)} low");
foreach (var issue in IssueSorting.SortIssues(issues))
{
    var where = issue.SheetNo is { } sheet ? $"sheet {sheet}" : issue.ViewName ?? "(no location)";
    Console.WriteLine($"  [{issue.Severity,-6}] {issue.RuleId} @ {where}: {issue.Description}");
}

if (enabled.Contains(DimensionProvenanceCheck.RuleId))
{
    var fullyDrafted = DimensionProvenanceCheck.DraftedViews(model, config);
    if (fullyDrafted.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Views to verify against the model ({fullyDrafted.Count}):");
        foreach (var view in fullyDrafted)
        {
            var sheetSuffix = view.SheetNo is { } sheet ? $" sheet {sheet}" : "";
            Console.WriteLine($"  - {view.Name} [{view.ViewType}]{sheetSuffix}");
        }
    }
}

if (jsonOut is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
    IssueJsonWriter.Write(issues, jsonOut);
    Console.WriteLine();
    Console.WriteLine($"Wrote {jsonOut}");
}

if (csvOut is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvOut))!);
    IssueCsvWriter.Write(issues, csvOut);
    Console.WriteLine();
    Console.WriteLine($"Wrote {csvOut}");
}

if (bcfOutDir is not null)
{
    Directory.CreateDirectory(bcfOutDir);
    var bcfFiles = IssueBcfWriter.ToBcfFiles(issues, model.DocTitle);
    Console.WriteLine();
    if (bcfFiles.Count == 0)
    {
        Console.WriteLine("No issues to export - nothing written.");
    }

    foreach (var (fileName, bytes) in bcfFiles)
    {
        var path = Path.Combine(bcfOutDir, fileName);
        File.WriteAllBytes(path, bytes);
        Console.WriteLine($"Wrote {path} ({bytes.Length} bytes)");
    }
}

// Non-zero only for findings that need action, mirroring
// scripts/check_capture.py - a low-severity coverage note alone must not
// fail a batch run.
return bySeverity.GetValueOrDefault("high", 0) > 0 ? 1 : 0;
