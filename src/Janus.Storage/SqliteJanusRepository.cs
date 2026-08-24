using System.Globalization;
using System.Text;
using Janus.Core.Abstractions;
using Janus.Core.Models;
using Microsoft.Data.Sqlite;

namespace Janus.Storage;

public sealed partial class SqliteJanusRepository(string databasePath) : IJanusRepository
{
    private readonly string _databasePath = Path.GetFullPath(databasePath);
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS assets (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    item_type_id TEXT NOT NULL REFERENCES item_types(id) ON DELETE RESTRICT,
                    type TEXT NULL,
                    manufacturer TEXT NULL,
                    model TEXT NULL,
                    serial_number TEXT NULL,
                    description TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS asset_aliases (
                    asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                    alias TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY (asset_id, alias)
                );

                CREATE TABLE IF NOT EXISTS facts (
                    id TEXT PRIMARY KEY,
                    asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                    key TEXT NOT NULL COLLATE NOCASE,
                    value TEXT NOT NULL,
                    value_type TEXT NOT NULL,
                    unit TEXT NULL,
                    source TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE (asset_id, key)
                );

                CREATE TABLE IF NOT EXISTS fact_history (
                    history_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    fact_id TEXT NOT NULL,
                    asset_id TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT NOT NULL,
                    value_type TEXT NOT NULL,
                    unit TEXT NULL,
                    source TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    archived_at TEXT NOT NULL,
                    operation TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS asset_events (
                    id TEXT PRIMARY KEY,
                    asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                    type TEXT NOT NULL,
                    notes TEXT NULL,
                    occurred_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS item_types (
                    id TEXT PRIMARY KEY,
                    key TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    display_name TEXT NOT NULL,
                    description TEXT NULL,
                    is_built_in INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS field_definitions (
                    id TEXT PRIMARY KEY,
                    item_type_id TEXT NOT NULL REFERENCES item_types(id) ON DELETE RESTRICT,
                    key TEXT NOT NULL COLLATE NOCASE,
                    display_name TEXT NOT NULL,
                    data_type TEXT NOT NULL,
                    required INTEGER NOT NULL,
                    description TEXT NULL,
                    enum_options TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    UNIQUE (item_type_id, key)
                );

                CREATE TABLE IF NOT EXISTS item_field_values (
                    item_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                    field_definition_id TEXT NOT NULL REFERENCES field_definitions(id) ON DELETE RESTRICT,
                    string_value TEXT NULL,
                    integer_value INTEGER NULL,
                    number_value REAL NULL,
                    boolean_value INTEGER NULL,
                    date_value TEXT NULL,
                    datetime_value TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (item_id, field_definition_id)
                );

                CREATE INDEX IF NOT EXISTS idx_assets_name ON assets(name COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_aliases_alias ON asset_aliases(alias COLLATE NOCASE);
                CREATE INDEX IF NOT EXISTS idx_facts_asset ON facts(asset_id);
                CREATE INDEX IF NOT EXISTS idx_events_asset ON asset_events(asset_id, occurred_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureDynamicTypeMigrationAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<Asset> CreateAssetAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.Name);
        await InitializeAsync(cancellationToken);
        if (asset.ItemTypeId == Guid.Empty)
        {
            asset.ItemTypeId = BasicItemTypeId;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertOrUpdateAssetAsync(connection, transaction, asset, isUpdate: false, cancellationToken);
        await ReplaceAliasesAsync(connection, transaction, asset.Id, asset.Aliases, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return asset;
    }

    public async Task<Asset?> UpdateAssetAsync(Asset asset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(asset.Name);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await InsertOrUpdateAssetAsync(connection, transaction, asset, isUpdate: true, cancellationToken);
        if (affected == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await ReplaceAliasesAsync(connection, transaction, asset.Id, asset.Aliases, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return asset;
    }

    public async Task<Asset?> GetAssetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, item_type_id, type, manufacturer, model, serial_number, description, created_at, updated_at FROM assets WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var asset = ReadAsset(reader);
        await reader.DisposeAsync();
        asset.Aliases = await GetAliasesAsync(connection, asset.Id, cancellationToken);
        return asset;
    }

    public async Task<IReadOnlyList<Asset>> FindAssetsAsync(string query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var assets = await GetAllAssetsAsync(cancellationToken);
        var tokens = Tokenize(query);
        if (tokens.Count == 0)
        {
            return assets;
        }

        var matches = new List<(Asset Asset, int Score)>();
        foreach (var asset in assets)
        {
            var facts = await GetFactsAsync(asset.Id, cancellationToken);
            var customValues = await GetItemFieldValuesAsync(asset.Id, cancellationToken);
            var identity = Normalize(string.Join(' ', new[]
            {
                asset.Name, asset.Type, asset.Manufacturer, asset.Model, asset.SerialNumber,
                asset.Description, string.Join(' ', asset.Aliases)
            }.Where(value => !string.IsNullOrWhiteSpace(value))));
            var factText = Normalize(string.Join(' ', facts.Select(fact => $"{fact.Key} {fact.Value} {fact.Unit}")
                .Concat(customValues.Select(value => $"{value.Key} {value.Value}"))));
            if (!tokens.All(token => identity.Contains(token, StringComparison.Ordinal) || factText.Contains(token, StringComparison.Ordinal)))
            {
                continue;
            }

            var score = tokens.Sum(token =>
                (identity.Contains(token, StringComparison.Ordinal) ? 3 : 0) +
                (factText.Contains(token, StringComparison.Ordinal) ? 1 : 0));
            matches.Add((asset, score));
        }

        return matches
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Asset)
            .ToArray();
    }

    public async Task<Fact> SetFactAsync(Fact fact, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact.Value);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var existing = await GetFactAsync(connection, transaction, fact.AssetId, fact.Key, cancellationToken);
        if (existing is not null)
        {
            await ArchiveFactAsync(connection, transaction, existing, "updated", cancellationToken);
        }

        var stored = existing is null
            ? fact
            : new Fact
            {
                Id = existing.Id,
                AssetId = existing.AssetId,
                Key = fact.Key.Trim(),
                Value = fact.Value.Trim(),
                ValueType = fact.ValueType,
                Unit = NormalizeOptional(fact.Unit),
                Source = NormalizeOptional(fact.Source),
                CreatedAt = existing.CreatedAt,
                UpdatedAt = fact.UpdatedAt
            };

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO facts (id, asset_id, key, value, value_type, unit, source, created_at, updated_at)
            VALUES ($id, $assetId, $key, $value, $valueType, $unit, $source, $createdAt, $updatedAt)
            ON CONFLICT(asset_id, key) DO UPDATE SET
                value = excluded.value,
                value_type = excluded.value_type,
                unit = excluded.unit,
                source = excluded.source,
                updated_at = excluded.updated_at
            """;
        AddFactParameters(command, stored);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    public async Task<IReadOnlyList<Fact>> GetFactsAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, asset_id, key, value, value_type, unit, source, created_at, updated_at FROM facts WHERE asset_id = $assetId ORDER BY key COLLATE NOCASE";
        command.Parameters.AddWithValue("$assetId", assetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var facts = new List<Fact>();
        while (await reader.ReadAsync(cancellationToken))
        {
            facts.Add(ReadFact(reader));
        }

        return facts;
    }

    public async Task<bool> RemoveFactAsync(Guid assetId, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var existing = await GetFactAsync(connection, transaction, assetId, key, cancellationToken);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await ArchiveFactAsync(connection, transaction, existing, "removed", cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM facts WHERE id = $id";
        command.Parameters.AddWithValue("$id", existing.Id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<AssetEvent> RecordEventAsync(AssetEvent assetEvent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetEvent.Type);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO asset_events (id, asset_id, type, notes, occurred_at, created_at)
            VALUES ($id, $assetId, $type, $notes, $occurredAt, $createdAt)
            """;
        command.Parameters.AddWithValue("$id", assetEvent.Id.ToString());
        command.Parameters.AddWithValue("$assetId", assetEvent.AssetId.ToString());
        command.Parameters.AddWithValue("$type", assetEvent.Type.Trim());
        command.Parameters.AddWithValue("$notes", DbValue(assetEvent.Notes));
        command.Parameters.AddWithValue("$occurredAt", FormatDate(assetEvent.OccurredAt));
        command.Parameters.AddWithValue("$createdAt", FormatDate(assetEvent.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return assetEvent;
    }

    public async Task<IReadOnlyList<AssetEvent>> GetEventsAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, asset_id, type, notes, occurred_at, created_at FROM asset_events WHERE asset_id = $assetId ORDER BY occurred_at DESC";
        command.Parameters.AddWithValue("$assetId", assetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<AssetEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new AssetEvent
            {
                Id = Guid.Parse(reader.GetString(0)),
                AssetId = Guid.Parse(reader.GetString(1)),
                Type = reader.GetString(2),
                Notes = GetNullableString(reader, 3),
                OccurredAt = ParseDate(reader.GetString(4)),
                CreatedAt = ParseDate(reader.GetString(5))
            });
        }

        return events;
    }

    private async Task<IReadOnlyList<Asset>> GetAllAssetsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, item_type_id, type, manufacturer, model, serial_number, description, created_at, updated_at FROM assets ORDER BY name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var assets = new List<Asset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            assets.Add(ReadAsset(reader));
        }

        await reader.DisposeAsync();
        foreach (var asset in assets)
        {
            asset.Aliases = await GetAliasesAsync(connection, asset.Id, cancellationToken);
        }

        return assets;
    }

    private static async Task<int> InsertOrUpdateAssetAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Asset asset,
        bool isUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = isUpdate
            ? """
              UPDATE assets SET name = $name, item_type_id = $itemTypeId, type = $type, manufacturer = $manufacturer,
                  model = $model, serial_number = $serialNumber, description = $description,
                  updated_at = $updatedAt
              WHERE id = $id
              """
            : """
              INSERT INTO assets (id, name, item_type_id, type, manufacturer, model, serial_number, description, created_at, updated_at)
              VALUES ($id, $name, $itemTypeId, $type, $manufacturer, $model, $serialNumber, $description, $createdAt, $updatedAt)
              """;
        command.Parameters.AddWithValue("$id", asset.Id.ToString());
        command.Parameters.AddWithValue("$name", asset.Name.Trim());
        command.Parameters.AddWithValue("$itemTypeId", asset.ItemTypeId.ToString());
        command.Parameters.AddWithValue("$type", DbValue(asset.Type));
        command.Parameters.AddWithValue("$manufacturer", DbValue(asset.Manufacturer));
        command.Parameters.AddWithValue("$model", DbValue(asset.Model));
        command.Parameters.AddWithValue("$serialNumber", DbValue(asset.SerialNumber));
        command.Parameters.AddWithValue("$description", DbValue(asset.Description));
        command.Parameters.AddWithValue("$createdAt", FormatDate(asset.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(asset.UpdatedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceAliasesAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid assetId,
        IEnumerable<string> aliases,
        CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM asset_aliases WHERE asset_id = $assetId";
            delete.Parameters.AddWithValue("$assetId", assetId.ToString());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var alias in aliases.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO asset_aliases (asset_id, alias) VALUES ($assetId, $alias)";
            insert.Parameters.AddWithValue("$assetId", assetId.ToString());
            insert.Parameters.AddWithValue("$alias", alias);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<string>> GetAliasesAsync(SqliteConnection connection, Guid assetId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias FROM asset_aliases WHERE asset_id = $assetId ORDER BY alias COLLATE NOCASE";
        command.Parameters.AddWithValue("$assetId", assetId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var aliases = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            aliases.Add(reader.GetString(0));
        }

        return aliases;
    }

    private static async Task<Fact?> GetFactAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid assetId,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT id, asset_id, key, value, value_type, unit, source, created_at, updated_at FROM facts WHERE asset_id = $assetId AND key = $key COLLATE NOCASE";
        command.Parameters.AddWithValue("$assetId", assetId.ToString());
        command.Parameters.AddWithValue("$key", key.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFact(reader) : null;
    }

    private static async Task ArchiveFactAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Fact fact,
        string operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO fact_history (fact_id, asset_id, key, value, value_type, unit, source, created_at, updated_at, archived_at, operation)
            VALUES ($id, $assetId, $key, $value, $valueType, $unit, $source, $createdAt, $updatedAt, $archivedAt, $operation)
            """;
        AddFactParameters(command, fact);
        command.Parameters.AddWithValue("$archivedAt", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$operation", operation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static Asset ReadAsset(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        ItemTypeId = Guid.Parse(reader.GetString(2)),
        Type = GetNullableString(reader, 3),
        Manufacturer = GetNullableString(reader, 4),
        Model = GetNullableString(reader, 5),
        SerialNumber = GetNullableString(reader, 6),
        Description = GetNullableString(reader, 7),
        CreatedAt = ParseDate(reader.GetString(8)),
        UpdatedAt = ParseDate(reader.GetString(9))
    };

    private static Fact ReadFact(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        AssetId = Guid.Parse(reader.GetString(1)),
        Key = reader.GetString(2),
        Value = reader.GetString(3),
        ValueType = reader.GetString(4),
        Unit = GetNullableString(reader, 5),
        Source = GetNullableString(reader, 6),
        CreatedAt = ParseDate(reader.GetString(7)),
        UpdatedAt = ParseDate(reader.GetString(8))
    };

    private static void AddFactParameters(SqliteCommand command, Fact fact)
    {
        command.Parameters.AddWithValue("$id", fact.Id.ToString());
        command.Parameters.AddWithValue("$assetId", fact.AssetId.ToString());
        command.Parameters.AddWithValue("$key", fact.Key.Trim());
        command.Parameters.AddWithValue("$value", fact.Value.Trim());
        command.Parameters.AddWithValue("$valueType", fact.ValueType.Trim());
        command.Parameters.AddWithValue("$unit", DbValue(fact.Unit));
        command.Parameters.AddWithValue("$source", DbValue(fact.Source));
        command.Parameters.AddWithValue("$createdAt", FormatDate(fact.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatDate(fact.UpdatedAt));
    }

    private static IReadOnlyList<string> Tokenize(string query) => Normalize(query)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static object DbValue(string? value) => NormalizeOptional(value) is { } normalized ? normalized : DBNull.Value;
    private static string? GetNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
