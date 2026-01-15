using DV.Web.Data;
using DV.Shared.Security;
using DV.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace DV.Web.Services;

/// <summary>
/// Service for managing user project access authorizations.
/// Users must have project access before they can be assigned project roles.
/// </summary>
public class UserProjectAccessService
{
    private readonly SecurityDbContext _securityContext;
    private readonly ProjectService _projectService;
    private readonly AuditService _auditService;

    public UserProjectAccessService(SecurityDbContext securityContext, ProjectService projectService, AuditService auditService)
    {
        _securityContext = securityContext;
        _projectService = projectService;
        _auditService = auditService;
    }

    /// <summary>
    /// Grants a user access to a specific project
    /// </summary>
    public async Task GrantProjectAccessAsync(int userId, int projectId, string? grantedBy = null, string? reason = null, DateTime? expiresDate = null)
    {
        // Check if access already exists
        var existing = await _securityContext.UserProjectAccess
            .FirstOrDefaultAsync(upa => upa.UserId == userId && upa.ProjectId == projectId);

        // Get user info for audit logging
        var user = await _securityContext.Users.FindAsync(userId);
        var username = user?.Username ?? "Unknown";

        if (existing != null)
        {
            if (existing.IsActive)
            {
                throw new InvalidOperationException("User already has access to this project");
            }
            
            // Reactivate existing access
            existing.IsActive = true;
            existing.GrantedDate = DateTime.UtcNow;
            existing.GrantedBy = grantedBy;
            existing.AccessReason = reason;
            existing.ExpiresDate = expiresDate;
        }
        else
        {
            var access = new DV.Shared.Security.UserProjectAccess
            {
                UserId = userId,
                ProjectId = projectId,
                GrantedDate = DateTime.UtcNow,
                GrantedBy = grantedBy,
                IsActive = true,
                AccessReason = reason,
                ExpiresDate = expiresDate
            };

            _securityContext.UserProjectAccess.Add(access);
        }

        await _securityContext.SaveChangesAsync();

        // Log the project access grant
        await _auditService.LogProjectAccessAsync(
            username,
            userId,
            projectId,
            AuditActions.GrantProjectAccess,
            AuditResults.Success,
            $"Project access granted. Reason: {reason ?? "Not specified"}",
            grantedBy);
    }

    /// <summary>
    /// Revokes a user's access to a specific project
    /// </summary>
    public async Task RevokeProjectAccessAsync(int userId, int projectId)
    {
        var access = await _securityContext.UserProjectAccess
            .FirstOrDefaultAsync(upa => upa.UserId == userId && upa.ProjectId == projectId);

        // Get user info for audit logging
        var user = await _securityContext.Users.FindAsync(userId);
        var username = user?.Username ?? "Unknown";

        if (access != null)
        {
            access.IsActive = false;
            await _securityContext.SaveChangesAsync();

            // Log the project access revocation
            await _auditService.LogProjectAccessAsync(
                username,
                userId,
                projectId,
                AuditActions.RevokeProjectAccess,
                AuditResults.Success,
                "Project access revoked");
        }
    }

    /// <summary>
    /// Checks if a user has access to a specific project
    /// </summary>
    public async Task<bool> UserHasProjectAccessAsync(int userId, int projectId)
    {
        return await _securityContext.UserProjectAccess
            .AnyAsync(upa => upa.UserId == userId && 
                           upa.ProjectId == projectId && 
                           upa.IsActive &&
                           (upa.ExpiresDate == null || upa.ExpiresDate > DateTime.UtcNow));
    }

