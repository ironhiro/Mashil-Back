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

    private static ImportRun Run(long id, ImportTrigger trigger, ImportRunStatus status, long size, int rows, int sourceId = 1) => new()
    {
        Id = id, SourceId = sourceId, Trigger = trigger, Status = status, FileSha256 = size > 0 ? "h" + id : "",
        NormalizerVersion = "0", FileSizeBytes = size, TotalRows = rows,
    };

    [Fact]
    public void Baseline_skips_renormalize_run_and_uses_last_file_import()
    {
        // #13: 매핑 규칙 추가 → 1곳짜리 재정규화 실행이 최신이 되어도 기준은 직전 파일 임포트
        var runs = new[]
        {
            Run(19, ImportTrigger.Scheduled, ImportRunStatus.Succeeded, 25_000_000, 53_580),
            Run(20, ImportTrigger.Renormalize, ImportRunStatus.Succeeded, 0, 1),
        };
        var last = ImportRunner.LastAppliedQuery(runs.AsQueryable(), 1).FirstOrDefault();
        Assert.Equal(19, last?.Id);
    }

    [Fact]
    public void Baseline_ignores_failed_rejected_skipped_and_other_sources()
    {
        var runs = new[]
        {
            Run(1, ImportTrigger.Manual, ImportRunStatus.PendingApproval, 1000, 100),
            Run(2, ImportTrigger.Upload, ImportRunStatus.Rejected, 10, 3),
            Run(3, ImportTrigger.Scheduled, ImportRunStatus.Failed, 0, 0),
            Run(4, ImportTrigger.Scheduled, ImportRunStatus.Skipped, 1000, 0),
            Run(5, ImportTrigger.Upload, ImportRunStatus.Succeeded, 2000, 200, sourceId: 2),
            Run(6, ImportTrigger.Renormalize, ImportRunStatus.Succeeded, 0, 100),
        };
        Assert.Equal(1, ImportRunner.LastAppliedQuery(runs.AsQueryable(), 1).FirstOrDefault()?.Id);
        Assert.Null(ImportRunner.LastAppliedQuery(runs.Where(r => r.Id >= 2).AsQueryable(), 1).FirstOrDefault());
    }

    [Fact]
    public void Tiny_upload_after_renormalize_is_rejected_against_file_baseline()
    {
        var prev = File(100);
        var runs = new[]
        {
            Run(1, ImportTrigger.Manual, ImportRunStatus.Succeeded, prev.Bytes.LongLength, 100),
            Run(2, ImportTrigger.Renormalize, ImportRunStatus.Succeeded, 0, 1),
        };
        var last = ImportRunner.LastAppliedQuery(runs.AsQueryable(), 1).FirstOrDefault();
        var v = ImportPreValidator.Validate(File(3), Adapter, last, ImportPolicy.Default);
        Assert.False(v.Accepted);
    }

    [Fact]
    public void Missing_header_and_empty_file_are_rejected()
    {
        Assert.False(ImportPreValidator.Validate(TestCsv.File("a,b,c", "1,2,3"), Adapter, null, ImportPolicy.Default).Accepted);
        Assert.False(ImportPreValidator.Validate(TestCsv.File(TestCsv.Header), Adapter, null, ImportPolicy.Default).Accepted);
    }
}
