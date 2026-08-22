using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data.Entities;

namespace OsuTracker.Web.Data;

public class TrackerDbContext(DbContextOptions<TrackerDbContext> options) : DbContext(options)
{
    public DbSet<Beatmapset> Beatmapsets => Set<Beatmapset>();
    public DbSet<Beatmap> Beatmaps => Set<Beatmap>();
    public DbSet<Score> Scores => Set<Score>();
    public DbSet<PlayCount> PlayCounts => Set<PlayCount>();
    public DbSet<SyncJob> SyncJobs => Set<SyncJob>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Beatmapset>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Status).HasConversion<int>();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.RankedDate);
        });

        b.Entity<Beatmap>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Mode).HasConversion<int>();
            e.Property(x => x.Status).HasConversion<int>();

            e.HasOne(x => x.Beatmapset)
             .WithMany(x => x.Beatmaps)
             .HasForeignKey(x => x.BeatmapsetId)
             .OnDelete(DeleteBehavior.Cascade);

            // Serves every progress query: WHERE Mode = @m AND Status IN (1,2)
            e.HasIndex(x => new { x.Mode, x.Status }).HasDatabaseName("ix_beatmap_mode_status");
            e.HasIndex(x => new { x.Mode, x.StarRating }).HasDatabaseName("ix_beatmap_mode_stars");
            e.HasIndex(x => x.BeatmapsetId).HasDatabaseName("ix_beatmap_set");
        });

        b.Entity<Score>(e =>
        {
            e.HasKey(x => x.BeatmapId);
            e.Property(x => x.BeatmapId).ValueGeneratedNever();
            e.HasOne(x => x.Beatmap)
             .WithOne(x => x.Score)
             .HasForeignKey<Score>(x => x.BeatmapId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.PlayedAtUnix).HasDatabaseName("ix_score_played");
            // No index on Grade or Accuracy — Rule 4 means nothing filters on them.
        });

        b.Entity<PlayCount>(e =>
        {
            e.HasKey(x => x.BeatmapId);
            e.Property(x => x.BeatmapId).ValueGeneratedNever();
            e.HasOne(x => x.Beatmap)
             .WithOne(x => x.PlayCount)
             .HasForeignKey<PlayCount>(x => x.BeatmapId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SyncJob>(e =>
        {
            e.HasKey(x => x.Name);
            e.Property(x => x.State).HasConversion<int>();
        });

        b.Entity<Setting>(e => e.HasKey(x => x.Key));
    }
}
