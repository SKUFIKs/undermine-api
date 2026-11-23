using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UnderMineApi.Data;
using UnderMineApi.DTOs;
using UnderMineApi.Services;
using Xunit;

namespace UnderMineApi.Tests;

/// <summary>
/// Covers the public report endpoint rules: explicit consent, required launcher log, the
/// Minecraft-specific file requirement, allowed file types and the anonymous device id.
/// </summary>
public class ClientReportTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _context;
    private readonly ClientReportService _service;

    public ClientReportTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(options);
        _context.Database.EnsureCreated();

        _service = new ClientReportService(
            _context,
            NullLogger<ClientReportService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static FormFile File(string field, string fileName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, fileName);
    }

    private static ClientReportForm LauncherForm(string deviceId = "device-0123456789abcdef") => new()
    {
        Kind = "launcher",
        Title = "Launcher error",
        Summary = "Something failed",
        Consent = true,
        ConsentVersion = "1",
        DeviceId = deviceId,
        LauncherLog = File("LauncherLog", "launcher.log", "log line")
    };

    [Fact]
    public async Task RejectsReportWithoutConsent()
    {
        var form = LauncherForm();
        form.Consent = false;

        var exception = await Assert.ThrowsAsync<ClientReportException>(
            () => _service.CreateAsync(form, null, "127.0.0.1"));

        Assert.Equal("CONSENT_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task StoresLauncherReportWithItsLog()
    {
        var id = await _service.CreateAsync(LauncherForm(), null, "127.0.0.1");

        var report = await _context.ClientReports
            .Include(r => r.Files)
            .SingleAsync(r => r.Id == id);

        Assert.Equal("launcher", report.Kind);
        Assert.Equal("new", report.Status);
        Assert.Equal("1", report.ConsentVersion);
        Assert.Single(report.Files);
        Assert.Equal("launcher_log", report.Files[0].Kind);
        Assert.True(report.Files[0].Data.Length > 0);
    }

    [Fact]
    public async Task MinecraftReportRequiresAtLeastOneMinecraftFile()
    {
        var form = LauncherForm();
        form.Kind = "minecraft";

        var exception = await Assert.ThrowsAsync<ClientReportException>(
            () => _service.CreateAsync(form, null, null));

        Assert.Equal("MINECRAFT_FILES_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task RejectsNonTextAttachments()
    {
        var form = LauncherForm();
        form.LauncherLog = File("LauncherLog", "launcher.exe", "MZ");

        var exception = await Assert.ThrowsAsync<ClientReportException>(
            () => _service.CreateAsync(form, null, null));

        Assert.Equal("FILE_TYPE_NOT_ALLOWED", exception.Code);
    }

    [Fact]
    public async Task AnonymousReportRequiresDeviceId()
    {
        var form = LauncherForm(deviceId: string.Empty);

        var exception = await Assert.ThrowsAsync<ClientReportException>(
            () => _service.CreateAsync(form, null, null));

        Assert.Equal("DEVICE_ID_REQUIRED", exception.Code);
    }
}
