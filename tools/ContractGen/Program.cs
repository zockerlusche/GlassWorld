using GlassWorld.Tools.ContractGen;

// Usage: dotnet run --project tools/ContractGen [-- --check]
//   (no argument) regenerates src/Contracts/Generated from contracts/schemas
//   --check        fails (exit 1) if the checked-in files differ from a fresh generation
var check = args.Contains("--check", StringComparer.Ordinal);
var root = RepoRoot.Find();
var schemaDirectory = Path.Combine(root, "contracts", "schemas");
var outputDirectory = Path.Combine(root, "src", "Contracts", "Generated");

var files = await Generator.GenerateAsync(schemaDirectory).ConfigureAwait(false);
var expected = files.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
var existing = Directory.Exists(outputDirectory)
    ? Directory.GetFiles(outputDirectory, "*.g.cs").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal)
    : [];

if (check)
{
    var problems = new List<string>();
    foreach (var (name, content) in files)
    {
        var path = Path.Combine(outputDirectory, name);
        if (!File.Exists(path))
        {
            problems.Add($"missing: {name}");
        }
        else if (File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) != content)
        {
            problems.Add($"outdated: {name}");
        }
    }

    problems.AddRange(existing.Except(expected).Select(name => $"stale: {name}"));
    if (problems.Count > 0)
    {
        Console.Error.WriteLine("Generated contract types are not up to date:");
        problems.ForEach(p => Console.Error.WriteLine("  " + p));
        Console.Error.WriteLine("Run: dotnet run --project tools/ContractGen");
        return 1;
    }

    Console.WriteLine($"Contract types up to date ({files.Count} files).");
    return 0;
}

Directory.CreateDirectory(outputDirectory);
foreach (var stale in existing.Except(expected))
{
    File.Delete(Path.Combine(outputDirectory, stale));
}

foreach (var (name, content) in files)
{
    File.WriteAllText(Path.Combine(outputDirectory, name), content);
}

Console.WriteLine($"Generated {files.Count} files into {outputDirectory}.");
return 0;
