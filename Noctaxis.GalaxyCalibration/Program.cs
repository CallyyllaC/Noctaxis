using System.Text.Json;
using Noctaxis.Desktop.Services;

if (args.Length == 3 && string.Equals(args[0], "--render-only", StringComparison.Ordinal))
{
    try
    {
        var hash = new SettlementGalaxyCalibrationHarness().RenderSelected(args[1], args[2]);
        Console.WriteLine(hash);
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        return 1;
    }
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Noctaxis.GalaxyCalibration <saved-location-directory> <original-reference-frames-directory> <output-directory>");
    Console.Error.WriteLine("   or: Noctaxis.GalaxyCalibration --render-only <saved-location-directory> <output-directory>");
    return 2;
}

try
{
    var result = new SettlementGalaxyCalibrationHarness().Run(args[0], args[1], args[2]);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
