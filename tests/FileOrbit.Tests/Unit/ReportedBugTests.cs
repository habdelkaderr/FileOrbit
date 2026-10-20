using ClosedXML.Excel;
using FileOrbit.App;
using FileOrbit.Core.Jobs;
using FileOrbit.Engines.Office;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Unit;

/// <summary>Regression tests for problems reported from real use.</summary>
public sealed class ReportedBugTests
{
    [Fact]
    public async Task Pdf_with_zero_pages_is_a_failure_not_a_success()
    {
        var engine = new FakeEngine("emptypdf").Supports("csv", "pdf");
        engine.Behavior = (context, _) =>
        {
            // A structurally valid PDF with no pages, like Office produces for an empty workbook.
            File.WriteAllBytes(context.PrimaryOutputPath, ZeroPagePdf());
            return Task.CompletedTask;
        };
        var pipeline = new Pipeline([engine]);
        using var dir = new TestDirectory();

        var job = pipeline.CreateJob(dir.Write("data.csv", "a\n1\n"), "convert:pdf");
        await pipeline.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("no pages", job.Message);
        Assert.False(File.Exists(dir.File("data.pdf")));
    }

    private static byte[] ZeroPagePdf()
    {
        string[] objects =
        [
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n",
        ];
        var body = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (var obj in objects)
        {
            offsets.Add(body.Length);
            body.Append(obj);
        }

        var xref = body.Length;
        body.Append("xref\n0 3\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            body.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        body.Append($"trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(body.ToString());
    }

    [Fact]
    public void Empty_file_is_explained_instead_of_offering_only_compression()
    {
        using var dir = new TestDirectory();
        var empty = dir.Write("New Microsoft Word Document.docx", Array.Empty<byte>());
        var real = SampleFiles.Docx(dir);
        using var services = new AppServices(dir.SubDirectory("appdata"));

        var (files, problems) = services.Detect([empty, real]);

        Assert.Equal("docx", Assert.Single(files).Format.Id);
        Assert.Contains("is empty (0 bytes)", Assert.Single(problems));
    }

    [Fact]
    [Trait("Category", "Office")]
    public async Task Empty_workbook_to_pdf_fails_with_a_clear_message()
    {
        using var dir = new TestDirectory();
        var path = dir.File("New Microsoft Excel Worksheet.xlsx");
        using (var workbook = new XLWorkbook())
        {
            workbook.Worksheets.Add("Sheet1");
            workbook.SaveAs(path);
        }

        var job = Pipeline.Real.CreateJob(path, "convert:pdf", engineId: OfficeEngine.EngineId);
        if (!job.Engine.Status.IsAvailable)
        {
            Assert.Skip("Microsoft Excel is not installed.");
        }

        await Pipeline.Real.Runner.RunAsync(job, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("nothing to print", job.Message);
        Assert.False(File.Exists(dir.File("New Microsoft Excel Worksheet.pdf")));
    }
}
