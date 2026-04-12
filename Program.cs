// ============================================================================
// Program.cs - Entry Point for Document Viewer Application
// ============================================================================

using DV.Web.Components;
using DV.Web.Security;
using DV.Shared.Security;
using DV.Shared.Interfaces;
using DV.Web.Services;
using DV.Shared.Models;
using DV.Web.Data;
using DV.Web.Middleware;
using DV.Web.Infrastructure.ErrorHandling;
using DV.Web.Infrastructure.Caching;
using DV.Web.Infrastructure.Validation;
using DV.Web.Infrastructure.Configuration;
using DV.Web.Infrastructure.Repositories;
using DV.Web.Infrastructure.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Security.Claims;

using DV.Shared.Constants;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// Configuration Setup
// ============================================================================
builder.Services.Configure<AppSettings>(builder.Configuration.GetSection("AppSettings"));

// Add configuration validation
builder.Services.AddOptions<AppSettings>()
    .Bind(builder.Configuration.GetSection("AppSettings"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Configure file upload security options
builder.Services.Configure<FileUploadSecurityOptions>(options =>
{
    options.DefaultMaxFileSizeBytes = 50 * 1024 * 1024; // 50MB
    options.AdminMaxFileSizeBytes = 100 * 1024 * 1024; // 100MB
});

// ============================================================================
// Logging Configuration
// ============================================================================
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ============================================================================
// Infrastructure Services
// ============================================================================
builder.Services.AddGlobalErrorHandling();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICacheService, MemoryCacheService>();
builder.Services.AddScoped<IValidationService, ValidationService>();
builder.Services.AddScoped<IInputValidationService, InputValidationService>();

// ============================================================================
// Security Middleware Configuration
// ============================================================================

// Rate Limiting Configuration
builder.Services.Configure<RateLimitingOptions>(options =>
{
    options.RequestsPerMinute = 100;
    options.RequestsPerHour = 1000;
    options.EnableRateLimiting = true;
    options.ExemptPaths = new[] { "/health", "/heartbeat", "/_blazor" };
});

// Security Headers Configuration
builder.Services.Configure<SecurityHeadersOptions>(options =>
{
    options.EnableHSTS = true;
    options.HSTSMaxAge = 31536000; // 1 year
    options.EnableCSP = true;
    // Relaxed CSP for Blazor Server compatibility
    options.ContentSecurityPolicy = 
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data: https:; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'";
});

// Request/Response Logging Configuration
builder.Services.Configure<RequestResponseLoggingOptions>(options =>
{
    options.EnableRequestLogging = true;
    options.EnableResponseLogging = true;
    options.LogHeaders = true;
    options.LogRequestBody = false; // Security: Don't log request bodies
    options.LogResponseBody = false; // Performance: Don't log response bodies
    options.SlowRequestThresholdMs = 5000;
    options.ExcludedPaths = new[] { "/health", "/heartbeat", "/_blazor", "/css", "/js", "/images", "/favicon.ico" };
});

// CORS Configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultPolicy", policy =>
    {
        policy.WithOrigins("https://localhost:7117", "http://localhost:5137")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
    
    options.AddPolicy("StrictPolicy", policy =>
    {
        policy.WithOrigins("https://localhost:7117") // Only HTTPS in production
              .WithMethods("GET", "POST")
              .WithHeaders("Authorization", "Content-Type")
              .AllowCredentials();
    });
});

// ============================================================================
// Service Configuration
// ============================================================================

// Add Razor Components with interactive server rendering
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register AppDbContext with TRANSIENT lifetime to avoid sharing across concurrent component initialization
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => {
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            npgsqlOptions.CommandTimeout(30);
        }
    );
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
}, ServiceLifetime.Transient);  // CRITICAL: Transient lifetime to avoid DbContext sharing

// Register SecurityDbContext with TRANSIENT lifetime to avoid sharing across concurrent component initialization
builder.Services.AddDbContext<SecurityDbContext>(options =>
{
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => {
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            npgsqlOptions.CommandTimeout(30);
        }
    );
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
}, ServiceLifetime.Transient);  // CRITICAL: Transient lifetime to avoid DbContext sharing

