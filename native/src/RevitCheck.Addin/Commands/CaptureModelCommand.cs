using System.IO;
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
/// Writes the metadata sweep to a <see cref="CaptureSerializer"/> JSON file
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
/// Prompts for a mapping file the same way <see cref="MetadataReconciliationCommand"/>
/// does, but only to read its <see cref="ParameterMapping.ScopeViewName"/> -
/// the capture's element scope should match whatever a real check run would
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
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not load mapping:\n\n{FullMessage(ex)}");
            return Result.Failed;
        }

        MetadataCollectionResult collected;
        try
        {
            collected = RevitMetadataElementSource.Collect(doc, mapping.ScopeViewName);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not collect elements:\n\n{FullMessage(ex)}");
            return Result.Failed;
        }

        var model = new RevitModel
        {
            DocTitle = doc.Title,
            RevitVersion = commandData.Application.Application.VersionNumber,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Elements = collected.Elements,
            ExtractionErrors = collected.ExtractionErrors,
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
            TaskDialog.Show("RevitCheck - Capture Model", $"Could not write the capture file:\n\n{FullMessage(ex)}");
            return Result.Failed;
        }

        TaskDialog.Show("RevitCheck - Capture Model",
            $"{collected.Elements.Count} element(s) captured" +
            (collected.ExtractionErrors.Count > 0 ? $", {collected.ExtractionErrors.Count} extraction error(s)" : "") +
            $".\n\nWritten to:\n{savePath}\n\n" +
            "Treat this file like a real model capture (PLANNING.md §2) - it contains real " +
            "parameter values from a real project.");

        return Result.Succeeded;
    }

    private static string? PromptForFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? PromptForSaveLocation(Document doc)
    {
        var docPath = doc.PathName;
        var suggestedName = (string.IsNullOrEmpty(docPath) ? doc.Title : Path.GetFileNameWithoutExtension(docPath)) ?? "model";

        var dialog = new SaveFileDialog
        {
            Title = "RevitCheck - save capture as",
            FileName = $"{suggestedName}.capture.json",
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>See MetadataReconciliationCommand.FullMessage - same reasoning, same fix.</summary>
    private static string FullMessage(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join("\n  --> ", parts);
    }
}
