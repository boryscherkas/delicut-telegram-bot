using System.Text.Json;
using DelicutTelegramBot.Models.Delicut;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using DelicutTelegramBot.Models.Domain;

namespace DelicutTelegramBot.Infrastructure;

public class AppDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<SelectionHistory> SelectionHistories => Set<SelectionHistory>();
    public DbSet<PendingSelection> PendingSelections => Set<PendingSelection>();
    public DbSet<MenuCache> MenuCaches => Set<MenuCache>();

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

        modelBuilder.Entity<MenuCache>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.UserId, m.DeliveryDate, m.MealCategory }).IsUnique();
            e.Property(m => m.Dishes).HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<List<Dish>>(v, JsonOptions) ?? new List<Dish>(),
                    new ValueComparer<List<Dish>>(
                        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
                        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
                        v => JsonSerializer.Deserialize<List<Dish>>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!));
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId);
        });
    }
}
