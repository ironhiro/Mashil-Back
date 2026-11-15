using Mashil.Domain;
using Mashil.Import.LocalData;

namespace Mashil.Import.Tests;

public class ImportPreValidatorTests
{
    private static readonly ISourceAdapter Adapter = new LocalDataPublicRestroomAdapter();

    private static string[] Rows(int n) =>
        Enumerable.Range(0, n).Select(i => TestCsv.Row2.Replace("202530000000100823", $"9{i:D17}")).ToArray();

    private static SourceFile File(int rows, params string[] extra) =>
        TestCsv.File([TestCsv.Header, .. Rows(rows), .. extra]);

    private static ImportRun Last(SourceFile f, int rows) => new()
    {
        FileSha256 = "x", NormalizerVersion = "0", FileSizeBytes = f.Bytes.LongLength, TotalRows = rows,
    };

    [Fact]
    public void First_run_without_history_is_accepted()
    {
        var v = ImportPreValidator.Validate(File(10), Adapter, null, ImportPolicy.Default);
        Assert.True(v.Accepted);
        Assert.Equal(10, v.Rows);
    }

    [Fact]
    public void Row_drop_over_5_percent_is_rejected()
    {
        var prev = File(100);
        var v = ImportPreValidator.Validate(File(90), Adapter, Last(prev, 100), ImportPolicy.Default with { MaxSizeChangeRatio = 1 });
        Assert.False(v.Accepted);
        Assert.Contains("행 수 감소", v.Reason);
    }

    [Fact]
    public void Small_drop_within_limit_is_accepted()
    {
        var prev = File(100);
        var v = ImportPreValidator.Validate(File(96), Adapter, Last(prev, 100), ImportPolicy.Default);
        Assert.True(v.Accepted, v.Reason);
    }

    [Fact]
    public void Size_change_over_30_percent_is_rejected()
    {
        var prev = File(100);
        var v = ImportPreValidator.Validate(File(140), Adapter, Last(prev, 100), ImportPolicy.Default);
        Assert.False(v.Accepted);
        Assert.Contains("파일 크기", v.Reason);
    }

    [Fact]
    public void SkipComparison_ignores_size_and_row_drop()
    {
        var prev = File(100);
        var v = ImportPreValidator.Validate(File(10), Adapter, Last(prev, 100), ImportPolicy.Default, skipComparison: true);
        Assert.True(v.Accepted);
    }

    [Fact]
    public void Too_many_broken_rows_is_rejected_even_with_skip_comparison()
    {
        var v = ImportPreValidator.Validate(File(10, "1,2,3"), Adapter, null, ImportPolicy.Default, skipComparison: true);
        Assert.False(v.Accepted);
        Assert.Contains("손상 행", v.Reason);
    }

    [Fact]
    public void Missing_header_and_empty_file_are_rejected()
    {
        Assert.False(ImportPreValidator.Validate(TestCsv.File("a,b,c", "1,2,3"), Adapter, null, ImportPolicy.Default).Accepted);
        Assert.False(ImportPreValidator.Validate(TestCsv.File(TestCsv.Header), Adapter, null, ImportPolicy.Default).Accepted);
    }
}
