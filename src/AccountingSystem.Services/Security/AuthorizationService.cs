using System;
using System.Collections.Generic;
using System.Linq;

namespace AccountingSystem.Services.Security
{
    /// <summary>
    /// Role names used throughout the application. Kept as constants so a typo
    /// cannot silently grant or deny access.
    /// </summary>
    public static class Roles
    {
        public const string Administrator = "Administrator";
        public const string Accountant = "Accountant";
        public const string Approver = "Approver";
        public const string Clerk = "Clerk";
        public const string Auditor = "Auditor";

        public static readonly IReadOnlyList<string> All =
            new[] { Administrator, Accountant, Approver, Clerk, Auditor };
    }

    /// <summary>
    /// Application-level permissions checked by controllers and services.
    /// </summary>
    public enum Permission
    {
        ViewLedger,
        CreateDraftJournal,
        ApproveJournal,
        PostJournal,
        VoidJournal,
        ManageMasterData,
        ManageUsers,
        ClosePeriod,
        ViewAuditTrail
    }

    /// <summary>
    /// Role-Based Access Control. The mapping below is the single source of
    /// truth for who may perform what. Controllers call
    /// <see cref="HasPermission"/> (or the <c>[RequirePermission]</c> action
    /// filter) before performing a sensitive operation, and the service layer
    /// re-checks the posting-critical permissions so a UI bypass cannot escalate
    /// privilege.
    /// </summary>
    public class AuthorizationService
    {
        private static readonly IDictionary<string, HashSet<Permission>> Map =
            new Dictionary<string, HashSet<Permission>>(StringComparer.OrdinalIgnoreCase)
            {
                [Roles.Administrator] = new HashSet<Permission>(Enum.GetValues(typeof(Permission)).Cast<Permission>()),

                [Roles.Accountant] = new HashSet<Permission>
                {
                    Permission.ViewLedger,
                    Permission.CreateDraftJournal,
                    Permission.PostJournal,
                    Permission.ManageMasterData,
                    Permission.ClosePeriod,
                    Permission.ViewAuditTrail
                },

                // An approver can approve but cannot post: separation of duties.
                [Roles.Approver] = new HashSet<Permission>
                {
                    Permission.ViewLedger,
                    Permission.ApproveJournal
                },

                // A clerk can prepare documents but cannot post or approve.
                [Roles.Clerk] = new HashSet<Permission>
                {
                    Permission.ViewLedger,
                    Permission.CreateDraftJournal
                },

                [Roles.Auditor] = new HashSet<Permission>
                {
                    Permission.ViewLedger,
                    Permission.ViewAuditTrail
                }
            };

        /// <summary>True when any of the supplied roles grants the permission.</summary>
        public bool HasPermission(IEnumerable<string> roles, Permission permission)
        {
            if (roles == null) return false;
            foreach (var role in roles)
            {
                if (role != null && Map.TryGetValue(role, out var permissions) && permissions.Contains(permission))
                    return true;
            }
            return false;
        }

        /// <summary>Throws <see cref="UnauthorizedAccessException"/> when the permission is absent.</summary>
        public void Demand(IEnumerable<string> roles, Permission permission)
        {
            if (!HasPermission(roles, permission))
                throw new UnauthorizedAccessException(
                    $"The current user does not have the '{permission}' permission.");
        }
    }
}
