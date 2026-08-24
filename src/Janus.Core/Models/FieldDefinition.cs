namespace Janus.Core.Models;

public sealed class FieldDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ItemTypeId { get; init; }
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public required string DataType { get; set; }
    public bool Required { get; set; }
    public string? Description { get; set; }
    public IReadOnlyList<string> EnumOptions { get; set; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
