namespace Janus.Core.Models;

public sealed class Asset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ItemTypeId { get; set; }
    public required string Name { get; set; }
    public string? Type { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string> Aliases { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
