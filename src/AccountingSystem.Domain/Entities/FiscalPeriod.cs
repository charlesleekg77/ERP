using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace AccountingSystem.Domain.Entities
{
    /// <summary>
    /// Accounting period. Posting into a closed period is rejected by both the
    /// service layer and <c>gl.usp_PostJournalEntry</c>.
    /// </summary>
    [Table("FiscalPeriod", Schema = "gl")]
    public class FiscalPeriod
    {
        [Key]
        public int FiscalPeriodId { get; set; }

        public short FiscalYear { get; set; }

        public byte PeriodNumber { get; set; }

        [Required, StringLength(40)]
        public string PeriodName { get; set; }

        [Column(TypeName = "date")]
        public DateTime StartDate { get; set; }

        [Column(TypeName = "date")]
        public DateTime EndDate { get; set; }

        public bool IsClosed { get; set; }

        [StringLength(100)]
        public string ClosedBy { get; set; }

        public DateTime? ClosedAt { get; set; }
    }
}
