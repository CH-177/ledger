using Ledger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Data;

/// <summary>
/// The single SQLite database holding all of the user's financial data.
/// </summary>
public class LedgerDbContext : DbContext
{
    private readonly TimeProvider _time;

    public LedgerDbContext(DbContextOptions<LedgerDbContext> options, TimeProvider? time = null)
        : base(options)
    {
        _time = time ?? TimeProvider.System;
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetItem> BudgetItems => Set<BudgetItem>();
    public DbSet<RecurringTransaction> RecurringTransactions => Set<RecurringTransaction>();
    public DbSet<RecurringSkip> RecurringSkips => Set<RecurringSkip>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<GoalContribution> GoalContributions => Set<GoalContribution>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobRate> JobRates => Set<JobRate>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<PaycheckRecord> PaycheckRecords => Set<PaycheckRecord>();
    public DbSet<JobDeduction> JobDeductions => Set<JobDeduction>();
    public DbSet<Paystub> Paystubs => Set<Paystub>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportProfile> ImportProfiles => Set<ImportProfile>();
    public DbSet<ImportPayeeRule> ImportPayeeRules => Set<ImportPayeeRule>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Financial history is never removed as a side effect of deleting something else.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            fk.DeleteBehavior = DeleteBehavior.Restrict;
        }

