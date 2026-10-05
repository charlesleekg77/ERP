using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Security.Principal;
using AccountingSystem.Services.Security;

namespace AccountingSystem.Web.Infrastructure
{
    /// <summary>
    /// Resolves the application roles held by the current principal.
    ///
    /// Windows authentication yields Windows group membership, and real Active
    /// Directory groups are rarely named "Administrator" or "Accountant". The
    /// mapping from Windows group to application role is therefore read from
    /// configuration, one key per role:
    ///
    /// <code>
    /// &lt;add key="Accounting:RoleMap:Administrator" value="DOMAIN\Accounting-Admins" /&gt;
    /// &lt;add key="Accounting:RoleMap:Approver"      value="DOMAIN\Accounting-Approvers" /&gt;
    /// </code>
    ///
    /// Multiple groups may be listed, separated by ';' or ','. When a custom
    /// role provider is in use (forms or claims authentication) the principal's
    /// own roles are used directly, so no mapping is required.
    ///
    /// Both <see cref="RequirePermissionAttribute"/> and the controllers call
    /// this single resolver, so the permission filter and any service-level
    /// authorisation always agree on a user's roles.
    /// </summary>
    public static class RoleResolver
    {
        private static readonly Lazy<IDictionary<string, string>> GroupToRoleMap =
            new Lazy<IDictionary<string, string>>(BuildGroupToRoleMap);

        /// <summary>Returns the application roles held by the principal.</summary>
        public static IEnumerable<string> GetRoles(IPrincipal user)
        {
            if (user?.Identity == null || !user.Identity.IsAuthenticated)
                return Enumerable.Empty<string>();

            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // (1) Map Windows group membership to application roles.
            if (user is WindowsPrincipal windowsPrincipal)
            {
                foreach (var mapping in GroupToRoleMap.Value)
                {
                    try
                    {
                        if (windowsPrincipal.IsInRole(mapping.Key)) resolved.Add(mapping.Value);
                    }
                    catch
                    {
                        // A group name that the OS cannot resolve is simply skipped.
                    }
                }
            }

            // (2) Honour role claims / role provider names directly. This also
            //     covers forms or federated authentication.
            foreach (var role in Roles.All)
            {
                try
                {
                    if (user.IsInRole(role)) resolved.Add(role);
                }
                catch
                {
                    // Ignore providers that throw on an unknown role name.
                }
            }

            // (3) Optional development escape hatch. Empty by default, so an
            //     unmapped authenticated user has NO roles and is denied.
            if (resolved.Count == 0)
            {
                var fallback = ConfigurationManager.AppSettings["Accounting:DefaultRoleForAuthenticatedUsers"];
                if (!string.IsNullOrWhiteSpace(fallback) && Roles.All.Contains(fallback, StringComparer.OrdinalIgnoreCase))
                {
                    resolved.Add(fallback);
                }
            }

            return resolved;
        }

        private static IDictionary<string, string> BuildGroupToRoleMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var role in Roles.All)
            {
                var configured = ConfigurationManager.AppSettings["Accounting:RoleMap:" + role];
                if (string.IsNullOrWhiteSpace(configured)) continue;

                foreach (var group in configured.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = group.Trim();
                    if (trimmed.Length > 0) map[trimmed] = role;
                }
            }

            return map;
        }
    }
}
