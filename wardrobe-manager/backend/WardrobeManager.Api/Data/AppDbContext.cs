using Microsoft.EntityFrameworkCore;
using WardrobeManager.Api.Models;

namespace WardrobeManager.Api.Data;

/// <summary>
/// Only Users is an EF entity. The module tables (TravelGuides, UserTravelPrefs,
/// WardrobeItems, WardrobeProfiles, WardrobeOutfits) are raw-SQL tables created
/// with CREATE TABLE IF NOT EXISTS — same pattern as the parent app, which keeps
/// this project entirely free of EF migrations.
/// </summary>
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
        });
    }
}
