using System.Text.Json;
using System.Text.Json.Nodes;
using Umamusume.CoreBridge;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class CareerSettingsLifecycleTests
{
    [Fact]
    public void Career_settings_refresh_catalog_repeatedly_without_duplicate_subscriptions()
    {
        var loadedDatabase = new TrackingUmaDatabase
        {
            IsLoaded = true,
        };
        using (var settings = new CareerTrainingTaskSettingsViewModel(loadedDatabase))
        {
            Assert.Equal(5, loadedDatabase.DatabaseLoadedSubscriberCount);
            AssertCatalogRefresh(settings, loadedDatabase, 101, "First refresh", "first-strategy");
            Assert.Equal(5, loadedDatabase.DatabaseLoadedSubscriberCount);
            AssertCatalogRefresh(settings, loadedDatabase, 102, "Second refresh", "second-strategy");
            Assert.Equal(5, loadedDatabase.DatabaseLoadedSubscriberCount);
        }
        Assert.Equal(0, loadedDatabase.DatabaseLoadedSubscriberCount);

        var pendingDatabase = new TrackingUmaDatabase();
        using (var settings = new CareerTrainingTaskSettingsViewModel(pendingDatabase))
        {
            Assert.Equal(5, pendingDatabase.DatabaseLoadedSubscriberCount);
            AssertCatalogRefresh(settings, pendingDatabase, 201, "Loaded catalog", "loaded-strategy");
            Assert.Equal(5, pendingDatabase.DatabaseLoadedSubscriberCount);
            AssertCatalogRefresh(settings, pendingDatabase, 202, "Reloaded catalog", "reloaded-strategy");
            Assert.Equal(5, pendingDatabase.DatabaseLoadedSubscriberCount);
        }
        Assert.Equal(0, pendingDatabase.DatabaseLoadedSubscriberCount);
    }

    [Fact]
    public void Disposing_career_settings_unsubscribes_before_database_load()
    {
        var database = new TrackingUmaDatabase();
        var settings = new CareerTrainingTaskSettingsViewModel(database);

        Assert.Equal(5, database.DatabaseLoadedSubscriberCount);
        Assert.DoesNotContain(
            settings.NormalTrainingStrategyOptions,
            option => option.Value == "late-strategy");

        settings.Dispose();

        Assert.Equal(0, database.DatabaseLoadedSubscriberCount);
        database.SetCatalogAndRaiseLoaded(CatalogWith(301, "Late skill", "late-strategy"), 301);
        Assert.Equal(0, database.DatabaseLoadedSubscriberCount);
        Assert.DoesNotContain(
            settings.NormalTrainingStrategyOptions,
            option => option.Value == "late-strategy");
    }

    [Fact]
    public void Disposing_career_settings_detaches_child_property_forwarding()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();
        var forwardedPropertyChangedCount = 0;
        settings.PropertyChanged += (_, _) => forwardedPropertyChangedCount++;

        settings.Dispose();
        settings.Dispose();
        settings.Independent.TrainingFocus = CareerTrainingTaskSettingsViewModel.IndependentTrainingFocusStamina;

        Assert.Equal(0, forwardedPropertyChangedCount);
    }

    [Fact]
    public void Legacy_scenario_id_is_ignored_and_not_exported()
    {
        var settings = new CareerTrainingTaskSettingsViewModel();
        var values = new JsonObject
        {
            ["scenarioId"] = "climax",
            ["manifestPath"] = "resource/hachimi/ura/manifest.json",
        };

        CareerTaskSettingsSerializer.Import(settings, values);
        var exported = CareerTaskSettingsSerializer.Export(settings);

        Assert.Equal("resource/hachimi/ura/manifest.json", settings.ManifestPath);
        Assert.False(exported.ContainsKey("scenarioId"));
    }

    private static void AssertCatalogRefresh(
        CareerTrainingTaskSettingsViewModel settings,
        TrackingUmaDatabase database,
        int skillId,
        string skillName,
        string strategyId)
    {
        database.SetCatalogAndRaiseLoaded(CatalogWith(skillId, skillName, strategyId), skillId);

        Assert.Contains(settings.NormalSkills.FilteredSkills, option => option.SkillId == skillId);
        Assert.DoesNotContain(
            settings.NormalSkills.FilteredSkills,
            option => option.SkillId != skillId);
        Assert.Contains(settings.NormalTrainingStrategyOptions, option => option.Value == strategyId);
        Assert.Equal(skillId, Assert.Single(settings.Entry.TraineeOptions).TraineeId);
        Assert.Equal(skillId, Assert.Single(settings.SupportDeck.FilteredSupportCardOptions).SupportCardId);
        Assert.Equal(skillId, Assert.Single(settings.SupportDeck.FriendSupportCardOptions).SupportCardId);
    }

    private static CoreCatalog CatalogWith(int skillId, string skillName, string strategyId) =>
        CoreCatalog.Empty with
        {
            Skills = [JsonSerializer.SerializeToElement(new
            {
                skillId,
                skillName,
                originalName = skillName,
                rarity = 1,
                needSkillPoint = 100,
                searchText = skillName,
                availableInGlobal = true,
                singleModeEnabled = true,
            })],
            Strategies = [JsonSerializer.SerializeToElement(new
            {
                id = strategyId,
                value = strategyId,
                label = strategyId,
                scenario = "ura",
            })],
        };

    private sealed class TrackingUmaDatabase : IUmaDatabaseService
    {
        private EventHandler? _databaseLoaded;

        public event EventHandler? DatabaseLoaded
        {
            add
            {
                _databaseLoaded += value;
                DatabaseLoadedSubscriberCount++;
            }
            remove
            {
                _databaseLoaded -= value;
                DatabaseLoadedSubscriberCount--;
            }
        }

        public int DatabaseLoadedSubscriberCount { get; private set; }
        public bool IsLoaded { get; set; }
        public CoreCatalog Catalog { get; set; } = CoreCatalog.Empty;
        public string? Region => "global";
        public IReadOnlyCollection<UmaBaseCharacterRecord> BaseCharacters => [];
        public IReadOnlyCollection<UmaTraineeRecord> Trainees { get; private set; } = [];
        public IReadOnlyCollection<UmaCareerRaceRecord> Races => [];
        public IReadOnlyCollection<UmaSupportCardRecord> SupportCards { get; private set; } = [];

        public bool TryGetTrainee(int traineeId, out UmaTraineeRecord? trainee)
        {
            trainee = Trainees.FirstOrDefault(item => item.TraineeId == traineeId);
            return trainee is not null;
        }

        public bool TryGetRace(int raceId, out UmaCareerRaceRecord? race)
        {
            race = null;
            return false;
        }

        public bool TryGetSupportCard(int supportCardId, out UmaSupportCardRecord? supportCard)
        {
            supportCard = SupportCards.FirstOrDefault(item => item.SupportCardId == supportCardId);
            return supportCard is not null;
        }

        public IReadOnlyList<UmaTraineeRecord> FindTraineesByName(string name) => [];
        public IReadOnlyList<UmaSupportCardRecord> FindSupportCardsByName(string name) => [];
        public IReadOnlyList<UmaSupportCardRecord> GetSupportCardsForCharacter(int baseCharacterId) => [];
        public string GetTraineeTemplateDirectory(int traineeId) => string.Empty;
        public string GetTraineeImageDirectory() => string.Empty;
        public string GetTraineeImagePath(int traineeId) => ExistingFilePath;
        public string GetTraineeLiveOutfitImageDirectory() => string.Empty;
        public string GetTraineeLiveOutfitImagePath(int baseCharacterId) => ExistingFilePath;
        public string GetTraineeLiveOutfitReferenceImagePath(int baseCharacterId) => ExistingFilePath;
        public string GetSupportCardImagePath(int supportCardId) => ExistingFilePath;
        public string GetTraineeReferenceImageDirectory() => string.Empty;
        public string GetTraineeReferenceImagePath(int traineeId) => ExistingFilePath;
        public string GetMaintenanceTraineeReferenceImageDirectory() => string.Empty;
        public string GetMaintenanceTraineeReferenceImagePath(int traineeId) => ExistingFilePath;
        public string GetSupportCardTemplateDirectory(int supportCardId) => string.Empty;

        public void RaiseDatabaseLoaded()
        {
            IsLoaded = true;
            _databaseLoaded?.Invoke(this, EventArgs.Empty);
        }

        public void SetCatalogAndRaiseLoaded(CoreCatalog catalog, int? itemId = null)
        {
            Catalog = catalog;
            var id = itemId ?? 1;
            Trainees = [new UmaTraineeRecord
            {
                TraineeId = id,
                BaseCharacterId = id,
                NameEn = $"Trainee {id}",
                Available = true,
            }];
            SupportCards = [new UmaSupportCardRecord
            {
                SupportCardId = id,
                NameEn = $"Support card {id}",
                Rarity = "SSR",
                Type = "Speed",
                Available = true,
            }];
            RaiseDatabaseLoaded();
        }

        private static string ExistingFilePath => typeof(CareerSettingsLifecycleTests).Assembly.Location;

    }
}
