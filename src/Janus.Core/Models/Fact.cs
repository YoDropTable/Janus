namespace Janus.Core.Models;

public sealed class Fact
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid AssetId { get; init; }
    public required string Key { get; set; }
    public required string Value { get; set; }
    public string ValueType { get; set; } = "string";
    public string? Unit { get; set; }
    public string? Source { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
