using Mashil.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.IntegrationTests;

public class ImportRunnerDbTests
{
    private const string Src = DataSourceCodes.LocalDataPublicRestroom;

    /// <summary>행 수가 크게 줄어드는 시나리오를 위해 비교 검증을 끈 정책.</summary>
    private static readonly ImportPolicy Loose = ImportPolicy.Default with { MaxRowDropRatio = 1, MaxSizeChangeRatio = 100, MaxRemovalRatio = 1 };

    private static async Task<ImportRun> Run(TestDatabase tdb, SourceFile file, ImportPolicy? policy = null, bool force = false)
    {
        await using var db = tdb.NewContext();
        return await new ImportRunner(db, policy).RunAsync(Src, file, ImportTrigger.Manual, new ImportOptions { Force = force });
    }

    private static async Task<ToiletStatus> StatusOf(TestDatabase tdb, string key)
    {
        await using var db = tdb.NewContext();
        return await db.Toilets.Where(t => t.ManagementNo == key).Select(t => t.Status).SingleAsync();
    }

    [DbFact]
    public async Task Missing_row_is_marked_then_restored_when_it_returns()
    {
        await using var tdb = await TestDatabase.CreateAsync();

        var r1 = await Run(tdb, Files.Of("K1", "K2"), Loose);
        Assert.Equal(ImportRunStatus.Succeeded, r1.Status);
        Assert.Equal(2, r1.InsertedRows);

        var r2 = await Run(tdb, Files.Of("K1"), Loose);
        Assert.Equal(1, r2.MissingRows);
        Assert.Equal(ToiletStatus.Missing, await StatusOf(tdb, "K2"));
        Assert.Equal(ToiletStatus.Active, await StatusOf(tdb, "K1"));

        // 같은 내용으로 돌아와도(원본 해시 동일) 복귀해야 한다
        var r3 = await Run(tdb, Files.Of("K1", "K2"), Loose);
        Assert.Equal(1, r3.RestoredRows);
        Assert.Equal(2, r3.UnchangedRows);
        Assert.Equal(ToiletStatus.Active, await StatusOf(tdb, "K2"));
    }

    [DbFact]
    public async Task Removed_after_three_consecutive_misses()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Run(tdb, Files.Of("K1", "K2"), Loose);

        var b = Files.Of("K1");
        await Run(tdb, b, Loose);
        await Run(tdb, b, Loose, force: true);
        Assert.Equal(ToiletStatus.Missing, await StatusOf(tdb, "K2"));

