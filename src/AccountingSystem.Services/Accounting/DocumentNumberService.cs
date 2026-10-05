using System;
using System.Data;
using System.Data.SqlClient;
using AccountingSystem.Data.Context;
using AccountingSystem.Domain.Entities;
using AccountingSystem.Domain.Enums;

namespace AccountingSystem.Services.Accounting
{
    /// <summary>
    /// Allocates sequential document numbers (voucher, invoice, bill, receipt)
    /// by delegating to <c>gl.usp_NextDocumentNumber</c>, which uses an
    /// UPDLOCK/HOLDLOCK counter so concurrent callers cannot collide.
    ///
    /// Numbering format: {PREFIX}-{YYYY}-{000000}, e.g. JV-2026-000042.
    /// </summary>
    public class DocumentNumberService
    {
        private static readonly string[] ValidPrefixes = { "JV", "REV", "INV", "RCPT", "BILL", "PV", "PO" };

        /// <summary>
        /// Returns the next number for the prefix in the given fiscal year, e.g.
        /// "JV-2026-000042". The counter increment happens inside its own short
        /// transaction on the server.
        /// </summary>
        public string GetNextNumber(string prefix, int fiscalYear)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Prefix is required.", nameof(prefix));

            prefix = prefix.Trim().ToUpperInvariant();
            if (Array.IndexOf(ValidPrefixes, prefix) < 0)
                throw new ArgumentException($"Unknown document prefix '{prefix}'.", nameof(prefix));

            int next;
            using (var connection = DbConnectionFactory.CreateOpenConnection())
            using (var command = DbConnectionFactory.CreateCommand("gl.usp_NextDocumentNumber", connection))
            {
                command.Parameters.Add("@Prefix", SqlDbType.NVarChar, 10).Value = prefix;
                command.Parameters.Add("@FiscalYear", SqlDbType.SmallInt).Value = (short)fiscalYear;
                var outParam = command.Parameters.Add("@NextNumber", SqlDbType.Int);
                outParam.Direction = ParameterDirection.Output;

                command.ExecuteNonQuery();
                next = (int)outParam.Value;
            }

            return $"{prefix}-{fiscalYear}-{next:D6}";
        }

        /// <summary>
        /// Convenience overload that derives the fiscal year from the transaction
        /// date. This is the method the journal service calls.
        /// </summary>
        public string GetNextNumber(string prefix, DateTime transactionDate)
            => GetNextNumber(prefix, transactionDate.Year);
    }
}
