using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import;

/// <summary>
/// 원본의 중복 행을 하나만 남기고 숨긴다 (<see cref="Mashil.Domain.Toilet.DuplicateOfId"/>).
/// 원본(LOCALDATA)에는 같은 화장실이 관리번호만 다르게 여러 번 들어 있다 (예: 양천구 "영송빌딩" 3건, 2026-11-18 운영 약 1,200건).
/// 중복 기준: 이름, 주소, 칸 수(남녀·장애인·어린이), 개방시간이 모두 같고 좌표가 약 10m 안 (좌표가 둘 다 없으면 같은 것으로).
/// 이름·주소가 같아도 칸 수가 다르면 층이나 건물이 다른 별도 화장실로 보고 둔다.
/// 남기는 행: Active 우선 → 평점·댓글이 많은 행 → Id 가 작은 행 (오래된 링크·즐겨찾기 유지).
/// 매번 처음부터 다시 계산하므로 원본이 바뀌면 숨김도 따라 바뀐다.
/// </summary>
public static class DuplicateMarker
{
    public static async Task<int> MarkAsync(MashilDbContext db, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("""
            WITH v AS (
                SELECT t."Id",
                       first_value(t."Id") OVER (
                           PARTITION BY t."Name", coalesce(t."RoadAddress", t."LotAddress"),
                                        t."MaleStallCount", t."MaleUrinalCount", t."MaleAccessibleStallCount", t."MaleAccessibleUrinalCount",
                                        t."MaleChildStallCount", t."MaleChildUrinalCount",
                                        t."FemaleStallCount", t."FemaleAccessibleStallCount", t."FemaleChildStallCount",
                                        t."OpenHours", round(t."Latitude"::numeric, 4), round(t."Longitude"::numeric, 4)
                           ORDER BY (t."Status" = 'Active') DESC,
                                    (SELECT count(*) FROM ratings r WHERE r."ToiletId" = t."Id")
                                      + (SELECT count(*) FROM comments c WHERE c."ToiletId" = t."Id") DESC,
                                    t."Id") AS keeper
                FROM toilets t
                WHERE t."Status" <> 'Removed')
            UPDATE toilets t
            SET "DuplicateOfId" = nullif(v.keeper, t."Id")
            FROM v
            WHERE v."Id" = t."Id" AND t."DuplicateOfId" IS DISTINCT FROM nullif(v.keeper, t."Id")
            """, ct);
        // 삭제된 행은 숨김 표시를 지운다 (삭제가 우선)
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE toilets SET "DuplicateOfId" = NULL WHERE "Status" = 'Removed' AND "DuplicateOfId" IS NOT NULL
            """, ct);
        return await db.Toilets.CountAsync(t => t.DuplicateOfId != null, ct);
    }
}
