using DV.Web.Data;
using DV.Shared.Interfaces;
using DV.Shared.Security;
// ============================================================================
// DocumentRepository.cs - Data Access Layer for Document Viewer Application
// ============================================================================
//
// Purpose: Provides methods for interacting with the database to retrieve and 
// manipulate data related to documents, projects, and document pages. This 
// repository uses Entity Framework Core and supports schema-based project separation.
//
// Created: [Date]
// Last Updated: [Date]
//
// Dependencies:
// - Microsoft.EntityFrameworkCore: For Entity Framework Core operations.
// - DocViewer_Proto.Models: Contains the entity models and AppDbContext.
//
// Notes:
// - This repository supports asynchronous operations for better scalability.
// - Includes methods for searching, retrieving, and listing documents and projects.
// - Each project has its own schema with Document and DocumentPage tables.
// - Uses Entity Framework Core instead of Dapper for database operations.
// ============================================================================

using DV.Shared.Models; // Imports models like Document, Project, etc.
using DV.Web.Infrastructure.Caching;
using Microsoft.EntityFrameworkCore; // Provides EF Core functionality
using System.Security.Claims;

namespace DV.Web.Services; 

// ============================================================================
// DocumentRepository Class
// ============================================================================
// Purpose: Acts as the data access layer for the application, providing methods 
// to interact with the database for documents, projects, and pages using 
// Entity Framework Core and schema-based project separation.
// ============================================================================
public class DocumentRepository
{
    private readonly AppDbContext _context; // Entity Framework context
    private readonly ICacheService _cache;
    private readonly IAccessGroupService? _accessGroupService;
    private readonly SecurityDbContext? _securityContext;

    // Explicit column lists to avoid SELECT * (prevents loading unnecessary data)
    private const string DocumentColumns = "\"DocumentId\", \"ProjectId\", \"DocumentIndex\", \"Issue\", \"DocumentStatus\", \"DocumentNumber\", \"Title\", \"Author\", \"DocumentDate\", \"Keywords\", \"Memo\", \"DocumentType\", \"OldDM\", \"CM\", \"GM\", \"EM\", \"Text01\", \"Text02\", \"Text03\", \"Text04\", \"Text05\", \"Text06\", \"Text07\", \"Text08\", \"Text09\", \"Text10\", \"Text11\", \"Text12\", \"Date01\", \"Date02\", \"Date03\", \"Date04\", \"Boolean01\", \"Boolean02\", \"Boolean03\", \"Number01\", \"Number02\", \"Number03\", \"Version\", \"Status\", \"Classification\", \"FilePath\", \"CreatedOn\", \"CreatedBy\", \"ModifiedOn\", \"ModifiedBy\", \"PublicToken\"";
    private const string DocumentListColumns = "\"DocumentId\", \"ProjectId\", \"DocumentNumber\", \"Title\", \"Author\", \"DocumentDate\", \"DocumentType\", \"Status\", \"Classification\", \"Keywords\", \"CreatedOn\", \"CreatedBy\", \"ModifiedOn\", \"ModifiedBy\", \"PublicToken\"";
    private const string PageColumnsNoBlob = "\"PageId\", \"DocumentId\", \"DocumentIndex\", \"PageNumber\", \"PageReference\", \"FrameNumber\", \"Level1\", \"Level2\", \"Level3\", \"Level4\", \"DiskNumber\", \"FileName\", \"FilePath\", \"FileType\", CAST(NULL AS bytea) AS \"FileContent\", \"FileSize\", \"FileFormat\", \"PageSize\", \"ContentType\", \"UploadedDate\", \"ChecksumMD5\", \"StorageType\", \"CreatedOn\", \"CreatedBy\", \"ModifiedOn\", \"ModifiedBy\"";

    // ========================================================================
    // Constructor: DocumentRepository
    // ========================================================================
    // Purpose: Initializes the repository with an Entity Framework context.
    // Parameters:
    // - context: An instance of AppDbContext for database operations.
    public DocumentRepository(AppDbContext context, ICacheService cache, IAccessGroupService? accessGroupService = null, SecurityDbContext? securityContext = null)
    {
        _context = context;
        _cache = cache;
        _accessGroupService = accessGroupService;
        _securityContext = securityContext;
    }

