using Microsoft.EntityFrameworkCore;
using Cosmos.Api.Models;

namespace Cosmos.Api.Data;

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
    public DbSet<PromptChain> PromptChains { get; set; }
    public DbSet<ChainNode> ChainNodes { get; set; }
    public DbSet<ChainEdge> ChainEdges { get; set; }
    public DbSet<FileAttachment> FileAttachments { get; set; }
    public DbSet<LifeCheckIn> LifeCheckIns { get; set; }
    public DbSet<LifeGoal> LifeGoals { get; set; }
    public DbSet<LifeDecision> LifeDecisions { get; set; }
    public DbSet<LifeRelationship> LifeRelationships { get; set; }
    public DbSet<LifeEvidence> LifeEvidence { get; set; }
    public DbSet<LifeCloudLink> LifeCloudLinks { get; set; }
    public DbSet<MeetingSession> MeetingSessions { get; set; }
    public DbSet<EmmdDocument> EmmdDocuments { get; set; }
    public DbSet<KnowledgeNode> KnowledgeNodes { get; set; }
    public DbSet<KnowledgeLink> KnowledgeLinks { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<TeamMember> TeamMembers { get; set; }
    public DbSet<PromptApproval> PromptApprovals { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

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

        modelBuilder.Entity<KnowledgeLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Kind).HasMaxLength(20);
            entity.Property(e => e.Label).HasMaxLength(200);
            entity.HasOne(e => e.SourceNode).WithMany().HasForeignKey(e => e.SourceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.TargetNode).WithMany().HasForeignKey(e => e.TargetId).OnDelete(DeleteBehavior.ClientCascade);
            entity.HasIndex(e => new { e.UserId, e.SourceId });
            entity.HasIndex(e => new { e.UserId, e.TargetId });
        });

        modelBuilder.Entity<LifeCheckIn>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.TopTasks).HasMaxLength(1000);
            entity.Property(e => e.Gratitude).HasMaxLength(1000);
            entity.Property(e => e.Notes).HasMaxLength(2000);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Date }).IsUnique();
        });

        modelBuilder.Entity<LifeGoal>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(240);
            entity.Property(e => e.Area).HasMaxLength(80);
            entity.Property(e => e.Status).HasMaxLength(20);
            entity.Property(e => e.Notes).HasMaxLength(1500);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Status });
        });

        modelBuilder.Entity<LifeDecision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(240);
            entity.Property(e => e.Reasoning).HasMaxLength(2000);
            entity.Property(e => e.Outcome).HasMaxLength(1200);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Date });
        });

        modelBuilder.Entity<LifeRelationship>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(160);
            entity.Property(e => e.Circle).HasMaxLength(80);
            entity.Property(e => e.Notes).HasMaxLength(1200);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<LifeEvidence>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.StoredFileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EvidenceType).HasMaxLength(40);
            entity.Property(e => e.Notes).HasMaxLength(1200);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
        });

        modelBuilder.Entity<LifeCloudLink>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(40);
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(220);
            entity.Property(e => e.Url).IsRequired().HasMaxLength(1200);
            entity.Property(e => e.Notes).HasMaxLength(1200);
            entity.Property(e => e.SyncStatus).HasMaxLength(80);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Provider });
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

        modelBuilder.Entity<PromptChain>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Category).HasMaxLength(100);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChainNode>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Label).HasMaxLength(200);

            entity.HasOne(e => e.Chain)
                  .WithMany(c => c.Nodes)
                  .HasForeignKey(e => e.ChainId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Prompt)
                  .WithMany()
                  .HasForeignKey(e => e.PromptId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ChainEdge>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Label).HasMaxLength(200);
            entity.Property(e => e.ConditionBranch).HasMaxLength(50);

            entity.HasOne(e => e.Chain)
                  .WithMany(c => c.Edges)
                  .HasForeignKey(e => e.ChainId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.SourceNode)
                  .WithMany()
                  .HasForeignKey(e => e.SourceNodeId)
                  .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(e => e.TargetNode)
                  .WithMany()
                  .HasForeignKey(e => e.TargetNodeId)
                  .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<FileAttachment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.StoredFileName).IsRequired().HasMaxLength(500);
            entity.Property(e => e.ContentType).IsRequired().HasMaxLength(200);
            entity.Property(e => e.EntityType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.EntityId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Source).HasMaxLength(20);
            entity.Property(e => e.GoogleDriveFileId).HasMaxLength(200);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.EntityType, e.EntityId });
        });
    }
}
