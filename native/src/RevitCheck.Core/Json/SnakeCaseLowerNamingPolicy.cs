using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevitCheck.Core.Json;

/// <summary>
/// A hand-rolled equivalent of <c>JsonNamingPolicy.SnakeCaseLower</c> (added
/// in System.Text.Json 7.0). Every serializer in this project used the
/// built-in one until a real Revit-machine run hit
/// <c>MissingMethodException: Method not found:
/// 'System.Text.Json.JsonNamingPolicy System.Text.Json.JsonNamingPolicy.
/// get_SnakeCaseLower()'</c> - Revit's process resolves a System.Text.Json
/// assembly older than the one this project builds against (Core targets
/// 8.0.5), and .NET Framework add-in hosting gives us no way to force our
/// own copy to win. This class only calls
/// <see cref="JsonNamingPolicy.ConvertName"/>'s abstract contract, present
/// since System.Text.Json's very first release, so it works regardless of
/// which physical assembly version actually loads at runtime.
/// </summary>
/// <remarks>
/// Must keep producing the exact same output the built-in policy already
/// produced for every property name currently in this project's schemas -
/// real files on disk (the committed sample mapping JSON, any capture
/// someone has saved) were serialized with the real
/// <c>JsonNamingPolicy.SnakeCaseLower</c> and have to keep loading
/// correctly. The two-pass regex below is the standard, widely-used
/// PascalCase/camelCase -&gt; snake_case algorithm (the same approach
/// Newtonsoft.Json's own SnakeCaseNamingStrategy uses) and matches the
/// built-in policy for every name this project actually has - single
/// words and simple compounds (KeyParameterName, ToleranceMm,
/// CaseInsensitive, ...), no runs of acronym letters that would need the
/// built-in policy's more elaborate word-boundary handling.
/// </remarks>
public sealed class SnakeCaseLowerNamingPolicy : JsonNamingPolicy
{
    public static readonly SnakeCaseLowerNamingPolicy Instance = new();

    // acronym run followed by a new word, e.g. "HTTPServer" -> "HTTP_Server"
    private static readonly Regex AcronymBoundary = new("([A-Z]+)([A-Z][a-z])", RegexOptions.Compiled);

    // lower/digit -> upper transition, e.g. "keyParameter" -> "key_Parameter"
    private static readonly Regex WordBoundary = new("([a-z0-9])([A-Z])", RegexOptions.Compiled);

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var withAcronymBoundaries = AcronymBoundary.Replace(name, "$1_$2");
        var withAllBoundaries = WordBoundary.Replace(withAcronymBoundaries, "$1_$2");
        return withAllBoundaries.ToLowerInvariant();
    }
}