    /// <summary>
    /// Gets all projects a user has access to
    /// </summary>
    public async Task<List<int>> GetUserAccessibleProjectIdsAsync(int userId)
    {
        return await _securityContext.UserProjectAccess
            .Where(upa => upa.UserId == userId && 
                         upa.IsActive &&
                         (upa.ExpiresDate == null || upa.ExpiresDate > DateTime.UtcNow))
            .Select(upa => upa.ProjectId)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all projects a user has access to with full project details
    /// </summary>
    public async Task<List<Project>> GetUserAccessibleProjectsAsync(int userId)
    {
        var projectIds = await GetUserAccessibleProjectIdsAsync(userId);
        var allProjects = await _projectService.GetAllProjectsAsync();
        
        return allProjects.Where(p => projectIds.Contains(p.ProjectId)).ToList();
    }

    /// <summary>
    /// Gets all users who have access to a specific project
    /// </summary>
    public async Task<List<UserProjectAccessInfo>> GetProjectUsersAsync(int projectId)
    {
        var accessList = await _securityContext.UserProjectAccess
            .Include(upa => upa.User)
            .Where(upa => upa.ProjectId == projectId && upa.IsActive)
            .ToListAsync();

        return accessList.Select(upa => new UserProjectAccessInfo
        {
            UserId = upa.UserId,
            ProjectId = upa.ProjectId,
            User = upa.User!,
            GrantedDate = upa.GrantedDate,
            GrantedBy = upa.GrantedBy,
            AccessReason = upa.AccessReason,
            ExpiresDate = upa.ExpiresDate
        }).ToList();
    }

    /// <summary>
    /// Gets all project access records for a specific user
    /// </summary>
    public async Task<List<UserProjectAccessInfo>> GetUserProjectAccessAsync(int userId)
    {
        var accessList = await _securityContext.UserProjectAccess
            .Include(upa => upa.User)
            .Where(upa => upa.UserId == userId && upa.IsActive)
            .ToListAsync();

        var projects = await _projectService.GetAllProjectsAsync();
        var projectsDict = projects.ToDictionary(p => p.ProjectId, p => p);

        return accessList.Select(upa => new UserProjectAccessInfo
        {
            UserId = upa.UserId,
            ProjectId = upa.ProjectId,
            User = upa.User!,
            Project = projectsDict.ContainsKey(upa.ProjectId) ? projectsDict[upa.ProjectId] : null,
            GrantedDate = upa.GrantedDate,
            GrantedBy = upa.GrantedBy,
            AccessReason = upa.AccessReason,
            ExpiresDate = upa.ExpiresDate
        }).ToList();
    }

    /// <summary>
    /// Gets all users who don't have access to a specific project
    /// </summary>
    public async Task<List<ApplicationUser>> GetUsersWithoutProjectAccessAsync(int projectId)
    {
        var usersWithAccess = await _securityContext.UserProjectAccess
            .Where(upa => upa.ProjectId == projectId && upa.IsActive)
            .Select(upa => upa.UserId)
            .ToListAsync();

        return await _securityContext.Users
            .Where(u => u.IsActive && !usersWithAccess.Contains(u.UserId))
            .ToListAsync();
    }

    /// <summary>
    /// Validates that a user has project access before assigning project roles
    /// </summary>
    public async Task ValidateUserProjectAccessAsync(int userId, int projectId)
    {
        var hasAccess = await UserHasProjectAccessAsync(userId, projectId);
        if (!hasAccess)
        {
            var user = await _securityContext.Users.FindAsync(userId);
            var projects = await _projectService.GetAllProjectsAsync();
            var project = projects.FirstOrDefault(p => p.ProjectId == projectId);
            
            throw new UnauthorizedAccessException(
                $"User '{user?.DisplayName ?? "Unknown"}' does not have access to project '{project?.ProjectName ?? "Unknown"}'. " +
                "Grant project access before assigning project roles.");
        }
    }

    /// <summary>
    /// Gets the total count of active user project access records for dashboard statistics
    /// </summary>
    public async Task<int> GetTotalAccessRecordsCountAsync()
    {
        return await _securityContext.UserProjectAccess
            .Where(upa => upa.IsActive &&
                         (upa.ExpiresDate == null || upa.ExpiresDate > DateTime.UtcNow))
            .CountAsync();
    }

    /// <summary>
    /// Gets the count of users who have at least one project access
    /// </summary>
    public async Task<int> GetUsersWithProjectAccessCountAsync()
    {
        return await _securityContext.UserProjectAccess
            .Where(upa => upa.IsActive &&
                         (upa.ExpiresDate == null || upa.ExpiresDate > DateTime.UtcNow))
            .Select(upa => upa.UserId)
            .Distinct()
            .CountAsync();
    }
}

/// <summary>
/// DTO for user project access information with full details
/// </summary>
public class UserProjectAccessInfo
{
    public int UserId { get; set; }
    public int ProjectId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Project? Project { get; set; }
    public DateTime GrantedDate { get; set; }
    public string? GrantedBy { get; set; }
    public string? AccessReason { get; set; }
    public DateTime? ExpiresDate { get; set; }
}