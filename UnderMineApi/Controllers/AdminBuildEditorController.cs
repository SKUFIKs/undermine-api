using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Minio.Exceptions;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers;

/// <summary>
/// Admin build editor: settings, file listing, text editing, uploads, downloads and renames.
/// Every file change updates S3, the build_files row and the stored client manifest together.
/// </summary>
[ApiController]
[Route("api/v1.0/admin/builds")]
[Authorize(Roles = "admin")]
[EnableRateLimiting(RateLimitingExtensions.AdminPolicy)]
public class AdminBuildEditorController : ControllerBase
{
    private readonly BuildEditorService _editor;
    private readonly ILogger<AdminBuildEditorController> _logger;

    public AdminBuildEditorController(
        BuildEditorService editor,
        ILogger<AdminBuildEditorController> logger)
    {
        _editor = editor;
        _logger = logger;
    }

    private string Actor => User.Identity?.Name ?? "system";

    [HttpGet("{id:long}/editor")]
    public async Task<IActionResult> GetEditor(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await _editor.GetEditorAsync(id, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpPut("{id:long}/settings")]
    public async Task<IActionResult> UpdateSettings(
        long id,
        [FromBody] UpdateBuildSettingsRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            return Ok(await _editor.UpdateSettingsAsync(id, request, Actor, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpGet("{id:long}/manifest")]
    public async Task<IActionResult> GetManifest(long id, CancellationToken ct)
    {
        try
        {
            return Ok(await _editor.GetManifestAsync(id, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpGet("{id:long}/files")]
    public async Task<IActionResult> GetDirectory(
        long id,
        [FromQuery] string? path,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _editor.GetDirectoryAsync(id, path, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpPost("{id:long}/directories")]
    public async Task<IActionResult> CreateDirectory(
        long id,
        [FromBody] CreateDirectoryRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            await _editor.CreateDirectoryAsync(id, request.Path, Actor, ct);
            return NoContent();
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpDelete("{id:long}/directories")]
    public async Task<IActionResult> DeleteDirectory(
        long id,
        [FromQuery] string path,
        CancellationToken ct)
    {
        try
        {
            await _editor.DeleteDirectoryAsync(id, path, Actor, ct);
            return NoContent();
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpGet("{id:long}/files/content")]
    public async Task<IActionResult> GetFileContent(
        long id,
        [FromQuery] string path,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _editor.ReadFileAsync(id, path, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpPut("{id:long}/files/content")]
    public async Task<IActionResult> SaveFile(
        long id,
        [FromBody] SaveBuildFileRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            return Ok(await _editor.SaveTextFileAsync(id, request, Actor, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpPost("{id:long}/files")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(BuildEditorService.MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = BuildEditorService.MaxUploadBytes)]
    public async Task<IActionResult> UploadFile(
        long id,
        [FromForm] IFormFile file,
        [FromForm] string? path,
        CancellationToken ct)
    {
        try
        {
            return Ok(await _editor.UploadFileAsync(id, path, file, Actor, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpGet("{id:long}/files/download")]
    public async Task<IActionResult> DownloadFile(
        long id,
        [FromQuery] string path,
        CancellationToken ct)
    {
        BuildDownloadInfo info;
        try
        {
            info = await _editor.GetDownloadAsync(id, path, ct);
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(), $"undermine-download-{Guid.NewGuid():N}.download");

        try
        {
            await _editor.DownloadToAsync(info, tempPath, ct);
        }
        catch (ObjectNotFoundException)
        {
            TryDeleteTemp(tempPath);
            return NotFound(new { error = "BUILD_FILE_NOT_FOUND", message = "The file is missing from storage." });
        }
        catch (Exception ex)
        {
            TryDeleteTemp(tempPath);
            _logger.LogError(ex, "Could not download build {BuildId} file {Path}", id, path);
            return StatusCode(502, new { error = "STORAGE_UNAVAILABLE", message = "Could not read the file from storage." });
        }

        HttpContext.Response.OnCompleted(() =>
        {
            try
            {
                if (System.IO.File.Exists(tempPath))
                    System.IO.File.Delete(tempPath);
            }
            catch
            {
                // Temp cleanup is best effort.
            }

            return Task.CompletedTask;
        });

        return PhysicalFile(
            tempPath,
            info.ContentType,
            info.FileName,
            enableRangeProcessing: true);
    }

    [HttpDelete("{id:long}/files")]
    public async Task<IActionResult> DeleteFile(
        long id,
        [FromQuery] string path,
        CancellationToken ct)
    {
        try
        {
            await _editor.DeleteFileAsync(id, path, Actor, ct);
            return NoContent();
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    [HttpPost("{id:long}/files/rename")]
    public async Task<IActionResult> RenameFile(
        long id,
        [FromBody] RenameBuildFileRequest request,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            return Ok(await _editor.RenameFileAsync(id, request, Actor, ct));
        }
        catch (BuildEditorException ex)
        {
            return EditorError(ex);
        }
    }

    private IActionResult EditorError(BuildEditorException ex) =>
        StatusCode(ex.StatusCode, new { error = ex.ErrorCode, message = ex.Message });

    private static void TryDeleteTemp(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
