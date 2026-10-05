using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;

namespace AccountingSystem.Data.Context
{
    /// <summary>
    /// Central access point for the accounting database connection string and
    /// for creating open connections.
    ///
    /// The connection string is read from
    /// <c>ConfigurationManager.ConnectionStrings["AccountingDbConnection"]</c>,
    /// which is defined in web.config. No connection string is ever hard-coded,
    /// so the same assemblies run against LocalDB, SQLEXPRESS, or a full SQL
    /// Server instance by changing configuration only.
    ///
    /// Security notes:
    ///  - Prefer Integrated Security (Windows authentication) in production so
    ///    no password is stored in configuration.
    ///  - When SQL authentication is required, keep the credential out of source
    ///    control (use an encrypted config section, a secrets vault, or IIS
    ///    application-pool environment variables).
    /// </summary>
    public static class DbConnectionFactory
    {
        /// <summary>Logical name of the connection string in web.config.</summary>
        public const string ConnectionStringName = "AccountingDbConnection";

        private static string _connectionString;

        /// <summary>
        /// The resolved connection string. Cached after first read.
        /// Throws <see cref="ConfigurationErrorsException"/> when the entry is
        /// missing so misconfiguration fails fast at startup rather than on the
        /// first query.
        /// </summary>
        public static string ConnectionString
        {
            get
            {
                if (_connectionString != null) return _connectionString;

                var setting = ConfigurationManager.ConnectionStrings[ConnectionStringName];
                if (setting == null || string.IsNullOrWhiteSpace(setting.ConnectionString))
                {
                    throw new ConfigurationErrorsException(
                        $"Connection string '{ConnectionStringName}' was not found in web.config. " +
                        "Add a <connectionStrings> entry with that name.");
                }

                _connectionString = setting.ConnectionString;
                return _connectionString;
            }
        }

        /// <summary>
        /// The provider invariant name for the configured connection, used by
        /// EF6 when constructing a context from a raw connection string.
        /// </summary>
        public static string ProviderName
        {
            get
            {
                var setting = ConfigurationManager.ConnectionStrings[ConnectionStringName];
                return setting?.ProviderName ?? "System.Data.SqlClient";
            }
        }

        /// <summary>
        /// Creates a new, closed <see cref="SqlConnection"/>. Callers are
        /// responsible for disposing it (use a <c>using</c> block).
        /// </summary>
        public static SqlConnection CreateConnection()
        {
            return new SqlConnection(ConnectionString);
        }

        /// <summary>
        /// Creates and opens a connection. The returned connection is open and
        /// ready for use.
        /// </summary>
        public static SqlConnection CreateOpenConnection()
        {
            var connection = new SqlConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Verifies connectivity and reports the effective server/database.
        /// Intended for a health-check page or startup diagnostics. Never expose
        /// the raw connection string (it may contain a password) to end users.
        /// </summary>
        public static bool TestConnection(out string serverDescription, out string errorMessage)
        {
            serverDescription = null;
            errorMessage = null;
            try
            {
                using (var connection = CreateOpenConnection())
                using (var command = new SqlCommand("SELECT DB_NAME(), SUSER_SNAME(), @@SERVERNAME;", connection))
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        serverDescription =
                            $"Database={reader.GetString(0)}; Login={reader.GetString(1)}; Server={reader.GetString(2)}";
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                // Surface a generic message to the caller; log the detail server-side.
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>Creates a command bound to a connection with a timeout.</summary>
        public static SqlCommand CreateCommand(string storedProcedure, SqlConnection connection,
                                               int timeoutSeconds = 30)
        {
            var command = new SqlCommand(storedProcedure, connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = timeoutSeconds
            };
            return command;
        }
    }
}
