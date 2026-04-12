using DV.Shared.Models;
using Microsoft.AspNetCore.Components.Authorization;
using System.Net.Http.Json;
using System.Text.Json;

namespace DV.Web.Services;

/// <summary>
/// Client-side service for bulk upload operations.
/// Calls the DV_API BulkUpload endpoints via HttpClient.
/// </summary>
public class BulkUploadApiService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BulkUploadApiService> _logger;
    private readonly AuthenticationStateProvider _authStateProvider;

    public BulkUploadApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<BulkUploadApiService> logger,
        AuthenticationStateProvider authStateProvider)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _authStateProvider = authStateProvider;
    }

    /// <summary>
    /// Upload a batch of files with metadata to the API.
    /// </summary>
    public async Task<BulkUploadResult?> UploadBatchAsync(
        string schemaName, int projectId,
        List<BulkUploadFileEntry> fileEntries,
        IProgress<BulkUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient("Api");

        using var content = new MultipartFormDataContent();

        // Build metadata list
        var request = new BulkUploadRequest
        {
            SchemaName = schemaName,
            ProjectId = projectId,
            FileMetadata = fileEntries.Select(f => f.Metadata).ToList()
        };

        var metadataJson = JsonSerializer.Serialize(request);
        content.Add(new StringContent(metadataJson), "metadata");

        // Add files
        for (int i = 0; i < fileEntries.Count; i++)
        {
            var entry = fileEntries[i];
            var stream = entry.File.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
            var streamContent = new StreamContent(stream);
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                entry.File.ContentType ?? "application/octet-stream");
            content.Add(streamContent, entry.Metadata.ClientFileId, entry.File.Name);

            progress?.Report(new BulkUploadProgress
            {
                Phase = "Preparing",
                CurrentFile = entry.File.Name,
                CurrentIndex = i + 1,
                TotalFiles = fileEntries.Count
            });
        }

        progress?.Report(new BulkUploadProgress
        {
            Phase = "Uploading",
            CurrentFile = "",
            CurrentIndex = 0,
            TotalFiles = fileEntries.Count
        });

        // Create request with auth header
        var httpRequest = await CreateRequestAsync(HttpMethod.Post, "api/BulkUpload/upload");
        httpRequest.Content = content;

        try
        {
            var response = await client.SendAsync(httpRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Bulk upload failed: {Status} - {Body}", response.StatusCode, errorBody);
                return new BulkUploadResult
                {
                    TotalFiles = fileEntries.Count,
                    FailedCount = fileEntries.Count,
                    FileResults = fileEntries.Select(f => new BulkUploadFileResult
                    {
                        FileName = f.File.Name,
                        ClientFileId = f.Metadata.ClientFileId,
                        Success = false,
                        Error = $"Server error: {response.StatusCode}"
                    }).ToList()
                };
            }

            var result = await response.Content.ReadFromJsonAsync<BulkUploadResult>(cancellationToken: cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk upload request failed");
            throw;
        }
    }

    /// <summary>
    /// Download the CSV template file
    /// </summary>
    public async Task<byte[]?> DownloadCsvTemplateAsync()
    {
        try
        {
            var client = _httpClientFactory.CreateClient("Api");
            var request = await CreateRequestAsync(HttpMethod.Get, "api/BulkUpload/csv-template");
            var response = await client.SendAsync(request);
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsByteArrayAsync();
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download CSV template");
            return null;
        }
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
}

/// <summary>
/// Wraps an IBrowserFile with its associated metadata for upload
/// </summary>
public class BulkUploadFileEntry
{
    public required Microsoft.AspNetCore.Components.Forms.IBrowserFile File { get; set; }
    public required BulkUploadFileMetadata Metadata { get; set; }
}

/// <summary>
/// Progress report during bulk upload
/// </summary>
public class BulkUploadProgress
{
    public string Phase { get; set; } = "";
    public string CurrentFile { get; set; } = "";
    public int CurrentIndex { get; set; }
    public int TotalFiles { get; set; }
}
