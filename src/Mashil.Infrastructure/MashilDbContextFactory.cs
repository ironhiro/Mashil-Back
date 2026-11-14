using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mashil.Infrastructure;

/// <summary>
/// `dotnet ef` 디자인 타임용. API 의 시작 설정(JWT 키 등)과 무관하게 마이그레이션을 만들고 적용할 수 있게 한다.
/// 연결 문자열: 환경 변수 <c>MASHIL_DB</c>, 없으면 docker-compose 기본값.
/// </summary>
public sealed class MashilDbContextFactory : IDesignTimeDbContextFactory<MashilDbContext>
{
    public MashilDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("MASHIL_DB")
            ?? "Host=localhost;Port=5432;Database=mashil;Username=mashil;Password=mashil_dev";
        var options = new DbContextOptionsBuilder<MashilDbContext>()
            .UseNpgsql(conn, npg => npg.UseNetTopologySuite())
            .Options;
        return new MashilDbContext(options);
    }
}
