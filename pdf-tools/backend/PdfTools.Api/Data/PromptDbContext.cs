using Microsoft.EntityFrameworkCore;
using PdfTools.Api.Models;

namespace PdfTools.Api.Data;

/// <summary>
/// Generated from the parent app's PromptDbContext, holding only the entities
/// this app uses. Built from empty with EnsureCreated (no migrations).
/// </summary>
public class PromptDbContext : DbContext
{
    public PromptDbContext(DbContextOptions<PromptDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.GoogleId).HasMaxLength(255);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.HasIndex(e => e.GoogleId).IsUnique();
            entity.HasIndex(e => e.ReferralCode).IsUnique();
        });
    }
}
