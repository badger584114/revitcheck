using RevitCheck.Core.Capture;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Mapping;
using RevitCheck.MappingBuilder;

// Off-machine tool, no Revit involved - the direct analogue of
// scripts/check_capture.py's role on the Python side.
if (args.Length < 4)
{
    Console.Error.WriteLine(
        "Usage: RevitCheck.MappingBuilder <capture.json> <reference.csv> <key-parameter-name> " +
        "<output-mapping.json> [key-csv-column]");
    Console.Error.WriteLine(
        "  key-csv-column defaults to key-parameter-name - pass it explicitly whenever the CSV's " +
        "key header differs from the Revit parameter name (e.g. 'Asset Identifier (Label)' vs " +
        "ATM_Asset_Identifier).");
    return 1;
}

var capturePath = args[0];
var csvPath = args[1];
var keyParameterName = args[2];
var outputPath = args[3];
var keyCsvColumn = args.Length > 4 ? args[4] : null;

var model = CaptureSerializer.Load(capturePath);
var csv = CsvReader.ReadFile(csvPath);

var result = MappingAutoBuilder.Build(model, csv, keyParameterName, keyCsvColumn);
ParameterMappingSerializer.Save(result.Mapping, outputPath);

Console.WriteLine($"Wrote starter mapping to {outputPath} - UNREVIEWED, confirm against real data before use.");
Console.WriteLine();
foreach (var line in result.Diagnostics)
{
    Console.WriteLine(line);
}

return 0;
