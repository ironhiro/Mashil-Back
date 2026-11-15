using Mashil.Domain;

namespace Mashil.Import.Normalization;

/// <summary>
/// 정규화가 다시 계산한 이슈를 화장실의 이슈 목록에 맞춘다 (DATA-12).
/// <list type="bullet">
/// <item>범위 안의 Open 이슈 중 이번에 다시 나온 것(필드·유형·원본 값이 같음)은 그대로 둔다 (접수 시각과 id 유지)</item>
/// <item>다시 나오지 않은 Open 이슈는 지운다</item>
/// <item>관리자가 이미 해결·무시한 이슈와 같은 것은 다시 만들지 않는다 (무시가 재임포트로 되살아나지 않게)</item>
/// </list>
/// 해결·무시 여부를 보려면 화장실의 이슈를 상태와 상관없이 불러와 있어야 한다. Open 만 불러왔으면 억제 없이 동작한다.
/// </summary>
public static class IssueSync
{
    private static (string, IssueType, string?) Key(NormalizationIssue i) => (i.FieldName, i.Type, i.RawValue);

    /// <returns>새로 만든 이슈 수 (이전에 없던 것만).</returns>
    public static int Replace(Toilet t, IEnumerable<IssueDraft> drafts, Func<NormalizationIssue, bool> scope, DateTime now)
    {
        var settled = t.Issues.Where(i => i.Status != IssueStatus.Open).Select(Key).ToHashSet();
        var wanted = new Dictionary<(string, IssueType, string?), IssueDraft>();
        foreach (var d in drafts)
        {
            var k = (d.Field, d.Type, d.RawValue);
            if (!settled.Contains(k)) wanted.TryAdd(k, d);
        }

        foreach (var i in t.Issues.Where(i => i.Status == IssueStatus.Open && scope(i)).ToList())
        {
            if (wanted.Remove(Key(i), out var d))
            {
                i.Severity = d.Severity;
                i.Note = d.Note;
            }
            else t.Issues.Remove(i);
        }

        foreach (var d in wanted.Values)
            t.Issues.Add(new NormalizationIssue
            {
                FieldName = d.Field, Type = d.Type, Severity = d.Severity, RawValue = d.RawValue, Note = d.Note, CreatedAt = now,
            });
        return wanted.Count;
    }
}
