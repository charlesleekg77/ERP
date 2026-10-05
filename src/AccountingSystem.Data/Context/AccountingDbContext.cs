using System.Data.Entity;
using System.Data.Entity.ModelConfiguration.Conventions;
using AccountingSystem.Data.Context;
using AccountingSystem.Domain.Entities;

namespace AccountingSystem.Data
{
    /// <summary>
    /// Entity Framework 6 DbContext for the accounting database.
    ///
    /// The context is constructed from the raw connection string read by
    /// <see cref="DbConnectionFactory"/> rather than by name, so the connection
    /// string defined in web.config is the single source of truth.
    ///
    /// Schema management is owned by the DDL scripts in /database, not by EF
    /// migrations. Automatic migrations and database initializers are therefore
    /// disabled: <c>Database.SetInitializer(null)</c> in the static constructor
    /// prevents EF from ever attempting to create or drop a schema.
    /// </summary>
    public class AccountingDbContext : DbContext
    {
        static AccountingDbContext()
        {
            // Never let EF create or drop the schema. The database is provisioned
            // by the versioned SQL scripts under /database.
            Database.SetInitializer<AccountingDbContext>(null);
        }

        /// <summary>
        /// Constructs the context using the web.config connection string.
        /// </summary>
        public AccountingDbContext()
            : base(DbConnectionFactory.CreateConnection(), contextOwnsConnection: true)
        {
            Configure();
        }

        /// <summary>
        /// Constructs the context with an explicit connection. Used by tests and
        /// by the service layer when participating in an ambient transaction.
        /// </summary>
        public AccountingDbContext(System.Data.Common.DbConnection connection, bool contextOwnsConnection)
            : base(connection, contextOwnsConnection)
        {
            Configure();
        }

        private void Configure()
        {
            Configuration.LazyLoadingEnabled = false;
            Configuration.ProxyCreationEnabled = false;
            Configuration.ValidateOnSaveEnabled = true;

            // Preserve decimal precision end to end; never truncate financial values.
            Configuration.AutoDetectChangesEnabled = true;
        }

        // ----------------------------- GL -----------------------------
        public virtual DbSet<Account> Accounts { get; set; }
        public virtual DbSet<FiscalPeriod> FiscalPeriods { get; set; }
        public virtual DbSet<JournalHeader> JournalHeaders { get; set; }
        public virtual DbSet<JournalDetail> JournalDetails { get; set; }

        // ----------------------------- AR -----------------------------
        public virtual DbSet<Customer> Customers { get; set; }
        public virtual DbSet<Invoice> Invoices { get; set; }
        public virtual DbSet<InvoiceLine> InvoiceLines { get; set; }

        // ----------------------------- AP -----------------------------
        public virtual DbSet<Vendor> Vendors { get; set; }
        public virtual DbSet<VendorBill> VendorBills { get; set; }
        public virtual DbSet<VendorBillLine> VendorBillLines { get; set; }

        // ------------------------- Security ---------------------------
        public virtual DbSet<AuditLog> AuditLogs { get; set; }

        protected override void OnModelCreating(DbModelBuilder modelBuilder)
        {
            // Use the exact table names declared via [Table] attributes; do not
            // pluralise.
            modelBuilder.Conventions.Remove<PluralizingTableNameConvention>();

            // Money is always DECIMAL(19,4). Set precision explicitly so EF does
            // not fall back to the provider default.
            modelBuilder.Entity<Account>().Property(a => a.AccountCode).HasMaxLength(20);
            modelBuilder.Entity<JournalHeader>().Property(j => j.TotalDebit).HasPrecision(19, 4);
            modelBuilder.Entity<JournalHeader>().Property(j => j.TotalCredit).HasPrecision(19, 4);
            modelBuilder.Entity<JournalDetail>().Property(j => j.Debit).HasPrecision(19, 4);
            modelBuilder.Entity<JournalDetail>().Property(j => j.Credit).HasPrecision(19, 4);
            modelBuilder.Entity<Invoice>().Property(i => i.SubTotal).HasPrecision(19, 4);
            modelBuilder.Entity<Invoice>().Property(i => i.TaxAmount).HasPrecision(19, 4);
            modelBuilder.Entity<Invoice>().Property(i => i.TotalAmount).HasPrecision(19, 4);
            modelBuilder.Entity<Invoice>().Property(i => i.PaidAmount).HasPrecision(19, 4);
            modelBuilder.Entity<InvoiceLine>().Property(i => i.Quantity).HasPrecision(19, 4);
            modelBuilder.Entity<InvoiceLine>().Property(i => i.UnitPrice).HasPrecision(19, 4);
            modelBuilder.Entity<InvoiceLine>().Property(i => i.TaxRate).HasPrecision(9, 4);
            modelBuilder.Entity<InvoiceLine>().Property(i => i.LineTotal).HasPrecision(19, 4);
            modelBuilder.Entity<Customer>().Property(c => c.CreditLimit).HasPrecision(19, 4);
            modelBuilder.Entity<Customer>().Property(c => c.Balance).HasPrecision(19, 4);
            modelBuilder.Entity<Vendor>().Property(v => v.Balance).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBill>().Property(v => v.SubTotal).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBill>().Property(v => v.TaxAmount).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBill>().Property(v => v.TotalAmount).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBill>().Property(v => v.PaidAmount).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBillLine>().Property(v => v.Quantity).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBillLine>().Property(v => v.UnitPrice).HasPrecision(19, 4);
            modelBuilder.Entity<VendorBillLine>().Property(v => v.TaxRate).HasPrecision(9, 4);
            modelBuilder.Entity<VendorBillLine>().Property(v => v.LineTotal).HasPrecision(19, 4);

            // A journal header owns its lines; deleting a draft removes them.
            modelBuilder.Entity<JournalDetail>()
                .HasRequired(d => d.Header)
                .WithMany(h => h.Lines)
                .HasForeignKey(d => d.JournalId)
                .WillCascadeOnDelete(true);

            base.OnModelCreating(modelBuilder);
        }
    }
}
