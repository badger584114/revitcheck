namespace RevitCheck.Core.Ir;

/// <summary>A point in project coordinates, millimetres.</summary>
public sealed class Point3D
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Z { get; init; }
}