        b.Entity<Account>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(100);
            e.Property(a => a.InstitutionName).HasMaxLength(100);
            e.Ignore(a => a.IsLiability);
            e.HasIndex(a => a.IsArchived);
        });

        b.Entity<Transaction>(e =>
        {
            e.Property(t => t.Payee).HasMaxLength(200);
            e.Property(t => t.Memo).HasMaxLength(1000);
            e.Property(t => t.ExternalId).HasMaxLength(200);

            e.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.TransferAccount).WithMany().HasForeignKey(t => t.TransferAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.Category).WithMany().HasForeignKey(t => t.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.RecurringTransaction).WithMany().HasForeignKey(t => t.RecurringTransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(t => t.ImportBatch).WithMany().HasForeignKey(t => t.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => t.ImportBatchId);
            e.HasIndex(t => t.ExternalId);

            e.HasIndex(t => t.AccountId);
            e.HasIndex(t => t.TransferAccountId);
            e.HasIndex(t => t.CategoryId);
            e.HasIndex(t => t.Date);
            e.HasIndex(t => t.TransactionType);
            e.HasIndex(t => new { t.RecurringTransactionId, t.RecurrenceDate })
                .IsUnique()
                .HasFilter("\"RecurringTransactionId\" IS NOT NULL");

            // Soft-deleted rows are invisible to every query unless explicitly requested.
            e.HasQueryFilter(t => !t.IsDeleted);

            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Transaction_AmountPositive", "\"AmountCents\" > 0");
                t.HasCheckConstraint("CK_Transaction_TransferAccount", TransferConstraintSql);
            });
        });

        b.Entity<Category>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.Icon).HasMaxLength(8);
            e.HasOne(c => c.ParentCategory)
                .WithMany(c => c.Children)
                .HasForeignKey(c => c.ParentCategoryId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(c => c.ParentCategoryId);
        });

        b.Entity<Budget>(e =>
        {
            e.HasIndex(x => new { x.Year, x.Month }).IsUnique();
            e.HasMany(x => x.Items).WithOne(i => i.Budget).HasForeignKey(i => i.BudgetId)
                // Items are part of the budget itself, not history, so they go with it.
                .OnDelete(DeleteBehavior.Cascade);
            e.ToTable(t => t.HasCheckConstraint("CK_Budget_Month", "\"Month\" BETWEEN 1 AND 12"));
        });

        b.Entity<BudgetItem>(e =>
        {
            e.HasOne(i => i.Category).WithMany().HasForeignKey(i => i.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(i => i.BudgetId);
            e.HasIndex(i => new { i.BudgetId, i.CategoryId }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_BudgetItem_PlannedNonNegative", "\"PlannedAmountCents\" >= 0"));
        });

        b.Entity<RecurringTransaction>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(100);
            e.Property(r => r.Payee).HasMaxLength(200);
            e.Property(r => r.Memo).HasMaxLength(1000);
            e.HasOne(r => r.Account).WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.TransferAccount).WithMany().HasForeignKey(r => r.TransferAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Category).WithMany().HasForeignKey(r => r.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => r.StartDate);
            e.HasIndex(r => r.IsActive);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Recurring_AmountPositive", "\"AmountCents\" > 0");
                t.HasCheckConstraint("CK_Recurring_TransferAccount", TransferConstraintSql);
                t.HasCheckConstraint("CK_Recurring_EndAfterStart", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
            });
        });

        b.Entity<RecurringSkip>(e =>
        {
            e.HasOne(s => s.RecurringTransaction).WithMany().HasForeignKey(s => s.RecurringTransactionId)
                // A skip is part of the rule itself, not history, so it goes with it.
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.RecurringTransactionId, s.Date }).IsUnique();
        });

        b.Entity<Goal>(e =>
        {
            e.Property(g => g.Name).HasMaxLength(100);
            e.HasOne(g => g.Category).WithMany().HasForeignKey(g => g.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(g => g.Account).WithMany().HasForeignKey(g => g.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Goal_TargetPositive", "\"TargetAmountCents\" > 0");
                t.HasCheckConstraint("CK_Goal_StartingNonNegative", "\"StartingAmountCents\" >= 0");
                t.HasCheckConstraint("CK_Goal_AccountTracking", "\"Tracking\" = 0 OR \"AccountId\" IS NOT NULL");
            });
        });

        b.Entity<GoalContribution>(e =>
        {
            e.Property(c => c.Note).HasMaxLength(200);
            e.HasOne(c => c.Goal).WithMany().HasForeignKey(c => c.GoalId)
                // Contributions are earmarks that belong to the goal, not money history.
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => c.GoalId);
            e.ToTable(t => t.HasCheckConstraint("CK_GoalContribution_NonZero", "\"AmountCents\" <> 0"));
        });

        b.Entity<Job>(e =>
        {
            e.Property(j => j.Name).HasMaxLength(100);
            e.Property(j => j.WorkLocationName).HasMaxLength(100);
            e.Property(j => j.WorkPsdCode).HasMaxLength(6);
            // Existing jobs get the same break rule as new ones (the user's employer policy).
            e.Property(j => j.UnpaidBreakMinutes).HasDefaultValue(30);
            e.Property(j => j.UnpaidBreakAfterMinutes).HasDefaultValue(300);
            e.Property(j => j.PaidBreakMinutes).HasDefaultValue(15);
            e.HasOne(j => j.DepositAccount).WithMany().HasForeignKey(j => j.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(j => j.IncomeCategory).WithMany().HasForeignKey(j => j.IncomeCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Job_PaydayOffset", "\"PaydayOffsetDays\" BETWEEN 0 AND 31");
                t.HasCheckConstraint("CK_Job_WorkEit", "\"WorkNonResidentEitBasisPoints\" BETWEEN 0 AND 1000");
                t.HasCheckConstraint("CK_Job_Lst", "\"WorkLstAnnualCents\" BETWEEN 0 AND 5200");
                t.HasCheckConstraint("CK_Job_Breaks",
                    "\"UnpaidBreakMinutes\" BETWEEN 0 AND 240 AND \"UnpaidBreakAfterMinutes\" BETWEEN 0 AND 1440 AND \"PaidBreakMinutes\" BETWEEN 0 AND 240");
            });
        });

        b.Entity<JobRate>(e =>
        {
            e.HasOne(r => r.Job).WithMany(j => j.Rates).HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.JobId, r.EffectiveDate }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_JobRate_Positive", "\"HourlyRateCents\" > 0"));
        });

        b.Entity<Shift>(e =>
        {
            e.Property(s => s.Note).HasMaxLength(200);
            e.HasOne(s => s.Job).WithMany().HasForeignKey(s => s.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.JobId, s.Date });
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Shift_Minutes", "\"ScheduledMinutes\" BETWEEN 1 AND 1440");
                t.HasCheckConstraint("CK_Shift_Break", "\"BreakOverrideMinutes\" IS NULL OR \"BreakOverrideMinutes\" >= 0");
            });
        });

        b.Entity<ImportBatch>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(260);
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ImportProfile>(e =>
        {
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.AccountId).IsUnique();
            e.Property(x => x.BankAccountKey).HasMaxLength(100);
            foreach (var p in new[] { "DateColumn", "DescriptionColumn", "AmountColumn", "DebitColumn", "CreditColumn", "MemoColumn", "ReferenceColumn", "DateFormat" })
            {
                e.Property(p).HasMaxLength(100);
            }
        });

        b.Entity<Reconciliation>(e =>
        {
            e.HasOne(r => r.Account).WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.AccountId, r.StatementDate });
        });

        b.Entity<ImportPayeeRule>(e =>
        {
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.Payee).HasMaxLength(200);
            e.HasIndex(x => x.Key).IsUnique();
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<JobDeduction>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(60);
            e.HasOne(d => d.Job).WithMany().HasForeignKey(d => d.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => d.JobId);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_JobDeduction_Amount", "\"AmountCents\" >= 0");
                t.HasCheckConstraint("CK_JobDeduction_Percent", "\"PercentBasisPoints\" IS NULL OR \"PercentBasisPoints\" BETWEEN 1 AND 10000");
                t.HasCheckConstraint("CK_JobDeduction_Dates", "\"EndDate\" IS NULL OR \"EndDate\" >= \"StartDate\"");
            });
        });

        b.Entity<Paystub>(e =>
        {
            e.HasOne(p => p.Job).WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => new { p.JobId, p.PeriodStart }).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Paystub_Amounts", "\"GrossCents\" >= 0 AND \"NetCents\" >= 0"));
        });

        b.Entity<PaycheckRecord>(e =>
        {
            e.HasOne(p => p.Job).WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Transaction).WithMany().HasForeignKey(p => p.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => new { p.JobId, p.PeriodStart }).IsUnique();
            // A paycheck whose recorded transaction was deleted counts as not recorded, like a bill.
            e.HasQueryFilter(p => !p.Transaction!.IsDeleted);
        });

        b.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });
    }

    /// <summary>A transfer must name a different destination account; other types must not name one.</summary>
    private const string TransferConstraintSql =
        "(\"TransactionType\" = 2 AND \"TransferAccountId\" IS NOT NULL AND \"TransferAccountId\" <> \"AccountId\") " +
        "OR (\"TransactionType\" <> 2 AND \"TransferAccountId\" IS NULL)";

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAuditFields()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Property(x => x.CreatedAt).IsModified = false;
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
