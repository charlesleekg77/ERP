using System;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using AccountingSystem.Data.Context;

namespace AccountingSystem.Web
{
    /// <summary>
    /// Application entry point. Registers MVC routes and filters and performs a
    /// fail-fast database readiness check at startup so a misconfigured
    /// connection string or missing schema is reported immediately rather than
    /// on the first user request.
    /// </summary>
    public class MvcApplication : HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();
            FilterConfig.RegisterGlobalFilters(GlobalFilters.Filters);
            RouteConfig.RegisterRoutes(RouteTable.Routes);

            // Strip server/framework version headers from every response.
            PreSendRequestHeaders += Application_PreSendRequestHeaders;

            // Fail fast on a broken deployment. Log only; do not surface the
            // connection details to end users.
            if (!DbInitializer.EnsureDatabaseReady(out var message))
            {
                System.Diagnostics.Trace.TraceError("Database readiness check failed: " + message);
            }
        }

        /// <summary>
        /// Suppress the ASP.NET version header and add a strict transport
        /// security header when serving over HTTPS.
        /// </summary>
        protected void Application_PreSendRequestHeaders(object sender, EventArgs e)
        {
            var response = Context?.Response;
            if (response == null) return;

            response.Headers.Remove("Server");
            response.Headers.Remove("X-AspNet-Version");
            response.Headers.Remove("X-AspNetMvc-Version");

            if (Request.IsSecureConnection)
                response.Headers.Add("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        }
    }
}
