using Microsoft.EntityFrameworkCore;
using MediaStudio.Api.Models;

namespace MediaStudio.Api.Data;

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
    public DbSet<Persona> Personas { get; set; }
    public DbSet<PromptExecution> PromptExecutions { get; set; }
    public DbSet<PromptVersion> PromptVersions { get; set; }
    public DbSet<PromptAuditEvent> PromptAuditEvents { get; set; }
    public DbSet<PromptTrace> PromptTraces { get; set; }
    public DbSet<TraceAlertRule> TraceAlertRules { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<TeamMember> TeamMembers { get; set; }
    public DbSet<PromptApproval> PromptApprovals { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PromptTrace>(e =>
        {
            e.HasKey(x => x.Id);
            // The three shapes the trace view actually queries in.
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => new { x.PromptId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.Status, x.CreatedAt });
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
            e.HasOne(x => x.Prompt).WithMany().HasForeignKey(x => x.PromptId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TraceAlertRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.IsActive });
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

        modelBuilder.Entity<Persona>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SystemPrompt).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Tone).HasMaxLength(100);
            entity.Property(e => e.Expertise).HasMaxLength(500);
            entity.Property(e => e.Constraints).HasMaxLength(1000);
            entity.Property(e => e.Icon).HasMaxLength(50);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromptExecution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PromptName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SystemPrompt).IsRequired();
            entity.Property(e => e.Model).IsRequired().HasMaxLength(50);
            entity.Property(e => e.MessagesJson).IsRequired();
            entity.Property(e => e.RatingNote).HasMaxLength(1000);

            entity.HasOne(e => e.Prompt)
                  .WithMany()
                  .HasForeignKey(e => e.PromptId)
                  .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.CreatedAt })
                  .IsDescending(false, true);
        });

        modelBuilder.Entity<PromptVersion>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PromptId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Content).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Status).HasMaxLength(30);
            entity.Property(e => e.ChangeNote).HasMaxLength(1000);

            entity.HasOne(e => e.Prompt)
                  .WithMany()
                  .HasForeignKey(e => e.PromptId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.PromptId, e.VersionNumber }).IsUnique();
        });

        modelBuilder.Entity<PromptAuditEvent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PromptId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(100);
            entity.Property(e => e.DetailsJson).IsRequired();

            entity.HasOne(e => e.Prompt)
                  .WithMany()
                  .HasForeignKey(e => e.PromptId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.PromptId, e.CreatedAt });
        });
    }
}
