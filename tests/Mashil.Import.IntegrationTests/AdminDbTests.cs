using System.Text.Json;
using Mashil.Domain;
using Mashil.Import.Admin;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.IntegrationTests;

/// <summary>관리자 기능 (ADM-01, 03~06): 매핑 사전과 범위 재정규화, 이슈 무시 유지, 직접 수정, 삭제 거부, 점검 상태.</summary>
public class AdminDbTests
{
    private const string Src = DataSourceCodes.LocalDataPublicRestroom;
    private static readonly ImportPolicy Loose = ImportPolicy.Default with { MaxRowDropRatio = 1, MaxSizeChangeRatio = 100, MaxRemovalRatio = 1 };

    private static async Task<ImportRun> Import(TestDatabase tdb, SourceFile file, ImportPolicy? policy = null, bool force = true)
    {
        await using var db = tdb.NewContext();
        return await new ImportRunner(db, policy ?? Loose).RunAsync(Src, file, ImportTrigger.Manual, new ImportOptions { Force = force, SkipValidation = true });
    }

    private static async Task<Guid> Admin(TestDatabase tdb)
    {
        await using var db = tdb.NewContext();
        var u = new UserProfile { Email = $"{Guid.NewGuid():N}@example.invalid", DisplayName = "운영자", Role = UserRole.Admin };
        db.UserProfiles.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static string WithPhone(string row, string phone) => row.Replace(",0212345678,", $",{phone},");

    [DbFact]
    public async Task Mapping_resolves_issues_and_scoped_renormalize_applies_it()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var first = await Import(tdb, Files.Raw(Files.Row("K1", openHours: "정시"), Files.Row("K2", openHours: "상시")));
        Assert.Equal(1, first.NewIssues); // K1: 정시인데 상세 없음

        var key = OpeningHoursParser.MappingKey("정시", null);
        long k1;
        await using (var db = tdb.NewContext())
        {
            k1 = await db.Toilets.Where(t => t.ManagementNo == "K1").Select(t => t.Id).SingleAsync();
            var groups = await IssueQueue.GroupsAsync(db, new IssueFilter(IssueFields.OpeningHours, Status: IssueStatus.Open), 0, 10, CancellationToken.None);
            Assert.Equal(key, Assert.Single(groups.Items).RawValue);
        }

        var hours = new HoursOutcome(ScheduleStatus.Scheduled,
            [.. OpeningHoursParser.Week.Take(5).Select(d => ScheduleEntry.Slot(d, 540, 1080)), ScheduleEntry.ClosedOn(DayType.Sat), ScheduleEntry.ClosedOn(DayType.Sun)]).ToJson();
        MappingChange change;
        await using (var db = tdb.NewContext())
            change = await ValueMappingService.CreateAsync(db, IssueFields.OpeningHours, key, hours, null, DateTime.UtcNow, CancellationToken.None);
        Assert.Equal(1, change.IssuesChanged);
        Assert.Equal(new[] { k1 }, change.AffectedToiletIds);

        await using (var db = tdb.NewContext())
        {
            // 작업이 끊겨도 "바뀐 것만" 재정규화가 처리하도록 버전이 비워져 있다
            Assert.Null(await db.Toilets.Where(t => t.Id == k1).Select(t => t.NormalizerVersion).SingleAsync());
            var run = await new ImportRunner(db).RenormalizeAsync(Src, toiletIds: change.AffectedToiletIds, label: "(mapping)");
            Assert.Equal(ImportRunStatus.Succeeded, run.Status);
            Assert.Equal(1, run.UpdatedRows);
            Assert.Equal(0, run.NewIssues);
        }

        await using (var db = tdb.NewContext())
        {
            var t = await db.Toilets.Include(x => x.Schedules).Include(x => x.Issues).SingleAsync(x => x.Id == k1);
            Assert.Equal(ScheduleStatus.Scheduled, t.ScheduleStatus);
            Assert.Contains(t.Schedules, s => s.DayType == DayType.Mon && s.OpenMinute == 540 && s.CloseMinute == 1080);
            Assert.Equal(ImportRunner.NormalizerVersion, t.NormalizerVersion);
            var issue = Assert.Single(t.Issues);
            Assert.Equal(IssueStatus.Resolved, issue.Status);
            Assert.Equal(change.Mapping.Id, issue.ResolvedByMappingId);
        }

        // 매핑을 끄면 이슈가 다시 열리고, 재정규화 후 같은 이슈 하나만 (중복 없음)
        await using (var db = tdb.NewContext())
        {
            var off = await ValueMappingService.DeactivateAsync(db, change.Mapping.Id, CancellationToken.None);
            Assert.Equal(1, off!.IssuesChanged);
            await new ImportRunner(db).RenormalizeAsync(Src, toiletIds: off.AffectedToiletIds);
        }
        await using (var db = tdb.NewContext())
        {
            var t = await db.Toilets.Include(x => x.Issues).SingleAsync(x => x.Id == k1);
            Assert.Equal(ScheduleStatus.Unknown, t.ScheduleStatus);
            Assert.Equal(IssueStatus.Open, Assert.Single(t.Issues).Status);
        }
    }

