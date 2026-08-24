using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Mapping;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Wires <c>revitcheck.metadata_reconciliation</c> up to a real Revit
/// document. The first button to actually run inside Revit (native-add-in
/// side) - see native/README.md's "What's not done" for the wiring this
/// closes.
/// </summary>
/// <remarks>
/// Mapping file and reference CSV are both picked per run, not read from a
/// fixed project convention - confirmed with the user 2026-08-24: even
/// within one client's projects, individual models vary enough that a
/// per-run picker is what actually works across all of them, rather than a
/// path baked into config that quietly stops matching. This also matches a
/// workflow the users already have: they keep these reference CSVs saved
/// locally as part of the firm's other metadata tools, so picking a file
/// each run is a familiar step, not a new one this add-in introduces.
/// Neither file is ever written back into this repo - same caution as a
/// real capture (PLANNING.md §2), both are client asset data
/// (native/README.md's open question #4).
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class MetadataReconciliationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiApp = commandData.Application;
        var doc = uiApp.ActiveUIDocument?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var mappingPath = PromptForFile(
            "RevitCheck - select mapping file",
            "Mapping JSON (*.mapping.json)|*.mapping.json|JSON files (*.json)|*.json|All files (*.*)|*.*");
        if (mappingPath is null)
        {
            return Result.Cancelled;
        }

        var csvPath = PromptForFile(
            "RevitCheck - select reference CSV",
            "CSV files (*.csv)|*.csv|All files (*.*)|*.*");
        if (csvPath is null)
        {
            return Result.Cancelled;
        }

        ParameterMapping mapping;
        CsvTable csv;
        try
        {
            mapping = ParameterMappingSerializer.Load(mappingPath);
            csv = CsvReader.ReadFile(csvPath);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Metadata Reconciliation",
                $"Could not load mapping or CSV:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        MetadataCollectionResult collected;
        try
        {
            collected = RevitMetadataElementSource.Collect(doc, mapping.ScopeViewName);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Metadata Reconciliation", $"Could not collect elements:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            Elements = collected.Elements,
            ExtractionErrors = collected.ExtractionErrors,
        };

        var config = new ReconciliationConfig();
        var rawIssues = MetadataReconciliationCheck.Run(model, mapping, csv, config);
        // Grouped for the human-facing output - the check's own result
        // (rawIssues) stays one issue per (element, field) finding; nobody
        // reading a report needs to see the same systematic error repeated
        // per element (see IssueGrouping's own docstring).
        var issues = IssueGrouping.GroupMetadataMismatches(model, rawIssues);

        string? outputPath = null;
        try
        {
            outputPath = IssueOutput.WriteNextToModel(doc, issues, "metadata_reconciliation");
        }
        catch (Exception ex)
        {
            // Not being able to write the file is not a reason to hide the
            // result from the user - report the count either way.
            TaskDialog.Show("RevitCheck - Metadata Reconciliation",
                $"{issues.Count} issue(s) found, but the results file could not be written:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Succeeded;
        }

        var groupingNote = rawIssues.Count != issues.Count
            ? $" ({rawIssues.Count} before grouping repeated findings together)"
            : "";
        TaskDialog.Show("RevitCheck - Metadata Reconciliation",
            $"{issues.Count} issue(s) found{groupingNote} ({collected.Elements.Count} element(s) checked).\n\n" +
            $"Written to (JSON, CSV and BCF, same folder):\n{outputPath}");

        return Result.Succeeded;
    }

    private static string? PromptForFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
