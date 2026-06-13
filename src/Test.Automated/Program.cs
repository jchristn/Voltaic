using Test.Shared;
using Touchstone.Cli;

string? resultsPath = null;
HashSet<string>? tags = null;

for (int i = 0; i < args.Length; i++)
{
    string arg = args[i];

    if (arg == "--results" && i + 1 < args.Length)
    {
        resultsPath = args[++i];
        continue;
    }

    if (arg == "--tag" && i + 1 < args.Length)
    {
        tags ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        tags.Add(args[++i]);
        continue;
    }

    if (arg == "--help" || arg == "-h")
    {
        Console.WriteLine("Usage: Test.Automated [--results <path>] [--tag <tag>]");
        return 0;
    }
}

if (!String.IsNullOrWhiteSpace(resultsPath))
{
    string? resultsDirectory = Path.GetDirectoryName(Path.GetFullPath(resultsPath));
    if (!String.IsNullOrEmpty(resultsDirectory))
    {
        Directory.CreateDirectory(resultsDirectory);
    }
}

return await ConsoleRunner.RunAsync(VoltaicSuites.All(tags), resultsPath: resultsPath);