    [DbFact]
    public async Task Ignored_issue_is_not_recreated_and_open_issue_keeps_its_id()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var file = Files.Raw(WithPhone(Files.Row("K1"), "12345"), WithPhone(Files.Row("K2"), "999"));
        await Import(tdb, file);

        long k2IssueId;
        await using (var db = tdb.NewContext())
        {
            var n = await IssueQueue.SetStatusAsync(db, null, new IssueFilter(IssueFields.Phone, IssueType.InvalidPhone, IssueStatus.Open, "12345"),
                IssueStatus.Ignored, null, DateTime.UtcNow, CancellationToken.None);
            Assert.Equal(1, n);
            k2IssueId = await db.NormalizationIssues.Where(i => i.RawValue == "999").Select(i => i.Id).SingleAsync();
        }

        await using (var db = tdb.NewContext())
        {
            var run = await new ImportRunner(db).RenormalizeAsync(Src);
            Assert.Equal(2, run.UpdatedRows);
            Assert.Equal(0, run.NewIssues);
        }

        await using (var db = tdb.NewContext())
        {
            var issues = await db.NormalizationIssues.OrderBy(i => i.RawValue).ToListAsync();
            Assert.Equal(2, issues.Count);
            Assert.Equal(IssueStatus.Ignored, issues.Single(i => i.RawValue == "12345").Status);
            Assert.Equal(k2IssueId, issues.Single(i => i.RawValue == "999").Id); // 다시 계산돼도 같은 행
        }
    }

    [DbFact]
    public async Task Admin_edit_wins_over_change_request_survives_reimport_and_reverts_to_source()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var file = Files.Raw(Files.Row("K1", name: "원본 이름"));
        await Import(tdb, file);
        var admin = await Admin(tdb);
        long id;
        await using (var db = tdb.NewContext()) id = await db.Toilets.Select(t => t.Id).SingleAsync();

        // 변경 요청 오버라이드(우선순위 20)보다 관리자(30)가 이긴다
        await using (var db = tdb.NewContext())
        {
            db.ToiletOverrides.Add(new ToiletOverride
            {
                ToiletId = id, FieldName = OverrideFields.Name, ValueJson = OverrideApplier.StringJson("제보 이름"),
                Source = OverrideSource.ChangeRequest, Priority = (int)OverrideSource.ChangeRequest,
            });
            await db.SaveChangesAsync();
        }

        using var body = JsonDocument.Parse("""{"name":"관리자 이름","status":"Removed","phone":"0311234567","bogus":1}""");
        await using (var db = tdb.NewContext())
        {
            var r = await ToiletAdminEditor.ApplyAsync(db, id, body.RootElement, admin, DateTime.UtcNow, CancellationToken.None);
            Assert.NotNull(r);
            Assert.Equal(new[] { OverrideFields.Name, OverrideFields.Status, OverrideFields.Phone }, r.AppliedFields);
            Assert.Equal(new[] { "bogus" }, r.IgnoredKeys);
        }

        await Import(tdb, file); // 재임포트해도 유지

        await using (var db = tdb.NewContext())
        {
            var t = await db.Toilets.SingleAsync();
            Assert.Equal("관리자 이름", t.Name);
            Assert.Equal("031-123-4567", t.Phone);
            Assert.Equal(ToiletStatus.Removed, t.Status);
        }

        // 이름 오버라이드를 끄면 다음 순위(변경 요청), 상태를 null 로 보내면 원본 기준 Active
        await using (var db = tdb.NewContext())
        {
            var adminName = await db.ToiletOverrides.SingleAsync(o => o.IsActive && o.Source == OverrideSource.Admin && o.FieldName == OverrideFields.Name);
            Assert.True(await ToiletAdminEditor.DeactivateOverrideAsync(db, id, adminName.Id, DateTime.UtcNow, CancellationToken.None));
        }
        using var clear = JsonDocument.Parse("""{"status":null}""");
        await using (var db = tdb.NewContext())
        {
            var r = await ToiletAdminEditor.ApplyAsync(db, id, clear.RootElement, admin, DateTime.UtcNow, CancellationToken.None);
            Assert.Equal(new[] { OverrideFields.Status }, r!.ClearedFields);
        }

        await using (var db = tdb.NewContext())
        {
            var t = await db.Toilets.SingleAsync();
            Assert.Equal("제보 이름", t.Name);
            Assert.Equal(ToiletStatus.Active, t.Status);

            var view = await ToiletAdminEditor.ViewAsync(db, id, CancellationToken.None);
            Assert.NotNull(view);
            Assert.Equal("원본 이름", view.Source!.Name);
            Assert.Equal(4, view.Overrides.Count); // 제보 이름, 관리자 이름(꺼짐), 상태(꺼짐), 전화
            Assert.Contains(view.Overrides, o => o.FieldName == OverrideFields.Name && o.Source == OverrideSource.Admin && !o.IsActive && o.CreatedByName == "운영자");
        }
    }

    [DbFact]
    public async Task Rejecting_mass_removal_keeps_rows_and_resets_miss_count()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var policy = Loose with { MaxRemovalRatio = 0.01 };
        await Import(tdb, Files.Of("K1", "K2"), policy);
        var b = Files.Of("K1");
        await Import(tdb, b, policy);
        await Import(tdb, b, policy);
        var pending = await Import(tdb, b, policy);
        Assert.Equal(ImportRunStatus.PendingApproval, pending.Status);

        await using (var db = tdb.NewContext())
            Assert.Equal(1, await MissingRecordProcessor.RejectPendingRemovalsAsync(db, pending.Id, policy, CancellationToken.None));

        await using (var db = tdb.NewContext())
        {
            Assert.Equal(ImportRunStatus.Succeeded, (await db.ImportRuns.SingleAsync(r => r.Id == pending.Id)).Status);
            Assert.Equal(ToiletStatus.Missing, await db.Toilets.Where(t => t.ManagementNo == "K2").Select(t => t.Status).SingleAsync());
            Assert.Equal(0, await db.RawToiletRecords.Where(r => r.SourceKey == "K2").Select(r => r.ConsecutiveMissing).SingleAsync());
        }

        var next = await Import(tdb, b, policy);
        Assert.Equal(ImportRunStatus.Succeeded, next.Status);
        Assert.Equal(0, next.RemovalCandidates);
    }

    [DbFact]
    public async Task Change_request_outcomes_and_reports()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var run = await Import(tdb, Files.Raw(Files.Row("K1", openHours: "정시"), WithPhone(Files.Row("K2"), "1")));
        var admin = await Admin(tdb);
        long id;
        ChangeRequest edit, add;
        await using (var db = tdb.NewContext())
        {
            id = await db.Toilets.Where(t => t.ManagementNo == "K1").Select(t => t.Id).SingleAsync();
            edit = new ChangeRequest { RequesterId = admin, Kind = ChangeRequestKind.Edit, TargetToiletId = id, PayloadJson = """{"name":"새 이름"}""" };
            add = new ChangeRequest { RequesterId = admin, Kind = ChangeRequestKind.Add, PayloadJson = """{"name":"추가한 곳"}""" };
            db.ChangeRequests.AddRange(edit, add);
            await db.SaveChangesAsync();
        }
        foreach (var cr in new[] { edit, add })
        {
            await using var db = tdb.NewContext();
            db.Attach(cr);
            await ChangeRequestApplier.ApplyAsync(db, cr, admin, DateTime.UtcNow, CancellationToken.None);
        }

        await using (var db = tdb.NewContext())
        {
            var outcomes = await AdminReports.ChangeRequestOutcomesAsync(db, [edit.Id, add.Id], CancellationToken.None);
            Assert.Equal(id, outcomes[edit.Id].ToiletId);
            Assert.Equal(new[] { OverrideFields.Name }, outcomes[edit.Id].ActiveFields);
            Assert.True(outcomes[add.Id].CreatedToilet);

            var report = await AdminReports.RunReportAsync(db, run.Id, CancellationToken.None);
            Assert.Equal(2, report!.Run.NewIssues);
            Assert.Equal(2, report.NewIssuesByType.Sum(x => x.Count));

            var norm = await AdminReports.NormalizationReportAsync(db, DateTime.UtcNow, CancellationToken.None);
            Assert.Equal(3, norm.Toilets);
            Assert.Equal(2, norm.TopUnresolved.Count);
            var phone = norm.Fields.Single(f => f.Field == IssueFields.Phone);
            Assert.Equal(1, phone.Ok);
        }
    }

    [DbFact]
    public async Task App_status_row_merges_with_config_fallback()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var config = new AppStatusSnapshot(false, false, "설정 문구", null);
        await using (var db = tdb.NewContext())
        {
            Assert.Equal(config, AppStatusStore.Merge(await AppStatusStore.LoadAsync(db, CancellationToken.None), config));
            var until = new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.FromHours(9));
            await AppStatusStore.SaveAsync(db, false, true, "  ", until, null, DateTime.UtcNow, CancellationToken.None);
        }
        await using (var db = tdb.NewContext())
        {
            var merged = AppStatusStore.Merge(await AppStatusStore.LoadAsync(db, CancellationToken.None), config);
            Assert.True(merged.ReadOnly && merged.WritesBlocked);
            Assert.False(merged.Maintenance);
            Assert.Equal("설정 문구", merged.Message); // DB 문구가 비면 설정 문구
            Assert.Equal(new DateTimeOffset(2026, 11, 15, 20, 0, 0, TimeSpan.Zero), merged.Until);

            // 설정의 비상 스위치는 DB 가 꺼져 있어도 켠다
            await AppStatusStore.SaveAsync(db, false, false, null, null, null, DateTime.UtcNow, CancellationToken.None);
            Assert.True(AppStatusStore.Merge(await AppStatusStore.LoadAsync(db, CancellationToken.None), config with { Maintenance = true }).Maintenance);
        }
    }
}
