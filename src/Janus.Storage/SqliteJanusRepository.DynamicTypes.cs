using System.Globalization;
using System.Text.Json;
using Janus.Core.Models;
using Microsoft.Data.Sqlite;

namespace Janus.Storage;

public sealed partial class SqliteJanusRepository
{
    private static readonly Guid BasicItemTypeId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static async Task EnsureDynamicTypeMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(assets)";
        var hasItemTypeId = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hasItemTypeId |= reader.GetString(1).Equals("item_type_id", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (!hasItemTypeId)
        {
            await using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE assets ADD COLUMN item_type_id TEXT NULL";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        var now = FormatDate(DateTimeOffset.UtcNow);
        await using var migrate = connection.CreateCommand();
        migrate.CommandText = """
            INSERT OR IGNORE INTO item_types
                (id, key, display_name, description, is_built_in, created_at, updated_at)
            VALUES ($id, 'basic', 'Basic', 'Default type for assets without a custom schema.', 1, $now, $now);
            UPDATE assets SET item_type_id = $id WHERE item_type_id IS NULL OR item_type_id = '';
            """;
        migrate.Parameters.AddWithValue("$id", BasicItemTypeId.ToString());
        migrate.Parameters.AddWithValue("$now", now);
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemType>> ListItemTypesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, key, display_name, description, is_built_in, created_at, updated_at FROM item_types ORDER BY key COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var types = new List<ItemType>();
        while (await reader.ReadAsync(cancellationToken))
        {
            types.Add(ReadItemType(reader));
        }

        await reader.DisposeAsync();
        foreach (var type in types)
        {
            type.Fields = await GetFieldDefinitionsAsync(connection, type.Id, cancellationToken);
        }

        return types;
    }

    public async Task<ItemType?> GetItemTypeAsync(string reference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Guid.TryParse(reference, out var id)
            ? "SELECT id, key, display_name, description, is_built_in, created_at, updated_at FROM item_types WHERE id = $reference"
            : "SELECT id, key, display_name, description, is_built_in, created_at, updated_at FROM item_types WHERE key = $reference COLLATE NOCASE";
        command.Parameters.AddWithValue("$reference", id == Guid.Empty ? reference.Trim() : id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var type = ReadItemType(reader);
        await reader.DisposeAsync();
        type.Fields = await GetFieldDefinitionsAsync(connection, type.Id, cancellationToken);
        return type;
    }

    public async Task<ItemType> CreateItemTypeAsync(ItemType itemType, CancellationToken cancellationToken = default)
    {
        ValidateKey(itemType.Key, nameof(itemType.Key));
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType.DisplayName);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_types (id, key, display_name, description, is_built_in, created_at, updated_at)
            VALUES ($id, $key, $displayName, $description, 0, $createdAt, $updatedAt)
            """;
        AddItemTypeParameters(command, itemType);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return itemType;
    }

    public async Task<ItemType?> UpdateItemTypeAsync(ItemType itemType, CancellationToken cancellationToken = default)
    {
        ValidateKey(itemType.Key, nameof(itemType.Key));
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType.DisplayName);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE item_types SET key = $key, display_name = $displayName,
                description = $description, updated_at = $updatedAt
            WHERE id = $id AND is_built_in = 0
            """;
        AddItemTypeParameters(command, itemType);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 0 ? null : itemType;
    }

    public async Task<bool> DeleteItemTypeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var fields = connection.CreateCommand())
        {
            fields.Transaction = transaction;
            fields.CommandText = """
                DELETE FROM field_definitions
                WHERE item_type_id = $id
                  AND EXISTS (
                    SELECT 1 FROM item_types
                    WHERE id = $id AND is_built_in = 0
                      AND NOT EXISTS (SELECT 1 FROM assets WHERE item_type_id = $id))
                """;
            fields.Parameters.AddWithValue("$id", id.ToString());
            await fields.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM item_types
            WHERE id = $id AND is_built_in = 0
              AND NOT EXISTS (SELECT 1 FROM assets WHERE item_type_id = $id)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        var deleted = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<FieldDefinition> AddFieldDefinitionAsync(FieldDefinition field, CancellationToken cancellationToken = default)
    {
        ValidateFieldDefinition(field);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (field.Required)
        {
            await using var existingItems = connection.CreateCommand();
            existingItems.CommandText = "SELECT EXISTS (SELECT 1 FROM assets WHERE item_type_id = $itemTypeId)";
            existingItems.Parameters.AddWithValue("$itemTypeId", field.ItemTypeId.ToString());
            if (Convert.ToBoolean(await existingItems.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture))
            {
                throw new InvalidOperationException("A required field cannot be added while items of this type already exist.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO field_definitions
                (id, item_type_id, key, display_name, data_type, required, description, enum_options, created_at, updated_at)
            VALUES ($id, $itemTypeId, $key, $displayName, $dataType, $required, $description, $enumOptions, $createdAt, $updatedAt)
            """;
        AddFieldParameters(command, field);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return field;
    }

    public async Task<FieldDefinition?> UpdateFieldDefinitionAsync(FieldDefinition field, CancellationToken cancellationToken = default)
    {
        ValidateFieldDefinition(field);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var safety = connection.CreateCommand())
        {
            safety.Transaction = transaction;
            safety.CommandText = """
                SELECT data_type,
                    EXISTS (SELECT 1 FROM item_field_values WHERE field_definition_id = $id)
                FROM field_definitions WHERE id = $id
                """;
            safety.Parameters.AddWithValue("$id", field.Id.ToString());
            await using var reader = await safety.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            if (reader.GetBoolean(1) && !reader.GetString(0).Equals(field.DataType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The data type cannot be changed while this field has values.");
            }
        }

        if (field.DataType.Equals("enum", StringComparison.OrdinalIgnoreCase))
        {
            await using var enumCheck = connection.CreateCommand();
            enumCheck.Transaction = transaction;
            enumCheck.CommandText = "SELECT DISTINCT string_value FROM item_field_values WHERE field_definition_id = $id";
            enumCheck.Parameters.AddWithValue("$id", field.Id.ToString());
            await using var reader = await enumCheck.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var storedValue = reader.GetString(0);
                if (!field.EnumOptions.Contains(storedValue, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Enum option '{storedValue}' cannot be removed while items use it.");
                }
            }
        }

        if (field.Required)
        {
            await using var requiredCheck = connection.CreateCommand();
            requiredCheck.Transaction = transaction;
            requiredCheck.CommandText = """
                SELECT COUNT(*) FROM assets a
                WHERE a.item_type_id = $itemTypeId
                  AND NOT EXISTS (
                    SELECT 1 FROM item_field_values v
                    WHERE v.item_id = a.id AND v.field_definition_id = $id)
                """;
            requiredCheck.Parameters.AddWithValue("$itemTypeId", field.ItemTypeId.ToString());
            requiredCheck.Parameters.AddWithValue("$id", field.Id.ToString());
            if (Convert.ToInt64(await requiredCheck.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
            {
                throw new InvalidOperationException("The field cannot be made required while items are missing a value.");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE field_definitions SET key = $key, display_name = $displayName,
                data_type = $dataType, required = $required, description = $description,
                enum_options = $enumOptions, updated_at = $updatedAt
            WHERE id = $id AND item_type_id = $itemTypeId
            """;
        AddFieldParameters(command, field);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected == 0 ? null : field;
    }

    public async Task<bool> RemoveFieldDefinitionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM field_definitions
            WHERE id = $id
              AND NOT EXISTS (SELECT 1 FROM item_field_values WHERE field_definition_id = $id)
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<ItemFieldValue>> GetItemFieldValuesAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT v.item_id, v.field_definition_id, f.key, f.data_type,
                v.string_value, v.integer_value, v.number_value, v.boolean_value,
                v.date_value, v.datetime_value, v.created_at, v.updated_at
            FROM item_field_values v
            JOIN field_definitions f ON f.id = v.field_definition_id
            WHERE v.item_id = $itemId ORDER BY f.key COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$itemId", assetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<ItemFieldValue>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(ReadItemFieldValue(reader));
        }

        return values;
    }

    public async Task<IReadOnlyList<ItemFieldValue>> ReplaceItemFieldValuesAsync(
        Guid assetId,
        IReadOnlyList<ItemFieldValue> values,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var definitions = await GetAssetFieldDefinitionsAsync(connection, transaction, assetId, cancellationToken);
        var supplied = values.ToDictionary(value => value.FieldDefinitionId);
        var unknown = supplied.Keys.Except(definitions.Select(field => field.Id)).Any();
        if (unknown)
        {
            throw new InvalidOperationException("A supplied property is not defined for the item's type.");
        }

        var missing = definitions.Where(field => field.Required && !supplied.ContainsKey(field.Id)).Select(field => field.Key).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Missing required properties: {string.Join(", ", missing)}.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM item_field_values WHERE item_id = $itemId";
            delete.Parameters.AddWithValue("$itemId", assetId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var value in values)
        {
            var definition = definitions.Single(field => field.Id == value.FieldDefinitionId);
            if (!definition.DataType.Equals(value.DataType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Property '{definition.Key}' has the wrong data type.");
            }

            await InsertItemFieldValueAsync(connection, transaction, value, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return values;
    }

    private static async Task<IReadOnlyList<FieldDefinition>> GetFieldDefinitionsAsync(
        SqliteConnection connection,
        Guid itemTypeId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, item_type_id, key, display_name, data_type, required, description,
                enum_options, created_at, updated_at
            FROM field_definitions WHERE item_type_id = $itemTypeId ORDER BY key COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$itemTypeId", itemTypeId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var fields = new List<FieldDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            fields.Add(ReadFieldDefinition(reader));
        }

        return fields;
    }

    private static async Task<IReadOnlyList<FieldDefinition>> GetAssetFieldDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT item_type_id FROM assets WHERE id = $id";
        command.Parameters.AddWithValue("$id", assetId.ToString());
        var itemTypeId = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("Item was not found.");
        return await GetFieldDefinitionsAsync(connection, Guid.Parse(itemTypeId), cancellationToken, transaction);
    }

    private static async Task InsertItemFieldValueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ItemFieldValue value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO item_field_values
                (item_id, field_definition_id, string_value, integer_value, number_value,
                 boolean_value, date_value, datetime_value, created_at, updated_at)
            VALUES ($itemId, $fieldId, $string, $integer, $number, $boolean, $date, $datetime, $createdAt, $updatedAt)
            """;
        command.Parameters.AddWithValue("$itemId", value.AssetId.ToString());
        command.Parameters.AddWithValue("$fieldId", value.FieldDefinitionId.ToString());
        command.Parameters.AddWithValue("$string", DbValue(value.DataType is "string" or "url" or "enum" ? value.Value : null));
        command.Parameters.AddWithValue("$integer", value.DataType == "integer" ? long.Parse(value.Value, CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$number", value.DataType == "number" ? double.Parse(value.Value, CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$boolean", value.DataType == "boolean" ? (bool.Parse(value.Value) ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$date", DbValue(value.DataType == "date" ? value.Value : null));
        command.Parameters.AddWithValue("$datetime", DbValue(value.DataType == "datetime" ? value.Value : null));
        command.Parameters.AddWithValue("$createdAt", FormatDate(value.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(value.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ItemType ReadItemType(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Key = reader.GetString(1),
        DisplayName = reader.GetString(2),
        Description = GetNullableString(reader, 3),
        IsBuiltIn = reader.GetBoolean(4),
        CreatedAt = ParseDate(reader.GetString(5)),
        UpdatedAt = ParseDate(reader.GetString(6))
    };

    private static FieldDefinition ReadFieldDefinition(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        ItemTypeId = Guid.Parse(reader.GetString(1)),
        Key = reader.GetString(2),
        DisplayName = reader.GetString(3),
        DataType = reader.GetString(4),
        Required = reader.GetBoolean(5),
        Description = GetNullableString(reader, 6),
        EnumOptions = reader.IsDBNull(7) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [],
        CreatedAt = ParseDate(reader.GetString(8)),
        UpdatedAt = ParseDate(reader.GetString(9))
    };

    private static ItemFieldValue ReadItemFieldValue(SqliteDataReader reader)
    {
        var dataType = reader.GetString(3);
        var value = dataType switch
        {
            "integer" => reader.GetInt64(5).ToString(CultureInfo.InvariantCulture),
            "number" => reader.GetDouble(6).ToString("R", CultureInfo.InvariantCulture),
            "boolean" => reader.GetBoolean(7).ToString().ToLowerInvariant(),
            "date" => reader.GetString(8),
            "datetime" => reader.GetString(9),
            _ => reader.GetString(4)
        };
        return new ItemFieldValue
        {
            AssetId = Guid.Parse(reader.GetString(0)),
            FieldDefinitionId = Guid.Parse(reader.GetString(1)),
            Key = reader.GetString(2),
            DataType = dataType,
            Value = value,
            CreatedAt = ParseDate(reader.GetString(10)),
            UpdatedAt = ParseDate(reader.GetString(11))
        };
    }

    private static void AddItemTypeParameters(SqliteCommand command, ItemType itemType)
    {
        command.Parameters.AddWithValue("$id", itemType.Id.ToString());
        command.Parameters.AddWithValue("$key", itemType.Key.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$displayName", itemType.DisplayName.Trim());
        command.Parameters.AddWithValue("$description", DbValue(itemType.Description));
        command.Parameters.AddWithValue("$createdAt", FormatDate(itemType.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(itemType.UpdatedAt));
    }

    private static void AddFieldParameters(SqliteCommand command, FieldDefinition field)
    {
        command.Parameters.AddWithValue("$id", field.Id.ToString());
        command.Parameters.AddWithValue("$itemTypeId", field.ItemTypeId.ToString());
        command.Parameters.AddWithValue("$key", field.Key.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$displayName", field.DisplayName.Trim());
        command.Parameters.AddWithValue("$dataType", field.DataType.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$required", field.Required);
        command.Parameters.AddWithValue("$description", DbValue(field.Description));
        command.Parameters.AddWithValue("$enumOptions", field.EnumOptions.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(field.EnumOptions));
        command.Parameters.AddWithValue("$createdAt", FormatDate(field.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(field.UpdatedAt));
    }

    private static void ValidateFieldDefinition(FieldDefinition field)
    {
        ValidateKey(field.Key, nameof(field.Key));
        ArgumentException.ThrowIfNullOrWhiteSpace(field.DisplayName);
        var supported = new[] { "string", "integer", "number", "boolean", "date", "datetime", "url", "enum" };
        if (!supported.Contains(field.DataType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported data type '{field.DataType}'. Supported types: {string.Join(", ", supported)}.");
        }

        if (field.DataType.Equals("enum", StringComparison.OrdinalIgnoreCase) && field.EnumOptions.Count == 0)
        {
            throw new ArgumentException("Enum fields require at least one option.");
        }

        if (!field.DataType.Equals("enum", StringComparison.OrdinalIgnoreCase) && field.EnumOptions.Count > 0)
        {
            throw new ArgumentException("Enum options may only be supplied for enum fields.");
        }
    }

    private static void ValidateKey(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!key.All(character => char.IsLower(character) || char.IsDigit(character) || character == '_') || !char.IsLetter(key[0]))
        {
            throw new ArgumentException("Keys must start with a lowercase letter and contain only lowercase letters, numbers, and underscores.", parameterName);
        }
    }
}
