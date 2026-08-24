namespace Janus.Core.Models;

public sealed class AssetEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid AssetId { get; init; }
    public required string Type { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
