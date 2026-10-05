using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>Accounts Payable vendor master record.</summary>
    [Table("Vendor", Schema = "ap")]
    public class Vendor
    {
        [Key]
        public int VendorId { get; set; }

        [Required, StringLength(20)]
        [Index("IX_Vendor_Code", IsUnique = true)]
        public string Code { get; set; }

        [Required, StringLength(150)]
        public string Name { get; set; }

        [EmailAddress, StringLength(200)]
        public string Email { get; set; }

        [StringLength(40)]
        public string Phone { get; set; }

        [StringLength(500)]
        public string Address { get; set; }

        [StringLength(40)]
        public string TaxId { get; set; }

        public short PaymentTermsDays { get; set; } = 30;

        /// <summary>Optional override of the AP control account for this vendor.</summary>
        public int? ApAccountId { get; set; }

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
    }
}
