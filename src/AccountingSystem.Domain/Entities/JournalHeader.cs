using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AccountingSystem.Domain.Enums;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>
    /// The journal voucher header. Aggregates one or more
    /// <see cref="JournalDetail"/> lines that must balance.
    /// </summary>
    [Table("JournalHeader", Schema = "gl")]
    public class JournalHeader
    {
        [Key]
        public long JournalId { get; set; }

        [Required, StringLength(30)]
        [Index("IX_JournalHeader_Voucher", IsUnique = true)]
        public string VoucherNumber { get; set; }

        [Column(TypeName = "date")]
        public DateTime TransactionDate { get; set; }

        [Column(TypeName = "date")]
        public DateTime? PostingDate { get; set; }

        [StringLength(100)]
        public string Reference { get; set; }

        [StringLength(500)]
        public string Description { get; set; }

        public int? FiscalPeriodId { get; set; }

        public JournalStatus Status { get; set; } = JournalStatus.Draft;

        [Column(TypeName = "decimal")]
        public decimal TotalDebit { get; set; }

        [Column(TypeName = "decimal")]
        public decimal TotalCredit { get; set; }

        [StringLength(20)]
        public string SourceModule { get; set; }

        public long? SourceDocumentId { get; set; }

        [StringLength(100)]
        public string CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        [StringLength(100)]
        public string ApprovedBy { get; set; }

        public DateTime? ApprovedAt { get; set; }

        [StringLength(100)]
        public string PostedBy { get; set; }

        public DateTime? PostedAt { get; set; }

        [StringLength(100)]
        public string VoidedBy { get; set; }

        public DateTime? VoidedAt { get; set; }

        [StringLength(300)]
        public string VoidReason { get; set; }

        public bool IsDeleted { get; set; }

        [Timestamp]
        public byte[] RowVersion { get; set; }

        [ForeignKey(nameof(FiscalPeriodId))]
        public virtual FiscalPeriod FiscalPeriod { get; set; }

        public virtual ICollection<JournalDetail> Lines { get; set; } = new List<JournalDetail>();

        /// <summary>True when the sum of debits equals the sum of credits.</summary>
        [NotMapped]
        public bool IsBalanced => TotalDebit == TotalCredit;

        /// <summary>True when the entry may still be edited (Draft or Approved).</summary>
        [NotMapped]
        public bool IsEditable => Status == JournalStatus.Draft || Status == JournalStatus.Approved;
    }
}
