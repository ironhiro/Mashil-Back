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
            e.Ignore(t => t.IsLocatable);
        });

        b.Entity<UserProfile>(e =>
        {
            e.ToTable("user_profiles");
            e.HasKey(u => u.Id);
            e.Property(u => u.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Role).HasConversion<string>();
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
