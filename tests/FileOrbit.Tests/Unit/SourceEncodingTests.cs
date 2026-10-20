using System.Runtime.CompilerServices;
using System.Text;

namespace FileOrbit.Tests.Unit;

/// <summary>
/// Guards against mojibake in user-visible text: UTF-8 punctuation (“ ” – · …) that was once read as
/// Windows-1252 and saved again shows up as "â€œ" or "Â·" in the app.
/// </summary>
public sealed class SourceEncodingTests
{
    private static readonly string[] Garbled = ["â€", "Â·", "Â ", "Ã©", "Ã¨"];

    [Fact]
    public void Source_files_contain_no_garbled_characters()
    {
        var root = Path.GetFullPath(Path.Combine(ThisDirectory(), "..", "..", ".."));
        var problems = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".md") || f.EndsWith(".wxs"))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}") &&
                                 !f.EndsWith(nameof(SourceEncodingTests) + ".cs")))
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Garbled.Any(lines[i].Contains))
                {
                    problems.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
                }
            }
        }

        Assert.True(problems.Count == 0, "Garbled characters (encoding damage) in: " + string.Join(", ", problems));
    }

    private static string ThisDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
