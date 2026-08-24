using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Janus.Core.Abstractions;
using Janus.Core.Models;
using ModelContextProtocol.Server;

namespace Janus.Server;

[McpServerToolType]
public sealed class JanusTools
{
    [McpServerTool(Name = "janus_search")]
    [Description("Search Janus by asset name, alias, manufacturer, model, fact key, fact value, or unit. Use this first for questions about an asset; results include current facts so common questions can be answered in one call.")]
    public static async Task<SearchResponse> SearchAsync(
        IJanusRepository repository,
        [Description("Natural-language search, for example 'John Deere cart tire pressure'.")] string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var assets = await repository.FindAssetsAsync(query, cancellationToken);
        var results = new List<AssetDetails>();
        foreach (var asset in assets)
        {
            results.Add(await GetDetailsAsync(repository, asset, cancellationToken));
        }

        return new SearchResponse(results.Count == 0 ? "not_found" : "ok", query, results);
    }

    [McpServerTool(Name = "janus_get_asset")]
    [Description("Get one item with its type, typed custom properties, aliases, current facts, and event history. The reference may be an ID, exact human-readable name, or alias. Returns candidates when ambiguous.")]
    public static async Task<AssetResponse> GetAssetAsync(
        IJanusRepository repository,
        [Description("Asset ID, name, or alias.")] string assetReference,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAssetAsync(repository, assetReference, cancellationToken);
        if (resolution.Asset is null)
        {
            return ResolutionResponse(resolution);
        }

        return new AssetResponse(
            "ok",
            "Asset found.",
            await GetDetailsAsync(repository, resolution.Asset, cancellationToken),
            []);
    }

    [McpServerTool(Name = "janus_create_asset")]
    [Description("Create an item in Janus. Use a defined type key plus customProperties for schema-validated data. Unknown type labels without custom properties remain compatible as basic assets.")]
    public static async Task<AssetResponse> CreateAssetAsync(
        IJanusRepository repository,
        [Description("Unique, human-readable asset name.")] string name,
        [Description("Defined item type key. An unknown legacy category is mapped to the basic type when no custom properties are supplied.")] string? type = null,
        [Description("Asset manufacturer.")] string? manufacturer = null,
        [Description("Manufacturer model name or number.")] string? model = null,
        [Description("Serial number, if known.")] string? serialNumber = null,
        [Description("Free-form description.")] string? description = null,
        [Description("Alternative human-readable names used to resolve this asset.")] string[]? aliases = null,
        [Description("Typed properties defined by the selected item type. JSON kinds must match the field definitions.")] Dictionary<string, JsonElement>? customProperties = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var selectedType = await ResolveTypeForAssetAsync(repository, type, customProperties is not null, cancellationToken);
        if (selectedType.Type is null)
        {
            return new AssetResponse("validation_error", selectedType.Error!, null, []);
        }

        var now = DateTimeOffset.UtcNow;
        var asset = new Asset
        {
            ItemTypeId = selectedType.Type.Id,
            Name = name.Trim(),
            Type = NormalizeOptional(type),
            Manufacturer = NormalizeOptional(manufacturer),
            Model = NormalizeOptional(model),
            SerialNumber = NormalizeOptional(serialNumber),
            Description = NormalizeOptional(description),
            Aliases = NormalizeAliases(aliases),
            CreatedAt = now,
            UpdatedAt = now
        };
        var validation = ValidateProperties(asset.Id, selectedType.Type, customProperties ?? []);
        if (validation.Error is not null)
        {
            return new AssetResponse("validation_error", validation.Error, null, []);
        }

        await repository.CreateAssetAsync(asset, cancellationToken);
        await repository.ReplaceItemFieldValuesAsync(asset.Id, validation.Values, cancellationToken);
        return new AssetResponse(
            "ok",
            "Asset created.",
            await GetDetailsAsync(repository, asset, cancellationToken),
            []);
    }

