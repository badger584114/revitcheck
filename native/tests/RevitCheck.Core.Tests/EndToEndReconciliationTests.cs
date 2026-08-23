using RevitCheck.Core.Capture;
using RevitCheck.Core.Checks;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Mapping;
using Xunit;

namespace RevitCheck.Core.Tests;

/// <summary>
/// Ties CaptureSerializer, CsvReader, ParameterMappingSerializer and
/// MetadataReconciliationCheck together from their actual JSON/CSV text
/// forms - the isolated unit tests elsewhere cover each piece against
/// in-memory objects; this proves they compose the way a real run (capture
/// file + CSV file + hand-edited mapping file) actually would.
/// </summary>
public class EndToEndReconciliationTests
{
    private const string CaptureJson = """
    {
      "doc_title": "SAMPLE-BRIDGE",
      "schema_version": 1,
      "elements": [
        {
          "element_id": 1001, "unique_id": "guid-1001",
          "category": "Structural Framing", "family_name": "PC_I_Beam", "key_value": "A1",
          "parameters": {
            "Asset_ID": { "storage_type": "string", "raw_string": "A1" },
            "Depth": { "storage_type": "double", "numeric_value": 450.0, "is_length": true },
            "Owner": { "storage_type": "string", "raw_string": "Roads Authority" }
          }
        },
        {
          "element_id": 1002, "unique_id": "guid-1002",
          "category": "Structural Framing", "family_name": "PC_Super_T_Girder", "key_value": "A2",
          "parameters": {
            "Asset_ID": { "storage_type": "string", "raw_string": "A2" },
            "Girder_Depth": { "storage_type": "double", "numeric_value": 598.0, "is_length": true },
            "Owner": { "storage_type": "string", "raw_string": "" }
          }
        }
      ]
    }
    """;

    private const string Csv = "Asset_ID,Girder Depth (mm),Owner\nA1,450,Roads Authority\nA2,600,Roads Authority\n";

    private const string MappingJson = """
    {
      "schema_version": 1,
      "key_parameter_name": "Asset_ID",
      "key_csv_column": "Asset_ID",
      "fields": {
        "girder_depth_mm": {
          "comparison": "numeric",
          "tolerance_mm": 5.0,
          "csv_column": "Girder Depth (mm)",
          "default_parameter": "Depth",
          "overrides": [
            { "match": { "family_name": "PC_Super_T_Girder" }, "parameter": "Girder_Depth" }
          ]
        },
        "owner": { "comparison": "exact_string", "default_parameter": "Owner" }
      }
    }
    """;

    [Fact]
    public void RealFileShapedInputs_ProduceExpectedFindings()
    {
        var model = CaptureSerializer.Loads(CaptureJson);
        var csv = CsvReader.ReadText(Csv);
        var mapping = ParameterMappingSerializer.Loads(MappingJson);

        var issues = MetadataReconciliationCheck.Run(model, mapping, csv, new ReconciliationConfig());

        // A1 (PC_I_Beam, resolves girder_depth_mm via the default "Depth"):
        // 450 vs 450, within tolerance - no issue. Owner matches - no issue.
        //
        // A2 (PC_Super_T_Girder, resolves girder_depth_mm via the family
        // override "Girder_Depth"): 598 vs 600, within the 5mm tolerance -
        // no issue. Owner is blank on the model while the CSV has data -
        // the "incorrectly filled" mismatch case.
        var issue = Assert.Single(issues);
        Assert.Equal(1002, issue.ElementId);
        Assert.Equal("metadata", issue.Category);
        Assert.Contains("owner: model value is blank", issue.Description);
        Assert.Contains("Roads Authority", issue.Description);
    }
}
