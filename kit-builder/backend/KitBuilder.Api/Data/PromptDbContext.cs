using Microsoft.EntityFrameworkCore;
using KitBuilder.Api.Models;

namespace KitBuilder.Api.Data;

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
    public DbSet<Skill> Skills { get; set; }
    public DbSet<UserMemory> UserMemories { get; set; }
    public DbSet<UserAiSettings> UserAiSettings { get; set; }
    public DbSet<Agent> Agents { get; set; }
    public DbSet<AgentRun> AgentRuns { get; set; }
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

        modelBuilder.Entity<UserAiSettings>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ModelId).HasMaxLength(100);
            entity.HasOne(e => e.User).WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.UserId).IsUnique();
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

        modelBuilder.Entity<AgentRun>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserId).IsRequired();
            entity.Property(e => e.Input).IsRequired();
            entity.Property(e => e.StepsJson).IsRequired();
            entity.Property(e => e.Status).IsRequired().HasMaxLength(20);
            entity.Property(e => e.ModelUsed).HasMaxLength(100);
            entity.Property(e => e.SafetyFlagsJson).IsRequired();
            entity.Property(e => e.BenchmarkGroupId).HasMaxLength(100);
            entity.Property(e => e.EstimatedCostUsd).HasColumnType("TEXT");

            entity.HasOne(e => e.Agent)
                  .WithMany()
                  .HasForeignKey(e => e.AgentId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.AgentId);
            entity.HasIndex(e => e.UserId);
        });
    }
}
