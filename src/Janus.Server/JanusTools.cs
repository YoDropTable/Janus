using System.ComponentModel;
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
    [Description("Get one asset with its aliases, current facts, and event history. The reference may be an asset ID, exact human-readable name, or alias. Returns candidates when the reference is ambiguous.")]
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
    [Description("Create a physical asset in Janus. Give it a clear human-readable name; aliases may contain shorter names people commonly use.")]
    public static async Task<AssetResponse> CreateAssetAsync(
        IJanusRepository repository,
        [Description("Unique, human-readable asset name.")] string name,
        [Description("General category, such as tractor, cart, appliance, bicycle, or HVAC system.")] string? type = null,
        [Description("Asset manufacturer.")] string? manufacturer = null,
        [Description("Manufacturer model name or number.")] string? model = null,
        [Description("Serial number, if known.")] string? serialNumber = null,
        [Description("Free-form description.")] string? description = null,
        [Description("Alternative human-readable names used to resolve this asset.")] string[]? aliases = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = DateTimeOffset.UtcNow;
        var asset = new Asset
        {
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
        await repository.CreateAssetAsync(asset, cancellationToken);
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
        [Description("New general category, or omit to keep it unchanged.")] string? type = null,
        [Description("New manufacturer, or omit to keep it unchanged.")] string? manufacturer = null,
        [Description("New model, or omit to keep it unchanged.")] string? model = null,
        [Description("New serial number, or omit to keep it unchanged.")] string? serialNumber = null,
        [Description("New description, or omit to keep it unchanged.")] string? description = null,
        [Description("Replacement aliases; omit to keep the current aliases, or pass an empty array to clear them.")] string[]? aliases = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAssetAsync(repository, assetReference, cancellationToken);
        if (resolution.Asset is null)
        {
            return ResolutionResponse(resolution);
        }

        var asset = resolution.Asset;
        asset.Name = NormalizeOptional(name) ?? asset.Name;
        asset.Type = NormalizeOptional(type) ?? asset.Type;
        asset.Manufacturer = NormalizeOptional(manufacturer) ?? asset.Manufacturer;
        asset.Model = NormalizeOptional(model) ?? asset.Model;
        asset.SerialNumber = NormalizeOptional(serialNumber) ?? asset.SerialNumber;
        asset.Description = NormalizeOptional(description) ?? asset.Description;
        asset.Aliases = aliases is null ? asset.Aliases : NormalizeAliases(aliases);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpdateAssetAsync(asset, cancellationToken);
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
        CancellationToken cancellationToken) => new(
            asset,
            await repository.GetFactsAsync(asset.Id, cancellationToken),
            await repository.GetEventsAsync(asset.Id, cancellationToken));

    private static AssetResponse ResolutionResponse(AssetResolution resolution) =>
        new(resolution.Status, resolution.Message, null, resolution.Candidates);

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> NormalizeAliases(IEnumerable<string>? aliases) => aliases?
        .Where(alias => !string.IsNullOrWhiteSpace(alias))
        .Select(alias => alias.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

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

public sealed record AssetDetails(Asset Asset, IReadOnlyList<Fact> Facts, IReadOnlyList<AssetEvent> Events);
public sealed record SearchResponse(string Status, string Query, IReadOnlyList<AssetDetails> Results);
public sealed record AssetResponse(string Status, string Message, AssetDetails? Result, IReadOnlyList<AssetCandidate> Candidates);
public sealed record FactResponse(string Status, string Message, Fact? Fact, IReadOnlyList<AssetCandidate> Candidates);
public sealed record EventResponse(string Status, string Message, AssetEvent? Event, IReadOnlyList<AssetCandidate> Candidates);
