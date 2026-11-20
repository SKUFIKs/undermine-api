using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UnderMineApi.DTOs;
using UnderMineApi.Extensions;
using UnderMineApi.Options;
using UnderMineApi.Services;

namespace UnderMineApi.Controllers;

/// <summary>
/// Serves the Java runtime used by the launcher. The API downloads Temurin once, verifies its
/// SHA-256 and caches it, so clients never depend on Adoptium or GitHub being reachable.
/// </summary>
[ApiController]
[Route("api/v1.0/java")]
[Authorize]
[EnableRateLimiting(RateLimitingExtensions.ApiPolicy)]
public class JavaController : ControllerBase
{
    private readonly JavaRuntimeService _javaRuntime;
    private readonly JavaRuntimeOptions _options;
    private readonly ILogger<JavaController> _logger;

    public JavaController(
        JavaRuntimeService javaRuntime,
        IOptions<JavaRuntimeOptions> options,
        ILogger<JavaController> logger)
    {
        _javaRuntime = javaRuntime;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns metadata for a Java runtime: version, archive name, size, SHA-256 and the download
    /// URL. The first call may download the archive from Adoptium, so it can take a while.
    /// </summary>
    [HttpGet("{major:int}")]
    public async Task<IActionResult> GetRuntime(
        int major,
        [FromQuery] string? os,
        [FromQuery] string? arch,
        CancellationToken ct)
    {
        var invalid = ValidateRequest(major, os, arch, out var normalizedOs, out var normalizedArch);
        if (invalid != null)
            return invalid;

        var file = await _javaRuntime.GetAsync(major, normalizedOs, normalizedArch, ct);
        if (file == null)
        {
            return StatusCode(503, new
            {
                error = "JAVA_RUNTIME_UNAVAILABLE",
                message = $"Java {major} for {normalizedOs}/{normalizedArch} is not available right now."
            });
        }

        var query = $"os={Uri.EscapeDataString(file.Os)}&arch={Uri.EscapeDataString(file.Arch)}";

        return Ok(new JavaRuntimeDto
        {
            Major = file.Major,
            Os = file.Os,
            Arch = file.Arch,
            Version = file.Version,
            FileName = file.FileName,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256,
            // Relative on purpose: the host header must never decide where the client sends
            // its bearer token next.
            DownloadUrl = $"/api/v1.0/java/{file.Major}/download?{query}"
        });
    }

    /// <summary>
    /// Streams the Java runtime archive. Range requests are supported so a launcher can resume
    /// an interrupted download.
    /// </summary>
    [HttpGet("{major:int}/download")]
    public async Task<IActionResult> Download(
        int major,
        [FromQuery] string? os,
        [FromQuery] string? arch,
        CancellationToken ct)
    {
        var invalid = ValidateRequest(major, os, arch, out var normalizedOs, out var normalizedArch);
        if (invalid != null)
            return invalid;

        var file = await _javaRuntime.GetAsync(major, normalizedOs, normalizedArch, ct);
        if (file == null)
        {
            return StatusCode(503, new
            {
                error = "JAVA_RUNTIME_UNAVAILABLE",
                message = $"Java {major} for {normalizedOs}/{normalizedArch} is not available right now."
            });
        }

        _logger.LogInformation(
            "Serving Java {Major} ({Os}/{Arch}) archive {File} ({Size} bytes)",
            file.Major, file.Os, file.Arch, file.FileName, file.SizeBytes);

        Response.Headers.CacheControl = "private, max-age=3600";
        Response.Headers.ETag = $"\"{file.Sha256}\"";

        return PhysicalFile(
            file.FilePath,
            file.ContentType,
            file.FileName,
            enableRangeProcessing: true);
    }

    private IActionResult? ValidateRequest(
        int major,
        string? os,
        string? arch,
        out string normalizedOs,
        out string normalizedArch)
    {
        normalizedOs = string.Empty;
        normalizedArch = string.Empty;

        if (major < _options.MinMajor || major > _options.MaxMajor)
        {
            return BadRequest(new
            {
                error = "UNSUPPORTED_JAVA_VERSION",
                message = $"Java {major} is not supported. Use a version between " +
                          $"{_options.MinMajor} and {_options.MaxMajor}."
            });
        }

        if (!JavaRuntimeCatalog.TryNormalizePlatform(os, arch, out normalizedOs, out normalizedArch, out var error))
        {
            return BadRequest(new
            {
                error = "UNSUPPORTED_PLATFORM",
                message = error
            });
        }

        return null;
    }
}
