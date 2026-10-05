using System.Web.Mvc;
using AccountingSystem.Data.Context;

namespace AccountingSystem.Web.Controllers
{
    /// <summary>Landing page and error pages.</summary>
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.UserName = User?.Identity?.Name;
            return View();
        }

        [AllowAnonymous]
        public ActionResult Error()
        {
            // The specific exception is logged server-side by the HandleError
            // filter; the view shows only a generic message.
            Response.StatusCode = 500;
            return View("~/Views/Shared/Error.cshtml");
        }

        [AllowAnonymous]
        public ActionResult NotFound()
        {
            Response.StatusCode = 404;
            return View("~/Views/Shared/NotFound.cshtml");
        }

        [AllowAnonymous]
        public ActionResult Forbidden()
        {
            Response.StatusCode = 403;
            return View("~/Views/Shared/Forbidden.cshtml");
        }

        /// <summary>
        /// Health probe reporting connectivity and schema readiness. Returns a
        /// terse JSON payload with no connection details, safe for monitoring.
        /// </summary>
        [AllowAnonymous]
        public JsonResult Health()
        {
            var ok = DbInitializer.EnsureDatabaseReady(out _);
            return Json(new { healthy = ok, detail = ok ? "ready" : "not ready" },
                        JsonRequestBehavior.AllowGet);
        }
    }
}
