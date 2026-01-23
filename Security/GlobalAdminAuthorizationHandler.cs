using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using DV.Web.Services;

using DV.Shared.Security;

namespace DV.Web.Security;

/// <summary>
/// Authorization handler that verifies if the current user is a global administrator
/// </summary>
public class GlobalAdminAuthorizationHandler : AuthorizationHandler<GlobalAdminRequirement>
{
    private readonly UserService _userService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public GlobalAdminAuthorizationHandler(
        UserService userService,
        IHttpContextAccessor httpContextAccessor)
    {
        _userService = userService;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, 
        GlobalAdminRequirement requirement)
    {
        // Get the username from the HttpContext
        var user = _httpContextAccessor.HttpContext?.User;
        
        if (user?.Identity?.IsAuthenticated != true)
        {
            return; // Not authenticated
        }

        // Check if the user is in the DV_Global_Admins group
        // We check standard ClaimTypes.Role and "groups" claims
        if (user.IsInRole("DV_Global_Admins") || 
            user.HasClaim(c => (c.Type == "groups" || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups") && c.Value == "DV_Global_Admins"))
        {
            context.Succeed(requirement);
        }
        
        await Task.CompletedTask;
    }
}