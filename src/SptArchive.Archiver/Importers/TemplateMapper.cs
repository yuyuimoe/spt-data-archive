using System.Text.Json;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Importers;

/// <summary>
/// Reads one server object, as <c>SPT_Data/database</c> holds it, into the values of a row. The
/// official importers and the run importer read the same shapes, so the rule that decides a column
/// exists once (ADR-0002: a promoted column is an extra, and the whole object stays in the jsonb one).
/// </summary>
/// <remarks>
/// The values are read apart from the row because the two owners of a row set a different column:
/// an official row states the release, a mod row states its dataset. The <c>Official</c> and
/// <c>Mod</c> helpers below are the only places where that column is chosen.
/// </remarks>
internal static class TemplateMapper
{
    public static ItemValues ReadItem(string id, JsonElement template)
    {
        var props = Object(template, "_props");

        return new ItemValues(
            id,
            template.GetScalarString("_name") ?? id,
            template.GetScalarString("_parent") ?? string.Empty,
            template.GetScalarString("_type") ?? string.Empty,
            template.GetScalarString("_proto"),
            props.GetDoubleOrNull("Weight"),
            props.GetInt32OrNull("Width"),
            props.GetInt32OrNull("Height"),
            props.GetInt32OrNull("StackMaxSize"),
            props.ValueKind == JsonValueKind.Object ? props.GetRawText() : "{}");
    }

    public static QuestValues ReadQuest(string id, JsonElement quest) =>
        new(
            id,
            quest.GetScalarString("QuestName"),
            quest.GetScalarString("traderId") ?? string.Empty,
            quest.GetScalarString("location"),
            quest.GetScalarString("type"),
            quest.GetScalarString("side"),
            quest.GetBool("restartable"),
            quest.GetRawText());

    public static CustomizationValues ReadCustomization(string id, JsonElement entry)
    {
        var props = Object(entry, "_props");

        return new CustomizationValues(
            id,
            entry.GetScalarString("_name") ?? id,
            entry.GetScalarString("_parent") ?? string.Empty,
            entry.GetScalarString("_type") ?? string.Empty,
            props.GetStringArray("Side"),
            props.GetScalarString("BodyPart"),
            props.GetNestedString("Prefab", "path"),
            props.GetBool("AvailableAsDefault"),
            props.ValueKind == JsonValueKind.Object ? props.GetRawText() : "{}");
    }

    public static TraderValues ReadTrader(
        string traderId,
        JsonElement baseJson,
        string? dialogue,
        string? suits,
        string? services) =>
        new(
            traderId,
            baseJson.GetScalarString("name") ?? traderId,
            baseJson.GetScalarString("nickname"),
            baseJson.GetScalarString("currency"),
            baseJson.GetScalarString("location"),
            baseJson.GetRawText(),
            dialogue,
            suits,
            services);

    public static Item OfficialItem(ItemValues values, string sptVersion) =>
        new()
        {
            SptVersion = sptVersion,
            Id = values.Id,
            Name = values.Name,
            ParentId = values.ParentId,
            Type = values.Type,
            Prototype = values.Prototype,
            Weight = values.Weight,
            Width = values.Width,
            Height = values.Height,
            StackMaxSize = values.StackMaxSize,
            Props = values.Props,
        };

    public static ModItem ModItem(ItemValues values, Guid datasetId) =>
        new()
        {
            DatasetId = datasetId,
            Id = values.Id,
            Name = values.Name,
            ParentId = values.ParentId,
            Type = values.Type,
            Prototype = values.Prototype,
            Weight = values.Weight,
            Width = values.Width,
            Height = values.Height,
            StackMaxSize = values.StackMaxSize,
            Props = values.Props,
        };

    public static Quest Quest(QuestValues values, string sptVersion) =>
        new()
        {
            SptVersion = sptVersion,
            Id = values.Id,
            QuestName = values.QuestName,
            TraderId = values.TraderId,
            Location = values.Location,
            Type = values.Type,
            Side = values.Side,
            Restartable = values.Restartable,
            Data = values.Data,
        };

    public static ModQuest ModQuest(QuestValues values, Guid datasetId) =>
        new()
        {
            DatasetId = datasetId,
            Id = values.Id,
            QuestName = values.QuestName,
            TraderId = values.TraderId,
            Location = values.Location,
            Type = values.Type,
            Side = values.Side,
            Restartable = values.Restartable,
            Data = values.Data,
        };

    public static Customization Customization(CustomizationValues values, string sptVersion) =>
        new()
        {
            SptVersion = sptVersion,
            Id = values.Id,
            Name = values.Name,
            ParentId = values.ParentId,
            Type = values.Type,
            Sides = values.Sides,
            BodyPart = values.BodyPart,
            PrefabPath = values.PrefabPath,
            AvailableAsDefault = values.AvailableAsDefault,
            Props = values.Props,
        };

    public static ModCustomization ModCustomization(CustomizationValues values, Guid datasetId) =>
        new()
        {
            DatasetId = datasetId,
            Id = values.Id,
            Name = values.Name,
            ParentId = values.ParentId,
            Type = values.Type,
            Sides = values.Sides,
            BodyPart = values.BodyPart,
            PrefabPath = values.PrefabPath,
            AvailableAsDefault = values.AvailableAsDefault,
            Props = values.Props,
        };

    public static Trader Trader(TraderValues values, string sptVersion) =>
        new()
        {
            SptVersion = sptVersion,
            Id = values.Id,
            Name = values.Name,
            Nickname = values.Nickname,
            Currency = values.Currency,
            Location = values.Location,
            Base = values.Base,
            Dialogue = values.Dialogue,
            Suits = values.Suits,
            Services = values.Services,
        };

    public static ModTrader ModTrader(TraderValues values, Guid datasetId) =>
        new()
        {
            DatasetId = datasetId,
            Id = values.Id,
            Name = values.Name,
            Nickname = values.Nickname,
            Currency = values.Currency,
            Location = values.Location,
            Base = values.Base,
            Dialogue = values.Dialogue,
            Suits = values.Suits,
            Services = values.Services,
        };

    private static JsonElement Object(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
}

internal sealed record ItemValues(
    string Id,
    string Name,
    string ParentId,
    string Type,
    string? Prototype,
    double? Weight,
    int? Width,
    int? Height,
    int? StackMaxSize,
    string Props);

internal sealed record QuestValues(
    string Id,
    string? QuestName,
    string TraderId,
    string? Location,
    string? Type,
    string? Side,
    bool Restartable,
    string Data);

internal sealed record CustomizationValues(
    string Id,
    string Name,
    string ParentId,
    string Type,
    string[] Sides,
    string? BodyPart,
    string? PrefabPath,
    bool AvailableAsDefault,
    string Props);

internal sealed record TraderValues(
    string Id,
    string Name,
    string? Nickname,
    string? Currency,
    string? Location,
    string Base,
    string? Dialogue,
    string? Suits,
    string? Services);
