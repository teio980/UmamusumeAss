using System.IO;
using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Services;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class CoreCatalogProjectionTests
{
    [Fact]
    public void Core_catalog_keeps_picker_fields_objectives_metadata_and_image_paths()
    {
        var service = new UmaDatabaseService();
        var notifications = 0;
        service.DatabaseLoaded += (_, _) => notifications++;
        var catalog = CreateCatalog();
        var resourceRoot = Path.GetFullPath("resource");

        service.LoadFromCoreCatalog(catalog, resourceRoot);

        Assert.True(service.IsLoaded);
        Assert.Equal("global", service.Region);
        Assert.Equal(1, notifications);
        Assert.Same(catalog, service.Catalog);
        Assert.Equal("retained", service.Catalog.AdditionalCategories["future_category"].GetProperty("value").GetString());
        Assert.Equal(1001, Assert.Single(service.BaseCharacters).BaseCharacterId);
        var trainee = Assert.Single(service.FindTraineesByName("  スペシャル  "));
        Assert.Equal(100101, trainee.TraineeId);
        Assert.Equal(1, Assert.Single(trainee.CareerObjectives).Target.PlacementAtMost);
        Assert.True(service.TryGetRace(101, out var race));
        Assert.Equal(1600, race!.Distance);
        Assert.Equal(20001, Assert.Single(service.GetSupportCardsForCharacter(1001)).SupportCardId);
        Assert.EndsWith(Path.Combine("trainees", "100101.webp"), service.GetTraineeImagePath(100101));
    }

    [Fact]
    public void Invalid_catalog_refresh_preserves_the_previous_picker_snapshot()
    {
        var service = new UmaDatabaseService();
        var catalog = CreateCatalog();
        service.LoadFromCoreCatalog(catalog, Path.GetFullPath("resource"));
        var notifications = 0;
        service.DatabaseLoaded += (_, _) => notifications++;
        var invalid = catalog with { Characters = Rows("[{\"trainee_id\":100101},{\"trainee_id\":100101}]") };

        Assert.Throws<InvalidDataException>(() => service.LoadFromCoreCatalog(invalid, Path.GetFullPath("other-resource")));

        Assert.Same(catalog, service.Catalog);
        Assert.Equal(0, notifications);
        Assert.Equal("Special Week", Assert.Single(service.Trainees).NameEn);
        Assert.Equal("global", service.Region);
    }

    private static CoreCatalog CreateCatalog() => CoreCatalog.Empty with
    {
        BaseCharacters = Rows("[{\"base_character_id\":1001,\"name_en\":\"Special Week\",\"trainee_ids\":[100101]}]"),
        Characters = Rows("""
            [{"trainee_id":100101,"base_character_id":1001,"name_en":"Special Week","name_jp":"スペシャルウィーク","available":true,"career_objectives":[{"objective_id":"goal","turn":24,"race_ids":["101"],"target":{"placement_at_most":1}}]}]
            """),
        Races = Rows("[{\"race_id\":101,\"name_en\":\"Test Cup\",\"distance\":1600,\"grade\":\"G1\"}]"),
        SupportCards = Rows("[{\"support_card_id\":20001,\"name_en\":\"Support\",\"featured_character_id\":1001}]"),
        AdditionalCategories = new Dictionary<string, JsonElement>
        {
            ["database_meta"] = Element("{\"region\":\"global\"}"),
            ["future_category"] = Element("{\"value\":\"retained\"}"),
        },
    };

    private static JsonElement[] Rows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
