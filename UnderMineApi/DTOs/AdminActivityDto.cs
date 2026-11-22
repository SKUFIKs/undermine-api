namespace UnderMineApi.DTOs;

/// <summary>One audited administrative action.</summary>
public class AdminActivityItemDto
{
    public long Id { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public long? TargetId { get; set; }
    public string? Details { get; set; }
    public long CreatedAt { get; set; }
}

/// <summary>Named counter used by the activity summary.</summary>
public class AdminActivityCountDto
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>Aggregates for the current filter selection.</summary>
public class AdminActivitySummaryDto
{
    public int TotalCount { get; set; }
    public int TodayCount { get; set; }
    public int Last7DaysCount { get; set; }
    public int UniqueActors { get; set; }
    public int ActionTypeCount { get; set; }
    public List<AdminActivityCountDto> TopActions { get; set; } = new();
    public List<AdminActivityCountDto> TopActors { get; set; } = new();
}

/// <summary>All values available in the activity filter dropdowns.</summary>
public class AdminActivityFilterOptionsDto
{
    public List<string> Actions { get; set; } = new();
    public List<string> Actors { get; set; } = new();
    public List<string> TargetTypes { get; set; } = new();
}

/// <summary>Paged activity log plus summary and filter metadata.</summary>
public class AdminActivityResponse
{
    public List<AdminActivityItemDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public AdminActivitySummaryDto Summary { get; set; } = new();
    public AdminActivityFilterOptionsDto FilterOptions { get; set; } = new();
}
