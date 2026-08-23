using RevitCheck.Core.Capture;
using RevitCheck.Core.Csv;
using RevitCheck.Core.Mapping;
using RevitCheck.MappingBuilder;

// Off-machine tool, no Revit involved - the direct analogue of
// scripts/check_capture.py's role on the Python side.
if (args.Length < 4)
{
    Console.Error.WriteLine(
        "Usage: RevitCheck.MappingBuilder <capture.json> <reference.csv> <key-parameter-name> <output-mapping.json>");
    return 1;
}

var capturePath = args[0];
var csvPath = args[1];
var keyParameterName = args[2];
var outputPath = args[3];

var model = CaptureSerializer.Load(capturePath);
var csv = CsvReader.ReadFile(csvPath);

var result = MappingAutoBuilder.Build(model, csv, keyParameterName);
ParameterMappingSerializer.Save(result.Mapping, outputPath);

Console.WriteLine($"Wrote starter mapping to {outputPath} - UNREVIEWED, confirm against real data before use.");
Console.WriteLine();
foreach (var line in result.Diagnostics)
{
    Console.WriteLine(line);
}

return 0;