// Register IDbContextFactory<SecurityDbContext> that resolves from the service provider
builder.Services.AddSingleton<IDbContextFactory<SecurityDbContext>>(sp =>
    new DV.Web.Data.SecurityDbContextFactory(sp));

// ============================================================================
// Application Services
// ============================================================================
builder.Services.AddScoped<DV.Web.Services.DocumentRepository>();
builder.Services.AddScoped<DatabaseMigrationService>();
builder.Services.AddScoped<SchemaService>();
builder.Services.AddScoped<ProjectService>();
builder.Services.AddTransient<UserService>();  // Transient to avoid DbContext sharing with transient services
builder.Services.AddScoped<RoleService>();
builder.Services.AddScoped<FileUploadSecurityService>();
builder.Services.AddScoped<DocumentUploadResultService>();
builder.Services.AddScoped<DocumentUploadService>();
builder.Services.AddScoped<SchemaBlobMigrationService>();
builder.Services.AddTransient<RoleContextService>();  // Transient to avoid DbContext sharing across concurrent component initialization
builder.Services.AddTransient<ProjectRoleService>();  // Transient to avoid DbContext sharing


// Removed UserProjectAccessService as part of Explicit Access removal
// builder.Services.AddTransient<UserProjectAccessService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<ProjectSelectionState>();
builder.Services.AddScoped<SessionManagementService>();
builder.Services.AddTransient<ICredentialService, CredentialService>(); // NIST SP 800-53: Local credential management
builder.Services.AddTransient<IAccessGroupService, AccessGroupService>(); // App-managed access groups
builder.Services.AddScoped<NotificationApiService>();
builder.Services.AddScoped<BulkUploadApiService>();
builder.Services.AddScoped<BulkExportApiService>();
builder.Services.AddScoped<BadFileReportApiService>();
builder.Services.AddScoped<ProjectRoleSeeder>();

// ============================================================================
// Health Checks Configuration
// ============================================================================

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<DocumentStorageHealthCheck>("document_storage")
    .AddCheck<CacheHealthCheck>("cache")
    .AddCheck<ApplicationHealthCheck>("application");

// Register health check services
builder.Services.AddScoped<DatabaseHealthCheck>();
builder.Services.AddScoped<DocumentStorageHealthCheck>();
builder.Services.AddScoped<CacheHealthCheck>();
builder.Services.AddScoped<ApplicationHealthCheck>();

// ============================================================================
// Repository Pattern Services - TODO: Complete integration after model updates
// ============================================================================
// Note: Repository pattern implementation requires model property updates
// builder.Services.AddScoped<Infrastructure.Repositories.IUserRepository, Infrastructure.Repositories.UserRepository>();
// builder.Services.AddScoped<Infrastructure.Repositories.IDocumentRepository, Infrastructure.Repositories.DocumentRepository>();
// builder.Services.AddScoped<Infrastructure.Repositories.IUnitOfWork, Infrastructure.Repositories.UnitOfWork>();

// ============================================================================
// API Client Configuration
// ============================================================================
builder.Services.AddScoped<DV.Web.Security.TokenProvider>();
builder.Services.AddScoped<DV.Web.Security.TokenDelegatingHandler>();

var apiClientBuilder = builder.Services.AddHttpClient("Api", client =>
{
    var baseUrl = builder.Configuration["Api:BaseUrl"];
    if (!string.IsNullOrEmpty(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl);
    }
});

if (builder.Environment.IsDevelopment())
{
    apiClientBuilder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
}

apiClientBuilder.AddHttpMessageHandler<DV.Web.Security.TokenDelegatingHandler>();

