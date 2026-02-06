using Xunit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using UnderMineApi.Controllers;
using System.Net;

public class DebugControllerTests
{
    [Fact]
    public void GetIp_ReturnsIpAndHeaders()
    {
        // Arrange
        var controller = new DebugController();
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.1");
        httpContext.Request.Headers["User-Agent"] = "TestAgent";

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Act
        var result = controller.GetIp();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result);
        dynamic val = okResult.Value;

        // Используем рефлексию, так как возвращается анонимный тип
        var ip = (string)val.GetType().GetProperty("ResolvedRemoteIp").GetValue(val, null);
        Assert.Equal("192.168.1.1", ip);
    }
}