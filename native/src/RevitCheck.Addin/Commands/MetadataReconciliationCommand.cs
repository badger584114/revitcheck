using System.IO;
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
                $"Could not load mapping or CSV:\n\n{FullMessage(ex)}");
            return Result.Failed;
        }

        var collected = RevitMetadataElementSource.Collect(doc);
        var model = new RevitModel
        {
            DocTitle = doc.Title,
            Elements = collected.Elements,
            ExtractionErrors = collected.ExtractionErrors,
        };

        var config = new ReconciliationConfig();
        var issues = MetadataReconciliationCheck.Run(model, mapping, csv, config);

        string? outputPath = null;
        try
        {
            outputPath = WriteIssuesNextToModel(doc, issues);
        }
        catch (Exception ex)
        {
            // Not being able to write the file is not a reason to hide the
            // result from the user - report the count either way.
            TaskDialog.Show("RevitCheck - Metadata Reconciliation",
                $"{issues.Count} issue(s) found, but the results file could not be written:\n\n{FullMessage(ex)}");
            return Result.Succeeded;
        }

        TaskDialog.Show("RevitCheck - Metadata Reconciliation",
            $"{issues.Count} issue(s) found ({collected.Elements.Count} element(s) checked).\n\n" +
            $"Written to:\n{outputPath}");

        return Result.Succeeded;
    }

    /// <summary>
    /// The outer message on a <see cref="TypeInitializationException"/> (or
    /// any wrapped exception) is close to useless on its own - "the type
    /// initializer for X threw an exception" names the symptom, not the
    /// cause, which is nested in InnerException. Walking the chain into the
    /// TaskDialog is the difference between a user being able to tell us
    /// what actually broke and a guessing game over chat.
    /// </summary>
    private static string FullMessage(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join("\n  --> ", parts);
    }

    private static string? PromptForFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string WriteIssuesNextToModel(Document doc, List<Core.Issues.Issue> issues)
    {
        var docPath = doc.PathName;
        var directory = string.IsNullOrEmpty(docPath) ? Path.GetTempPath() : Path.GetDirectoryName(docPath) ?? Path.GetTempPath();
        var baseName = string.IsNullOrEmpty(docPath) ? (doc.Title ?? "model") : Path.GetFileNameWithoutExtension(docPath);
        var path = Path.Combine(directory, $"{baseName}.metadata_reconciliation.json");
        return IssueJsonWriter.Write(issues, path);
    }
}
