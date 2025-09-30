using Microsoft.EntityFrameworkCore;
using CASINO_MASS_PROGRAM.Models;

namespace CASINO_MASS_PROGRAM.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRow> ImportRows => Set<ImportRow>();
    public DbSet<ImportCellError> ImportCellErrors => Set<ImportCellError>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<AwardSettlement> AwardSettlements => Set<AwardSettlement>();
    public DbSet<TeamRepresentative> TeamRepresentatives => Set<TeamRepresentative>();
    public DbSet<TeamRepresentativeMember> TeamRepresentativeMembers => Set<TeamRepresentativeMember>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Import tracking
        modelBuilder.Entity<ImportBatch>()
            .HasMany(b => b.Rows)
            .WithOne(r => r.Batch!)
            .HasForeignKey(r => r.BatchId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ImportRow>()
            .HasMany(r => r.Errors)
            .WithOne(e => e.Row!)
            .HasForeignKey(e => e.RowId)
            .OnDelete(DeleteBehavior.Cascade);

        // Member uniqueness
        modelBuilder.Entity<Member>()
            .HasIndex(m => m.MemberCode)
            .IsUnique();

        // TeamRepresentative uniqueness by external ID
        modelBuilder.Entity<TeamRepresentative>()
            .HasIndex(tr => tr.ExternalId)
            .IsUnique();

        // Join table composite PK and relationships
        modelBuilder.Entity<TeamRepresentativeMember>()
            .HasKey(x => new { x.TeamRepresentativeId, x.MemberId });

        modelBuilder.Entity<TeamRepresentativeMember>()
            .HasOne(x => x.TeamRepresentative)
            .WithMany(tr => tr.Members)
            .HasForeignKey(x => x.TeamRepresentativeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TeamRepresentativeMember>()
            .HasOne(x => x.Member)
            .WithMany()
            .HasForeignKey(x => x.MemberId)
            .OnDelete(DeleteBehavior.Cascade);

        // AwardSettlement monetary precision
        modelBuilder.Entity<AwardSettlement>()
            .Property(a => a.CasinoWinLoss)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<AwardSettlement>()
            .Property(a => a.AwardSettlementAmount)
            .HasColumnType("decimal(18,2)");
    }
}