using System.Text;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mashil.Import.IntegrationTests;

/// <summary>
/// 실제 PostgreSQL(PostGIS)에 테스트마다 임시 DB 를 만들고 마이그레이션을 적용한다. 끝나면 삭제.
/// 환경 변수 <c>MASHIL_TEST_DB</c> = DB 생성 권한이 있는 연결 문자열 (예: docker-compose 의 mashil 계정, Database=postgres).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public const string EnvVar = "MASHIL_TEST_DB";

    private readonly string _admin;
    public string Name { get; }
    public string ConnectionString { get; }

    private TestDatabase(string admin, string name)
    {
        _admin = admin;
        Name = name;
        ConnectionString = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false }.ConnectionString;
    }

    public static async Task<TestDatabase> CreateAsync()
    {
        var admin = Environment.GetEnvironmentVariable(EnvVar) ?? throw new InvalidOperationException($"{EnvVar} 가 없습니다");
        var db = new TestDatabase(admin, "mashil_test_" + Guid.NewGuid().ToString("N")[..12]);
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{db.Name}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using (var ctx = db.NewContext())
            await ctx.Database.MigrateAsync();
        return db;
    }

    public MashilDbContext NewContext() => new(new DbContextOptionsBuilder<MashilDbContext>()
        .UseNpgsql(ConnectionString, npg => npg.UseNetTopologySuite())
        .Options);

    public async ValueTask DisposeAsync()
    {
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary><see cref="TestDatabase.EnvVar"/> 가 없으면 건너뛰는 테스트.</summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestDatabase.EnvVar)))
            Skip = $"{TestDatabase.EnvVar} 가 설정되지 않아 DB 통합 테스트를 건너뜀";
    }
}

/// <summary>LOCALDATA 34컬럼 형식의 테스트 파일.</summary>
internal static class Files
{
    static Files() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Header =
        "개방자치단체코드,관리번호,구분명,근거법령명,화장실명,소재지도로명주소,소재지지번주소," +
        "남성용-대변기수,남성용-소변기수,남성용-장애인용대변기수,남성용-장애인용소변기수,남성용-어린이용대변기수,남성용-어린이용소변기수," +
        "여성용-대변기수,여성용-장애인용대변기수,여성용-어린이용대변기수,관리기관명,전화번호,개방시간,개방시간상세,설치연월," +
        "화장실소유구분명,오물처리방식,안전관리시설설치대상여부,비상벨설치여부,비상벨설치장소,화장실입구CCTV설치유무," +
        "기저귀교환대유무,기저귀교환대장소,리모델링연월,데이터기준일자,데이터갱신구분,데이터갱신시점,최종수정시점";

    public static string Row(string key, string name = "테스트 화장실", string openHours = "상시") =>
        $"3000000,{key},공중화장실,,{name},서울특별시 종로구 테스트로 1,,1,1,0,0,0,0,1,0,0,종로구청,0212345678,{openHours},,,공공기관-지방자치단체,수세식,N,N,,N,N,,,2026-11-10,U,2026-11-11 10:00:00,2026-11-10 10:00:00";

    public static SourceFile Of(params string[] keys) => Raw(keys.Select(k => Row(k)).ToArray());

    public static SourceFile Raw(params string[] rows) =>
        new(Encoding.GetEncoding(949).GetBytes(string.Join("\r\n", [Header, .. rows]) + "\r\n"), "test.csv");
}
