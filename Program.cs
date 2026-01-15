// ============================================================================
// Program.cs - Entry Point for Document Viewer Application
// ============================================================================

using DV.Web.Components;
using DV.Web.Security;
using DV.Shared.Security;
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
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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

// Add session configuration for security
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// ============================================================================
// Service Configuration
// ============================================================================

// Add Razor Components with interactive server rendering
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register Pooled DbContextFactory for AppDbContext (for Blazor components)
// This provides both IDbContextFactory<AppDbContext> for factory pattern
// and scoped AppDbContext instances for dependency injection
builder.Services.AddPooledDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => {
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            sqlOptions.CommandTimeout(30);
        }
    );
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// Register AppDbContext with TRANSIENT lifetime to avoid sharing across concurrent component initialization
builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => {
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            sqlOptions.CommandTimeout(30);
        }
    );
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
}, ServiceLifetime.Transient);  // CRITICAL: Transient lifetime to avoid DbContext sharing

// Register SecurityDbContext factory for pooled context support
builder.Services.AddPooledDbContextFactory<SecurityDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => {
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            sqlOptions.CommandTimeout(30);
        }
    );
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
});

// Register SecurityDbContext as scoped - NOTE: Each service gets its own instance per request
// to avoid concurrency issues
// Register SecurityDbContext with TRANSIENT lifetime to avoid sharing across concurrent component initialization
builder.Services.AddDbContext<SecurityDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => {
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5);
            sqlOptions.CommandTimeout(30);
        }
    );
    
    if (builder.Environment.IsDevelopment())
    {
        options.EnableSensitiveDataLogging();
        options.EnableDetailedErrors();
    }
}, ServiceLifetime.Transient);  // CRITICAL: Transient lifetime to avoid DbContext sharing

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
builder.Services.AddTransient<UserProjectAccessService>();  // Transient to avoid DbContext sharing
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<SessionManagementService>();
builder.Services.AddScoped<GlobalAdminMigrationService>();
builder.Services.AddScoped<ProjectRoleSeeder>();
builder.Services.AddTransient<FirstUserAdminService>(); // Transient to avoid DbContext sharing during concurrent initialization

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
builder.Services.AddScoped<DV.Web.Security.TokenDelegatingHandler>();

builder.Services.AddHttpClient("Api", client =>
{
    var baseUrl = builder.Configuration["Api:BaseUrl"];
    if (!string.IsNullOrEmpty(baseUrl))
    {
        client.BaseAddress = new Uri(baseUrl);
    }
})
.AddHttpMessageHandler<DV.Web.Security.TokenDelegatingHandler>();

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
        OnTokenValidated = context =>
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
            
            return Task.CompletedTask;
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

    // Add the GlobalAdminOnly policy
    options.AddPolicy("GlobalAdminOnly", policy =>
        policy.AddRequirements(new GlobalAdminRequirement()));
    
    // Note: RoleBasedAccess policies are now created dynamically by RoleBasedAuthorizationPolicyProvider
});

// Register authorization handlers
builder.Services.AddScoped<IAuthorizationHandler, RoleBasedAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, GlobalAdminAuthorizationHandler>();

// ============================================================================
// Session & Background Services
// ============================================================================
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddHostedService<SessionCleanupService>();

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

// Security middleware
app.UseHttpsRedirection();
app.UseStaticFiles();

// Authentication & Authorization
app.UseAuthentication();
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