using Mashil.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.IntegrationTests;

public class DuplicateMarkerDbTests
{
    private static async Task Import(TestDatabase tdb, SourceFile file)
    {
        await using var db = tdb.NewContext();
        await new ImportRunner(db, ImportPolicy.Default with { MaxRowDropRatio = 1, MaxSizeChangeRatio = 100, MaxRemovalRatio = 1 })
            .RunAsync(DataSourceCodes.LocalDataPublicRestroom, file, ImportTrigger.Manual, new ImportOptions { Force = true });
    }

    [DbFact]
    public async Task Same_toilet_listed_under_several_management_numbers_shows_once()
    {
        // 원본 예: 양천구 "영송빌딩" 이 관리번호만 다르게 3번 (2026-11-18)
        await using var tdb = await TestDatabase.CreateAsync();
        var otherFloor = Files.Row("D").Replace(",1,1,0,0,0,0,1,0,0,", ",3,2,0,0,0,0,4,0,0,"); // 같은 이름·주소지만 칸 수가 다름 → 별도 화장실
        await Import(tdb, Files.Raw(Files.Row("A"), Files.Row("B"), Files.Row("C"), otherFloor));

        await using (var db = tdb.NewContext())
        {
            var rows = await db.Toilets.OrderBy(t => t.Id).ToListAsync();
            var a = rows.Single(t => t.ManagementNo == "A");
            Assert.Null(a.DuplicateOfId); // Id 가 가장 작은 행을 남긴다
            Assert.All(rows.Where(t => t.ManagementNo is "B" or "C"), t => Assert.Equal(a.Id, t.DuplicateOfId));
            Assert.Null(rows.Single(t => t.ManagementNo == "D").DuplicateOfId);
        }

        // A 가 원본에서 빠져도(Missing) 남은 Active 행 중 하나가 보인다
        await Import(tdb, Files.Raw(Files.Row("B"), Files.Row("C"), otherFloor));
        await using var check = tdb.NewContext();
        var after = await check.Toilets.OrderBy(t => t.Id).ToListAsync();
        var b = after.Single(t => t.ManagementNo == "B");
        Assert.Null(b.DuplicateOfId);
        Assert.Equal(b.Id, after.Single(t => t.ManagementNo == "C").DuplicateOfId);
        Assert.Equal(b.Id, after.Single(t => t.ManagementNo == "A").DuplicateOfId);
    }
}
