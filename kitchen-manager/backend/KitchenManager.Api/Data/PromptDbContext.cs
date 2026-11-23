using Microsoft.EntityFrameworkCore;
using KitchenManager.Api.Models;

namespace KitchenManager.Api.Data;

/// <summary>
/// Generated from the parent app's PromptDbContext, holding only the entities
/// this app uses. Built from empty with EnsureCreated (no migrations).
/// </summary>
public class PromptDbContext : DbContext
{
    public PromptDbContext(DbContextOptions<PromptDbContext> options) : base(options)
    {
    }

    public DbSet<Prompt> Prompts { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<MeetingSession> MeetingSessions { get; set; }
    public DbSet<KnowledgeNode> KnowledgeNodes { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<TeamMember> TeamMembers { get; set; }
    public DbSet<PromptApproval> PromptApprovals { get; set; }
    public DbSet<ReaderBook> ReaderBooks { get; set; }
    public DbSet<BookCharacter> BookCharacters { get; set; }
    public DbSet<Recipe> Recipes { get; set; }
    public DbSet<IngredientAlias> IngredientAliases { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IngredientAlias>(e =>
        {
            e.HasIndex(a => a.Alias).IsUnique();
        });

        modelBuilder.Entity<Recipe>(e =>
        {
            e.HasIndex(r => new { r.UserId, r.CreatedAt });
            e.HasIndex(r => r.SourceBookId);
        });

        modelBuilder.Entity<ReaderBook>(e =>
        {
            e.HasIndex(b => new { b.UserId, b.DeletedAt, b.UploadedAt });
            e.HasMany(b => b.Characters).WithOne(c => c.Book)
                .HasForeignKey(c => c.BookId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Team>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => x.OrganizationId);
            e.HasMany(x => x.Members).WithOne(m => m.Team).HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TeamMember>(e =>
        {
            e.HasKey(x => x.Id);
            // One row per person per team; the unique index is what stops a
            // double-invite silently granting two roles.
            e.HasIndex(x => new { x.TeamId, x.UserId }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<PromptApproval>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TeamId, x.Status });
            e.HasIndex(x => new { x.PromptId, x.Status });
            e.HasOne(x => x.Prompt).WithMany().HasForeignKey(x => x.PromptId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Prompt>(e =>
        {
            e.HasIndex(x => x.TeamId);
            e.HasOne(x => x.Team).WithMany().HasForeignKey(x => x.TeamId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Prompt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.ApprovalStatus).HasMaxLength(30);

            // User relationship
            entity.HasOne(e => e.User)
                  .WithMany(u => u.Prompts)
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(300);
            entity.Property(e => e.Kind).HasMaxLength(20);
            entity.Property(e => e.Source).HasMaxLength(20);
            entity.Property(e => e.SourceModule).HasMaxLength(60);
            entity.Property(e => e.SourceRef).HasMaxLength(1000);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Kind });
            entity.HasIndex(e => new { e.UserId, e.UpdatedAt });
        });

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
