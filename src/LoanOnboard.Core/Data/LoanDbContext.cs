using LoanOnboard.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace LoanOnboard.Core.Data;

/// <summary>
/// EF Core model. The tables are created by database/schema.sql, which must stay in sync with this file.
/// </summary>
public class LoanDbContext(DbContextOptions<LoanDbContext> options) : DbContext(options)
{
    public DbSet<LoanApplication> Applications => Set<LoanApplication>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LoanStatusHistory> StatusHistory => Set<LoanStatusHistory>();
    public DbSet<StageExecution> StageExecutions => Set<StageExecution>();
    public DbSet<StagePlan> StagePlans => Set<StagePlan>();
    public DbSet<ApplicationDocument> Documents => Set<ApplicationDocument>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<AgentReview> AgentReviews => Set<AgentReview>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<LoanApplication>(e =>
        {
            e.ToTable("Application");
            e.HasKey(x => x.AckId);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.DealerId).HasMaxLength(50).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.ProductType).HasMaxLength(30).IsRequired();
            e.Property(x => x.RequestedAmount).HasColumnType("decimal(18,2)");
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.CurrentStage).HasMaxLength(30);
            e.Property(x => x.LoanNumber).HasMaxLength(20);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.DealerId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => x.LoanNumber).IsUnique().HasFilter("[LoanNumber] IS NOT NULL");
            e.HasIndex(x => new { x.DealerId, x.Status });

            e.HasMany<Customer>().WithOne().HasForeignKey(x => x.AckId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany<ApplicationDocument>().WithOne().HasForeignKey(x => x.AckId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany<LoanStatusHistory>().WithOne().HasForeignKey(x => x.AckId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany<StageExecution>().WithOne().HasForeignKey(x => x.AckId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany<AgentReview>().WithOne().HasForeignKey(x => x.AckId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Customer>(e =>
        {
            e.ToTable("Customer");
            e.HasKey(x => x.CustomerId);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
            e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(30).IsRequired();
            e.Property(x => x.IdentifierHash).HasColumnType("char(64)");
            e.Property(x => x.IdentifierLast4).HasColumnType("char(4)");
            e.HasIndex(x => x.AckId).IsUnique();
            e.HasIndex(x => x.IdentifierHash);
        });

        b.Entity<LoanStatusHistory>(e =>
        {
            e.ToTable("LoanStatusHistory");
            e.HasKey(x => x.Id);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.CorrelationId).HasColumnType("char(26)");
            e.Property(x => x.Stage).HasMaxLength(30);
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(400);
            e.Property(x => x.Source).HasMaxLength(50).IsRequired();
            e.HasIndex(x => x.EventId).IsUnique();
            e.HasIndex(x => new { x.AckId, x.OccurredAt });
        });

        b.Entity<StageExecution>(e =>
        {
            e.ToTable("StageExecution");
            e.HasKey(x => x.Id);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.Stage).HasMaxLength(30).IsRequired();
            e.Property(x => x.Outcome).HasMaxLength(20);
            e.Property(x => x.ReasonCode).HasMaxLength(100);
            e.HasIndex(x => new { x.AckId, x.Stage, x.Attempt });
            e.HasIndex(x => x.DueBy).HasFilter("[Outcome] IS NULL");
        });

        b.Entity<StagePlan>(e =>
        {
            e.ToTable("StagePlan");
            e.HasKey(x => new { x.ProductType, x.Sequence });
            e.Property(x => x.ProductType).HasMaxLength(30);
            e.Property(x => x.Stage).HasMaxLength(30).IsRequired();
            e.Property(x => x.StatusName).HasMaxLength(30).IsRequired();
        });

        b.Entity<ApplicationDocument>(e =>
        {
            e.ToTable("Document");
            e.HasKey(x => x.DocId);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.Type).HasMaxLength(50).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.BlobPath).HasMaxLength(500).IsRequired();
            e.Property(x => x.Sha256).HasColumnType("char(64)");
            e.Property(x => x.ScanStatus).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.AckId);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("Outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.Type).HasMaxLength(50).IsRequired();
            e.Property(x => x.Topic).HasMaxLength(80).IsRequired();
            e.Property(x => x.Stage).HasMaxLength(30);
            e.Property(x => x.Payload).IsRequired();
            e.HasIndex(x => x.Id).HasFilter("[PublishedAt] IS NULL").HasDatabaseName("IX_Outbox_Pending");
        });

        b.Entity<ProcessedMessage>(e =>
        {
            e.ToTable("ProcessedMessage");
            e.HasKey(x => new { x.EventId, x.Consumer });
            e.Property(x => x.EventId).HasMaxLength(100);
            e.Property(x => x.Consumer).HasMaxLength(50);
        });

        b.Entity<AgentReview>(e =>
        {
            e.ToTable("AgentReview");
            e.HasKey(x => x.Id);
            e.Property(x => x.AckId).HasColumnType("char(26)");
            e.Property(x => x.AgentId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Decision).HasMaxLength(20).IsRequired();
            e.Property(x => x.ConsentRef).HasMaxLength(200).IsRequired();
            e.Property(x => x.ScriptVersion).HasMaxLength(50).IsRequired();
            e.Property(x => x.CallReference).HasMaxLength(200).IsRequired();
            e.Property(x => x.Comment).HasMaxLength(1000);
            e.HasIndex(x => x.AckId);
        });
    }
}
