using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using RevitCheck.Core.Checks;

namespace RevitCheck.Addin.Commands;

/// <summary>
/// Shows, exports, imports or resets this model's <see cref="RuleConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Added 2026-09-09, per the user: "the config needs to be able to be
/// saved off the machine, to Forma so that nothing is relying on having a
/// file that lives on someone's C: drive."</b> The working copy still lives
/// under LocalApplicationData - that is what lets a check run with no
/// prompts, and what works offline - but it is now a cache of a portable
/// artefact rather than the only copy that exists. Export puts it beside a
/// capture for upload; Import brings it back on any machine, including a
/// different person's.
/// </para>
/// <para>
/// It is also the answer to a real, twice-repeated failure. A config
/// written before 2026-09-09 pins every setting as it stood then, so it
/// silently overrides later recalibration - and the only way to clear one
/// was to find a file in LocalApplicationData whose name you had to know in
/// advance. That blocked two consecutive real runs (PLANNING.md §23),
/// including one where the user deleted a file that turned out not to be
/// the one being read. Reset makes that a button.
/// </para>
/// <para>
/// Import and Reset both replace a file a person may have hand-edited, so
/// each states exactly what is about to be lost and defaults to doing
/// nothing - the same care <c>CaptureModelCommand</c> takes in never
/// overwriting a config it did not write.
/// </para>
/// </remarks>
[Transaction(TransactionMode.ReadOnly)]
[Regeneration(RegenerationOption.Manual)]
public class RuleConfigCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var doc = commandData?.Application?.ActiveUIDocument?.Document;
            if (doc is null)
            {
                TaskDialog.Show("RevitCheck - Rule Config", "No active document.");
                return Result.Cancelled;
            }

            return Show(doc);
        }
        catch (Exception ex)
        {
            message = ExceptionMessage.Full(ex);
            return Result.Failed;
        }
    }

    private static Result Show(Document doc)
    {
        var path = RuleConfigSource.PathFor(doc);
        var json = RuleConfigSource.ReadRaw(doc);

        var dialog = new TaskDialog("RevitCheck - Rule Config")
        {
            MainInstruction = json is null
                ? "This model has no config - the checks are using built-in defaults."
                : "This model has a config.",
            MainContent = Describe(doc, path, json),
            CommonButtons = TaskDialogCommonButtons.Close,
        };

        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            "Export for Forma",
            "Save this model's config to a file, to upload alongside its capture.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
            "Import from a file",
            "Replace this model's config with one downloaded from Forma or copied from another machine.");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3,
            "Reset to built-in defaults",
            "Delete this model's config. Every setting then tracks the current build, including any " +
            "tolerance recalibrated since the config was written.");

        // Set after the links exist - a real crash already on record (§16).
        dialog.DefaultButton = TaskDialogResult.Close;

        return dialog.Show() switch
        {
            TaskDialogResult.CommandLink1 => Export(doc, json),
            TaskDialogResult.CommandLink2 => Import(doc),
            TaskDialogResult.CommandLink3 => Reset(doc, json),
            _ => Result.Cancelled,
        };
    }

    /// <summary>
    /// What this config actually does to a run: where it is, when it was
    /// written, and every setting it holds away from the current defaults.
    /// </summary>
    private static string Describe(Document doc, string path, string? json)
    {
        if (json is null)
        {
            return
                $"It would live at:\n{path}\n\n" +
                "Run Capture Model to write a starter config listing what this project actually contains.";
        }

        var described = $"Stored at:\n{path}\n\n";

        var provenance = RuleConfigSerializer.DescribeProvenance(json);
        described += provenance is null
            ? "It records no date, so it was written before 2026-09-09 - meaning it pins EVERY setting as it " +
              "stood then, including any recalibrated since. That is the situation that caused two real runs " +
              "to use superseded tolerances.\n\n"
            : provenance + "\n\n";

        try
        {
            var overrides = RuleConfigSerializer.DescribeOverrides(json);
            described += overrides.Count == 0
                ? "It changes nothing from the built-in defaults."
                : $"It overrides {overrides.Count} built-in default(s):\n  " + string.Join("\n  ", overrides);
        }
        catch (Exception ex)
        {
            described += $"It could not be read: {ExceptionMessage.Full(ex)}";
        }

        return described;
    }

    private static Result Export(Document doc, string? json)
    {
        if (json is null)
        {
            TaskDialog.Show("RevitCheck - Rule Config", "There is no config to export.");
            return Result.Cancelled;
        }

        var dialog = new SaveFileDialog
        {
            Title = "RevitCheck - export config as",
            FileName = RuleConfigSource.FileNameFor(doc),
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        try
        {
            File.WriteAllText(dialog.FileName, json);
            TaskDialog.Show("RevitCheck - Rule Config",
                $"Config written to:\n{dialog.FileName}\n\nUpload it to Forma alongside this model's capture, " +
                "so a run can be reproduced without depending on this machine.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Rule Config", $"Could not write the file:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }
    }

    private static Result Import(Document doc)
    {
        var dialog = new OpenFileDialog
        {
            Title = "RevitCheck - import config",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return Result.Cancelled;
        }

        string incoming;
        try
        {
            incoming = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Rule Config", $"Could not read that file:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }

        // Say what it will do before doing it, since this replaces a file
        // someone may have edited by hand.
        var summary = Describe(doc, dialog.FileName, incoming);
        var confirm = new TaskDialog("RevitCheck - Rule Config")
        {
            MainInstruction = "Replace this model's config with the imported one?",
            MainContent = summary,
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
        };
        confirm.DefaultButton = TaskDialogResult.No;

        if (confirm.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        try
        {
            RuleConfigSource.Install(doc, incoming);
            TaskDialog.Show("RevitCheck - Rule Config",
                $"Imported. This model now uses:\n{RuleConfigSource.PathFor(doc)}");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Rule Config",
                $"That file was not installed - it could not be read as a config:\n\n{ExceptionMessage.Full(ex)}" +
                "\n\nThe existing config is unchanged.");
            return Result.Failed;
        }
    }

    private static Result Reset(Document doc, string? json)
    {
        if (json is null)
        {
            TaskDialog.Show("RevitCheck - Rule Config", "There is no config to reset - defaults are already in use.");
            return Result.Cancelled;
        }

        var confirm = new TaskDialog("RevitCheck - Rule Config")
        {
            MainInstruction = "Delete this model's config?",
            MainContent =
                "Every setting will then track this build's own defaults. Anything this project genuinely " +
                "differs on - a category name, a schedule column heading, a calibrated tolerance - will be " +
                "lost unless it has been exported.\n\n" + Describe(doc, RuleConfigSource.PathFor(doc), json),
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
        };
        confirm.DefaultButton = TaskDialogResult.No;

        if (confirm.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        try
        {
            var deleted = RuleConfigSource.Reset(doc);
            TaskDialog.Show("RevitCheck - Rule Config",
                deleted
                    ? "Deleted. Run Capture Model to write a fresh starter config for this model."
                    : "There was nothing to delete.");
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitCheck - Rule Config", $"Could not delete it:\n\n{ExceptionMessage.Full(ex)}");
            return Result.Failed;
        }
    }
}
