using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using AccountingSystem.Domain.Entities;

namespace AccountingSystem.Web.Models
{
    /// <summary>
    /// One editable line on the journal entry grid. Bound from the dynamic
    /// rows the user adds with jQuery.
    /// </summary>
    public class JournalLineViewModel
    {
        public int AccountId { get; set; }

        [StringLength(300)]
        public string Description { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Debit cannot be negative.")]
        public decimal Debit { get; set; }

        [Range(0, double.MaxValue, ErrorMessage = "Credit cannot be negative.")]
        public decimal Credit { get; set; }

        [StringLength(50)]
        public string CostCenter { get; set; }
    }

    /// <summary>
    /// View model for the "Create Journal Entry" screen. Carries the header
    /// fields, the dynamic lines, and the account list used to populate the
    /// account picker.
    /// </summary>
    public class JournalEntryViewModel
    {
        public long? JournalId { get; set; }

        [Display(Name = "Voucher Number")]
        [StringLength(30)]
        public string VoucherNumber { get; set; }

        [Required(ErrorMessage = "A transaction date is required.")]
        [DataType(DataType.Date)]
        [Display(Name = "Transaction Date")]
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow.Date;

        [StringLength(100)]
        public string Reference { get; set; }

        [StringLength(500)]
        public string Description { get; set; }

        /// <summary>The dynamic debit/credit lines.</summary>
        public List<JournalLineViewModel> Lines { get; set; } = new List<JournalLineViewModel>();

        /// <summary>Selectable postable accounts, for the account dropdowns.</summary>
        public IEnumerable<Account> Accounts { get; set; } = new List<Account>();

        /// <summary>Server-computed totals, echoed back so the grid can verify.</summary>
        public decimal TotalDebit => Lines?.Sum(l => l.Debit) ?? 0m;
        public decimal TotalCredit => Lines?.Sum(l => l.Credit) ?? 0m;
        public bool IsBalanced => TotalDebit == TotalCredit && TotalDebit > 0;
    }

    /// <summary>Request payload for the post/approve/void AJAX endpoints.</summary>
    public class JournalActionRequest
    {
        [Required]
        public long JournalId { get; set; }

        /// <summary>Required only for void.</summary>
        [StringLength(300)]
        public string Reason { get; set; }

        /// <summary>Optimistic-concurrency token (base64 RowVersion), when supplied.</summary>
        public string RowVersion { get; set; }
    }
}
