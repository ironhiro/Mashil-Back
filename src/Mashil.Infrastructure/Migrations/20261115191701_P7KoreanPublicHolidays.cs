using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mashil.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class P7KoreanPublicHolidays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 「관공서의 공휴일에 관한 규정」의 공휴일 + 대체공휴일 + 선거일, 2026~2027년.
            // 출처: 정부 발표를 반영한 Google 대한민국 공휴일 캘린더와 Nager.Date 를 대조 (2026-11-16). 둘이 다른 날은 법령 기준으로 합침.
            // 해마다 다음 해 날짜를 새 마이그레이션으로 추가한다 (docs/OPERATIONS.md). 임시공휴일은 지정되면 그때 추가.
            migrationBuilder.InsertData(
                table: "public_holidays",
                columns: new[] { "Date", "Name" },
                values: new object[,]
                {
                    { new DateOnly(2026, 1, 1), "신정" },
                    { new DateOnly(2026, 2, 16), "설날 연휴" },
                    { new DateOnly(2026, 2, 17), "설날" },
                    { new DateOnly(2026, 2, 18), "설날 연휴" },
                    { new DateOnly(2026, 3, 1), "삼일절" },
                    { new DateOnly(2026, 3, 2), "대체공휴일(삼일절)" },
                    { new DateOnly(2026, 5, 1), "노동절" },
                    { new DateOnly(2026, 5, 5), "어린이날" },
                    { new DateOnly(2026, 5, 24), "부처님오신날" },
                    { new DateOnly(2026, 5, 25), "대체공휴일(부처님오신날)" },
                    { new DateOnly(2026, 6, 3), "전국동시지방선거일" },
                    { new DateOnly(2026, 6, 6), "현충일" },
                    { new DateOnly(2026, 7, 17), "제헌절" },
                    { new DateOnly(2026, 8, 15), "광복절" },
                    { new DateOnly(2026, 8, 17), "대체공휴일(광복절)" },
                    { new DateOnly(2026, 9, 24), "추석 연휴" },
                    { new DateOnly(2026, 9, 25), "추석" },
                    { new DateOnly(2026, 9, 26), "추석 연휴" },
                    { new DateOnly(2026, 10, 3), "개천절" },
                    { new DateOnly(2026, 10, 5), "대체공휴일(개천절)" },
                    { new DateOnly(2026, 10, 9), "한글날" },
                    { new DateOnly(2026, 12, 25), "성탄절" },
                    { new DateOnly(2027, 1, 1), "신정" },
                    { new DateOnly(2027, 2, 6), "설날 연휴" },
                    { new DateOnly(2027, 2, 7), "설날" },
                    { new DateOnly(2027, 2, 8), "설날 연휴" },
                    { new DateOnly(2027, 2, 9), "대체공휴일(설날)" },
                    { new DateOnly(2027, 3, 1), "삼일절" },
                    { new DateOnly(2027, 5, 1), "노동절" },
                    { new DateOnly(2027, 5, 3), "대체공휴일(노동절)" },
                    { new DateOnly(2027, 5, 5), "어린이날" },
                    { new DateOnly(2027, 5, 13), "부처님오신날" },
                    { new DateOnly(2027, 6, 6), "현충일" },
                    { new DateOnly(2027, 7, 17), "제헌절" },
                    { new DateOnly(2027, 7, 19), "대체공휴일(제헌절)" },
                    { new DateOnly(2027, 8, 15), "광복절" },
                    { new DateOnly(2027, 8, 16), "대체공휴일(광복절)" },
                    { new DateOnly(2027, 9, 14), "추석 연휴" },
                    { new DateOnly(2027, 9, 15), "추석" },
                    { new DateOnly(2027, 9, 16), "추석 연휴" },
                    { new DateOnly(2027, 10, 3), "개천절" },
                    { new DateOnly(2027, 10, 4), "대체공휴일(개천절)" },
                    { new DateOnly(2027, 10, 9), "한글날" },
                    { new DateOnly(2027, 10, 11), "대체공휴일(한글날)" },
                    { new DateOnly(2027, 12, 25), "성탄절" },
                    { new DateOnly(2027, 12, 27), "대체공휴일(성탄절)" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DELETE FROM public_holidays WHERE "Date" BETWEEN '2026-01-01' AND '2027-12-31';""");
        }
    }
}
