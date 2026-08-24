namespace RevitCheck.Addin.Commands;

/// <summary>
/// The outer message on a <see cref="TypeInitializationException"/> (or any
/// wrapped exception) is close to useless on its own - "the type initializer
/// for X threw an exception" names the symptom, not the cause, which is
/// nested in <see cref="Exception.InnerException"/>. Walking the chain into
/// a <c>TaskDialog</c> is the difference between a user being able to tell
/// us what actually broke and a guessing game over chat.
/// </summary>
/// <remarks>
/// Factored out of <c>MetadataReconciliationCommand</c>/<c>CaptureModelCommand</c>,
/// which each had their own copy, once a third and fourth command
/// (<c>DimensionProvenanceCommand</c>/<c>DimensionOverrideConsistencyCommand</c>)
/// needed the same thing - per the dimension-adapter plan's own note not to
/// copy-paste it a third time.
/// </remarks>
internal static class ExceptionMessage
{
    public static string Full(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join("\n  --> ", parts);
    }
}
