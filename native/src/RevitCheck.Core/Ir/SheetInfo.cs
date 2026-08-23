namespace RevitCheck.Core.Ir;

public sealed class SheetInfo
{
    public required long ElementId { get; init; }
    public required string SheetNumber { get; init; }
    public string? Name { get; init; }

    /// <summary>Revit's Element.UniqueId for the sheet. Used as the BCF anchor for dimension/view findings instead of the dimension's/view's own unique_id - a real Forma import confirmed those "may not match the current model," where a sheet is exactly what a document-coordination platform navigates to directly.</summary>
    public string? UniqueId { get; init; }
}
