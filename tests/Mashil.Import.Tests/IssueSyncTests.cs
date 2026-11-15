using Mashil.Domain;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;

namespace Mashil.Import.Tests;

public class IssueSyncTests
{
    private static Toilet With(params NormalizationIssue[] issues) => new() { ManagementNo = "K", Kind = "", Name = "", Issues = [.. issues] };

    private static NormalizationIssue Issue(string raw, IssueStatus status = IssueStatus.Open, string field = IssueFields.Phone) =>
        new() { FieldName = field, Type = IssueType.InvalidPhone, RawValue = raw, Status = status, CreatedAt = new DateTime(2026, 1, 1) };

    private static IssueDraft Draft(string raw) => new(IssueFields.Phone, IssueType.InvalidPhone, IssueSeverity.Info, raw);

    [Fact]
    public void Keeps_reproduced_open_issue_drops_stale_and_counts_only_new()
    {
        var kept = Issue("1");
        var t = With(kept, Issue("2"));
        var added = IssueSync.Replace(t, [Draft("1"), Draft("3")], _ => true, DateTime.UtcNow);

        Assert.Equal(1, added);
        Assert.Same(kept, t.Issues.Single(i => i.RawValue == "1")); // 같은 이슈는 같은 행 (접수 시각 유지)
        Assert.DoesNotContain(t.Issues, i => i.RawValue == "2");
        Assert.Contains(t.Issues, i => i.RawValue == "3" && i.Status == IssueStatus.Open);
    }

    [Fact]
    public void Ignored_or_resolved_issue_is_not_recreated()
    {
        var t = With(Issue("1", IssueStatus.Ignored), Issue("2", IssueStatus.Resolved));
        Assert.Equal(0, IssueSync.Replace(t, [Draft("1"), Draft("2")], _ => true, DateTime.UtcNow));
        Assert.Equal(2, t.Issues.Count);
    }

    [Fact]
    public void Out_of_scope_open_issues_are_untouched()
    {
        var geo = Issue("addr", field: IssueFields.Geocode);
        var t = With(geo);
        IssueSync.Replace(t, [], i => i.FieldName != IssueFields.Geocode, DateTime.UtcNow);
        Assert.Same(geo, Assert.Single(t.Issues));
    }

    [Fact]
    public void App_status_config_flags_win_and_db_text_falls_back()
    {
        var config = new AppStatusSnapshot(true, false, "설정", null);
        Assert.Equal(config, AppStatusStore.Merge(null, config));
        var merged = AppStatusStore.Merge(new AppStatusSetting { Maintenance = false, ReadOnly = true, Message = "DB" }, config);
        Assert.True(merged.Maintenance);
        Assert.True(merged.ReadOnly);
        Assert.Equal("DB", merged.Message);
    }
}
