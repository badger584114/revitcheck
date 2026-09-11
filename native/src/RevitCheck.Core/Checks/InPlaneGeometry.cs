using System;
using RevitCheck.Core.Ir;

namespace RevitCheck.Core.Checks;

/// <summary>
/// Distances as a drawing measures them: in the view plane, with depth
/// along the view direction removed.
/// </summary>
/// <remarks>
/// <para>
/// <b>The correction at the centre of PLANNING.md §28.</b> A section or
/// elevation is a flat projection, so what it dimensions is the separation
/// <i>seen</i>, not the true 3D distance between two points that may sit at
/// different depths. Four probe runs compared points in 3D and averaged
/// 21.09mm of error against real dimensions; the same points measured in
/// the view plane average <b>1.44mm</b>. Individually: 93.06 to 0.22,
/// 21.35 to 0.01, 18.05 to 0.01, 5.21 to 0.00.
/// </para>
/// <para>
/// For an <b>elevation</b> this is not a refinement but the only thing that
/// can work: its plane sits outside the geometry, so every candidate point
/// is at some depth behind it and a depth difference between the two ends
/// is guaranteed.
/// </para>
/// </remarks>
public static class InPlaneGeometry
{
    /// <summary>
    /// The distance between two points as seen in a view - or the plain 3D
    /// distance when the view direction is unknown.
    /// </summary>
    /// <remarks>
    /// Falling back to 3D rather than refusing is deliberate: a capture
    /// taken before <see cref="ViewInfo.ViewDirection"/> existed carries
    /// none, and 3D is what every earlier run used. It is the weaker
    /// answer, so a caller that cares must ask
    /// <see cref="CanMeasureInPlane"/> rather than infer it from a number
    /// that looks the same either way.
    /// </remarks>
    public static double DistanceMm(Point3D a, Point3D b, Point3D? viewDirection)
    {
        if (Normalize(viewDirection) is not { } normal)
        {
            return Distance(a, b);
        }

        return Distance(Flatten(a, normal), Flatten(b, normal));
    }

    /// <summary>Whether a real in-plane measurement is possible here - see <see cref="DistanceMm"/>.</summary>
    public static bool CanMeasureInPlane(Point3D? viewDirection) => Normalize(viewDirection) is not null;

    /// <summary>
    /// How far a point sits out of the view plane. Reported in a finding so
    /// a reviewer can see whether the geometry chosen is anywhere near what
    /// the drawing shows.
    /// </summary>
    public static double? DepthMm(Point3D point, Point3D? viewDirection, Point3D? planeOrigin = null)
    {
        if (Normalize(viewDirection) is not { } normal)
        {
            return null;
        }

        var origin = planeOrigin ?? new Point3D { X = 0, Y = 0, Z = 0 };
        return ((point.X - origin.X) * normal.X)
             + ((point.Y - origin.Y) * normal.Y)
             + ((point.Z - origin.Z) * normal.Z);
    }

    /// <summary>
    /// True when a face is seen edge-on in this view - its normal
    /// perpendicular to the view direction, within
    /// <paramref name="toleranceSine"/>.
    /// </summary>
    /// <remarks>
    /// Such a face draws as a line, and that line is what a drafter
    /// dimensions to; a face whose normal points along the view direction
    /// is the background behind the cut, which nobody dimensions to. On
    /// real probe data this excludes roughly 59 of every 70 candidate
    /// faces. A face with no readable normal passes - failing towards
    /// "look at it" costs a candidate a check can reject, while failing the
    /// other way silently discards the right one.
    /// </remarks>
    public static bool IsEdgeOn(Point3D? faceNormal, Point3D? viewDirection, double toleranceSine)
    {
        if (Normalize(faceNormal) is not { } normal || Normalize(viewDirection) is not { } direction)
        {
            return true;
        }

        var alignment = Math.Abs((normal.X * direction.X) + (normal.Y * direction.Y) + (normal.Z * direction.Z));
        return alignment < toleranceSine;
    }

    private static Point3D Flatten(Point3D point, Point3D normal)
    {
        var depth = (point.X * normal.X) + (point.Y * normal.Y) + (point.Z * normal.Z);
        return new Point3D
        {
            X = point.X - (depth * normal.X),
            Y = point.Y - (depth * normal.Y),
            Z = point.Z - (depth * normal.Z),
        };
    }

    private static double Distance(Point3D a, Point3D b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static Point3D? Normalize(Point3D? vector)
    {
        if (vector is not { } v)
        {
            return null;
        }

        var length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y) + (v.Z * v.Z));
        if (length < 1e-9)
        {
            return null;
        }

        return new Point3D { X = v.X / length, Y = v.Y / length, Z = v.Z / length };
    }
}
