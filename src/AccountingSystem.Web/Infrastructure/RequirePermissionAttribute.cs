using System;
using System.Web;
using System.Web.Mvc;
using AccountingSystem.Services.Security;

namespace AccountingSystem.Web.Infrastructure
{
    /// <summary>
    /// MVC action filter that enforces the RBAC permission model. Apply it to a
    /// controller or action to require a specific <see cref="Permission"/>.
    ///
    /// <code>
    /// [RequirePermission(Permission.PostJournal)]
    /// public ActionResult Post(long id) { ... }
    /// </code>
    ///
    /// Roles are read from the authenticated principal's role claims (populated
    /// by Windows authentication / the application role provider). A user whose
    /// roles do not grant the permission receives HTTP 403.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public class RequirePermissionAttribute : AuthorizeAttribute
    {
        private readonly Permission _permission;
        private static readonly AuthorizationService Authorization = new AuthorizationService();

        public RequirePermissionAttribute(Permission permission)
        {
            _permission = permission;
        }

        protected override bool AuthorizeCore(HttpContextBase httpContext)
        {
            if (httpContext == null) throw new ArgumentNullException(nameof(httpContext));
            if (!httpContext.User.Identity.IsAuthenticated) return false;

            var roles = RoleResolver.GetRoles(httpContext.User);
            return Authorization.HasPermission(roles, _permission);
        }
    }
}
