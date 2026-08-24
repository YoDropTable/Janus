namespace Janus.Core.Models;

public sealed class ItemFieldValue
{
    public required Guid AssetId { get; init; }
    public required Guid FieldDefinitionId { get; init; }
    public required string Key { get; init; }
    public required string DataType { get; init; }
    public required string Value { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