// ============================================================================
// Authentication & Authorization
// ============================================================================
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    // Use cookie scheme for challenge too - it will redirect to our login page
    // OIDC will only be used when explicitly triggered via /auth/login-sso
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; 
    options.Cookie.SameSite = SameSiteMode.Lax;
    // Redirect to our login choice page instead of triggering OIDC automatically
    options.LoginPath = "/auth/login";
    options.AccessDeniedPath = "/access-denied";
})
.AddOpenIdConnect(options =>
{
    options.Authority = builder.Configuration["Adfs:Authority"];
    options.MetadataAddress = builder.Configuration["Adfs:MetadataAddress"];
    options.ClientId = builder.Configuration["Adfs:ClientId"];
    options.ClientSecret = builder.Configuration["Adfs:ClientSecret"];
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    
    options.Scope.Clear();
    var scopes = builder.Configuration["Api:Scopes"] ?? "openid profile";
    foreach(var scope in scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        options.Scope.Add(scope);
    }

    options.MapInboundClaims = false;
    options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
    {
        // AD FS sends username in "unique_name" claim (format: AD\username)
        NameClaimType = "unique_name",
        RoleClaimType = "role"
    };

    // Request Windows Integrated Authentication from AD FS
    // This tells AD FS to use the user's existing Kerberos/Windows credentials
    options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
    {
        OnRedirectToIdentityProvider = context =>
        {
            // Request Windows authentication - AD FS will use Kerberos credentials
            context.ProtocolMessage.SetParameter("wauth", "urn:federation:authentication:windows");
            
            // Optional: Add prompt=none to avoid any login UI if WIA is available
            // context.ProtocolMessage.Prompt = "none";
            
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"OIDC Auth Failed: {context.Exception?.Message}");
            return Task.CompletedTask;
        },
        OnTokenValidated = async context =>
        {
            var principal = context.Principal;
            Console.WriteLine($"=== OIDC Token Validated ===");
            Console.WriteLine($"Identity Name: {principal?.Identity?.Name ?? "(null)"}");
            Console.WriteLine($"Identity AuthType: {principal?.Identity?.AuthenticationType}");
            Console.WriteLine($"Claims count: {principal?.Claims.Count() ?? 0}");
            
            if (principal?.Claims != null)
            {
                Console.WriteLine("All claims:");
                foreach (var claim in principal.Claims)
                {
                    Console.WriteLine($"  {claim.Type} = {claim.Value}");
                }
            }

            // Sync Global Admin Status
            if (principal?.Identity?.Name != null)
            {
                try
                {
                    // Create a scope to resolve scoped services
                    // context.HttpContext.RequestServices is already scoped to the request
                    var userService = context.HttpContext.RequestServices.GetRequiredService<UserService>();
                    var username = principal.Identity.Name;
                    
                    var isGlobalAdmin = principal.IsInRole("GlobalAdmin") || 
                                        principal.IsInRole(Roles.GlobalAdminGroup) ||
                                        principal.HasClaim(c => c.Type == "groups" && c.Value == Roles.GlobalAdminGroup);

                    await userService.SyncGlobalAdminStatusAsync(username, isGlobalAdmin);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error syncing global admin status: {ex.Message}");
                }
            }
            
            // return Task.CompletedTask; // Not needed if async
        }
    };
});

// Register custom policy provider for role-based authorization
builder.Services.AddSingleton<IAuthorizationPolicyProvider, RoleBasedAuthorizationPolicyProvider>();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    options.AddPolicy("RequireAdminRole", policy =>
        policy.RequireRole("Admin"));

    // Note: RoleBasedAccess policies are now created dynamically by RoleBasedAuthorizationPolicyProvider
});

// Register authorization handlers
builder.Services.AddScoped<IAuthorizationHandler, RoleBasedAuthorizationHandler>();

// ============================================================================
// Session & Background Services (NIST SP 800-53 AC-12 compliant)
// ============================================================================
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(SessionConfig.IdleTimeoutMinutes);
    options.Cookie.Name = SessionConfig.CookieName;
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddHostedService<SessionCleanupService>();

// ============================================================================
// Response Compression
// ============================================================================
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// ============================================================================
// Web Services
// ============================================================================
builder.Services.AddControllersWithViews();  // With views for MVC login pages
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ============================================================================
// Middleware Configuration
// ============================================================================

