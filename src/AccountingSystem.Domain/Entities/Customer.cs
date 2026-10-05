using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>
    /// Accounts Receivable customer master record.
    /// </summary>
    [Table("Customer", Schema = "ar")]
    public class Customer
    {
        [Key]
        public int CustomerId { get; set; }

        [Required, StringLength(20)]
        [Index("IX_Customer_Code", IsUnique = true)]
        public string Code { get; set; }

        [Required, StringLength(150)]
        public string Name { get; set; }

        [EmailAddress, StringLength(200)]
        public string Email { get; set; }

        [StringLength(40)]
        public string Phone { get; set; }

        [StringLength(500)]
        public string BillingAddress { get; set; }

        [StringLength(40)]
        public string TaxId { get; set; }

        [Column(TypeName = "decimal")]
        public decimal CreditLimit { get; set; }

        public short PaymentTermsDays { get; set; } = 30;

        /// <summary>Optional override of the AR control account for this customer.</summary>
        public int? ArAccountId { get; set; }

        [Column(TypeName = "decimal")]
        public decimal Balance { get; set; }

        public bool IsActive { get; set; } = true;

        public bool IsDeleted { get; set; }

        [StringLength(100)]
        public string CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        [StringLength(100)]
        public string ModifiedBy { get; set; }

        public DateTime? ModifiedAt { get; set; }

        [Timestamp]
        public byte[] RowVersion { get; set; }

        /// <summary>True when a new invoice of the given amount would breach the credit limit.</summary>
        public bool WouldExceedCreditLimit(decimal additionalAmount) =>
            CreditLimit > 0 && Balance + additionalAmount > CreditLimit;
    }
}
