using System.IO;
using Autodesk.Revit.DB;
using RevitCheck.Addin.Commands;
using RevitCheck.Addin.UI;
using RevitCheck.Core.Reporting;

namespace RevitCheck.Addin;

/// <summary>
/// Cross-command session state for the interactive checking workflow
/// (PLANNING.md §16 Stage 3) - the first static state shared between
/// separate ribbon-button invocations in this codebase (every command
/// before this one is a one-shot: collect, check, write a file, done).
/// <see cref="Session"/> and <see cref="Window"/> are what let
/// <see cref="Commands.DimensionTriageCommand"/>, the two pile commands,
/// and <see cref="ChecklistWindow"/>'s own buttons all act on the same
/// live state across separate <c>IExternalCommand.Execute</c> calls.
/// </summary>
/// <remarks>
/// Session-restart survival (a confirmed real requirement, PLANNING.md
/// §16) is <see cref="CheckingSessionSerializer"/>'s job, not this class's
/// - this only holds the in-memory live session for as long as Revit stays
/// open, plus the fixed on-disk path <see cref="SessionFilePathFor"/>
/// derives so a later Revit session can find and offer to resume it.
/// </remarks>
internal static class CheckingSessionHost
{
    public static CheckingSession? Session { get; set; }

    public static ChecklistWindow? Window { get; set; }

    public static string? SessionFilePath { get; set; }

    /// <summary>
    /// A fixed per-document location under
    /// <c>%LOCALAPPDATA%\RevitCheck\Sessions\</c>, keyed on the same
    /// <see cref="DocumentPaths.SafeBaseName"/> every save-dialog
    /// suggestion already uses - a document that produces a sensible
    /// suggested filename also produces a sensible, stable session path,
    /// and the "never throws" discipline that name already has to follow
    /// (a cloud-worksharing model's <c>PathName</c>, see
    /// <see cref="DocumentPaths"/>'s own remarks) is exactly what this
    /// needs too.
    /// </summary>
    public static string SessionFilePathFor(Document doc)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitCheck", "Sessions");
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{DocumentPaths.SafeBaseName(doc)}.checking-session.json");
    }

    /// <summary>
    /// Saves <see cref="Session"/> to <see cref="SessionFilePath"/> - a
    /// no-op if either is unset. Callers wrap this in their own try/catch
    /// (matching every other file-write in this codebase) rather than this
    /// method swallowing a failure silently - a session that stops
    /// surviving restarts is worth surfacing, not hiding.
    /// </summary>
    public static void Autosave()
    {
        if (Session is null || SessionFilePath is null)
        {
            return;
        }

        CheckingSessionSerializer.Save(Session, SessionFilePath);
    }
}
