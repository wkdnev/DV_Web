using Microsoft.AspNetCore.Authorization;

using DV.Shared.Security;

namespace DV.Web.Security;

/// <summary>
/// Authorization requirement that checks if the current user is a global admin
/// </summary>
public class GlobalAdminRequirement : IAuthorizationRequirement
{
}