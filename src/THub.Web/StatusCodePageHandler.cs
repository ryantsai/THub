namespace THub.Web;

internal static class StatusCodePageHandler
{
    public static Task HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Response.StatusCode == StatusCodes.Status404NotFound)
        {
            httpContext.Response.Redirect("/not-found");
        }

        return Task.CompletedTask;
    }
}