    // ========================================================================
    // Method: GetProjectsForUserAsync
    // ========================================================================
    // Purpose: Retrieves projects based on User's AD Group membership.
    // Parameters:
    // - user: The ClaimsPrincipal containing user's identity and groups.
    // Returns: Filtered list of projects.
    public async Task<IEnumerable<Project>> GetProjectsForUserAsync(ClaimsPrincipal user)
    {
        // 1. Get all active projects (cached)
        var allProjects = await GetProjectsAsync();

        // 2. Global Admins can see ALL projects
        if (user.IsInRole(DV.Shared.Constants.Roles.GlobalAdminGroup))
        {
            return allProjects;
        }

        // 3. Filter Projects where ReadPrincipal OR EditPrincipal matches one of the User's Groups
        //    Check AD claims AND app-managed group memberships
        var userGroups = await GetUserGroupsAsync(user);

        return allProjects.Where(p => 
            (!string.IsNullOrEmpty(p.ReadPrincipal) && userGroups.Contains(p.ReadPrincipal)) ||
            (!string.IsNullOrEmpty(p.EditPrincipal) && userGroups.Contains(p.EditPrincipal)));
    }

    // ========================================================================
    // Method: HasProjectAccessAsync
    // ========================================================================
    // Purpose: Checks if a User has access to a specific Project via AD Groups.
    // Parameters:
    // - user: The ClaimsPrincipal containing user's identity and groups.
    // - projectId: The ID of the project to check.
    // Returns: boolean indicating access.
    public async Task<bool> HasProjectAccessAsync(ClaimsPrincipal user, int projectId)
    {
        // Global Admins have access to all projects
        if (user.IsInRole(DV.Shared.Constants.Roles.GlobalAdminGroup))
        {
            return true;
        }

        // 1. Get the project
        var project = await GetProjectAsync("DefaultConnection", projectId);
        if (project == null) 
        {
            Console.WriteLine($"HasProjectAccessAsync: Project {projectId} not found.");
            return false;
        }

        // 2. Check AD claims AND app-managed group memberships
        var userGroups = await GetUserGroupsAsync(user);

        Console.WriteLine($"HasProjectAccessAsync: Checking access for Project {projectId} ({project.ProjectName})");
        Console.WriteLine($"  - ReadPrincipal: '{project.ReadPrincipal}'");
        Console.WriteLine($"  - EditPrincipal: '{project.EditPrincipal}'");
        Console.WriteLine($"  - User Groups: {string.Join(", ", userGroups.Select(g => $"'{g}'"))}");

        // 3. Check Permissions
        bool canRead = !string.IsNullOrEmpty(project.ReadPrincipal) && userGroups.Contains(project.ReadPrincipal);
        bool canEdit = !string.IsNullOrEmpty(project.EditPrincipal) && userGroups.Contains(project.EditPrincipal);
        
        Console.WriteLine($"  - Match Result: Read={canRead}, Edit={canEdit}");

        return canRead || canEdit;
    }

