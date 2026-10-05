using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using AccountingSystem.Data;
using AccountingSystem.Domain.Entities;
using AccountingSystem.Domain.Enums;
using AccountingSystem.Services.Security;

namespace AccountingSystem.Services.Accounting
{
    /// <summary>
    /// Chart of Accounts read operations used by the UI (account pickers) and by
    /// validation. Writes to the COA are administrative and handled separately.
    /// </summary>
    public class AccountService
    {
        private readonly AuthorizationService _authorization;

        public AccountService() : this(new AuthorizationService()) { }

        public AccountService(AuthorizationService authorization)
        {
            _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        }

        /// <summary>
        /// Returns all active, postable accounts ordered by code, projected to a
        /// light shape suitable for a dropdown.
        /// </summary>
        public IList<Account> GetPostableAccounts()
        {
            using (var context = new AccountingDbContext())
            {
                return context.Accounts
                    .AsNoTracking()
                    .Where(a => a.IsActive && a.IsPostable && !a.IsDeleted)
                    .OrderBy(a => a.AccountCode)
                    .ToList();
            }
        }

        /// <summary>Returns the full account tree (for administration screens).</summary>
        public IList<Account> GetAllAccounts()
        {
            using (var context = new AccountingDbContext())
            {
                return context.Accounts
                    .AsNoTracking()
                    .Where(a => !a.IsDeleted)
                    .OrderBy(a => a.AccountCode)
                    .ToList();
            }
        }

        /// <summary>Looks up a single account by its code, e.g. "1100".</summary>
        public Account GetByCode(string accountCode)
        {
            if (string.IsNullOrWhiteSpace(accountCode)) return null;

            using (var context = new AccountingDbContext())
            {
                return context.Accounts
                    .AsNoTracking()
                    .FirstOrDefault(a => a.AccountCode == accountCode && !a.IsDeleted);
            }
        }

        /// <summary>
        /// Validates that every referenced account exists, is active and is
        /// postable. Returns the list of offending account ids (empty when valid).
        /// </summary>
        public IList<int> FindInvalidAccounts(IEnumerable<int> accountIds)
        {
            var ids = accountIds?.Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return ids;

            using (var context = new AccountingDbContext())
            {
                var valid = context.Accounts
                    .Where(a => ids.Contains(a.AccountId) && a.IsActive && a.IsPostable && !a.IsDeleted)
                    .Select(a => a.AccountId)
                    .ToList();

                return ids.Except(valid).ToList();
            }
        }
    }
}
