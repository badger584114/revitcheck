using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Parses a printed bearing call's degrees-minutes-seconds text into
/// decimal degrees - real confirmed format on this project (PLANNING.md
/// §14, InspectPileSetout.pushbutton's real run): <c>165° 07' 01"</c>, a
/// literal degree/minute/second symbol, no label, sometimes trailing
/// whitespace/CR. Matches the value pattern directly rather than
/// depending on finding a "BEARING" label first first - same reasoning
/// the archived PDF/DWG pipeline's own `_BEARING_DMS_RE` used
/// (ARCHIVE-pdf-dwg.md).
/// </summary>
public static class BearingText
{
    // Degrees: 1-3 digits (0-360). Minutes/seconds: 1-2 digits, seconds may
    // carry a decimal fraction. Quote/prime characters accept both the
    // straight ASCII forms and the real curly ones seen in Revit's own
    // TextNote export (’/”), not just one or the other.
    private static readonly Regex Pattern = new(
        @"(?<deg>\d{1,3})\s*°\s*(?<min>\d{1,2})\s*['’′]\s*(?<sec>\d{1,2}(?:\.\d+)?)\s*[""”″]",
        RegexOptions.Compiled);

    /// <summary>The first DMS-shaped bearing found in <paramref name="text"/>, as decimal degrees, or null if none matches. Does not validate range (e.g. minutes &lt; 60) - a malformed real value should surface as an obviously-wrong comparison downstream, not disappear as a silent null.</summary>
    public static double? TryParseDegrees(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var deg = double.Parse(match.Groups["deg"].Value, CultureInfo.InvariantCulture);
        var min = double.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture);
        var sec = double.Parse(match.Groups["sec"].Value, CultureInfo.InvariantCulture);
        return deg + (min / 60.0) + (sec / 3600.0);
    }
}
