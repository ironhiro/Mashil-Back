using Mashil.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Infrastructure;

public sealed class MashilDbContext(DbContextOptions<MashilDbContext> options) : DbContext(options)
{
    public DbSet<Toilet> Toilets => Set<Toilet>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<ChangeRequest> ChangeRequests => Set<ChangeRequest>();

    // --- 원본 보존 / 임포트 (P1) ---
    public DbSet<DataSource> DataSources => Set<DataSource>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<RawToiletRecord> RawToiletRecords => Set<RawToiletRecord>();
    public DbSet<ToiletOverride> ToiletOverrides => Set<ToiletOverride>();

    // --- 정규화 (P2) ---
    public DbSet<OpeningSchedule> OpeningSchedules => Set<OpeningSchedule>();
    public DbSet<NormalizationIssue> NormalizationIssues => Set<NormalizationIssue>();
    public DbSet<ValueMapping> ValueMappings => Set<ValueMapping>();
    public DbSet<GeocodeCacheEntry> GeocodeCache => Set<GeocodeCacheEntry>();
    public DbSet<GeocodeDailyUsage> GeocodeDailyUsage => Set<GeocodeDailyUsage>();
    public DbSet<PublicHoliday> PublicHolidays => Set<PublicHoliday>();

    // --- 인증·스토어 필수 기능 (P4) ---
    public DbSet<UserIdentity> UserIdentities => Set<UserIdentity>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuthNonce> AuthNonces => Set<AuthNonce>();
    public DbSet<UserConsent> UserConsents => Set<UserConsent>();
    public DbSet<IdpRevocation> IdpRevocations => Set<IdpRevocation>();
    public DbSet<CommentReport> CommentReports => Set<CommentReport>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasPostgresExtension("postgis");

