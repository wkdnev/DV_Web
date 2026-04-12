using DV.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace DV.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DocumentBlobController : ControllerBase
{
    private readonly DocumentUploadService _documentUploadService;
    private readonly ILogger<DocumentBlobController> _logger;
    private readonly IMemoryCache _cache;

    public DocumentBlobController(DocumentUploadService documentUploadService, ILogger<DocumentBlobController> logger, IMemoryCache cache)
    {
        _documentUploadService = documentUploadService;
        _logger = logger;
        _cache = cache;
    }

    [HttpGet("page/{pageId:int}")]
    public async Task<IActionResult> GetDocumentPage(int pageId, bool inline = true)
    {
        try
        {
            var content = await _documentUploadService.GetDocumentPageContentAsync(pageId);

            if (content == null)
            {
                _logger.LogWarning("Document page {PageId} not found", pageId);
                return NotFound("Document page not found");
            }

            var (fileContent, contentType, fileName) = content.Value;

            var contentDisposition = inline ? "inline" : "attachment";
            Response.Headers["Content-Disposition"] = $"{contentDisposition}; filename=\"{fileName}\"";
            Response.Headers["Cache-Control"] = "public, max-age=3600";

            return File(fileContent, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving document page {PageId}", pageId);
            return StatusCode(500, "Error retrieving document");
        }
    }

    [HttpGet("page/{pageId:int}/download")]
    public async Task<IActionResult> DownloadDocumentPage(int pageId)
    {
        return await GetDocumentPage(pageId, inline: false);
    }

    [HttpGet("page/{pageId:int}/jpeg")]
    public async Task<IActionResult> GetDocumentPageAsJpeg(int pageId)
    {
        try
        {
            var content = await _documentUploadService.GetDocumentPageContentAsync(pageId);
            if (content == null)
                return NotFound("Document page not found");

            var (fileContent, contentType, fileName) = content.Value;

            // If already JPEG, return as-is
            if (contentType != null && contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers["Cache-Control"] = "public, max-age=3600";
                return File(fileContent, "image/jpeg");
            }

            // Convert to JPEG
            using var inputStream = new MemoryStream(fileContent);
            using var image = Image.Load(inputStream);
            using var outputStream = new MemoryStream();
            image.SaveAsJpeg(outputStream, new JpegEncoder { Quality = 90 });

            Response.Headers["Cache-Control"] = "public, max-age=3600";
            return File(outputStream.ToArray(), "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting document page {PageId} to JPEG", pageId);
            return StatusCode(500, "Error converting document to JPEG");
        }
    }

    [HttpGet("page/{pageId:int}/image-info")]
    public async Task<IActionResult> GetImageInfo(int pageId)
    {
        try
        {
            var cacheKey = $"imginfo_{pageId}";
            if (_cache.TryGetValue(cacheKey, out object? cached))
                return Ok(cached);

            var content = await _documentUploadService.GetDocumentPageContentAsync(pageId);
            if (content == null)
                return NotFound("Document page not found");

            var (fileContent, contentType, fileName) = content.Value;

            var isImage = contentType != null && (
                contentType.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                contentType.Contains("tiff", StringComparison.OrdinalIgnoreCase));

            if (!isImage)
                return BadRequest("Not an image file");

            using var stream = new MemoryStream(fileContent);
            var imageInfo = Image.Identify(stream);

            if (imageInfo == null)
                return BadRequest("Could not identify image dimensions");

            int tileSize = 256;
            int maxDim = Math.Max(imageInfo.Width, imageInfo.Height);
            int maxLevel = maxDim > 1 ? (int)Math.Ceiling(Math.Log2(maxDim)) : 0;

            var result = new
            {
                width = imageInfo.Width,
                height = imageInfo.Height,
                tileSize = tileSize,
                maxLevel = maxLevel,
                format = contentType,
                fileSize = fileContent.Length
            };

            _cache.Set(cacheKey, result, TimeSpan.FromHours(4));
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image info for page {PageId}", pageId);
            return StatusCode(500, "Error retrieving image info");
        }
    }

    [HttpGet("page/{pageId:int}/tile/{level:int}/{col:int}/{row:int}")]
    public async Task<IActionResult> GetTile(int pageId, int level, int col, int row)
    {
        try
        {
            int tileSize = 256;
            var tileCacheKey = $"tile_{pageId}_{level}_{col}_{row}";

            if (_cache.TryGetValue(tileCacheKey, out byte[]? cachedTile) && cachedTile != null)
            {
                Response.Headers["Cache-Control"] = "public, max-age=86400";
                return File(cachedTile, "image/jpeg");
            }

            var blobCacheKey = $"blob_{pageId}";
            if (!_cache.TryGetValue(blobCacheKey, out (byte[] bytes, string contentType, string fileName) blob))
            {
                var content = await _documentUploadService.GetDocumentPageContentAsync(pageId);
                if (content == null)
                    return NotFound("Document page not found");

                blob = content.Value;
                _cache.Set(blobCacheKey, blob,
                    new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) });
            }

            using var inputStream = new MemoryStream(blob.bytes);
            using var image = Image.Load(inputStream);

            int imgWidth = image.Width;
            int imgHeight = image.Height;
            int maxDim = Math.Max(imgWidth, imgHeight);
            int maxLevel = maxDim > 1 ? (int)Math.Ceiling(Math.Log2(maxDim)) : 0;

            if (level > maxLevel || level < 0)
                return NotFound("Invalid level");

            double scaleFactor = Math.Pow(2, level) / Math.Pow(2, maxLevel);
            int levelWidth = Math.Max(1, (int)Math.Ceiling(imgWidth * scaleFactor));
            int levelHeight = Math.Max(1, (int)Math.Ceiling(imgHeight * scaleFactor));

            int tilesX = (int)Math.Ceiling((double)levelWidth / tileSize);
            int tilesY = (int)Math.Ceiling((double)levelHeight / tileSize);

            if (col >= tilesX || row >= tilesY || col < 0 || row < 0)
                return NotFound("Tile out of bounds");

            double invScale = Math.Pow(2, maxLevel - level);
            int srcX = (int)Math.Round(col * tileSize * invScale);
            int srcY = (int)Math.Round(row * tileSize * invScale);
            int srcW = (int)Math.Round(tileSize * invScale);
            int srcH = (int)Math.Round(tileSize * invScale);

            srcX = Math.Min(srcX, imgWidth - 1);
            srcY = Math.Min(srcY, imgHeight - 1);
            srcW = Math.Min(srcW, imgWidth - srcX);
            srcH = Math.Min(srcH, imgHeight - srcY);

            if (srcW <= 0 || srcH <= 0)
                return NotFound("Tile region empty");

            int outW = Math.Max(1, Math.Min(tileSize, levelWidth - col * tileSize));
            int outH = Math.Max(1, Math.Min(tileSize, levelHeight - row * tileSize));

            using var tile = image.Clone(ctx =>
            {
                ctx.Crop(new Rectangle(srcX, srcY, srcW, srcH));
                ctx.Resize(outW, outH);
            });

            using var outputStream = new MemoryStream();
            tile.SaveAsJpeg(outputStream, new JpegEncoder { Quality = 85 });

            var tileBytes = outputStream.ToArray();

            _cache.Set(tileCacheKey, tileBytes,
                new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(1) });

            Response.Headers["Cache-Control"] = "public, max-age=86400";
            return File(tileBytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating tile for page {PageId} level={Level} col={Col} row={Row}",
                pageId, level, col, row);
            return StatusCode(500, "Error generating tile");
        }
    }
}
