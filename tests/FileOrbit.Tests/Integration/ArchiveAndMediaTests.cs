using System.IO.Compression;
using FileOrbit.Core.Jobs;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Integration;

public sealed class ArchiveAndMediaTests
{
    private static readonly Pipeline P = Pipeline.Real;

    [Fact]
    public async Task Zip_extracts_into_new_folder_preserving_structure()
    {
        using var dir = new TestDirectory();
        var zip = SampleFiles.Zip(dir);

        var job = await P.ConvertAsync(zip, "extract:folder", engineId: "dotnet-archive");

        Assert.Equal(dir.File("bundle"), job.PrimaryOutput);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(job.PrimaryOutput!, "docs", "readme.txt"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(job.PrimaryOutput!, "data", "values.csv")));
    }

    [Fact]
    public async Task Extracting_twice_never_merges_into_existing_folder()
    {
        using var dir = new TestDirectory();
        var zip = SampleFiles.Zip(dir);

        var first = await P.ConvertAsync(zip, "extract:folder", engineId: "dotnet-archive");
        var second = await P.ConvertAsync(zip, "extract:folder", engineId: "dotnet-archive");

        Assert.NotEqual(first.PrimaryOutput, second.PrimaryOutput);
        Assert.EndsWith("bundle (1)", second.PrimaryOutput);
    }

    [Fact]
    public async Task Zip_slip_entry_is_rejected_and_nothing_is_written_outside()
    {
        using var dir = new TestDirectory();
        var zip = SampleFiles.Zip(dir, "evil.zip", ("ok.txt", "fine"), ("../../escaped.txt", "pwned"));
        var job = P.CreateJob(zip, "extract:folder", engineId: "dotnet-archive");

        await P.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("unsafe path", job.Message);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(dir.Path)!, "escaped.txt")));
        P.AssertNoStagingLeft(dir.Path);
    }

    [Fact]
    public async Task Tar_gz_extracts_and_skips_symlinks()
    {
        using var dir = new TestDirectory();
        var tgz = SampleFiles.TarGz(dir, includeSymlink: true);

        var job = await P.ConvertAsync(tgz, "extract:folder", engineId: "dotnet-archive");

        Assert.Equal("tar content", await File.ReadAllTextAsync(Path.Combine(job.PrimaryOutput!, "notes.txt"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(job.PrimaryOutput!, "link")));
        Assert.Contains(job.Warnings, w => w.Contains("Links"));
    }

    [Fact]
    public async Task Gz_decompresses_single_file_next_to_archive()
    {
        using var dir = new TestDirectory();
        var gz = SampleFiles.Gz(dir);

        var job = await P.ConvertAsync(gz, "extract:folder", engineId: "dotnet-archive");

        Assert.Equal(dir.File("log.txt"), job.PrimaryOutput);
        Assert.Equal("compressed log line\n", await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Folder_compresses_to_zip_and_tgz()
    {
        using var dir = new TestDirectory();
        var folder = dir.SubDirectory("Photos");
        await File.WriteAllTextAsync(Path.Combine(folder, "a.txt"), "A", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await File.WriteAllTextAsync(Path.Combine(folder, "sub", "b.txt"), "B", TestContext.Current.CancellationToken);

        var zip = await P.ConvertAsync(folder, "compress:zip");
        var tgz = await P.ConvertAsync(folder, "compress:tgz");

        P.AssertFormat(zip.PrimaryOutput!, "zip");
        P.AssertFormat(tgz.PrimaryOutput!, "tgz");
        Assert.EndsWith("Photos.tar.gz", tgz.PrimaryOutput);
        using var archive = ZipFile.OpenRead(zip.PrimaryOutput!);
        Assert.Contains(archive.Entries, e => e.FullName == "Photos/sub/b.txt");
    }

    [Fact]
    public async Task Any_file_can_be_compressed()
    {
        using var dir = new TestDirectory();
        var csv = SampleFiles.Csv(dir);

        var job = await P.ConvertAsync(csv, "compress:zip");

        Assert.EndsWith("people.zip", job.PrimaryOutput);
        using var archive = ZipFile.OpenRead(job.PrimaryOutput!);
        Assert.Equal("people.csv", Assert.Single(archive.Entries).FullName);
    }

    [Fact]
    public async Task Zip_repackages_as_tar_gz()
    {
        using var dir = new TestDirectory();
        var zip = SampleFiles.Zip(dir);

        var job = await P.ConvertAsync(zip, "convert:tgz");

        P.AssertFormat(job.PrimaryOutput!, "tgz");
    }

    [Fact]
    public async Task SevenZip_round_trip_when_installed()
    {
        using var dir = new TestDirectory();
        var csv = SampleFiles.Csv(dir);

        var archive = await P.ConvertAsync(csv, "compress:7z");
        P.AssertFormat(archive.PrimaryOutput!, "7z");

        var extracted = await P.ConvertAsync(archive.PrimaryOutput!, "extract:folder");
        Assert.Equal(dir.File("people (1).csv"), extracted.PrimaryOutput);
        Assert.Equal(await File.ReadAllTextAsync(csv, TestContext.Current.CancellationToken), await File.ReadAllTextAsync(extracted.PrimaryOutput!, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("m4a")]
    [InlineData("flac")]
    [InlineData("wma")]
    public async Task Wav_transcodes_with_media_foundation(string target)
    {
        using var dir = new TestDirectory();
        var wav = SampleFiles.Wav(dir);

        var job = await P.ConvertAsync(wav, $"convert:{target}", engineId: "media-foundation");

        P.AssertFormat(job.PrimaryOutput!, target);
    }

    [Fact]
    public async Task Mp3_back_to_wav_with_media_foundation()
    {
        using var dir = new TestDirectory();
        var wav = SampleFiles.Wav(dir);
        var mp3 = await P.ConvertAsync(wav, "convert:mp3", engineId: "media-foundation");

        var back = await P.ConvertAsync(mp3.PrimaryOutput!, "convert:wav", engineId: "media-foundation");

        P.AssertFormat(back.PrimaryOutput!, "wav");
        Assert.EndsWith("tone (1).wav", back.PrimaryOutput);
    }
}
