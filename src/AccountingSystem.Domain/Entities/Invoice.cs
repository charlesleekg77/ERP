using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AccountingSystem.Domain.Enums;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>Accounts Receivable sales invoice.</summary>
    [Table("Invoice", Schema = "ar")]
    public class Invoice
    {
        [Key]
        public long InvoiceId { get; set; }

        [Required, StringLength(30)]
        [Index("IX_Invoice_Number", IsUnique = true)]
        public string InvoiceNumber { get; set; }

        public int CustomerId { get; set; }

        [Column(TypeName = "date")]
        public DateTime IssueDate { get; set; }

        [Column(TypeName = "date")]
        public DateTime DueDate { get; set; }

        [Column(TypeName = "decimal")]
        public decimal SubTotal { get; set; }

        [Column(TypeName = "decimal")]
        public decimal TaxAmount { get; set; }

        [Column(TypeName = "decimal")]
        public decimal TotalAmount { get; set; }

        [Column(TypeName = "decimal")]
        public decimal PaidAmount { get; set; }

        public DocumentStatus Status { get; set; } = DocumentStatus.Draft;

        /// <summary>The GL journal that recognised this invoice.</summary>
        public long? JournalId { get; set; }

        [StringLength(500)]
        public string Notes { get; set; }

        public bool IsDeleted { get; set; }

        [StringLength(100)]
        public string CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        [StringLength(100)]
        public string ModifiedBy { get; set; }

        public DateTime? ModifiedAt { get; set; }

        [Timestamp]
        public byte[] RowVersion { get; set; }

        [ForeignKey(nameof(CustomerId))]
        public virtual Customer Customer { get; set; }

        public virtual ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();

        [NotMapped]
        public decimal OutstandingAmount => TotalAmount - PaidAmount;

        [NotMapped]
        public bool IsOverdue => Status != DocumentStatus.Paid
                              && Status != DocumentStatus.Void
                              && DueDate < DateTime.UtcNow.Date;
    }

    /// <summary>Itemised line on a sales invoice.</summary>
    [Table("InvoiceLine", Schema = "ar")]
    public class InvoiceLine
    {
        [Key]
        public long InvoiceLineId { get; set; }

        public long InvoiceId { get; set; }

        public int LineNumber { get; set; }

        [Required, StringLength(300)]
        public string Description { get; set; }

        [Column(TypeName = "decimal")]
        public decimal Quantity { get; set; } = 1;

        [Column(TypeName = "decimal")]
        public decimal UnitPrice { get; set; }

        /// <summary>Tax rate as a percentage, e.g. 20.0000 for 20%.</summary>
        [Column(TypeName = "decimal")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal")]
        public decimal LineTotal { get; set; }

        public int? RevenueAccountId { get; set; }

        [ForeignKey(nameof(InvoiceId))]
        public virtual Invoice Invoice { get; set; }

        [NotMapped]
        public decimal NetAmount => Math.Round(Quantity * UnitPrice, 4, MidpointRounding.AwayFromZero);

        [NotMapped]
        public decimal TaxAmount => Math.Round(NetAmount * TaxRate / 100m, 4, MidpointRounding.AwayFromZero);
    }
}