    [McpServerTool(Name = "janus_update_asset")]
    [Description("Update an existing asset resolved by ID, name, or alias. Omitted fields keep their current values. Supplying aliases replaces the alias list. Returns candidates instead of guessing when ambiguous.")]
    public static async Task<AssetResponse> UpdateAssetAsync(
        IJanusRepository repository,
        [Description("Asset ID, name, or alias.")] string assetReference,
        [Description("New asset name, or omit to keep it unchanged.")] string? name = null,
        [Description("New defined item type key or legacy basic category; omit to keep it unchanged.")] string? type = null,
        [Description("New manufacturer, or omit to keep it unchanged.")] string? manufacturer = null,
        [Description("New model, or omit to keep it unchanged.")] string? model = null,
        [Description("New serial number, or omit to keep it unchanged.")] string? serialNumber = null,
        [Description("New description, or omit to keep it unchanged.")] string? description = null,
        [Description("Replacement aliases; omit to keep the current aliases, or pass an empty array to clear them.")] string[]? aliases = null,
        [Description("Complete replacement set of typed custom properties; omit to keep current values.")] Dictionary<string, JsonElement>? customProperties = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAssetAsync(repository, assetReference, cancellationToken);
        if (resolution.Asset is null)
        {
            return ResolutionResponse(resolution);
        }

        var asset = resolution.Asset;
        var selectedType = type is null
            ? await repository.GetItemTypeAsync(asset.ItemTypeId.ToString(), cancellationToken)
            : (await ResolveTypeForAssetAsync(repository, type, customProperties is not null, cancellationToken)).Type;
        if (selectedType is null)
        {
            return new AssetResponse("validation_error", $"Item type '{type}' does not exist. Create it before assigning custom properties.", null, []);
        }

        IReadOnlyList<ItemFieldValue>? validatedValues = null;
        if (customProperties is not null || selectedType.Id != asset.ItemTypeId)
        {
            var validation = ValidateProperties(asset.Id, selectedType, customProperties ?? []);
            if (validation.Error is not null)
            {
                return new AssetResponse("validation_error", validation.Error, null, []);
            }

            validatedValues = validation.Values;
        }

        asset.ItemTypeId = selectedType.Id;
        asset.Name = NormalizeOptional(name) ?? asset.Name;
        asset.Type = NormalizeOptional(type) ?? asset.Type;
        asset.Manufacturer = NormalizeOptional(manufacturer) ?? asset.Manufacturer;
        asset.Model = NormalizeOptional(model) ?? asset.Model;
        asset.SerialNumber = NormalizeOptional(serialNumber) ?? asset.SerialNumber;
        asset.Description = NormalizeOptional(description) ?? asset.Description;
        asset.Aliases = aliases is null ? asset.Aliases : NormalizeAliases(aliases);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpdateAssetAsync(asset, cancellationToken);
        if (validatedValues is not null)
        {
            await repository.ReplaceItemFieldValuesAsync(asset.Id, validatedValues, cancellationToken);
        }
        return new AssetResponse(
            "ok",
            "Asset updated.",
            await GetDetailsAsync(repository, asset, cancellationToken),
            []);
    }

