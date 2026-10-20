using FileOrbit.Core.Planning;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

public sealed class PathAndPlanningTests
{
    [Theory]
    [InlineData(@"relative\file.txt")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\folder\file.txt:secret")]
    [InlineData(@"C:\folder\CON\x.txt")]
    [InlineData(@"C:\folder\nul.txt")]
    [InlineData("")]
    public void Unsafe_input_paths_are_rejected(string path) =>
        Assert.Throws<UnsafePathException>(() => PathSafety.NormalizeInputPath(path));

    [Fact]
    public void Paths_with_spaces_unicode_and_quotes_are_normalised()
    {
        using var dir = new TestDirectory();
        var path = dir.Write("Rapport financier – été 2024 (final) #1 & co.pdf", "x");
        Assert.Equal(path, PathSafety.ValidateInput("\"" + path + "\"", allowDirectory: false));
    }

    [Fact]
    public void Missing_input_is_reported()
    {
        using var dir = new TestDirectory();
        Assert.Throws<UnsafePathException>(() => PathSafety.ValidateInput(dir.File("gone.pdf"), allowDirectory: false));
    }

    [Fact]
    public void Windows_and_program_files_are_not_valid_destinations()
    {
        Assert.Throws<UnsafePathException>(() => PathSafety.ValidateOutputDirectory(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.Throws<UnsafePathException>(() => PathSafety.ValidateOutputDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vendor")));
    }

    [Theory]
    [InlineData("report:v2?.pdf", "report_v2_.pdf")]
    [InlineData("CON", "_CON")]
    [InlineData("  name. ", "name")]
    [InlineData("...", "output")]
    [InlineData("a/b\\c", "a_b_c")]
    public void File_names_are_sanitised(string input, string expected) =>
        Assert.Equal(expected, PathSafety.SanitizeFileName(input));

    [Theory]
    [InlineData("docs/readme.txt", true)]
    [InlineData("../evil.txt", false)]
    [InlineData("docs/../../evil.txt", false)]
    [InlineData("/etc/passwd", true)] // Leading slashes are stripped and the entry stays inside.
    [InlineData("C:/Windows/evil.dll", false)]
    [InlineData("docs/con/x.txt", false)]
    public void Archive_entries_cannot_escape_the_destination(string entry, bool allowed)
    {
        using var dir = new TestDirectory();
        if (allowed)
        {
            Assert.StartsWith(dir.Path, PathSafety.ResolveArchiveEntry(dir.Path, entry));
        }
        else
        {
            Assert.Throws<UnsafePathException>(() => PathSafety.ResolveArchiveEntry(dir.Path, entry));
        }
    }

    [Fact]
    public void Collision_names_are_numbered()
    {
        using var dir = new TestDirectory();
        dir.Write("report.pdf", "1");
        dir.Write("report (1).pdf", "2");

        Assert.Equal(dir.File("report (2).pdf"), OutputPlanner.GetAvailablePath(dir.Path, "report", ".pdf"));
        Assert.Equal(dir.File("other.pdf"), OutputPlanner.GetAvailablePath(dir.Path, "other", ".pdf"));
    }

    [Fact]
    public void Collision_handles_compound_extensions_and_folders()
    {
        using var dir = new TestDirectory();
        dir.Write("backup.tar.gz", "x");
        dir.SubDirectory("photos");

        Assert.Equal(dir.File("backup (1).tar.gz"), OutputPlanner.GetAvailablePath(dir.Path, "backup", ".tar.gz"));
        Assert.Equal(dir.File("photos (1)"), OutputPlanner.GetAvailablePath(dir.Path, "photos", string.Empty));
    }

    [Fact]
    public void Collision_policies_never_overwrite_the_source()
    {
        using var dir = new TestDirectory();
        var source = dir.Write("data.csv", "x");
        var existing = dir.Write("data.json", "{}");

        Assert.Equal(existing, OutputPlanner.ResolveCollision(dir.Path, "data", ".json", CollisionPolicy.Replace, source));
        Assert.Null(OutputPlanner.ResolveCollision(dir.Path, "data", ".json", CollisionPolicy.Skip, source));
        Assert.Equal(dir.File("data (1).json"), OutputPlanner.ResolveCollision(dir.Path, "data", ".json", CollisionPolicy.KeepBoth, source));

        // Even "Replace" picks a new name when the target is the source itself.
        Assert.Equal(dir.File("data (1).csv"), OutputPlanner.ResolveCollision(dir.Path, "data", ".csv", CollisionPolicy.Replace, source));
    }
}
