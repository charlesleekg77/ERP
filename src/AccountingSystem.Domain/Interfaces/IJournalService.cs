using System;
using System.Collections.Generic;
using AccountingSystem.Domain.Entities;

namespace AccountingSystem.Domain.Interfaces
{
    /// <summary>
    /// Contract for the double-entry posting engine. Implemented by
    /// <c>AccountingSystem.Services.Accounting.JournalService</c>.
    /// </summary>
    public interface IJournalService
    {
        /// <summary>
        /// Persists a new journal entry in Draft state after validating the
        /// double-entry invariants. Returns the new JournalId.
        /// </summary>
        long CreateDraft(JournalHeader header, IEnumerable<JournalDetail> lines, string userName);

        /// <summary>
        /// Approves a draft entry (Draft -> Approved). Only users with the
        /// Approver or Administrator role may call this.
        /// </summary>
        void Approve(long journalId, string userName);

        /// <summary>
        /// Atomically posts an entry (Draft/Approved -> Posted) after verifying
        /// that total debits equal total credits. Throws
        /// <see cref="InvalidOperationException"/> when the entry is unbalanced
        /// or otherwise invalid. The whole operation runs inside a database
        /// transaction and rolls back on failure.
        /// </summary>
        void PostJournalEntry(long journalId, string userName);

        /// <summary>
        /// Voids a posted entry and generates the reversing entry.
        /// </summary>
        long VoidJournalEntry(long journalId, string userName, string reason);

        /// <summary>Loads a header with its lines.</summary>
        JournalHeader GetById(long journalId);
    }
}