    [McpServerTool(Name = "janus_set_fact")]
    [Description("Create or replace a current fact on an asset resolved by ID, name, or alias. Keep measurements split into value and explicit unit, for example value '30' and unit 'psi'. Previous values are retained in history.")]
    public static async Task<FactResponse> SetFactAsync(
        IJanusRepository repository,
        [Description("Asset ID, name, or alias.")] string assetReference,
        [Description("Stable machine-readable fact key, for example 'tire_pressure'.")] string key,
        [Description("Fact value without its unit, for example '30'.")] string value,
        [Description("Explicit unit, for example 'psi'; omit only when the fact is unitless.")] string? unit = null,
        [Description("Value type such as string, number, boolean, or date.")] string valueType = "string",
        [Description("Where this fact came from, such as owner_manual or user.")] string? source = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAssetAsync(repository, assetReference, cancellationToken);
        if (resolution.Asset is null)
        {
            return new FactResponse(resolution.Status, resolution.Message, null, resolution.Candidates);
        }

        var now = DateTimeOffset.UtcNow;
        var fact = await repository.SetFactAsync(new Fact
        {
            AssetId = resolution.Asset.Id,
            Key = key.Trim(),
            Value = value.Trim(),
            Unit = NormalizeOptional(unit),
            ValueType = string.IsNullOrWhiteSpace(valueType) ? "string" : valueType.Trim(),
            Source = NormalizeOptional(source),
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken);
        return new FactResponse("ok", "Fact saved.", fact, []);
    }

    [McpServerTool(Name = "janus_remove_fact")]
    [Description("Remove a current fact from an asset resolved by ID, name, or alias. The removed value is retained in fact history.")]
    public static async Task<FactResponse> RemoveFactAsync(
        IJanusRepository repository,
        [Description("Asset ID, name, or alias.")] string assetReference,
        [Description("Fact key to remove.")] string key,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAssetAsync(repository, assetReference, cancellationToken);
        if (resolution.Asset is null)
        {
            return new FactResponse(resolution.Status, resolution.Message, null, resolution.Candidates);
        }

        var removed = await repository.RemoveFactAsync(resolution.Asset.Id, key, cancellationToken);
        return new FactResponse(
            removed ? "ok" : "not_found",
            removed ? "Fact removed." : $"Fact '{key}' was not found on '{resolution.Asset.Name}'.",
            null,
            []);
    }

    [McpServerTool(Name = "janus_list_item_types")]
    [Description("List every available item type and its field definitions. Use this before creating an item when its type is uncertain.")]
    public static async Task<ItemTypesResponse> ListItemTypesAsync(
        IJanusRepository repository,
        CancellationToken cancellationToken = default) =>
        new("ok", "Item types listed.", await repository.ListItemTypesAsync(cancellationToken));

    [McpServerTool(Name = "janus_get_item_type")]
    [Description("Inspect one item type and all of its field definitions by stable key or ID.")]
    public static async Task<ItemTypeResponse> GetItemTypeAsync(
        IJanusRepository repository,
        [Description("Stable item type key or ID.")] string typeReference,
        CancellationToken cancellationToken = default)
    {
        var itemType = await repository.GetItemTypeAsync(typeReference, cancellationToken);
        return itemType is null
            ? new ItemTypeResponse("not_found", $"Item type '{typeReference}' was not found.", null)
            : new ItemTypeResponse("ok", "Item type found.", itemType);
    }

    [McpServerTool(Name = "janus_create_item_type")]
    [Description("Create a persistent item type. Afterward add field definitions before creating typed items.")]
    public static async Task<ItemTypeResponse> CreateItemTypeAsync(
        IJanusRepository repository,
        [Description("Stable lowercase key, for example 'soap'.")] string key,
        [Description("Human-readable type name, for example 'Soap'.")] string displayName,
        [Description("What this type represents.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var created = await repository.CreateItemTypeAsync(new ItemType
            {
                Key = key.Trim(),
                DisplayName = displayName.Trim(),
                Description = NormalizeOptional(description),
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken);
            return new ItemTypeResponse("ok", "Item type created.", created);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new ItemTypeResponse("validation_error", exception.Message, null);
        }
    }

    [McpServerTool(Name = "janus_update_item_type")]
    [Description("Update an item type's key, display name, or description. Omitted values remain unchanged.")]
    public static async Task<ItemTypeResponse> UpdateItemTypeAsync(
        IJanusRepository repository,
        [Description("Current stable item type key or ID.")] string typeReference,
        [Description("New stable lowercase key.")] string? key = null,
        [Description("New human-readable name.")] string? displayName = null,
        [Description("New description.")] string? description = null,
        CancellationToken cancellationToken = default)
    {
        var itemType = await repository.GetItemTypeAsync(typeReference, cancellationToken);
        if (itemType is null)
        {
            return new ItemTypeResponse("not_found", $"Item type '{typeReference}' was not found.", null);
        }

        itemType.Key = NormalizeOptional(key) ?? itemType.Key;
        itemType.DisplayName = NormalizeOptional(displayName) ?? itemType.DisplayName;
        itemType.Description = NormalizeOptional(description) ?? itemType.Description;
        itemType.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            var updated = await repository.UpdateItemTypeAsync(itemType, cancellationToken);
            if (updated is null)
            {
                return new ItemTypeResponse("validation_error", "The built-in basic item type cannot be changed.", null);
            }

            return new ItemTypeResponse("ok", "Item type updated.", itemType);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new ItemTypeResponse("validation_error", exception.Message, null);
        }
    }

    [McpServerTool(Name = "janus_delete_item_type")]
    [Description("Delete an unused custom item type. Built-in types and types assigned to items cannot be deleted.")]
    public static async Task<OperationResponse> DeleteItemTypeAsync(
        IJanusRepository repository,
        [Description("Stable item type key or ID.")] string typeReference,
        CancellationToken cancellationToken = default)
    {
        var itemType = await repository.GetItemTypeAsync(typeReference, cancellationToken);
        if (itemType is null)
        {
            return new OperationResponse("not_found", $"Item type '{typeReference}' was not found.");
        }

        var deleted = await repository.DeleteItemTypeAsync(itemType.Id, cancellationToken);
        return new OperationResponse(
            deleted ? "ok" : "in_use",
            deleted ? "Item type deleted." : "The built-in type or a type assigned to an item cannot be deleted.");
    }

    [McpServerTool(Name = "janus_add_field_definition")]
    [Description("Add a typed custom field to an item type. Supported types are string, integer, number, boolean, date, datetime, url, and enum.")]
    public static async Task<FieldDefinitionResponse> AddFieldDefinitionAsync(
        IJanusRepository repository,
        [Description("Item type key or ID.")] string typeReference,
        [Description("Stable lowercase property key, for example 'size_oz'.")] string key,
        [Description("Human-readable field name.")] string displayName,
        [Description("One of string, integer, number, boolean, date, datetime, url, or enum.")] string dataType,
        [Description("Whether every item of this type must have a value.")] bool required = false,
        [Description("What the field means.")] string? description = null,
        [Description("Allowed values; required only for enum fields.")] string[]? enumOptions = null,
        CancellationToken cancellationToken = default)
    {
        var itemType = await repository.GetItemTypeAsync(typeReference, cancellationToken);
        if (itemType is null)
        {
            return new FieldDefinitionResponse("not_found", $"Item type '{typeReference}' was not found.", null);
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            var field = await repository.AddFieldDefinitionAsync(new FieldDefinition
            {
                ItemTypeId = itemType.Id,
                Key = key.Trim(),
                DisplayName = displayName.Trim(),
                DataType = dataType.Trim().ToLowerInvariant(),
                Required = required,
                Description = NormalizeOptional(description),
                EnumOptions = NormalizeEnumOptions(enumOptions),
                CreatedAt = now,
                UpdatedAt = now
            }, cancellationToken);
            return new FieldDefinitionResponse("ok", "Field definition added.", field);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new FieldDefinitionResponse("validation_error", exception.Message, null);
        }
    }

    [McpServerTool(Name = "janus_update_field_definition")]
    [Description("Update a field definition. A field's data type cannot change while values use it.")]
    public static async Task<FieldDefinitionResponse> UpdateFieldDefinitionAsync(
        IJanusRepository repository,
        [Description("Item type key or ID.")] string typeReference,
        [Description("Current field key or ID.")] string fieldReference,
        [Description("New stable lowercase key.")] string? key = null,
        [Description("New human-readable name.")] string? displayName = null,
        [Description("New supported data type.")] string? dataType = null,
        [Description("Set the required state; omit to keep it unchanged.")] bool? required = null,
        [Description("New description.")] string? description = null,
        [Description("Complete replacement enum options; omit to keep current options.")] string[]? enumOptions = null,
        CancellationToken cancellationToken = default)
    {
        var itemType = await repository.GetItemTypeAsync(typeReference, cancellationToken);
        var field = itemType?.Fields.FirstOrDefault(candidate =>
            candidate.Key.Equals(fieldReference, StringComparison.OrdinalIgnoreCase) ||
            candidate.Id.ToString().Equals(fieldReference, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return new FieldDefinitionResponse("not_found", $"Field '{fieldReference}' was not found on type '{typeReference}'.", null);
        }

        field.Key = NormalizeOptional(key) ?? field.Key;
        field.DisplayName = NormalizeOptional(displayName) ?? field.DisplayName;
        field.DataType = NormalizeOptional(dataType)?.ToLowerInvariant() ?? field.DataType;
        field.Required = required ?? field.Required;
        field.Description = NormalizeOptional(description) ?? field.Description;
        field.EnumOptions = enumOptions is null ? field.EnumOptions : NormalizeEnumOptions(enumOptions);
        field.UpdatedAt = DateTimeOffset.UtcNow;
        try
        {
            await repository.UpdateFieldDefinitionAsync(field, cancellationToken);
            return new FieldDefinitionResponse("ok", "Field definition updated.", field);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new FieldDefinitionResponse("validation_error", exception.Message, null);
        }
    }

    [McpServerTool(Name = "janus_remove_field_definition")]
    [Description("Remove an unused field definition. Fields with stored values cannot be removed.")]
    public static async Task<OperationResponse> RemoveFieldDefinitionAsync(
        IJanusRepository repository,
        [Description("Item type key or ID.")] string typeReference,
        [Description("Field key or ID.")] string fieldReference,
        CancellationToken cancellationToken = default)
    {
        var itemType = await repository.GetItemTypeAsync(typeReference, cancellationToken);
        var field = itemType?.Fields.FirstOrDefault(candidate =>
            candidate.Key.Equals(fieldReference, StringComparison.OrdinalIgnoreCase) ||
            candidate.Id.ToString().Equals(fieldReference, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            return new OperationResponse("not_found", $"Field '{fieldReference}' was not found on type '{typeReference}'.");
        }

        var removed = await repository.RemoveFieldDefinitionAsync(field.Id, cancellationToken);
        return new OperationResponse(
            removed ? "ok" : "in_use",
            removed ? "Field definition removed." : "The field has stored values and cannot be removed.");
    }

    [McpServerTool(Name = "janus_record_event")]
    [Description("Record a dated historical event for an asset resolved by ID, name, or alias, such as purchased, oil changed, tire replaced, or serviced.")]
    public static async Task<EventResponse> RecordEventAsync(
        IJanusRepository repository,
        [Description("Asset ID, name, or alias.")] string assetReference,
        [Description("Short event type, such as 'oil changed'.")] string eventType,
        [Description("Optional event details.")] string? notes = null,
        [Description("When the event happened. Defaults to now when omitted.")] DateTimeOffset? occurredAt = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAssetAsync(repository, assetReference, cancellationToken);
        if (resolution.Asset is null)
        {
            return new EventResponse(resolution.Status, resolution.Message, null, resolution.Candidates);
        }

        var assetEvent = await repository.RecordEventAsync(new AssetEvent
        {
            AssetId = resolution.Asset.Id,
            Type = eventType.Trim(),
            Notes = NormalizeOptional(notes),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
        return new EventResponse("ok", "Event recorded.", assetEvent, []);
    }

    private static async Task<AssetResolution> ResolveAssetAsync(
        IJanusRepository repository,
        string assetReference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetReference);
        var reference = assetReference.Trim();
        if (Guid.TryParse(reference, out var id))
        {
            var byId = await repository.GetAssetAsync(id, cancellationToken);
            return byId is null
                ? AssetResolution.NotFound(reference)
                : AssetResolution.Found(byId);
        }

        var matches = await repository.FindAssetsAsync(reference, cancellationToken);
        var exactMatches = matches.Where(asset =>
            asset.Name.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            asset.Aliases.Any(alias => alias.Equals(reference, StringComparison.OrdinalIgnoreCase))).ToArray();
        var candidates = exactMatches.Length > 0 ? exactMatches : matches.ToArray();
        return candidates.Length switch
        {
            0 => AssetResolution.NotFound(reference),
            1 => AssetResolution.Found(candidates[0]),
            _ => AssetResolution.Ambiguous(reference, candidates)
        };
    }

    private static async Task<AssetDetails> GetDetailsAsync(
        IJanusRepository repository,
        Asset asset,
        CancellationToken cancellationToken)
    {
        var itemType = await repository.GetItemTypeAsync(asset.ItemTypeId.ToString(), cancellationToken)
            ?? throw new InvalidOperationException($"Item type '{asset.ItemTypeId}' was not found.");
        return new AssetDetails(
            asset,
            itemType,
            await repository.GetItemFieldValuesAsync(asset.Id, cancellationToken),
            await repository.GetFactsAsync(asset.Id, cancellationToken),
            await repository.GetEventsAsync(asset.Id, cancellationToken));
    }

    private static AssetResponse ResolutionResponse(AssetResolution resolution) =>
        new(resolution.Status, resolution.Message, null, resolution.Candidates);

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeAliases(IEnumerable<string>? aliases) => aliases?
        .Where(alias => !string.IsNullOrWhiteSpace(alias))
        .Select(alias => alias.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private static IReadOnlyList<string> NormalizeEnumOptions(IEnumerable<string>? options) => options?
        .Where(option => !string.IsNullOrWhiteSpace(option))
        .Select(option => option.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private static async Task<(ItemType? Type, string? Error)> ResolveTypeForAssetAsync(
        IJanusRepository repository,
        string? typeReference,
        bool hasCustomProperties,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(typeReference))
        {
            var requested = await repository.GetItemTypeAsync(typeReference, cancellationToken);
            if (requested is not null)
            {
                return (requested, null);
            }

            if (hasCustomProperties)
            {
                return (null, $"Item type '{typeReference}' does not exist. Create it and define its fields first.");
            }
        }

        var basic = await repository.GetItemTypeAsync("basic", cancellationToken);
        return basic is null
            ? (null, "The built-in basic item type is unavailable.")
            : (basic, null);
    }

    private static (IReadOnlyList<ItemFieldValue> Values, string? Error) ValidateProperties(
        Guid assetId,
        ItemType itemType,
        IReadOnlyDictionary<string, JsonElement> properties)
    {
        var definitions = itemType.Fields.ToDictionary(field => field.Key, StringComparer.OrdinalIgnoreCase);
        var unknown = properties.Keys.Where(key => !definitions.ContainsKey(key)).ToArray();
        if (unknown.Length > 0)
        {
            return ([], $"Properties not defined for type '{itemType.Key}': {string.Join(", ", unknown)}.");
        }

        var missing = itemType.Fields
            .Where(field => field.Required && !properties.Keys.Contains(field.Key, StringComparer.OrdinalIgnoreCase))
            .Select(field => field.Key)
            .ToArray();
        if (missing.Length > 0)
        {
            return ([], $"Missing required properties for type '{itemType.Key}': {string.Join(", ", missing)}.");
        }

        var now = DateTimeOffset.UtcNow;
        var values = new List<ItemFieldValue>();
        foreach (var property in properties)
        {
            var definition = definitions[property.Key];
            var parsed = ParseProperty(definition, property.Value);
            if (parsed.Error is not null)
            {
                return ([], parsed.Error);
            }

            values.Add(new ItemFieldValue
            {
                AssetId = assetId,
                FieldDefinitionId = definition.Id,
                Key = definition.Key,
                DataType = definition.DataType,
                Value = parsed.Value!,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        return (values, null);
    }

    private static (string? Value, string? Error) ParseProperty(FieldDefinition field, JsonElement element)
    {
        string Error(string expected) => $"Property '{field.Key}' must be {expected}; received JSON {element.ValueKind.ToString().ToLowerInvariant()}.";
        switch (field.DataType)
        {
            case "string":
                return element.ValueKind == JsonValueKind.String
                    ? (element.GetString()!, null)
                    : (null, Error("a string"));
            case "integer":
                return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integer)
                    ? (integer.ToString(CultureInfo.InvariantCulture), null)
                    : (null, Error("an integer"));
            case "number":
                return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number)
                    ? (number.ToString("R", CultureInfo.InvariantCulture), null)
                    : (null, Error("a finite number"));
            case "boolean":
                return element.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? (element.GetBoolean().ToString().ToLowerInvariant(), null)
                    : (null, Error("a boolean"));
            case "date":
                if (element.ValueKind != JsonValueKind.String ||
                    !DateOnly.TryParseExact(element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    return (null, $"Property '{field.Key}' must be an ISO date in yyyy-MM-dd format.");
                }

                return (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null);
            case "datetime":
                if (element.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParse(element.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
                {
                    return (null, $"Property '{field.Key}' must be an ISO 8601 datetime with an offset.");
                }

                return (dateTime.ToString("O", CultureInfo.InvariantCulture), null);
            case "url":
                if (element.ValueKind != JsonValueKind.String ||
                    !Uri.TryCreate(element.GetString(), UriKind.Absolute, out var uri))
                {
                    return (null, $"Property '{field.Key}' must be an absolute URL.");
                }

                return (uri.AbsoluteUri, null);
            case "enum":
                if (element.ValueKind != JsonValueKind.String)
                {
                    return (null, Error($"one of: {string.Join(", ", field.EnumOptions)}"));
                }

                var option = field.EnumOptions.FirstOrDefault(candidate =>
                    candidate.Equals(element.GetString(), StringComparison.OrdinalIgnoreCase));
                return option is null
                    ? (null, $"Property '{field.Key}' must be one of: {string.Join(", ", field.EnumOptions)}.")
                    : (option, null);
            default:
                return (null, $"Property '{field.Key}' uses unsupported type '{field.DataType}'.");
        }
    }

    private sealed record AssetResolution(string Status, string Message, Asset? Asset, IReadOnlyList<AssetCandidate> Candidates)
    {
        public static AssetResolution Found(Asset asset) => new("ok", "Asset resolved.", asset, []);

        public static AssetResolution NotFound(string reference) =>
            new("not_found", $"No asset matched '{reference}'.", null, []);

        public static AssetResolution Ambiguous(string reference, IEnumerable<Asset> assets) =>
            new(
                "ambiguous",
                $"More than one asset matched '{reference}'. Choose a candidate ID or a more specific name.",
                null,
                assets.Select(AssetCandidate.FromAsset).ToArray());
    }
}

public sealed record AssetCandidate(Guid Id, string Name, string? Type, string? Manufacturer, string? Model, IReadOnlyList<string> Aliases)
{
    public static AssetCandidate FromAsset(Asset asset) =>
        new(asset.Id, asset.Name, asset.Type, asset.Manufacturer, asset.Model, asset.Aliases);
}

public sealed record AssetDetails(
    Asset Asset,
    ItemType ItemType,
    IReadOnlyList<ItemFieldValue> CustomProperties,
    IReadOnlyList<Fact> Facts,
    IReadOnlyList<AssetEvent> Events);
public sealed record SearchResponse(string Status, string Query, IReadOnlyList<AssetDetails> Results);
public sealed record AssetResponse(string Status, string Message, AssetDetails? Result, IReadOnlyList<AssetCandidate> Candidates);
public sealed record FactResponse(string Status, string Message, Fact? Fact, IReadOnlyList<AssetCandidate> Candidates);
public sealed record EventResponse(string Status, string Message, AssetEvent? Event, IReadOnlyList<AssetCandidate> Candidates);
public sealed record ItemTypesResponse(string Status, string Message, IReadOnlyList<ItemType> ItemTypes);
public sealed record ItemTypeResponse(string Status, string Message, ItemType? ItemType);
public sealed record FieldDefinitionResponse(string Status, string Message, FieldDefinition? FieldDefinition);
public sealed record OperationResponse(string Status, string Message);
