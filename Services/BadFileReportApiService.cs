// ============================================================================
// BadFileReportApiService.cs - Bad File Report API Client for Web UI
// ============================================================================
//
// All bad file report data access goes through the API (DV_API).
// No direct database access from UI applications.
// ============================================================================

using DV.Shared.DTOs;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;

namespace DV.Web.Services;

public class BadFileReportApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BadFileReportApiService> _logger;
    private readonly AuthenticationStateProvider _authStateProvider;

    public BadFileReportApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<BadFileReportApiService> logger,
        AuthenticationStateProvider authStateProvider)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _authStateProvider = authStateProvider;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        try
        {
            var authState = await _authStateProvider.GetAuthenticationStateAsync();
            var username = authState.User?.Identity?.Name;
            if (!string.IsNullOrEmpty(username))
                request.Headers.Add("X-Forwarded-User", username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve auth state for X-Forwarded-User header");
        }
        return request;
    }

    public async Task<BadFileReportListResponseDto> GetReportsAsync(
        string schemaName, string? status = null, string? reportType = null,
        string? priority = null, int page = 1, int pageSize = 20)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var queryParams = $"?schemaName={Uri.EscapeDataString(schemaName)}&page={page}&pageSize={pageSize}";
            if (!string.IsNullOrEmpty(status)) queryParams += $"&status={Uri.EscapeDataString(status)}";
            if (!string.IsNullOrEmpty(reportType)) queryParams += $"&reportType={Uri.EscapeDataString(reportType)}";
            if (!string.IsNullOrEmpty(priority)) queryParams += $"&priority={Uri.EscapeDataString(priority)}";

            var request = await CreateRequestAsync(HttpMethod.Get, $"api/BadFileReport{queryParams}");
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BadFileReportListResponseDto>()
                ?? new BadFileReportListResponseDto();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get bad file reports for schema {Schema}", schemaName);
            return new BadFileReportListResponseDto();
        }
    }

    public async Task<BadFileReportDto?> GetReportAsync(string schemaName, int reportId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var request = await CreateRequestAsync(HttpMethod.Get, $"api/BadFileReport/{Uri.EscapeDataString(schemaName)}/{reportId}");
            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<BadFileReportDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get bad file report {Id} in schema {Schema}", reportId, schemaName);
            return null;
        }
    }

    public async Task<BadFileReportDto?> CreateReportAsync(CreateBadFileReportDto dto)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var request = await CreateRequestAsync(HttpMethod.Post, "api/BadFileReport");
            request.Content = JsonContent.Create(dto);
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BadFileReportDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create bad file report in schema {Schema}", dto.SchemaName);
            return null;
        }
    }

    public async Task<BadFileReportDto?> UpdateReportAsync(string schemaName, int reportId, UpdateBadFileReportDto dto)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var request = await CreateRequestAsync(HttpMethod.Put, $"api/BadFileReport/{Uri.EscapeDataString(schemaName)}/{reportId}");
            request.Content = JsonContent.Create(dto);
            var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<BadFileReportDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update bad file report {Id} in schema {Schema}", reportId, schemaName);
            return null;
        }
    }
}
