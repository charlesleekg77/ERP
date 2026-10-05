using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>
    /// Append-only audit trail row. Written by the service-layer
    /// <c>AuditService</c> and by database triggers for direct SQL access.
    /// Rows are never updated or deleted (enforced by <c>sec.trg_AuditLog_Immutable</c>).
    /// </summary>
    [Table("AuditLog", Schema = "sec")]
    public class AuditLog
    {
        [Key]
        public long AuditId { get; set; }

        [Required, StringLength(20)]
        public string TableSchema { get; set; }

        [Required, StringLength(128)]
        public string TableName { get; set; }

        [Required, StringLength(64)]
        public string RecordId { get; set; }

        [Required, StringLength(10)]
        public string Action { get; set; }

        /// <summary>JSON snapshot of the row before the change (NULL on insert).</summary>
        public string OldValues { get; set; }

        /// <summary>JSON snapshot of the row after the change (NULL on delete).</summary>
        public string NewValues { get; set; }

        [StringLength(100)]
        public string UserId { get; set; }

        [StringLength(150)]
        public string UserName { get; set; }

        [StringLength(45)]
        public string IpAddress { get; set; }

        public DateTime OccurredAt { get; set; }
    }
}
