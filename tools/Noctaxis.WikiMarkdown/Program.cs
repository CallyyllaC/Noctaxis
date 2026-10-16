using Markdig;
using Markdig.Extensions.AutoIdentifiers;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: Noctaxis.WikiMarkdown <prepared wiki directory>");
    return 1;
}

// Keep the same renderer on Windows PowerShell 5.1 and PowerShell 7.
// Files avoid the legacy shell's native stdout encoding and preserve Unicode.
var pipeline = new MarkdownPipelineBuilder()
    .UsePipeTables()
    .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
    .Build();
foreach (var path in Directory.EnumerateFiles(args[0], "*.md"))
{
    File.WriteAllText(Path.ChangeExtension(path, ".html"),
        Markdown.ToHtml(File.ReadAllText(path), pipeline));
}
return 0;