    // ========================================================================
    // Method: GetUserGroupsAsync
    // ========================================================================
    // Purpose: Merges AD group claims with app-managed group memberships.
    // Returns: A HashSet of all group names the user belongs to.
    private async Task<HashSet<string>> GetUserGroupsAsync(ClaimsPrincipal user)
    {
        var userGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Extract AD groups from claims
        foreach (var claim in user.Claims)
        {
            if (claim.Type == "groups" ||
                claim.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups" ||
                claim.Type == "http://schemas.xmlsoap.org/claims/Group" ||
                claim.Type == ClaimTypes.GroupSid ||
                claim.Type == ClaimTypes.Role ||
                claim.Type == "role")
            {
                userGroups.Add(claim.Value);
            }
        }

        Console.WriteLine($"GetUserGroupsAsync: AD groups from claims: [{string.Join(", ", userGroups)}]");
        Console.WriteLine($"GetUserGroupsAsync: _accessGroupService is {(_accessGroupService != null ? "injected" : "NULL")}");
        Console.WriteLine($"GetUserGroupsAsync: _securityContext is {(_securityContext != null ? "injected" : "NULL")}");

        // 2. Merge app-managed group memberships
        if (_accessGroupService != null && _securityContext != null)
        {
            try
            {
                var username = user.Identity?.Name;
                Console.WriteLine($"GetUserGroupsAsync: Looking up user by Identity.Name = '{username}'");

                if (!string.IsNullOrEmpty(username))
                {
                    var dbUser = await _securityContext.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

                    Console.WriteLine($"GetUserGroupsAsync: DB user lookup result: {(dbUser != null ? $"Found UserId={dbUser.UserId}, Username='{dbUser.Username}'" : "NOT FOUND")}");

                    if (dbUser != null)
                    {
                        var appGroupNames = await _accessGroupService.GetUserGroupNamesAsync(dbUser.UserId);
                        Console.WriteLine($"GetUserGroupsAsync: App group names for UserId {dbUser.UserId}: [{string.Join(", ", appGroupNames)}]");

                        foreach (var groupName in appGroupNames)
                        {
                            userGroups.Add(groupName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetUserGroupsAsync: Error loading app groups: {ex.Message}");
                Console.WriteLine($"GetUserGroupsAsync: Stack trace: {ex.StackTrace}");
                // Continue with AD groups only — don't block access
            }
        }

        Console.WriteLine($"GetUserGroupsAsync: Final merged groups: [{string.Join(", ", userGroups)}]");
        return userGroups;
    }

    // ========================================================================
    // Method: IsUserInGroupAsync
    // ========================================================================
    // Purpose: Checks if a user belongs to a specific group (AD or app-managed).
    // Used by UI components (NavMenu, EditProject) that need group checks.
    public async Task<bool> IsUserInGroupAsync(ClaimsPrincipal user, string groupName)
    {
        if (string.IsNullOrEmpty(groupName)) return false;
        var groups = await GetUserGroupsAsync(user);
        return groups.Contains(groupName);
    }

    // ========================================================================
    // Method: HasProjectEditAccessAsync
    // ========================================================================
    // Purpose: Checks if a user has EDIT access to a specific project.
    //          Unlike HasProjectAccessAsync (read OR edit), this checks EditPrincipal only.
    // Used by: ManageLink, Manage page, and any edit-related functionality.
    public async Task<bool> HasProjectEditAccessAsync(ClaimsPrincipal user, int projectId)
    {
        if (user.IsInRole(DV.Shared.Constants.Roles.GlobalAdminGroup))
            return true;

        var project = await GetProjectAsync("DefaultConnection", projectId);
        if (project == null) return false;

        var userGroups = await GetUserGroupsAsync(user);
        return !string.IsNullOrEmpty(project.EditPrincipal) && userGroups.Contains(project.EditPrincipal);
    }

    // ========================================================================
    // Method: GetProjectsAsync (Overload)
    // ========================================================================
    // Purpose: Retrieves all projects from the database.
    // Returns: An IEnumerable of Project objects.
    public async Task<IEnumerable<Project>> GetProjectsAsync()
    {
        return await GetProjectsAsync("DefaultConnection"); // Call the overloaded method for compatibility
    }

    // ========================================================================
    // Method: GetProjectsAsync
    // ========================================================================
    // Purpose: Retrieves all projects from the database (database parameter kept for compatibility).
    // Parameters:
    // - database: The name of the database (ignored, kept for compatibility).
    // Returns: An IEnumerable of Project objects.
    public async Task<IEnumerable<Project>> GetProjectsAsync(string database)
    {
        return await _cache.GetOrSetAsync("projects:all", async () =>
        {
            return await _context.Projects
                .Where(p => p.IsActive)
                .OrderBy(p => p.ProjectName)
                .ToListAsync();
        }, TimeSpan.FromHours(1));
    }

    // ========================================================================
    // Method: GetProjectAsync
    // ========================================================================
    // Purpose: Retrieves a specific project by its ID.
    // Parameters:
    // - database: The name of the database (ignored, kept for compatibility).
    // - projectId: The ID of the project to retrieve.
    // Returns: A Project object or null if not found.
    public async Task<Project?> GetProjectAsync(string database, int projectId)
    {
        return await _cache.GetOrSetAsync($"project:id:{projectId}", async () =>
        {
            return await _context.Projects
                .FirstOrDefaultAsync(p => p.ProjectId == projectId);
        }, TimeSpan.FromHours(1));
    }

    // ========================================================================
    // Method: SearchAsync
    // ========================================================================
    // Purpose: Searches for documents based on a term, with pagination support.
    // Parameters:
    // - database: The name of the database (ignored, kept for compatibility).
    // - searchTerm: The term to search for in document fields.
    // - page: The page number for pagination (1-based).
    // - pageSize: The number of results per page.
    // - projectId: Optional project ID to filter results.
    // Returns: An IEnumerable of Document objects matching the search criteria.
    public async Task<IEnumerable<Document>> SearchAsync(string database, string? searchTerm, int page, int pageSize, int? projectId = null)
    {
        // First, get the project to determine the schema
        if (projectId.HasValue)
        {
            var project = await GetProjectAsync(database, projectId.Value);
            if (project != null && !string.IsNullOrEmpty(project.SchemaName))
            {
                return await SearchInSchemaAsync(project.SchemaName, searchTerm, page, pageSize, projectId);
            }
        }

        // If no specific project or project not found, search across all schemas
        return await SearchAcrossAllSchemasAsync(searchTerm, page, pageSize, projectId);
    }

    // ========================================================================
    // Method: SearchInSchemaAsync
    // ========================================================================
    // Purpose: Searches for documents within a specific project schema.
    private async Task<IEnumerable<Document>> SearchInSchemaAsync(string schemaName, string? searchTerm, int page, int pageSize, int? projectId)
    {
        var sql = $"SELECT {DocumentColumns} FROM \"{schemaName}\".\"Document\" WHERE 1=1";
        var parameters = new List<object>();

        if (projectId.HasValue)
        {
            sql += " AND \"ProjectId\" = {" + parameters.Count + "}";
            parameters.Add(projectId.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            sql += " AND (\"Title\" LIKE {" + parameters.Count + "} OR \"Author\" LIKE {" + (parameters.Count + 1) + "} OR \"DocumentNumber\" LIKE {" + (parameters.Count + 2) + "} OR \"Keywords\" LIKE {" + (parameters.Count + 3) + "})";
            var searchPattern = $"%{searchTerm}%";
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
        }

        sql += " ORDER BY \"CreatedOn\" DESC";
        sql += $" LIMIT {pageSize} OFFSET {(page - 1) * pageSize}";

        var documents = await _context.Database.SqlQueryRaw<Document>(sql, parameters.ToArray()).ToListAsync();
        
        // Populate SchemaName for each document
        foreach (var doc in documents)
        {
            doc.SchemaName = schemaName;
        }
        Console.WriteLine($"SearchInSchemaAsync: Schema='{schemaName}', Matches={documents.Count}");
        
        return documents;
    }

    // ========================================================================
    // Method: SearchAcrossAllSchemasAsync
    // ========================================================================
    // Purpose: Searches for documents across all project schemas.
    private async Task<IEnumerable<Document>> SearchAcrossAllSchemasAsync(string? searchTerm, int page, int pageSize, int? projectId)
    {
        // Get all active projects with schemas
        var projects = await _context.Projects
            .Where(p => p.IsActive && !string.IsNullOrEmpty(p.SchemaName))
            .ToListAsync();

        if (!projects.Any())
        {
            return new List<Document>();
        }

        // Cap per-schema results to avoid loading entire tables into memory
        var perSchemaLimit = pageSize * 3;

        // Query each schema in parallel with capped results
        var tasks = projects
            .Where(p => !projectId.HasValue || p.ProjectId == projectId.Value)
            .Select(project => SafeSearchInSchemaAsync(project.SchemaName, searchTerm, 1, perSchemaLimit, project.ProjectId));

        var results = await Task.WhenAll(tasks);

        // Merge and paginate combined results
        return results
            .SelectMany(r => r)
            .OrderByDescending(d => d.CreatedOn)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);
    }

    private async Task<IEnumerable<Document>> SafeSearchInSchemaAsync(string schemaName, string? searchTerm, int page, int pageSize, int? projectId)
    {
        try
        {
            return await SearchInSchemaAsync(schemaName, searchTerm, page, pageSize, projectId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SafeSearchInSchemaAsync: Error searching schema '{schemaName}': {ex.Message}");
            return Enumerable.Empty<Document>();
        }
    }

    // ========================================================================
    // Method: SearchWithMetadataAsync
    // ========================================================================
    // Purpose: Searches for documents with full pagination metadata, filters,
    //          and sorting support.
    // Returns: Tuple of (Documents list, TotalCount).
    public async Task<(List<Document> Documents, int TotalCount)> SearchWithMetadataAsync(
        string database, string? searchTerm, int page, int pageSize, int? projectId = null,
        string? documentType = null, string? status = null,
        DateTime? fromDate = null, DateTime? toDate = null,
        string? sortColumn = null, string? sortDirection = null)
    {
        if (projectId.HasValue)
        {
            var project = await GetProjectAsync(database, projectId.Value);
            if (project != null && !string.IsNullOrEmpty(project.SchemaName))
            {
                return await SearchInSchemaWithMetadataAsync(
                    project.SchemaName, searchTerm, page, pageSize, projectId,
                    documentType, status, fromDate, toDate, sortColumn, sortDirection);
            }
        }

        // Fallback to legacy search
        var results = (await SearchAcrossAllSchemasAsync(searchTerm, page, pageSize, projectId)).ToList();
        return (results, results.Count);
    }

    private async Task<(List<Document> Documents, int TotalCount)> SearchInSchemaWithMetadataAsync(
        string schemaName, string? searchTerm, int page, int pageSize, int? projectId,
        string? documentType, string? status,
        DateTime? fromDate, DateTime? toDate,
        string? sortColumn, string? sortDirection)
    {
        // Build WHERE clause (shared between COUNT and SELECT)
        var whereClause = "WHERE 1=1";
        var parameters = new List<object>();

        if (projectId.HasValue)
        {
            whereClause += " AND \"ProjectId\" = {" + parameters.Count + "}";
            parameters.Add(projectId.Value);
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            whereClause += " AND (\"Title\" LIKE {" + parameters.Count + "} OR \"Author\" LIKE {"
                + (parameters.Count + 1) + "} OR \"DocumentNumber\" LIKE {"
                + (parameters.Count + 2) + "} OR \"Keywords\" LIKE {"
                + (parameters.Count + 3) + "})";
            var searchPattern = $"%{searchTerm}%";
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
            parameters.Add(searchPattern);
        }

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            whereClause += " AND \"DocumentType\" = {" + parameters.Count + "}";
            parameters.Add(documentType);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            whereClause += " AND \"Status\" = {" + parameters.Count + "}";
            parameters.Add(status);
        }

        if (fromDate.HasValue)
        {
            whereClause += " AND \"CreatedOn\" >= {" + parameters.Count + "}";
            parameters.Add(fromDate.Value.Date);
        }

        if (toDate.HasValue)
        {
            whereClause += " AND \"CreatedOn\" < {" + parameters.Count + "}";
            parameters.Add(toDate.Value.Date.AddDays(1));
        }

        // Count query
        var countSql = $"SELECT COUNT(*) AS \"Value\" FROM \"{schemaName}\".\"Document\" {whereClause}";
        var totalCount = await _context.Database
            .SqlQueryRaw<int>(countSql, parameters.ToArray())
            .FirstOrDefaultAsync();

        // Validate sort column (whitelist to prevent SQL injection)
        var validSortColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DocumentNumber"] = "DocumentNumber",
            ["Title"] = "Title",
            ["Author"] = "Author",
            ["DocumentType"] = "DocumentType",
            ["Status"] = "Status",
            ["CreatedOn"] = "CreatedOn"
        };

        var orderCol = validSortColumns.GetValueOrDefault(sortColumn ?? "", "CreatedOn");
        var orderDir = string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

        // Data query
        var dataSql = $"SELECT {DocumentColumns} FROM \"{schemaName}\".\"Document\" {whereClause} ORDER BY \"{orderCol}\" {orderDir}"
            + $" LIMIT {pageSize} OFFSET {(page - 1) * pageSize}";

        var documents = await _context.Database
            .SqlQueryRaw<Document>(dataSql, parameters.ToArray())
            .ToListAsync();

        foreach (var doc in documents)
        {
            doc.SchemaName = schemaName;
        }

        Console.WriteLine($"SearchInSchemaWithMetadataAsync: Schema='{schemaName}', Page={page}, Total={totalCount}, Returned={documents.Count}");
        return (documents, totalCount);
    }

    // ========================================================================
    // Method: GetDocumentAsync
    // ========================================================================
    // Purpose: Retrieves a specific document by its ID.
    // Parameters:
    // - database: The name of the database (ignored, kept for compatibility).
    // - documentId: The ID of the document to retrieve.
    // Returns: A Document object or null if not found.
    public async Task<Document?> GetDocumentAsync(string database, int documentId)
    {
        // Build a single UNION ALL query across all schemas instead of N+1 queries
        var schemas = await _cache.GetOrSetAsync("projects:active-schemas", async () =>
        {
            return await _context.Projects
                .Where(p => p.IsActive && !string.IsNullOrEmpty(p.SchemaName))
                .Select(p => p.SchemaName)
                .ToListAsync();
        }, TimeSpan.FromMinutes(10));

        if (!schemas.Any())
            return null;

        var unionParts = schemas.Select(schema =>
            $"SELECT {DocumentColumns} FROM \"{schema}\".\"Document\" WHERE \"DocumentId\" = {{0}}");
        var sql = string.Join(" UNION ALL ", unionParts);

        return await _context.Database.SqlQueryRaw<Document>(sql, documentId).FirstOrDefaultAsync();
    }

    // ========================================================================
    // Method: GetDocumentAsync (with schema)
    // ========================================================================
    // Purpose: Retrieves a specific document by its ID from a specific schema.
    public async Task<Document?> GetDocumentAsync(string database, string schemaName, int documentId)
    {
        var sql = $"SELECT {DocumentColumns} FROM \"{schemaName}\".\"Document\" WHERE \"DocumentId\" = {{0}}";
        return await _context.Database.SqlQueryRaw<Document>(sql, documentId).FirstOrDefaultAsync();
    }

    // ========================================================================
    // Method: GetDocumentByTokenAsync
    // ========================================================================
    // Purpose: Retrieves a document by its opaque public token (UNION ALL across all schemas).
    // Note: Document.SchemaName is [NotMapped] so SqlQueryRaw won't populate it.
    //       We look up the schema from the Project table after the query.
    public async Task<Document?> GetDocumentByTokenAsync(string token)
    {
        var schemas = await _cache.GetOrSetAsync("projects:active-schemas", async () =>
        {
            return await _context.Projects
                .Where(p => p.IsActive && !string.IsNullOrEmpty(p.SchemaName))
                .Select(p => p.SchemaName)
                .ToListAsync();
        }, TimeSpan.FromMinutes(10));

        if (!schemas.Any())
            return null;

        var unionParts = schemas.Select(schema =>
            $"SELECT {DocumentColumns} FROM \"{schema}\".\"Document\" WHERE \"PublicToken\" = {{0}}");
        var sql = string.Join(" UNION ALL ", unionParts);

        var doc = await _context.Database.SqlQueryRaw<Document>(sql, token).FirstOrDefaultAsync();

        if (doc?.ProjectId != null)
        {
            var project = await _context.Projects.FirstOrDefaultAsync(p => p.ProjectId == doc.ProjectId.Value);
            doc.SchemaName = project?.SchemaName;
        }

        return doc;
    }

    // ========================================================================
    // Method: BackfillDocumentTokensAsync
    // ========================================================================
    // Purpose: Generates PublicToken for any documents that don't have one yet.
    public async Task<int> BackfillDocumentTokensAsync()
    {
        var schemas = await _context.Projects
            .Where(p => p.IsActive && !string.IsNullOrEmpty(p.SchemaName))
            .Select(p => p.SchemaName)
            .ToListAsync();

        int totalUpdated = 0;
        foreach (var schema in schemas)
        {
            try
            {
                var docs = await _context.Database
                    .SqlQueryRaw<Document>($"SELECT {DocumentColumns} FROM \"{schema}\".\"Document\" WHERE \"PublicToken\" IS NULL")
                    .ToListAsync();

                foreach (var doc in docs)
                {
                    var token = DV.Shared.Constants.DocumentTokenGenerator.GenerateToken();
                    await _context.Database.ExecuteSqlRawAsync(
                        $"UPDATE \"{schema}\".\"Document\" SET \"PublicToken\" = {{0}} WHERE \"DocumentId\" = {{1}}",
                        token, doc.DocumentId);
                    totalUpdated++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BackfillDocumentTokensAsync: Error in schema '{schema}': {ex.Message}");
            }
        }

        return totalUpdated;
    }

    // ========================================================================
    // Method: GetPagesAsync
    // ========================================================================
    // Purpose: Retrieves all pages for a specific document.
    public async Task<IEnumerable<DocumentPage>> GetPagesAsync(string database, int documentId)
    {
        // Build a single UNION ALL query across all schemas instead of N+1 queries
        var schemas = await _cache.GetOrSetAsync("projects:active-schemas", async () =>
        {
            return await _context.Projects
                .Where(p => p.IsActive && !string.IsNullOrEmpty(p.SchemaName))
                .Select(p => p.SchemaName)
                .ToListAsync();
        }, TimeSpan.FromMinutes(10));

        if (!schemas.Any())
            return new List<DocumentPage>();

        var unionParts = schemas.Select(schema =>
            $"SELECT {PageColumnsNoBlob} FROM \"{schema}\".\"DocumentPage\" WHERE \"DocumentId\" = {{0}}");
        var sql = string.Join(" UNION ALL ", unionParts) + " ORDER BY \"PageNumber\"";

        return await _context.Database.SqlQueryRaw<DocumentPage>(sql, documentId).ToListAsync();
    }

    // ========================================================================
    // Method: GetPagesAsync (Overload with Schema)
    // ========================================================================
    // Purpose: Retrieves all pages for a specific document from a specific schema.
    public async Task<IEnumerable<DocumentPage>> GetPagesAsync(string database, string schemaName, int documentId)
    {
        try
        {
            var sql = $"SELECT {PageColumnsNoBlob} FROM \"{schemaName}\".\"DocumentPage\" WHERE \"DocumentId\" = {{0}} ORDER BY \"PageNumber\"";
            return await _context.Database.SqlQueryRaw<DocumentPage>(sql, documentId).ToListAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"GetPagesAsync Error: {ex.Message}");
            return new List<DocumentPage>();
        }
    }

    // ========================================================================
    // Method: GetDocumentPagesAsync (Alias for GetPagesAsync)
    // ========================================================================
    // Purpose: Alias for GetPagesAsync to support BLOB viewer component.
    public async Task<List<DocumentPage>> GetDocumentPagesAsync(string database, int documentId)
    {
        var pages = await GetPagesAsync(database, documentId);
        return pages.ToList();
    }

     // ========================================================================
    // Method: GetDocumentPagesAsync (Alias with Schema)
    // ========================================================================
    public async Task<List<DocumentPage>> GetDocumentPagesAsync(string database, string schemaName, int documentId)
    {
        var pages = await GetPagesAsync(database, schemaName, documentId);
        return pages.ToList();
    }

    // ========================================================================
    // Method: GetSchemaForDocumentAsync
    // ========================================================================
    // Purpose: Finds which schema contains a specific document.
    public async Task<string?> GetSchemaForDocumentAsync(string database, int documentId)
    {
        var projects = await _context.Projects
            .Where(p => p.IsActive && !string.IsNullOrEmpty(p.SchemaName))
            .ToListAsync();

        if (!projects.Any())
            return null;

        // Build a single UNION ALL query to find the schema in one round-trip
        var unionParts = projects.Select(project =>
            $"SELECT '{project.SchemaName}' AS \"Value\" FROM \"{project.SchemaName}\".\"Document\" WHERE \"DocumentId\" = {{0}}");
        var sql = string.Join(" UNION ALL ", unionParts);

        return await _context.Database.SqlQueryRaw<string>(sql, documentId).FirstOrDefaultAsync();
    }
}