using System.IO;
using RevitCheck.Core.Checks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitCheck.Addin.Adapters;
using RevitCheck.Core.Capture;
using RevitCheck.Core.Ir;
using RevitCheck.Core.Mapping;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Writes a full model sweep to a <see cref="CaptureSerializer"/> JSON file
/// - the native add-in's counterpart to the Python side's Capture Model
/// button and its whole dev-loop role (native/README.md, CLAUDE.md's
/// "Development setup"): a point-in-time snapshot, not a live sync, taken
/// once and then iterated against off the Revit machine as many times as
/// needed. Existed to unblock building output/reporting logic (grouping by
/// family/type/field/values) against real element diversity without a
/// Revit-machine round trip for every change - confirmed with the user
/// 2026-08-24.
/// </summary>
/// <remarks>
/// Captures both halves of <see cref="RevitModel"/> in one file, matching
/// Python's own <c>capture.py</c> (which never split metadata from
/// sheets/views/dimensions into separate captures either) - extended
/// 2026-08-25 for the dimension-adapter port, having started as
/// metadata-only while that was the only adapter that existed. Prompts for
/// a mapping file the same way <see cref="MetadataReconciliationCommand"/>
/// does, but only to read its <see cref="ParameterMapping.ScopeViewName"/> -
/// the metadata scope should match whatever a real reconciliation run would
/// actually see, not a separately-chosen scope that could quietly drift
/// from it. Neither the mapping's <c>Fields</c> nor any CSV are used here.
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
public class CaptureModelCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            message = "No active document.";
            return Result.Failed;
        }

        var mappingPath = PromptForFile(
            "RevitCheck - select mapping file (for its scope view only)",
            "Mapping JSON (*.mapping.json)|*.mapping.json|JSON files (*.json)|*.json|All files (*.*)|*.*");
        if (mappingPath is null)
        {
            return Result.Cancelled;
        }

        ParameterMapping mapping;
        try
        {
            mapping = ParameterMappingSerializer.Load(mappingPath);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not load mapping:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        MetadataCollectionResult collectedMetadata;
        DimensionCollectionResult collectedDimensions;
        try
        {
            collectedMetadata = RevitMetadataElementSource.Collect(doc, mapping.ScopeViewName);
            collectedDimensions = RevitDimensionSource.Collect(doc);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not collect the model:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var extractionErrors = new List<string>(collectedMetadata.ExtractionErrors);
        extractionErrors.AddRange(collectedDimensions.ExtractionErrors);

        // Schedule HEADERS only - three empty candidate lists mean "no
        // schedule qualifies for a body read" (RevitScheduleSource's own
        // null-vs-empty distinction), which is all the starter config needs
        // and avoids the expensive per-cell read that a ReadOnly
        // transaction cannot perform anyway (PLANNING.md §16).
        var scheduleErrors = new List<string>();
        var collectedSchedules = RevitScheduleSource.Collect(
            doc, scheduleErrors, new List<string>(), new List<string>(), new List<string>());
        extractionErrors.AddRange(scheduleErrors);

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            RevitVersion = commandData.Application.Application.VersionNumber,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Elements = collectedMetadata.Elements,
            Sheets = collectedDimensions.Sheets,
            Views = collectedDimensions.Views,
            Dimensions = collectedDimensions.Dimensions,
            Schedules = collectedSchedules,
            ExtractionErrors = extractionErrors,
            ExcludedWorksets = collectedDimensions.ExcludedWorksets,
        };

        var savePath = PromptForSaveLocation(doc);
        if (savePath is null)
        {
            return Result.Cancelled;
        }

        try
        {
            CaptureSerializer.Save(model, savePath);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not write the capture file:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        var starterNote = WriteStarterConfig(doc, model);

        TaskDialog.Show("RevitCheck - Capture Model",
            $"{collectedMetadata.Elements.Count} element(s), {collectedDimensions.Sheets.Count} sheet(s), " +
            $"{collectedDimensions.Views.Count} view(s), {collectedDimensions.Dimensions.Count} dimension(s), " +
            $"{collectedSchedules.Count} schedule(s) captured" +
            (extractionErrors.Count > 0 ? $", {extractionErrors.Count} extraction error(s)" : "") +
            $".\n\nWritten to:\n{savePath}\n\n{starterNote}\n\n" +
            "Treat this file like a real model capture (PLANNING.md §2) - it contains real " +
            "parameter values from a real project.");

        return Result.Succeeded;
    }

    private static string? PromptForFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Writes a starter <see cref="RuleConfig"/> for this model if it
    /// hasn't got one, so the checks can be configured for a new project by
    /// editing a file rather than rebuilding the add-in. Never overwrites
    /// an existing one - a person's own edits outrank anything derived
    /// automatically.
    /// </summary>
    /// <remarks>
    /// This is the loop metadata reconciliation has always had (capture the
    /// model, build a per-model mapping from it, let a human finish it) and
    /// which the checks built afterwards never joined - see
    /// <see cref="RuleConfigStarter"/>'s remarks.
    /// </remarks>
    private static string WriteStarterConfig(Document doc, RevitModel model)
    {
        string path;
        try
        {
            path = RuleConfigSource.PathFor(doc);
        }
        catch (Exception ex)
        {
            return $"No starter config written (could not resolve a path: {ex.Message}).";
        }

        if (File.Exists(path))
        {
            return $"This model already has a config, left untouched:\n{path}";
        }

        try
        {
            var starter = RuleConfigStarter.Build(model);
            RuleConfigSerializer.Save(starter.Config, path);
            return
                $"Starter config written to:\n{path}\n\nReview it before trusting a run - " +
                string.Join("\n\n", starter.Diagnostics);
        }
        catch (Exception ex)
        {
            return $"No starter config written: {ExceptionMessage.Full(ex)}";
        }
    }

    private static string? PromptForSaveLocation(Document doc)
    {
        var suggestedName = DocumentPaths.SafeBaseName(doc);

        var dialog = new SaveFileDialog
        {
            Title = "RevitCheck - save capture as",
            FileName = $"{suggestedName}.capture.json",
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

}
