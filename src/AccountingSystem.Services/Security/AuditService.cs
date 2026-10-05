using System;
using AccountingSystem.Data;
using AccountingSystem.Domain.Entities;
using AccountingSystem.Domain.Enums;
using Newtonsoft.Json;

namespace AccountingSystem.Services.Security
{
    /// <summary>
    /// Writes entries to the append-only <c>sec.AuditLog</c> table.
    ///
    /// Every mutating service operation calls <see cref="Write"/> with the
    /// before-image and after-image of the affected record. The images are
    /// serialised to JSON with a bounded depth so a rich object graph cannot
    /// blow up the row. The table is protected against UPDATE/DELETE by
    /// <c>sec.trg_AuditLog_Immutable</c>, so once written a row cannot be altered
    /// even by a database administrator using the application login.
    /// </summary>
    public class AuditService
    {
        private const int MaxJsonLength = 200_000;

        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            // Ignore navigation properties and cycles; audit only scalar state.
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss.fffZ",
            MaxDepth = 8
        };

        /// <summary>
        /// Adds an audit row to the supplied context. The caller is responsible
        /// for calling <c>SaveChanges</c> so the audit row commits in the same
        /// transaction as the change it describes.
        /// </summary>
        /// <param name="context">The active EF context (participates in its transaction).</param>
        /// <param name="schema">Logical schema, e.g. "gl".</param>
        /// <param name="table">Table name, e.g. "JournalHeader".</param>
        /// <param name="recordId">Primary key of the affected row, as a string.</param>
        /// <param name="action">Insert, Update or Delete.</param>
        /// <param name="oldValues">Snapshot before the change (null for inserts).</param>
        /// <param name="newValues">Snapshot after the change (null for deletes).</param>
        /// <param name="userId">Authenticated user id (login name).</param>
        /// <param name="ipAddress">Client IP address, when available.</param>
        public void Write(AccountingDbContext context, string schema, string table, string recordId,
                          AuditAction action, object oldValues, object newValues,
                          string userId, string ipAddress)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            var entry = new AuditLog
            {
                TableSchema = schema,
                TableName = table,
                RecordId = recordId ?? string.Empty,
                Action = action.ToString().ToUpperInvariant(),
                OldValues = Serialize(oldValues),
                NewValues = Serialize(newValues),
                UserId = userId,
                UserName = userId,
                IpAddress = ipAddress,
                OccurredAt = DateTime.UtcNow
            };

            context.AuditLogs.Add(entry);
        }

        private static string Serialize(object value)
        {
            if (value == null) return null;
            var json = JsonConvert.SerializeObject(value, Settings);
            if (json.Length > MaxJsonLength)
                json = json.Substring(0, MaxJsonLength) + "\"...truncated\"";
            return json;
        }
    }
}