// Request/Response logging (early in pipeline for complete coverage)
// app.UseMiddleware<RequestResponseLoggingMiddleware>();

// Rate limiting (early to prevent abuse before expensive operations)
// app.UseMiddleware<RateLimitingMiddleware>();

// Security headers (early to apply to all responses)
// app.UseMiddleware<SecurityHeadersMiddleware>();

// Global error handling
app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
}

// CORS (before authentication)
app.UseCors("DefaultPolicy");

// Response compression (before static files for dynamic content)
app.UseResponseCompression();

// Security middleware
app.UseHttpsRedirection();
app.UseStaticFiles();

// Authentication & Authorization
app.UseAuthentication();

// Development: auto-sign-in as dev user, bypassing AD FS
if (app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";
        var isAuthPath = path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/login-choice", StringComparison.OrdinalIgnoreCase)
                      || path.StartsWith("/logout", StringComparison.OrdinalIgnoreCase);

        if (context.User.Identity?.IsAuthenticated != true && !isAuthPath)
        {
            var claims = new List<Claim>
            {
                new Claim("unique_name", @"AD\neil.rainsforth"),
                new Claim(System.Security.Claims.ClaimTypes.Name, @"AD\neil.rainsforth"),
                new Claim("role", Roles.GlobalAdminGroup),
                new Claim("role", Roles.AdminGroup),
                new Claim("role", Roles.AuditorGroup),
                new Claim("role", Roles.SecurityGroup),
                new Claim("groups", Roles.GlobalAdminGroup),
                new Claim("auth_method", "dev_bypass"),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, "unique_name", "role");
            var principal = new ClaimsPrincipal(identity);

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            context.User = principal;
        }
        await next();
    });
}

app.UseAuthorization();

// Session middleware
app.UseSession();
app.UseMiddleware<SessionTrackingMiddleware>();

// Antiforgery protection
app.UseAntiforgery();

// ============================================================================
// Routing Configuration
// ============================================================================
app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ============================================================================
// Health Check Endpoints
// ============================================================================
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var response = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(x => new
            {
                name = x.Key,
                status = x.Value.Status.ToString(),
                duration = x.Value.Duration.TotalMilliseconds,
                description = x.Value.Description,
                data = x.Value.Data
            }),
            totalDuration = report.TotalDuration.TotalMilliseconds
        };
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(response, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
});

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false // Only basic liveness check
});

// ============================================================================
// Database Migration
// ============================================================================
try
{
    using (var scope = app.Services.CreateScope())
    {
        var migrationService = scope.ServiceProvider.GetRequiredService<DatabaseMigrationService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        
        logger.LogInformation("Checking for database migrations...");
        if (await migrationService.CheckIfMigrationNeededAsync())
        {
            logger.LogInformation("Migration needed. executing migration...");
            await migrationService.ExecuteProjectSchemaMigrationAsync();
            logger.LogInformation("Migration completed successfully.");
        }
        else
        {
            logger.LogInformation("Database is up to date.");
        }

        // Migrate per-project schema tables (adds new columns like PublicToken)
        var schemaService = scope.ServiceProvider.GetRequiredService<SchemaService>();
        await schemaService.MigrateAllExistingSchemasAsync();

        // Backfill PublicToken for any documents that don't have one
        var repo = scope.ServiceProvider.GetRequiredService<DocumentRepository>();
        var backfilled = await repo.BackfillDocumentTokensAsync();
        if (backfilled > 0)
            logger.LogInformation("Backfilled PublicToken for {Count} documents.", backfilled);
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogCritical(ex, "An error occurred while migrating the database.");
}

// ============================================================================
// Application Startup
// ============================================================================
try
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("Starting Document Viewer application");
    
    app.Run();
}
catch (Exception ex)
{
    var logger = app.Services.GetService<ILogger<Program>>();
    logger?.LogCritical(ex, "Application terminated unexpectedly");
    throw;
}