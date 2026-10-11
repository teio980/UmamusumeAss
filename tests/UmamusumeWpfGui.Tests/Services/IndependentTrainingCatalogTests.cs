using System.Text.Json;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Services.Training;

namespace UmamusumeWpfGui.Tests.Services;

public sealed class IndependentTrainingCatalogTests
{
    [Fact]
    public void Empty_catalog_before_native_initialization_is_a_valid_unavailable_projection()
    {
        var catalog = IndependentTrainingCatalog.FromCoreCatalog(CoreCatalog.Empty);

        Assert.Empty(catalog.Races);
        Assert.Empty(catalog.Skills);
        Assert.False(catalog.IsAvailable);
        Assert.False(catalog.SkillSource.IsExplicitGlobalClient);
    }

    [Fact]
    public void Native_catalog_projection_preserves_agenda_picker_and_skill_fields()
    {
        var catalog = IndependentTrainingCatalog.FromCoreCatalog(new CoreCatalog(
            CoreCatalog.Empty.Characters,
            CoreCatalog.Empty.BaseCharacters,
            CoreCatalog.Empty.Races,
            Rows("""
                [
                  {"raceName":"Unique Cup","grade":"G1","year":"First Year","turn":"10_01","type":"Turf","location":"Tokyo ⇒","length":"Mile","lengthM":"1600m","raceId":101,"sourceOrder":3,"gameTrack":"Tokyo","gameDistance":1600,"gameGround":"Turf","gameAvailable":true,"gameSchedules":[{"month":10,"day":1,"timeName":"Junior"}],"gameOrder":7},
                  {"raceName":"Shared Cup A","grade":"G2","year":"Second Year","turn":"01_01","type":"Turf","location":"Tokyo ⇒","length":"Mile","lengthM":"1600m","raceId":201,"gameTrack":"Tokyo","gameDistance":1600,"gameGround":"Turf","gameAvailable":true},
                  {"raceName":"Shared Cup B","grade":"G2","year":"Second Year","turn":"01_01","type":"Turf","location":"Tokyo ⇒","length":"Mile","lengthM":"1600m","raceId":202,"gameTrack":"Tokyo","gameDistance":1600,"gameGround":"Turf","gameAvailable":true}
                ]
                """),
            CoreCatalog.Empty.Skills,
            CoreCatalog.Empty.SupportCards,
            CoreCatalog.Empty.Events,
            CoreCatalog.Empty.Strategies,
            Metadata(("independent_race_metadata", "{\"sourceUrl\":\"https://catalog.test/races\"}"),
                ("skill_metadata", "{\"sourceUrl\":\"https://catalog.test/skills\",\"source\":{\"sourceName\":\"game master\",\"masterSha256\":\"abc\",\"clientVersion\":\"1.0\",\"region\":\"global\",\"sourceType\":\"game-client-master-db\"}}"))));

        Assert.Equal("https://catalog.test/races", catalog.RaceSourceUrl);
        Assert.Equal("https://catalog.test/skills", catalog.SkillSourceUrl);
        Assert.True(catalog.SkillSource.IsExplicitGlobalClient);
        Assert.Equal(3, catalog.Races.Count);
        Assert.True(catalog.TryGetAgendaPickerEntry(
            new("First Year", "10_01", "Unique Cup"), out var unique));
        Assert.Equal(101, unique.RaceId);
        Assert.Equal(10, unique.Month);
        Assert.Equal(1, unique.Day);
        Assert.Equal("Junior", unique.TimeName);
        Assert.Equal(7, unique.GameOrder);
        Assert.True(catalog.TryGetAgendaPickerOcrTarget(unique, out var header));
        Assert.Equal("Tokyo Turf 1600m (Mile) Right", header);

        Assert.True(catalog.TryGetAgendaPickerEntry(
            new("Second Year", "01_01", "Shared Cup A"), out var shared));
        Assert.Equal(201, shared.RaceId);
        Assert.False(catalog.TryGetAgendaPickerOcrTarget(
            catalog.Races.Single(item => item.RaceId == 201), out _));
    }

    [Fact]
    public void Skill_projection_keeps_native_search_and_availability_semantics()
    {
        CoreCatalog core = CoreCatalog.Empty with
        {
            Skills = Rows("""
                [
                  {"skillId":201412,"skillName":"1,500,000 CC","originalName":"Special skill","rarity":2,"gradeValue":1,"skillCategory":"Unique","effectSummary":"Speed up","needSkillPoint":180,"activationCondition":"Late","iconId":12,"searchText":"500","searchResultRow":9,"gameSearchMapped":true,"aliases":["coins"],"availableInGlobal":true,"availabilitySource":"master","singleModeEnabled":true,"skillCategoryId":3,"otherTiers":""}
                ]
                """),
        };

        var skill = Assert.Single(IndependentTrainingCatalog.FromCoreCatalog(core).Skills);
        Assert.Equal(201412, skill.SkillId);
        Assert.Equal("1,500,000 CC", skill.OcrTargetText);
        Assert.Equal("500", skill.EffectiveSearchText);
        Assert.Equal(["coins"], skill.SearchAliases);
        Assert.True(skill.IsSelectable);
        Assert.Equal(1, skill.SearchResultPage);
        Assert.Equal(0, skill.SearchResultPickerRow);
    }

    private static JsonElement[] Rows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static Dictionary<string, JsonElement> Metadata(params (string Key, string Json)[] values) =>
        values.ToDictionary(value => value.Key, value => ParseElement(value.Json));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
