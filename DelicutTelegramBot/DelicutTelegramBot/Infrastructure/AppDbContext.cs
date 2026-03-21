using Microsoft.EntityFrameworkCore;
using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Infrastructure;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SelectionHistory> SelectionHistories => Set<SelectionHistory>();
    public DbSet<PendingSelection> PendingSelections => Set<PendingSelection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.TelegramUserId).IsUnique();
            e.HasOne(u => u.Settings).WithOne(s => s.User)
             .HasForeignKey<UserSettings>(s => s.UserId);
            e.HasMany(u => u.SelectionHistories).WithOne(h => h.User)
             .HasForeignKey(h => h.UserId);
            e.HasMany(u => u.PendingSelections).WithOne(p => p.User)
             .HasForeignKey(p => p.UserId);
        });

        modelBuilder.Entity<UserSettings>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Strategy).HasConversion<string>();
            e.Property(s => s.StopWords).HasDefaultValueSql("'{}'::text[]");
            e.Property(s => s.FavouriteDishNames).HasDefaultValueSql("'{}'::text[]");
        });

        modelBuilder.Entity<SelectionHistory>(e =>
        {
            e.HasKey(h => h.Id);
            e.HasIndex(h => new { h.UserId, h.DishId, h.SelectedDate, h.MealCategory })
             .IsUnique();
        });

        modelBuilder.Entity<PendingSelection>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Status).HasConversion<string>();
        });
    }
}
