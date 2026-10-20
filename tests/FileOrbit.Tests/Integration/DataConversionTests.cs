using System.Text.Json;
using ClosedXML.Excel;
using FileOrbit.Tests.Support;

namespace FileOrbit.Tests.Integration;

public sealed class DataConversionTests
{
    private static readonly Pipeline P = Pipeline.Real;

    [Fact]
    public async Task Csv_to_xlsx_infers_numbers_but_keeps_leading_zeros_and_multiline_text()
    {
        using var dir = new TestDirectory();
        var csv = SampleFiles.Csv(dir);

        var job = await P.ConvertAsync(csv, "convert:xlsx");

        P.AssertFormat(job.PrimaryOutput!, "xlsx");
        using var workbook = new XLWorkbook(job.PrimaryOutput!);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("Name", sheet.Cell(1, 1).GetString());
        Assert.Equal(36, sheet.Cell(2, 2).GetDouble());
        Assert.Equal("02139", sheet.Cell(2, 3).GetString());
        Assert.Equal("Likes \"maths\", engines", sheet.Cell(2, 5).GetString());
        Assert.Equal("Line one\nLine two", sheet.Cell(3, 5).GetString());
        Assert.Equal(new DateTime(2024, 1, 15), sheet.Cell(2, 4).GetDateTime());
    }

    [Fact]
    public async Task Xlsx_to_csv_exports_first_sheet_with_formula_values()
    {
        using var dir = new TestDirectory();
        var xlsx = SampleFiles.Xlsx(dir);

        var job = await P.ConvertAsync(xlsx, "convert:csv", engineId: "tabular");

        var csv = await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("Total,1550.5", csv);
        Assert.Contains(job.Warnings, w => w.Contains("2 sheets"));
    }

    [Fact]
    public async Task Xlsx_all_sheets_to_csv_creates_folder()
    {
        using var dir = new TestDirectory();
        var xlsx = SampleFiles.Xlsx(dir);

        var job = await P.ConvertAsync(xlsx, "convert:csv", new Dictionary<string, string> { ["sheets"] = "all" }, engineId: "tabular");

        Assert.True(Directory.Exists(job.PrimaryOutput));
        Assert.Equal(2, Directory.GetFiles(job.PrimaryOutput!, "*.csv").Length);
    }

    [Fact]
    public async Task Xlsx_to_json_uses_header_row_and_numbers()
    {
        using var dir = new TestDirectory();
        var xlsx = SampleFiles.Xlsx(dir);

        var job = await P.ConvertAsync(xlsx, "convert:json");

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken));
        var first = json.RootElement[0];
        Assert.Equal("Rent", first.GetProperty("Item").GetString());
        Assert.Equal(1200, first.GetProperty("Cost").GetDouble());
    }

    [Fact]
    public async Task Json_to_csv_unions_columns_and_keeps_nested_values_as_json()
    {
        using var dir = new TestDirectory();
        var json = SampleFiles.Json(dir);

        var job = await P.ConvertAsync(json, "convert:csv");

        var lines = (await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("id,name,tags,price,inStock", lines[0]);
        Assert.Equal(0xEF, (await File.ReadAllBytesAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken))[0]); // UTF-8 BOM so Excel detects the encoding
        Assert.Contains("\"[\"\"a\"\",\"\"b\"\"]\"", lines[1]);
        Assert.EndsWith("TRUE", lines[2]);
    }

    [Fact]
    public async Task Csv_formula_cells_are_neutralised_on_csv_export()
    {
        using var dir = new TestDirectory();
        var csv = SampleFiles.Csv(dir);

        var job = await P.ConvertAsync(csv, "convert:tsv");

        var tsv = await File.ReadAllTextAsync(job.PrimaryOutput!, TestContext.Current.CancellationToken);
        Assert.Contains("'=1+2", tsv);
    }

    [Fact]
    public async Task Tsv_to_csv_and_html()
    {
        using var dir = new TestDirectory();
        var tsv = SampleFiles.Tsv(dir);

        var csv = await P.ConvertAsync(tsv, "convert:csv");
        var html = await P.ConvertAsync(tsv, "convert:html");

        Assert.Equal("a,b,c\r\n1,2,3\r\n4,5,6\r\n", await File.ReadAllTextAsync(csv.PrimaryOutput!, TestContext.Current.CancellationToken));
        Assert.Contains("<th>a</th>", await File.ReadAllTextAsync(html.PrimaryOutput!, TestContext.Current.CancellationToken));
    }
}
