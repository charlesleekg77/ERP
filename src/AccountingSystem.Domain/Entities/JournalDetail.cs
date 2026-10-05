using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>
    /// A single debit or credit line belonging to a <see cref="JournalHeader"/>.
    /// Exactly one of <see cref="Debit"/> or <see cref="Credit"/> is non-zero.
    /// </summary>
    [Table("JournalDetail", Schema = "gl")]
    public class JournalDetail
    {
        [Key]
        public long JournalDetailId { get; set; }

        public long JournalId { get; set; }

        public int LineNumber { get; set; }

        public int AccountId { get; set; }

        [Column(TypeName = "decimal")]
        [Range(0, double.MaxValue, ErrorMessage = "Debit cannot be negative.")]
        public decimal Debit { get; set; }

        [Column(TypeName = "decimal")]
        [Range(0, double.MaxValue, ErrorMessage = "Credit cannot be negative.")]
        public decimal Credit { get; set; }

        [StringLength(300)]
        public string Description { get; set; }

        [StringLength(50)]
        public string CostCenter { get; set; }

        [ForeignKey(nameof(JournalId))]
        public virtual JournalHeader Header { get; set; }

        [ForeignKey(nameof(AccountId))]
        public virtual Account Account { get; set; }

        /// <summary>The signed amount from the perspective of the account's natural side.</summary>
        [NotMapped]
        public decimal SignedAmount => Debit - Credit;
    }
}
