using Microsoft.AspNetCore.Antiforgery;
using NextRdp.Platform.Application;

namespace NextRdp.ControlPlane;

public sealed class CookieAntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                throw new PlatformProblemException(400, "antiforgery_failed", "Request verification failed", "Refresh the page and retry the operation.");
            }
        }
        return await next(context);
    }
}
