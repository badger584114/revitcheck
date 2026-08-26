namespace RevitCheck.Core.Checks;

/// <summary>Plain trig helpers for comparing a reconstructed chain direction against a printed bearing call - no Revit/IR types, deliberately generic.</summary>
public static class BearingMath
{
    /// <summary>
    /// The compass bearing (degrees, 0-360, measured clockwise from north)
    /// from (eastingFrom, northingFrom) to (eastingTo, northingTo). Real
    /// data confirms this project's printed convention is a plain 0-360
    /// azimuth (e.g. "165° 07' 01""), not a quadrant bearing
    /// (e.g. "S15°E") - <c>Math.Atan2(dE, dN)</c> gives exactly that
    /// directly, with north (dE=0, dN&gt;0) at 0°, east at 90°.
    /// </summary>
    public static double AzimuthDegrees(double eastingFrom, double northingFrom, double eastingTo, double northingTo)
    {
        var dE = eastingTo - eastingFrom;
        var dN = northingTo - northingFrom;
        var azimuth = Math.Atan2(dE, dN) * (180.0 / Math.PI);
        return Normalize(azimuth);
    }

    /// <summary>The bearing describing the exact same physical line, walked the other direction - a chain's own walk-order (which endpoint it started from) is arbitrary, so a reconstructed bearing and a printed one can legitimately differ by exactly this and still describe the same line.</summary>
    public static double Reciprocal(double bearingDegrees) => Normalize(bearingDegrees + 180.0);

    /// <summary>The smaller of the two ways around the compass between two bearings (0-180°) - never the "long way around", so a comparison near the 0°/360° wrap doesn't falsely read as a huge mismatch.</summary>
    public static double AngularDifference(double aDegrees, double bDegrees)
    {
        var diff = Math.Abs(Normalize(aDegrees) - Normalize(bDegrees)) % 360.0;
        return diff > 180.0 ? 360.0 - diff : diff;
    }

    private static double Normalize(double degrees)
    {
        var result = degrees % 360.0;
        return result < 0 ? result + 360.0 : result;
    }
}
