using RevitCheck.Core.Csv;
using Xunit;

namespace RevitCheck.Core.Tests;

public class CsvReaderTests
{
    [Fact]
    public void ReadsHeadersAndRows()
    {
        var csv = "Asset ID,Girder Depth (mm),Owner\nASSET-001,450,Roads Authority\nASSET-002,600,Roads Authority\n";

        var table = CsvReader.ReadText(csv);

        Assert.Equal(new[] { "Asset ID", "Girder Depth (mm)", "Owner" }, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("450", table.Rows[0]["Girder Depth (mm)"]);
    }

    [Fact]
    public void HandlesQuotedFieldsWithEmbeddedCommas()
    {
        var csv = "Asset ID,Owner\nASSET-001,\"Roads, Bridges and Tunnels Authority\"\n";

        var table = CsvReader.ReadText(csv);

        Assert.Equal("Roads, Bridges and Tunnels Authority", table.Rows[0]["Owner"]);
    }

    [Fact]
    public void RowsForKey_FindsMatchingRow()
    {
        var csv = "Asset ID,Owner\nASSET-001,A\nASSET-002,B\n";
        var table = CsvReader.ReadText(csv);

        var matches = table.RowsForKey("Asset ID", "ASSET-002");

        Assert.Single(matches);
        Assert.Equal("B", matches[0]["Owner"]);
    }

    [Fact]
    public void RowsForKey_ReturnsAllMatches_WhenKeyIsDuplicated()
    {
        var csv = "Asset ID,Owner\nASSET-001,A\nASSET-001,B\n";
        var table = CsvReader.ReadText(csv);

        var matches = table.RowsForKey("Asset ID", "ASSET-001");

        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void RowsForKey_ReturnsEmpty_WhenNoMatch()
    {
        var csv = "Asset ID,Owner\nASSET-001,A\n";
        var table = CsvReader.ReadText(csv);

        Assert.Empty(table.RowsForKey("Asset ID", "ASSET-999"));
    }

    [Fact]
    public void ColumnLookup_IsCaseInsensitive()
    {
        // Regression test: a mapping file's csv_column (or its default, the
        // field's own conventionally-lowercase key) commonly differs in
        // case from the sheet's real header - this must not silently drop
        // the column as unresolvable. Caught by EndToEndReconciliationTests
        // during development; locked in here directly against CsvReader too.
        var csv = "Asset ID,Owner\nASSET-001,Roads Authority\n";
        var table = CsvReader.ReadText(csv);

        var row = table.RowsForKey("asset id", "ASSET-001").Single();

        Assert.Equal("Roads Authority", row["owner"]);
    }
}