        var r = await Run(tdb, b, Loose, force: true);
        Assert.Equal(1, r.RemovalCandidates);
        Assert.Equal(1, r.RemovedRows);
        Assert.Equal(ToiletStatus.Removed, await StatusOf(tdb, "K2"));
    }

    [DbFact]
    public async Task Same_file_is_skipped_and_does_not_count_as_a_miss()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Run(tdb, Files.Of("K1", "K2"), Loose);
        var b = Files.Of("K1");
        await Run(tdb, b, Loose);

        var skipped = await Run(tdb, b, Loose);
        Assert.Equal(ImportRunStatus.Skipped, skipped.Status);

        await using var db = tdb.NewContext();
        Assert.Equal(1, await db.RawToiletRecords.Where(r => r.SourceKey == "K2").Select(r => r.ConsecutiveMissing).SingleAsync());
    }

    [DbFact]
    public async Task Mass_removal_waits_for_approval()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var policy = Loose with { MaxRemovalRatio = 0.01 };
        await Run(tdb, Files.Of("K1", "K2"), policy);
        var b = Files.Of("K1");
        await Run(tdb, b, policy);
        await Run(tdb, b, policy, force: true);

        var pending = await Run(tdb, b, policy, force: true);
        Assert.Equal(ImportRunStatus.PendingApproval, pending.Status);
        Assert.Equal(1, pending.RemovalCandidates);
        Assert.Equal(0, pending.RemovedRows);
        Assert.Equal(ToiletStatus.Missing, await StatusOf(tdb, "K2"));

        await using (var db = tdb.NewContext())
            Assert.Equal(1, await MissingRecordProcessor.ApprovePendingRemovalsAsync(db, pending.Id, policy, CancellationToken.None));
        Assert.Equal(ToiletStatus.Removed, await StatusOf(tdb, "K2"));

        await using var check = tdb.NewContext();
        Assert.Equal(ImportRunStatus.Succeeded, (await check.ImportRuns.SingleAsync(r => r.Id == pending.Id)).Status);
    }

    [DbFact]
    public async Task Legacy_rows_without_source_become_missing_but_never_removed()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await using (var db = tdb.NewContext())
        {
            db.Toilets.Add(new Toilet { ManagementNo = "LEGACY-1", Kind = "공중화장실", Name = "예전 데이터" });
            await db.SaveChangesAsync();
        }

        var a = Files.Of("K1");
        var first = await Run(tdb, a, Loose);
        Assert.Equal(1, first.MissingRows);
        for (var i = 0; i < 4; i++) await Run(tdb, a, Loose, force: true);

        Assert.Equal(ToiletStatus.Missing, await StatusOf(tdb, "LEGACY-1"));
    }

    [DbFact]
    public async Task Status_override_is_not_touched_by_missing_processing()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await Run(tdb, Files.Of("K1", "K2"), Loose);
        await using (var db = tdb.NewContext())
        {
            var id = await db.Toilets.Where(t => t.ManagementNo == "K2").Select(t => t.Id).SingleAsync();
            db.ToiletOverrides.Add(new ToiletOverride
            {
                ToiletId = id, FieldName = OverrideFields.Status, ValueJson = "\"Active\"",
                Source = OverrideSource.Admin, Priority = (int)OverrideSource.Admin,
            });
            await db.SaveChangesAsync();
        }

        var b = Files.Of("K1");
        await Run(tdb, b, Loose);
        await Run(tdb, b, Loose, force: true);
        await Run(tdb, b, Loose, force: true);

        Assert.Equal(ToiletStatus.Active, await StatusOf(tdb, "K2"));
    }

    [DbFact]
    public async Task Rejected_file_leaves_data_untouched()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var keys = Enumerable.Range(1, 20).Select(i => $"K{i}").ToArray();
        await Run(tdb, Files.Of(keys));

        var r = await Run(tdb, Files.Of("K1")); // 기본 정책: 행 수 95% 감소 → 거부
        Assert.Equal(ImportRunStatus.Rejected, r.Status);

        await using var db = tdb.NewContext();
        Assert.Equal(20, await db.Toilets.CountAsync(t => t.Status == ToiletStatus.Active));
        Assert.Equal(0, await db.RawToiletRecords.SumAsync(x => x.ConsecutiveMissing));
    }

    [DbFact]
    public async Task Renormalize_run_is_not_the_prevalidation_baseline()
    {
        // #13: 재정규화(파일 없음, 1행)가 최신 성공 실행이어도 작은 파일은 직전 파일 임포트 기준으로 거부
        await using var tdb = await TestDatabase.CreateAsync();
        var keys = Enumerable.Range(1, 20).Select(i => $"K{i}").ToArray();
        await Run(tdb, Files.Of(keys));

        await using (var db = tdb.NewContext())
        {
            var id = await db.Toilets.Where(t => t.ManagementNo == "K1").Select(t => t.Id).SingleAsync();
            var renorm = await new ImportRunner(db).RenormalizeAsync(Src, toiletIds: [id]);
            Assert.Equal(ImportRunStatus.Succeeded, renorm.Status);
        }

        var r = await Run(tdb, Files.Of("K1", "K2", "K3"));
        Assert.Equal(ImportRunStatus.Rejected, r.Status);

        await using var check = tdb.NewContext();
        Assert.Equal(20, await check.Toilets.CountAsync(t => t.Status == ToiletStatus.Active));

        // 같은 원본을 다시 올리면 재정규화 실행이 끼어 있어도 직전 파일과 같아 건너뜀
        var same = await Run(tdb, Files.Of(keys));
        Assert.Equal(ImportRunStatus.Skipped, same.Status);
    }

    [DbFact]
    public async Task Concurrent_run_is_refused_by_advisory_lock()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await using var holder = tdb.NewContext();
        var source = await holder.DataSources.SingleAsync(s => s.Code == Src);
        await using var held = await ImportLock.TryAcquireAsync(holder, source.Id, CancellationToken.None);
        Assert.NotNull(held);

        await Assert.ThrowsAsync<ImportBusyException>(() => Run(tdb, Files.Of("K1")));
    }

    [DbFact]
    public async Task Stale_running_run_is_marked_abandoned()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        long staleId;
        await using (var db = tdb.NewContext())
        {
            var source = await db.DataSources.SingleAsync(s => s.Code == Src);
            var stale = new ImportRun
            {
                SourceId = source.Id, FileSha256 = "dead", NormalizerVersion = "0",
                Status = ImportRunStatus.Running, StartedAt = DateTime.UtcNow.AddHours(-3), HeartbeatAt = DateTime.UtcNow.AddHours(-3),
            };
            db.ImportRuns.Add(stale);
            await db.SaveChangesAsync();
            staleId = stale.Id;
        }

        var r = await Run(tdb, Files.Of("K1"));
        Assert.Equal(ImportRunStatus.Succeeded, r.Status);

        await using var check = tdb.NewContext();
        Assert.Equal(ImportRunStatus.Abandoned, (await check.ImportRuns.SingleAsync(x => x.Id == staleId)).Status);
    }
}
