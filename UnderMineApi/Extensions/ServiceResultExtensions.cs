using Microsoft.AspNetCore.Mvc;

namespace UnderMineApi.Extensions
{
    public static class ServiceResultExtensions
    {
        public static IActionResult ToActionResult(this (bool Success, string? Error) result)
        {
            if (result.Success) return new OkResult();
            return result.Error switch
            {
                "USER_NOT_FOUND" => new NotFoundObjectResult(new { Error = result.Error }),
                "USER_NOT_BANNED" => new BadRequestObjectResult(new { Error = result.Error }),
                "SERVER_ALREADY_EXISTS" => new ConflictObjectResult(new { Error = result.Error }),
                "BUILD_NOT_FOUND" => new BadRequestObjectResult(new { Error = result.Error, Message = "The selected build does not exist or is not completed yet." }),
                "NOT_FOUND" => new NotFoundResult(),
                _ => new ObjectResult(new { Error = "INTERNAL_ERROR" }) { StatusCode = 500 }
            };
        }

        public static IActionResult ToActionResult<T>(this (bool Success, T? Data, string? Error) result)
        {
            if (result.Success) return new OkObjectResult(result.Data);
            return result.Error switch
            {
                "SERVER_ALREADY_EXISTS" => new ConflictObjectResult(new { Error = result.Error }),
                "NOT_FOUND" => new NotFoundResult(),
                _ => new ObjectResult(new { Error = "INTERNAL_ERROR" }) { StatusCode = 500 }
            };
        }
    }
}