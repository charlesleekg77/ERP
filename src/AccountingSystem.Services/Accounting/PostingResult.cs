namespace AccountingSystem.Services.Accounting
{
    /// <summary>
    /// Outcome of a posting attempt. A posting is never partially applied: it
    /// either commits fully (Success = true) or rolls back with a reason.
    /// </summary>
    public class PostingResult
    {
        public bool Success { get; private set; }
        public long JournalId { get; private set; }
        public string VoucherNumber { get; private set; }
        public decimal TotalDebit { get; private set; }
        public decimal TotalCredit { get; private set; }
        public string ErrorMessage { get; private set; }

        public static PostingResult Ok(long journalId, string voucherNumber, decimal debit, decimal credit)
        {
            return new PostingResult
            {
                Success = true,
                JournalId = journalId,
                VoucherNumber = voucherNumber,
                TotalDebit = debit,
                TotalCredit = credit
            };
        }

        public static PostingResult Fail(string message)
        {
            return new PostingResult { Success = false, ErrorMessage = message };
        }
    }
}
