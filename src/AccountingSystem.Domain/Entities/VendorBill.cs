using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AccountingSystem.Domain.Enums;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>Accounts Payable vendor bill (supplier invoice).</summary>
    [Table("VendorBill", Schema = "ap")]
    public class VendorBill
    {
        [Key]
        public long BillId { get; set; }

        /// <summary>Our internal document number.</summary>
        [Required, StringLength(30)]
        [Index("IX_VendorBill_Number", IsUnique = true)]
        public string BillNumber { get; set; }

        /// <summary>The vendor's own invoice reference.</summary>
        [StringLength(40)]
        public string VendorInvoiceNo { get; set; }

        public int VendorId { get; set; }

        public long? PurchaseOrderId { get; set; }

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

        [ForeignKey(nameof(VendorId))]
        public virtual Vendor Vendor { get; set; }

        public virtual ICollection<VendorBillLine> Lines { get; set; } = new List<VendorBillLine>();

        [NotMapped]
        public decimal OutstandingAmount => TotalAmount - PaidAmount;
    }

    /// <summary>Itemised line on a vendor bill.</summary>
    [Table("VendorBillLine", Schema = "ap")]
    public class VendorBillLine
    {
        [Key]
        public long BillLineId { get; set; }

        public long BillId { get; set; }

        public int LineNumber { get; set; }

        [Required, StringLength(300)]
        public string Description { get; set; }

        [Column(TypeName = "decimal")]
        public decimal Quantity { get; set; } = 1;

        [Column(TypeName = "decimal")]
        public decimal UnitPrice { get; set; }

        [Column(TypeName = "decimal")]
        public decimal TaxRate { get; set; }

        [Column(TypeName = "decimal")]
        public decimal LineTotal { get; set; }

        public int? ExpenseAccountId { get; set; }

        [ForeignKey(nameof(BillId))]
        public virtual VendorBill Bill { get; set; }

        [NotMapped]
        public decimal NetAmount => Math.Round(Quantity * UnitPrice, 4, MidpointRounding.AwayFromZero);

        [NotMapped]
        public decimal TaxAmount => Math.Round(NetAmount * TaxRate / 100m, 4, MidpointRounding.AwayFromZero);
    }
}
