using Microsoft.EntityFrameworkCore;
using TestApp.Data.Models;

namespace TestApp.Data;

/// <summary>
/// Контекст SQLite для настроек юзербота и журнала рассылок.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<TargetChat> TargetChats => Set<TargetChat>();

    public DbSet<AdvertisingTemplate> AdvertisingTemplates => Set<AdvertisingTemplate>();

    public DbSet<ExecutionLog> ExecutionLogs => Set<ExecutionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TargetChat>(entity =>
        {
            entity.ToTable("TargetChats");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Title).HasMaxLength(256).IsRequired();
            entity.Property(e => e.SlowModeSeconds).HasDefaultValue(600);
            entity.Property(e => e.IsActive).HasDefaultValue(true);
        });

        modelBuilder.Entity<AdvertisingTemplate>(entity =>
        {
            entity.ToTable("AdvertisingTemplates");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BaseText).IsRequired();
            entity.HasIndex(e => e.IsCurrent);
        });

        modelBuilder.Entity<ExecutionLog>(entity =>
        {
            entity.ToTable("ExecutionLogs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ErrorMessage).HasMaxLength(2048);
            entity.HasIndex(e => e.ChatId);
            entity.HasIndex(e => e.SentAt);
        });
    }
}
