using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace OneDiff.Tests;

/// <summary>
/// Keeps a code block in a Markdown page equal to code that compiles and runs in a test: the lines between
/// <c>// --- begin snippet NAME</c> and <c>// --- end snippet NAME</c> in the calling test's source must appear, in order and ignoring
/// indentation, comments and runs of spaces, in one of the page's <c>csharp</c> blocks.
/// </summary>
internal static partial class DocSnippet
{
    public static void AssertInPage(string pageFromRepoRoot, string name, [CallerFilePath] string testSource = "")
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testSource)!, "..", ".."));
        string[] wanted = Code(Between(File.ReadAllText(testSource), name));
        Assert.NotEmpty(wanted);
        string page = File.ReadAllText(Path.Combine(root, pageFromRepoRoot));
        foreach (Match block in Blocks().Matches(page))
        {
            string[] have = Code(block.Groups[1].Value);
            for (int i = 0; i + wanted.Length <= have.Length; i++)
            {
                if (have.AsSpan(i, wanted.Length).SequenceEqual(wanted))
                {
                    return;
                }
            }
        }

        Assert.Fail($"snippet '{name}' is not in a csharp block of {pageFromRepoRoot}:\n{string.Join('\n', wanted)}");
    }

    private static string Between(string source, string name)
    {
        string begin = "// --- begin snippet " + name, end = "// --- end snippet " + name;
        int a = source.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        int b = source.IndexOf(end, StringComparison.Ordinal);
        Assert.True(a >= begin.Length && b > a, "snippet markers for " + name);
        return source[a..b];
    }

    private static string[] Code(string text) => text.Split('\n')
        .Select(l => Spaces().Replace(Comment().Replace(l, string.Empty).Trim(), " "))
        .Where(l => l.Length > 0)
        .ToArray();

    [GeneratedRegex("```csharp\\r?\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"\s//.*$")]
    private static partial Regex Comment();

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();
}
