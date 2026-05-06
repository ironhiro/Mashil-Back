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

        b.Entity<Rating>(e =>
        {
            e.ToTable("ratings");
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.UserId, r.ToiletId }).IsUnique();
            e.HasIndex(r => r.ToiletId);
        });

        b.Entity<Comment>(e =>
        {
            e.ToTable("comments");
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.ToiletId);
            e.HasIndex(c => new { c.ToiletId, c.Hidden });
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
        });
    }
}
