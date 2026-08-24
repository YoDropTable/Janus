using System.Text.Json;
using Janus.Server;
using Janus.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Janus.Tests;

public sealed class VerticalSliceTests
{
    [Fact]
    public async Task AssetAndFactPersistAcrossRepositoryRestart()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var firstInstance = new SqliteJanusRepository(fixture.DatabasePath);
            var created = await JanusTools.CreateAssetAsync(
                firstInstance,
                "John Deere 8Y Cart",
                type: "cart",
                manufacturer: "John Deere",
                model: "8Y",
                aliases: ["John Deere cart"]);

            Assert.Equal("ok", created.Status);

            var saved = await JanusTools.SetFactAsync(
                firstInstance,
                "John Deere 8Y Cart",
                "tire_pressure",
                "30",
                unit: "psi",
                valueType: "number",
                source: "user");

            Assert.Equal("ok", saved.Status);

            // A new repository instance models a Janus process restart.
            var restartedInstance = new SqliteJanusRepository(fixture.DatabasePath);
            var search = await JanusTools.SearchAsync(
                restartedInstance,
                "John Deere cart tire pressure");

            var result = Assert.Single(search.Results);
            Assert.Equal("John Deere 8Y Cart", result.Asset.Name);
            var fact = Assert.Single(result.Facts);
            Assert.Equal("tire_pressure", fact.Key);
            Assert.Equal("30", fact.Value);
            Assert.Equal("psi", fact.Unit);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task AmbiguousHumanReferenceReturnsCandidates()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateAssetAsync(repository, "North Workshop Cart", aliases: ["shop cart"]);
            await JanusTools.CreateAssetAsync(repository, "South Workshop Cart", aliases: ["shop cart"]);

            var response = await JanusTools.SetFactAsync(
                repository,
                "shop cart",
                "tire_pressure",
                "30",
                unit: "psi");

            Assert.Equal("ambiguous", response.Status);
            Assert.Equal(2, response.Candidates.Count);
            Assert.Null(response.Fact);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task UpdateEventAndFactRemovalUseHumanAlias()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateAssetAsync(repository, "John Deere 8Y Cart", aliases: ["yard cart"]);
            await JanusTools.SetFactAsync(repository, "yard cart", "tire_pressure", "30", unit: "psi");

            var updated = await JanusTools.UpdateAssetAsync(
                repository,
                "yard cart",
                description: "Utility dump cart");
            var recorded = await JanusTools.RecordEventAsync(
                repository,
                "yard cart",
                "tire inflated",
                "Inflated both tires to specification.");
            var removed = await JanusTools.RemoveFactAsync(repository, "yard cart", "tire_pressure");
            var fetched = await JanusTools.GetAssetAsync(repository, "yard cart");

            Assert.Equal("Utility dump cart", updated.Result?.Asset.Description);
            Assert.Equal("ok", recorded.Status);
            Assert.Equal("ok", removed.Status);
            Assert.Empty(fetched.Result?.Facts ?? throw new InvalidOperationException("Expected an asset result."));
            Assert.Single(fetched.Result.Events);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task UnforeseenSoapTypeAndTypedValuesPersistAcrossRestart()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            var type = await JanusTools.CreateItemTypeAsync(repository, "soap", "Soap", "Personal cleansing product");
            Assert.Equal("ok", type.Status);

            await AddField(repository, "soap", "brand", "Brand", "string", required: true);
            await AddField(repository, "soap", "scent", "Scent", "string");
            await AddField(repository, "soap", "form", "Form", "enum", enumOptions: ["bar", "liquid", "foam"]);
            await AddField(repository, "soap", "size_oz", "Size (oz)", "number");
            await AddField(repository, "soap", "antibacterial", "Antibacterial", "boolean");

            var created = await JanusTools.CreateAssetAsync(
                repository,
                "Dr. Squatch Pine Tar",
                type: "soap",
                customProperties: Properties("""
                    {
                      "brand": "Dr. Squatch",
                      "scent": "Pine Tar",
                      "form": "bar",
                      "size_oz": 5,
                      "antibacterial": false
                    }
                    """));

