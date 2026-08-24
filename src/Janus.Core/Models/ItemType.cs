namespace Janus.Core.Models;

public sealed class ItemType
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public string? Description { get; set; }
    public bool IsBuiltIn { get; init; }
    public IReadOnlyList<FieldDefinition> Fields { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
