using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace PTCenter.Api.Authorization;

/// <summary>Keep the existing authorization result; format new clinical API denials consistently.</summary>
public sealed class ClinicalAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler defaults = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Succeeded && (context.Request.Path.StartsWithSegments("/api/Doctor") || context.Request.Path.StartsWithSegments("/api/ReassessmentRequests")))
        {
            var status = result.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;
            if (result.Challenged) context.Response.Headers.WWWAuthenticate = "Bearer";
            await Results.Problem(statusCode: status, title: result.Challenged ? "Doctor.AuthenticationRequired" : "Doctor.AccessDenied",
                detail: result.Challenged ? "Sign in to access clinical records." : "Your account is not authorized for this clinical operation.").ExecuteAsync(context);
            return;
        }
        await defaults.HandleAsync(next, context, policy, result);
    }
}
