using Mashil.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import.IntegrationTests;

/// <summary>로그인 없이 매기는 별점 (기기 키): 주인은 회원이나 기기 키 둘 중 하나, 기기 키마다 화장실 하나에 하나.</summary>
public class RatingDbTests
{
    private static readonly string KeyA = new('a', 64);
    private static readonly string KeyB = new('b', 64);

    [DbFact]
    public async Task Device_key_ratings_are_unique_per_toilet_and_count_with_member_ratings()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        long toiletId;
        await using (var db = tdb.NewContext())
        {
            var t = new Toilet { ManagementNo = "R-1", Name = "별점", Kind = "공중화장실" };
            db.Toilets.Add(t);
            await db.SaveChangesAsync();
            toiletId = t.Id;
            db.Ratings.Add(new Rating { DeviceKeyHash = KeyA, ToiletId = toiletId, Score = 5 });
            db.Ratings.Add(new Rating { DeviceKeyHash = KeyB, ToiletId = toiletId, Score = 3 });
            await db.SaveChangesAsync();
        }

        await using (var db = tdb.NewContext())
        {
            db.Ratings.Add(new Rating { DeviceKeyHash = KeyA, ToiletId = toiletId, Score = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); // 같은 기기 키로 두 번째 줄은 안 된다
        }

        await using (var db = tdb.NewContext())
        {
            var avg = await db.Ratings.Where(r => r.ToiletId == toiletId).AverageAsync(r => (double)r.Score);
            Assert.Equal(4.0, avg);
        }
    }

    [DbFact]
    public async Task A_rating_needs_exactly_one_owner()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        await using var db = tdb.NewContext();
        var t = new Toilet { ManagementNo = "R-2", Name = "주인", Kind = "공중화장실" };
        db.Toilets.Add(t);
        await db.SaveChangesAsync();

        db.Ratings.Add(new Rating { ToiletId = t.Id, Score = 4 }); // 회원도 기기 키도 없음
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
