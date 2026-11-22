using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using UnderMineApi.Services.Interfaces;

namespace UnderMineApi.Services.Security;

/// <summary>Requires one exact permission.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission) => Permission = permission;

    public string Permission { get; }
}

/// <summary>Requires at least one permission from a group (used for the Users page).</summary>
public sealed class AnyPermissionRequirement : IAuthorizationRequirement
{
    public AnyPermissionRequirement(params string[] permissions) => Permissions = permissions;

    public IReadOnlyList<string> Permissions { get; }
}

public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionService _permissions;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PermissionAuthorizationHandler(
        IPermissionService permissions,
        IHttpContextAccessor httpContextAccessor)
    {
        _permissions = permissions;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var role = context.User.FindFirst(ClaimTypes.Role)?.Value;
        if (string.IsNullOrWhiteSpace(role))
            return;

        var cancellationToken = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        if (await _permissions.HasPermissionAsync(role, requirement.Permission, cancellationToken))
            context.Succeed(requirement);
    }
}

public sealed class AnyPermissionAuthorizationHandler : AuthorizationHandler<AnyPermissionRequirement>
{
    private readonly IPermissionService _permissions;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AnyPermissionAuthorizationHandler(
        IPermissionService permissions,
        IHttpContextAccessor httpContextAccessor)
    {
        _permissions = permissions;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AnyPermissionRequirement requirement)
    {
        var role = context.User.FindFirst(ClaimTypes.Role)?.Value;
        if (string.IsNullOrWhiteSpace(role))
            return;

        var cancellationToken = _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        if (await _permissions.HasAnyPermissionAsync(role, requirement.Permissions, cancellationToken))
            context.Succeed(requirement);
    }
}
