using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using AccountingSystem.Domain.Enums;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>
    /// A node in the Chart of Accounts. Accounts form a tree via
    /// <see cref="ParentAccountId"/>. Only leaf accounts with
    /// <see cref="IsPostable"/> set may be referenced by journal lines.
    /// </summary>
    [Table("Account", Schema = "gl")]
    public class Account
    {
        [Key]
        public int AccountId { get; set; }

        /// <summary>Hierarchical code, e.g. 1010. See the numbering convention in the DDL.</summary>
        [Required, StringLength(20)]
        [Index("IX_Account_Code", IsUnique = true)]
        public string AccountCode { get; set; }

        [Required, StringLength(150)]
        public string AccountName { get; set; }

        public AccountType AccountType { get; set; }

        public NormalBalance NormalBalance { get; set; }

        public int? ParentAccountId { get; set; }

        [StringLength(500)]
        public string Description { get; set; }

        /// <summary>True when journal lines may target this account (i.e. it is a leaf).</summary>
        public bool IsPostable { get; set; } = true;

        public bool IsActive { get; set; } = true;

        /// <summary>Soft-delete flag; master data is never physically removed.</summary>
        public bool IsDeleted { get; set; }

        [StringLength(100)]
        public string CreatedBy { get; set; }

        public DateTime CreatedAt { get; set; }

        [StringLength(100)]
        public string ModifiedBy { get; set; }

        public DateTime? ModifiedAt { get; set; }

        [Timestamp]
        public byte[] RowVersion { get; set; }

        [ForeignKey(nameof(ParentAccountId))]
        public virtual Account ParentAccount { get; set; }

        public virtual ICollection<Account> Children { get; set; } = new List<Account>();
    }
}
