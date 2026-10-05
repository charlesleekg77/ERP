using System.Web.Mvc;

namespace AccountingSystem.Web
{
    /// <summary>Registers global MVC action filters.</summary>
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            // Never render an unhandled exception (or its stack trace) to the user.
            filters.Add(new HandleErrorAttribute());

            // Require an authenticated user everywhere by default; individual
            // actions opt out with [AllowAnonymous].
            filters.Add(new AuthorizeAttribute());
        }
    }
}
