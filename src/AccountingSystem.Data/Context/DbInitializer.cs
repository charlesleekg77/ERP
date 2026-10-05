using System.Linq;

namespace AccountingSystem.Data.Context
{
    /// <summary>
    /// Schema initialisation is deliberately a no-op.
    ///
    /// The database is provisioned by the versioned DDL scripts in /database
    /// and applied through SSMS or the Visual Studio SQL project. EF is used
    /// purely as an ORM against that schema, so we never let it create, migrate
    /// or drop tables. <see cref="AccountingDbContext"/> sets a null initializer,
    /// and this class documents that decision and provides a health check.
    /// </summary>
    public static class DbInitializer
    {
        /// <summary>
        /// Confirms the schema is present and reachable. Returns false (with a
        /// reason) when the database or core table is missing, so the caller can
        /// present an actionable message instead of an EF stack trace.
        /// </summary>
        public static bool EnsureDatabaseReady(out string message)
        {
            try
            {
                using (var context = new AccountingDbContext())
                {
                    // Touching a mapped table proves both connectivity and that
                    // the DDL scripts have been applied.
                    var accountCount = context.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM gl.Account").FirstOrDefault();

                    if (accountCount == 0)
                    {
                        message = "The database is reachable but the Chart of Accounts is empty. " +
                                  "Run database/05_SeedChartOfAccounts.sql.";
                        return false;
                    }

                    message = $"Database ready. {accountCount} accounts loaded.";
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                message = "Database is not ready: " + ex.Message;
                return false;
            }
        }
    }
}