            Assert.Equal("ok", created.Status);
            Assert.Equal("soap", created.Result?.ItemType.Key);
            Assert.Equal(5, created.Result?.CustomProperties.Count);

            var restarted = new SqliteJanusRepository(fixture.DatabasePath);
            var fetchedType = await JanusTools.GetItemTypeAsync(restarted, "soap");
            var search = await JanusTools.SearchAsync(restarted, "Pine Tar soap bar");

            Assert.Equal(5, fetchedType.ItemType?.Fields.Count);
            var result = Assert.Single(search.Results);
            Assert.Equal("soap", result.ItemType.Key);
            Assert.Equal("Dr. Squatch", result.CustomProperties.Single(value => value.Key == "brand").Value);
            Assert.Equal("5", result.CustomProperties.Single(value => value.Key == "size_oz").Value);
            Assert.Equal("false", result.CustomProperties.Single(value => value.Key == "antibacterial").Value);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Theory]
    [InlineData("{\"brand\":\"Example\",\"size_oz\":\"large\",\"form\":\"bar\"}", "size_oz")]
    [InlineData("{\"brand\":\"Example\",\"size_oz\":5,\"form\":\"cube\"}", "form")]
    [InlineData("{\"size_oz\":5,\"form\":\"bar\"}", "brand")]
    [InlineData("{\"brand\":\"Example\",\"size_oz\":5,\"form\":\"bar\",\"color\":\"black\"}", "color")]
    public async Task TypedPropertiesReturnUsefulValidationErrors(string json, string expectedField)
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateItemTypeAsync(repository, "soap", "Soap");
            await AddField(repository, "soap", "brand", "Brand", "string", required: true);
            await AddField(repository, "soap", "size_oz", "Size", "number");
            await AddField(repository, "soap", "form", "Form", "enum", enumOptions: ["bar", "liquid"]);

            var response = await JanusTools.CreateAssetAsync(
                repository,
                "Invalid soap",
                type: "soap",
                customProperties: Properties(json));

            Assert.Equal("validation_error", response.Status);
            Assert.Contains(expectedField, response.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty((await repository.FindAssetsAsync("Invalid soap")));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task TypeAndFieldManagementProtectsDefinitionsInUse()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateItemTypeAsync(repository, "soap", "Soap");
            var added = await AddField(repository, "soap", "brand", "Brand", "string");

            var renamed = await JanusTools.UpdateItemTypeAsync(repository, "soap", displayName: "Bath Soap");
            var updatedField = await JanusTools.UpdateFieldDefinitionAsync(
                repository, "soap", "brand", displayName: "Maker");
            var created = await JanusTools.CreateAssetAsync(
                repository,
                "Test Soap",
                type: "soap",
                customProperties: Properties("{\"brand\":\"Acme\"}"));
            var removeField = await JanusTools.RemoveFieldDefinitionAsync(repository, "soap", added.FieldDefinition!.Id.ToString());
            var deleteType = await JanusTools.DeleteItemTypeAsync(repository, "soap");

            Assert.Equal("Bath Soap", renamed.ItemType?.DisplayName);
            Assert.Equal("Maker", updatedField.FieldDefinition?.DisplayName);
            Assert.Equal("ok", created.Status);
            Assert.Equal("in_use", removeField.Status);
            Assert.Equal("in_use", deleteType.Status);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task LegacyDatabaseIsAutomaticallyMappedToBasicTypeWithoutLosingFacts()
    {
        var fixture = new DatabaseFixture();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.DatabasePath)!);
            var assetId = Guid.NewGuid();
            var factId = Guid.NewGuid();
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            await using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE assets (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, type TEXT NULL,
                        manufacturer TEXT NULL, model TEXT NULL, serial_number TEXT NULL,
                        description TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                    CREATE TABLE facts (
                        id TEXT PRIMARY KEY, asset_id TEXT NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
                        key TEXT NOT NULL COLLATE NOCASE, value TEXT NOT NULL, value_type TEXT NOT NULL,
                        unit TEXT NULL, source TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                        UNIQUE (asset_id, key));
                    INSERT INTO assets VALUES ($assetId, 'Legacy Cart', 'cart', NULL, NULL, NULL, NULL, $timestamp, $timestamp);
                    INSERT INTO facts VALUES ($factId, $assetId, 'tire_pressure', '30', 'number', 'psi', 'user', $timestamp, $timestamp);
                    """;
                command.Parameters.AddWithValue("$assetId", assetId.ToString());
                command.Parameters.AddWithValue("$factId", factId.ToString());
                command.Parameters.AddWithValue("$timestamp", timestamp);
                await command.ExecuteNonQueryAsync();
            }

            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            var fetched = await JanusTools.GetAssetAsync(repository, "Legacy Cart");

            Assert.Equal("ok", fetched.Status);
            Assert.Equal("basic", fetched.Result?.ItemType.Key);
            Assert.Equal("cart", fetched.Result?.Asset.Type);
            Assert.Equal("30", Assert.Single(fetched.Result?.Facts ?? []).Value);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task UnusedTypeWithDefinitionsCanBeDeleted()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateItemTypeAsync(repository, "unused", "Unused");
            await AddField(repository, "unused", "note", "Note", "string");

            var deleted = await JanusTools.DeleteItemTypeAsync(repository, "unused");

            Assert.Equal("ok", deleted.Status);
            Assert.Null((await JanusTools.GetItemTypeAsync(repository, "unused")).ItemType);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task AllSupportedFieldTypesRoundTripThroughTypedStorage()
    {
        var fixture = new DatabaseFixture();
        try
        {
            var repository = new SqliteJanusRepository(fixture.DatabasePath);
            await JanusTools.CreateItemTypeAsync(repository, "sample", "Sample");
            await AddField(repository, "sample", "text", "Text", "string");
            await AddField(repository, "sample", "count", "Count", "integer");
            await AddField(repository, "sample", "amount", "Amount", "number");
            await AddField(repository, "sample", "enabled", "Enabled", "boolean");
            await AddField(repository, "sample", "day", "Day", "date");
            await AddField(repository, "sample", "moment", "Moment", "datetime");
            await AddField(repository, "sample", "website", "Website", "url");
            await AddField(repository, "sample", "choice", "Choice", "enum", enumOptions: ["one", "two"]);

            var created = await JanusTools.CreateAssetAsync(
                repository,
                "Typed sample",
                type: "sample",
                customProperties: Properties("""
                    {
                      "text": "hello",
                      "count": 42,
                      "amount": 12.5,
                      "enabled": true,
                      "day": "2026-08-24",
                      "moment": "2026-08-24T12:34:56Z",
                      "website": "https://example.com/item",
                      "choice": "two"
                    }
                    """));

            Assert.Equal("ok", created.Status);
            var values = created.Result!.CustomProperties.ToDictionary(value => value.Key, value => value.Value);
            Assert.Equal("42", values["count"]);
            Assert.Equal("12.5", values["amount"]);
            Assert.Equal("true", values["enabled"]);
            Assert.Equal("2026-08-24", values["day"]);
            Assert.Equal("https://example.com/item", values["website"]);
            Assert.Equal("two", values["choice"]);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static async Task<FieldDefinitionResponse> AddField(
        SqliteJanusRepository repository,
        string type,
        string key,
        string displayName,
        string dataType,
        bool required = false,
        string[]? enumOptions = null)
    {
        var response = await JanusTools.AddFieldDefinitionAsync(
            repository, type, key, displayName, dataType, required, enumOptions: enumOptions);
        Assert.Equal("ok", response.Status);
        return response;
    }

    private static Dictionary<string, JsonElement> Properties(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private sealed class DatabaseFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"janus-tests-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "janus.db");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
