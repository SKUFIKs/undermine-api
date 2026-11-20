using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using UnderMineApi.Middleware;
using Xunit;

namespace UnderMineApi.Tests;

public class ApiErrorFilterTests
{
    private static ResultExecutingContext CreateContext(IActionResult result)
    {
        var httpContext = new DefaultHttpContext();
        var actionContext = new ActionContext(
            httpContext, new RouteData(), new ActionDescriptor());

        return new ResultExecutingContext(
            actionContext,
            Array.Empty<IFilterMetadata>(),
            result,
            controller: new object());
    }

    [Fact]
    public void PhysicalFileResult_IsLeftUntouched()
    {
        var file = new PhysicalFileResult("/tmp/example.json", "application/json");
        var context = CreateContext(file);

        new ApiErrorNormalizationFilter().OnResultExecuting(context);

        Assert.Same(file, context.Result);
        Assert.Equal(StatusCodes.Status200OK, context.HttpContext.Response.StatusCode);
    }

    [Fact]
    public void BadRequestObjectResult_IsNormalized()
    {
        var context = CreateContext(new BadRequestObjectResult(new
        {
            error = "INVALID_PATH",
            message = "Path is invalid."
        }));

        new ApiErrorNormalizationFilter().OnResultExecuting(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }
}