        b.Entity<Toilet>(e =>
        {
            e.ToTable("toilets");
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.ManagementNo).IsUnique();
            e.Property(t => t.Location).HasColumnType("geography (Point, 4326)");
            e.HasIndex(t => t.Location).HasMethod("gist");
            e.Property(t => t.AddressSource).HasConversion<string>();
            e.Property(t => t.CoordinateSource).HasConversion<string>();
            e.Property(t => t.Origin).HasConversion<string>().HasDefaultValue(ToiletOrigin.PublicData).HasSentinel((ToiletOrigin)(-1));
            e.Property(t => t.Status).HasConversion<string>().HasDefaultValue(ToiletStatus.Active).HasSentinel((ToiletStatus)(-1));
            e.HasIndex(t => t.Status);
            e.Property(t => t.KindCode).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ToiletKind.Other).HasSentinel((ToiletKind)(-1));
            e.Property(t => t.ScheduleStatus).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ScheduleStatus.Unknown).HasSentinel((ScheduleStatus)(-1));
            e.Property(t => t.NormalizerVersion).HasMaxLength(16);
            e.Ignore(t => t.IsLocatable);
        });

        b.Entity<OpeningSchedule>(e =>
        {
            e.ToTable("opening_schedules", t =>
            {
                t.HasCheckConstraint("CK_opening_schedules_Closed_or_Times", "\"IsClosed\" OR (\"OpenMinute\" IS NOT NULL AND \"CloseMinute\" IS NOT NULL)");
                t.HasCheckConstraint("CK_opening_schedules_Minutes", "\"OpenMinute\" BETWEEN 0 AND 1440 AND \"CloseMinute\" BETWEEN 0 AND 1440");
            });
            e.HasKey(s => s.Id);
            e.Property(s => s.DayType).HasConversion<string>().HasMaxLength(8);
            e.HasOne(s => s.Toilet).WithMany(t => t.Schedules).HasForeignKey(s => s.ToiletId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.ToiletId, s.DayType });
            e.Ignore(s => s.IsFullDay);
        });

        b.Entity<NormalizationIssue>(e =>
        {
            e.ToTable("normalization_issues");
            e.HasKey(i => i.Id);
            e.Property(i => i.FieldName).HasMaxLength(32);
            e.Property(i => i.Type).HasConversion<string>().HasMaxLength(24);
            e.Property(i => i.Severity).HasConversion<string>().HasMaxLength(8);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(8);
            e.HasOne(i => i.Toilet).WithMany(t => t.Issues).HasForeignKey(i => i.ToiletId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ValueMapping>().WithMany().HasForeignKey(i => i.ResolvedByMappingId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(i => i.ResolvedById).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(i => new { i.Status, i.Type });
            e.HasIndex(i => new { i.FieldName, i.RawValue }); // 같은 원본 값 일괄 처리
        });

        b.Entity<GeocodeCacheEntry>(e =>
        {
            e.ToTable("geocode_cache");
            e.HasKey(c => c.QueryKey);
            e.Property(c => c.QueryKey).HasMaxLength(256);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<PublicHoliday>(e =>
        {
            e.ToTable("public_holidays");
            e.HasKey(h => h.Date);
            e.Property(h => h.Name).HasMaxLength(64);
        });

        b.Entity<GeocodeDailyUsage>(e =>
        {
            e.ToTable("geocode_daily_usage");
            e.HasKey(u => u.UsageDate);
        });

        b.Entity<ValueMapping>(e =>
        {
            e.ToTable("value_mappings");
            e.HasKey(m => m.Id);
            e.Property(m => m.FieldName).HasMaxLength(32);
            e.Property(m => m.CanonicalJson).HasColumnType("jsonb");
            e.HasOne<UserProfile>().WithMany().HasForeignKey(m => m.CreatedById).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.FieldName, m.RawKey }).IsUnique().HasFilter("\"IsActive\"");
        });

        b.Entity<UserProfile>(e =>
        {
            e.ToTable("user_profiles");
            e.HasKey(u => u.Id);
            // 이메일은 선택·비식별 (USR-01) — 유니크 없음
            e.Property(u => u.Email).HasMaxLength(256);
            e.HasIndex(u => u.Email);
            e.Property(u => u.DisplayName).HasMaxLength(40);
            e.Property(u => u.Role).HasConversion<string>();
            e.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<UserIdentity>(e =>
        {
            e.ToTable("user_identities");
            e.HasKey(i => i.Id);
            e.Property(i => i.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(i => i.ProviderSubject).HasMaxLength(255);
            e.Property(i => i.ClientId).HasMaxLength(255);
            e.Property(i => i.EmailAtProvider).HasMaxLength(320);
            e.HasIndex(i => new { i.Provider, i.ProviderSubject }).IsUnique();
            e.HasIndex(i => i.UserId);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.HasKey(t => t.Id);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.FamilyId).HasMaxLength(32);
            e.Property(t => t.DeviceLabel).HasMaxLength(16);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.FamilyId);
            e.HasIndex(t => t.UserId).HasFilter("\"RevokedAt\" IS NULL");
            e.HasOne<UserProfile>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuthNonce>(e =>
        {
            e.ToTable("auth_nonces");
            e.HasKey(n => n.Id);
            e.Property(n => n.NonceHash).HasMaxLength(64);
            e.HasIndex(n => n.NonceHash).IsUnique();
            e.HasIndex(n => n.ExpiresAt);
        });

        b.Entity<UserConsent>(e =>
        {
            e.ToTable("user_consents");
            e.HasKey(c => c.Id);
            e.Property(c => c.DocType).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.DocVersion).HasMaxLength(32);
            e.HasIndex(c => new { c.UserId, c.DocType, c.DocVersion }).IsUnique();
            e.HasOne<UserProfile>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<IdpRevocation>(e =>
        {
            e.ToTable("idp_revocations");
            e.HasKey(r => r.Id);
            e.Property(r => r.Provider).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.ProviderSubject).HasMaxLength(255);
            e.Property(r => r.ClientId).HasMaxLength(255);
            e.HasIndex(r => new { r.Status, r.NextAttemptAt });
        });

        b.Entity<CommentReport>(e =>
        {
            e.ToTable("comment_reports");
            e.HasKey(r => r.Id);
            e.Property(r => r.Reason).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Detail).HasMaxLength(500);
            e.HasIndex(r => new { r.CommentId, r.ReporterId }).IsUnique();
            e.HasIndex(r => r.Status);
            e.HasOne<Comment>().WithMany().HasForeignKey(r => r.CommentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(r => r.ReporterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(r => r.ReviewedById).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserBlock>(e =>
        {
            e.ToTable("user_blocks", t => t.HasCheckConstraint("CK_user_blocks_not_self", "\"BlockerId\" <> \"BlockedId\""));
            e.HasKey(x => new { x.BlockerId, x.BlockedId });
            e.HasIndex(x => x.BlockedId);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(x => x.BlockedId).OnDelete(DeleteBehavior.Restrict);
        });

        // FK 는 모두 Restrict — 사용자·화장실은 soft delete (status) 로만 지운다 (G5).
        b.Entity<Rating>(e =>
        {
            e.ToTable("ratings");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.UserId, r.ToiletId }).IsUnique();
            e.HasIndex(r => r.ToiletId);
            e.HasOne<Toilet>().WithMany().HasForeignKey(r => r.ToiletId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_ratings_Score", "\"Score\" BETWEEN 1 AND 5"));
        });

        b.Entity<Comment>(e =>
        {
            e.ToTable("comments");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.ToiletId);
            e.HasIndex(c => new { c.ToiletId, c.Hidden });
            e.HasOne<Toilet>().WithMany().HasForeignKey(c => c.ToiletId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ChangeRequest>(e =>
        {
            e.ToTable("change_requests");
            e.HasKey(c => c.Id);
            e.Property(c => c.Kind).HasConversion<string>();
            e.Property(c => c.Status).HasConversion<string>();
            e.Property(c => c.PayloadJson).HasColumnType("jsonb");
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.RequesterId);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(c => c.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(c => c.ReviewedById).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Toilet>().WithMany().HasForeignKey(c => c.TargetToiletId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DataSource>(e =>
        {
            e.ToTable("data_sources");
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.Code).IsUnique();
            e.Property(s => s.Code).HasMaxLength(64);
            e.HasData(new DataSource
            {
                Id = 1,
                Code = DataSourceCodes.LocalDataPublicRestroom,
                Name = "공중화장실정보 (전국파일)",
                Provider = "LOCALDATA",
                Format = "CSV",
                Encoding = "Auto",
                InfoUrl = "https://file.localdata.go.kr/file/public_restroom_info/info",
                DownloadUrl = "https://file.localdata.go.kr/file/download/public_restroom_info/info",
                License = null,
                IsActive = true,
            });
        });

        b.Entity<ImportRun>(e =>
        {
            e.ToTable("import_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Trigger).HasConversion<string>();
            e.Property(r => r.Status).HasConversion<string>();
            e.Property(r => r.FileSha256).HasMaxLength(64);
            e.HasOne(r => r.Source).WithMany().HasForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.SourceId, r.Status, r.StartedAt });
            // 소스당 Running 은 1건 (advisory lock 의 보조 안전장치)
            e.HasIndex(r => r.SourceId).IsUnique().HasFilter("\"Status\" = 'Running'").HasDatabaseName("UX_import_runs_SourceId_Running");
        });

        b.Entity<RawToiletRecord>(e =>
        {
            e.ToTable("raw_toilet_records");
            e.HasKey(r => r.Id);
            e.Property(r => r.PayloadJson).HasColumnType("jsonb");
            e.Property(r => r.RowHash).HasMaxLength(64);
            e.HasIndex(r => new { r.SourceId, r.SourceKey }).IsUnique();
            e.HasIndex(r => new { r.SourceId, r.LastSeenAt });
            e.HasIndex(r => r.ToiletId);
            e.HasOne(r => r.Source).WithMany().HasForeignKey(r => r.SourceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Toilet).WithMany().HasForeignKey(r => r.ToiletId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ImportRun>().WithMany().HasForeignKey(r => r.LastImportRunId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ToiletOverride>(e =>
        {
            e.ToTable("toilet_overrides");
            e.HasKey(o => o.Id);
            e.Property(o => o.FieldName).HasMaxLength(64);
            e.Property(o => o.ValueJson).HasColumnType("jsonb");
            e.Property(o => o.Source).HasConversion<string>();
            e.HasOne(o => o.Toilet).WithMany(t => t.Overrides).HasForeignKey(o => o.ToiletId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ChangeRequest>().WithMany().HasForeignKey(o => o.ChangeRequestId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<UserProfile>().WithMany().HasForeignKey(o => o.CreatedById).OnDelete(DeleteBehavior.Restrict);
            // 소스당 필드별 활성 오버라이드는 1건
            e.HasIndex(o => new { o.ToiletId, o.FieldName, o.Source }).IsUnique().HasFilter("\"IsActive\"");
        });
    }
}
