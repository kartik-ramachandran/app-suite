using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Models;

namespace PromptStudio.Api.Data;

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
    public DbSet<PromptExecution> PromptExecutions { get; set; }
    public DbSet<FileAttachment> FileAttachments { get; set; }
    public DbSet<PromptEvaluation> PromptEvaluations { get; set; }
    public DbSet<Skill> Skills { get; set; }
    public DbSet<UserMemory> UserMemories { get; set; }
    public DbSet<Agent> Agents { get; set; }
    public DbSet<PromptVersion> PromptVersions { get; set; }
    public DbSet<PromptAuditEvent> PromptAuditEvents { get; set; }
    public DbSet<PromptCollection> PromptCollections { get; set; }
    public DbSet<CollectionItem> CollectionItems { get; set; }
    public DbSet<KnowledgeNode> KnowledgeNodes { get; set; }
    public DbSet<TestSuite> TestSuites { get; set; }
    public DbSet<TestCase> TestCases { get; set; }
    public DbSet<TestSuiteRun> TestSuiteRuns { get; set; }
    public DbSet<TestCaseResult> TestCaseResults { get; set; }
    public DbSet<PromptTrace> PromptTraces { get; set; }
    public DbSet<TraceAlertRule> TraceAlertRules { get; set; }
    public DbSet<Team> Teams { get; set; }
    public DbSet<TeamMember> TeamMembers { get; set; }
    public DbSet<PromptApproval> PromptApprovals { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TestSuite>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.IsActive });
            e.HasIndex(x => x.NextRunAt);
            e.HasOne(x => x.Prompt).WithMany().HasForeignKey(x => x.PromptId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Cases).WithOne(c => c.TestSuite).HasForeignKey(c => c.TestSuiteId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Runs).WithOne(r => r.TestSuite).HasForeignKey(r => r.TestSuiteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestCase>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TestSuiteId);
        });

        modelBuilder.Entity<TestSuiteRun>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TestSuiteId, x.StartedAt });
            e.HasIndex(x => x.UserId);
            e.Property(x => x.TotalCostUsd).HasPrecision(18, 6);
            e.HasMany(x => x.Results).WithOne(r => r.Run).HasForeignKey(r => r.TestSuiteRunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TestCaseResult>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TestSuiteRunId);
            e.Property(x => x.CostUsd).HasPrecision(18, 6);
        });

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

        modelBuilder.Entity<Skill>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Instruction).IsRequired();
            entity.Property(e => e.Category).HasMaxLength(100);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Tags).HasMaxLength(500);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.IsPublic);
            entity.HasIndex(e => e.Category);
        });

        modelBuilder.Entity<UserMemory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Key).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Value).IsRequired();
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.Key }).IsUnique();
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Instructions).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Model).HasMaxLength(100);
            entity.Property(e => e.EnabledTools).IsRequired();
            entity.Property(e => e.InputSchemaJson).IsRequired();
            entity.Property(e => e.MemoryConfigJson).IsRequired();
            entity.Property(e => e.GuardrailsJson).IsRequired();
            entity.Property(e => e.OutputSchemaJson).IsRequired();
            entity.Property(e => e.KnowledgeSourcesJson).IsRequired();
            entity.Property(e => e.IntegrationActionsJson).IsRequired();
            entity.Property(e => e.ModelRoutingJson).IsRequired();
            entity.Property(e => e.BenchmarkConfigJson).IsRequired();
            entity.Property(e => e.GovernanceJson).IsRequired();
            entity.Property(e => e.TemplateCategory).HasMaxLength(100);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<PromptEvaluation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PromptA).IsRequired();
            entity.Property(e => e.PromptB).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(50);
            entity.Property(e => e.Winner).HasMaxLength(10);
            entity.Property(e => e.Notes).HasMaxLength(2000);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.UserId, e.CreatedAt });
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

        modelBuilder.Entity<PromptCollection>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.Property(e => e.Emoji).HasMaxLength(10);

            entity.HasOne(e => e.User)
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<CollectionItem>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasOne(e => e.Collection)
                  .WithMany(c => c.Items)
                  .HasForeignKey(e => e.CollectionId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Prompt)
                  .WithMany()
                  .HasForeignKey(e => e.PromptId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => new { e.CollectionId, e.PromptId }).IsUnique();
        });
    }
}
