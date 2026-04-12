using DV.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;

namespace DV.Web.Services;

/// <summary>
/// Web-side service for calling the Bulk Export API endpoints.
/// </summary>
public class BulkExportApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BulkExportApiService> _logger;
    private readonly AuthenticationStateProvider _authStateProvider;

    public BulkExportApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<BulkExportApiService> logger,
        AuthenticationStateProvider authStateProvider)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _authStateProvider = authStateProvider;
    }

    /// <summary>
    /// Start a bulk export job. Returns the job status with ID for polling.
    /// </summary>
    public async Task<BulkExportJobStatus?> StartExportAsync(BulkExportRequest request)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var httpRequest = await CreateRequestAsync(HttpMethod.Post, "api/BulkExport/start");
            httpRequest.Content = JsonContent.Create(request);

            var response = await client.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                _logger.LogError("Start export failed: {Status} - {Error}", response.StatusCode, error);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<BulkExportJobStatus>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start bulk export");
            return null;
        }
    }

    /// <summary>
    /// Poll the status of an export job.
    /// </summary>
    public async Task<BulkExportJobStatus?> GetStatusAsync(Guid jobId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var httpRequest = await CreateRequestAsync(HttpMethod.Get, $"api/BulkExport/status/{jobId}");
            var response = await client.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<BulkExportJobStatus>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get export status for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Download the completed export ZIP as a byte array.
    /// </summary>
    public async Task<(byte[]? data, string? fileName)?> DownloadExportAsync(Guid jobId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var httpRequest = await CreateRequestAsync(HttpMethod.Get, $"api/BulkExport/download/{jobId}");
            var response = await client.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode)
                return null;

            var data = await response.Content.ReadAsByteArrayAsync();
            var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? $"export_{jobId}.zip";

            return (data, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download export {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Cancel an in-progress export job.
    /// </summary>
    public async Task<bool> CancelExportAsync(Guid jobId)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var httpRequest = await CreateRequestAsync(HttpMethod.Post, $"api/BulkExport/cancel/{jobId}");
            var response = await client.SendAsync(httpRequest);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel export {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Preview count of documents matching the filter criteria.
    /// </summary>
    public async Task<int> GetPreviewCountAsync(BulkExportRequest request)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var httpRequest = await CreateRequestAsync(HttpMethod.Post, "api/BulkExport/preview-count");
            httpRequest.Content = JsonContent.Create(request);
            var response = await client.SendAsync(httpRequest);

            if (!response.IsSuccessStatusCode) return -1;

            var result = await response.Content.ReadFromJsonAsync<PreviewCountResult>();
            return result?.Count ?? -1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get preview count");
            return -1;
        }
    }

    private record PreviewCountResult(int Count);

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
}
