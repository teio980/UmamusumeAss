using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.Services.Tasks;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Tests.ViewModels;

public sealed class CareerSettingsLifecycleTests
{
    [Fact]
    public void Career_settings_subscribe_once_only_when_database_is_not_loaded()
    {
        var loadedDatabase = new TrackingUmaDatabase { IsLoaded = true };
        using (var settings = new CareerTrainingTaskSettingsViewModel(loadedDatabase))
        {
            Assert.Equal(0, loadedDatabase.DatabaseLoadedSubscriberCount);
        }

        var pendingDatabase = new TrackingUmaDatabase();
        using (var settings = new CareerTrainingTaskSettingsViewModel(pendingDatabase))
        {
            Assert.Equal(2, pendingDatabase.DatabaseLoadedSubscriberCount);

            pendingDatabase.RaiseDatabaseLoaded();

            Assert.Equal(0, pendingDatabase.DatabaseLoadedSubscriberCount);
        }

        Assert.Equal(0, pendingDatabase.DatabaseLoadedSubscriberCount);
    }

    [Fact]
    public void Disposing_career_settings_unsubscribes_before_database_load()
    {
        var database = new TrackingUmaDatabase();
        var settings = new CareerTrainingTaskSettingsViewModel(database);

        Assert.Equal(2, database.DatabaseLoadedSubscriberCount);

        settings.Dispose();

        Assert.Equal(0, database.DatabaseLoadedSubscriberCount);
        database.RaiseDatabaseLoaded();
        Assert.Equal(0, database.DatabaseLoadedSubscriberCount);
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
        public string? Region => "global";
        public IReadOnlyCollection<UmaBaseCharacterRecord> BaseCharacters => [];
        public IReadOnlyCollection<UmaTraineeRecord> Trainees => [];
        public IReadOnlyCollection<UmaSupportCardRecord> SupportCards => [];

        public Task LoadAsync(string resourceRoot, CancellationToken cancellationToken = default)
        {
            IsLoaded = true;
            RaiseDatabaseLoaded();
            return Task.CompletedTask;
        }

        public bool TryGetTrainee(int traineeId, out UmaTraineeRecord? trainee)
        {
            trainee = null;
            return false;
        }

        public bool TryGetSupportCard(int supportCardId, out UmaSupportCardRecord? supportCard)
        {
            supportCard = null;
            return false;
        }

        public IReadOnlyList<UmaTraineeRecord> FindTraineesByName(string name) => [];
        public IReadOnlyList<UmaSupportCardRecord> FindSupportCardsByName(string name) => [];
        public IReadOnlyList<UmaSupportCardRecord> GetSupportCardsForCharacter(int baseCharacterId) => [];
        public string GetTraineeTemplateDirectory(int traineeId) => string.Empty;
        public string GetTraineeImageDirectory() => string.Empty;
        public string GetTraineeImagePath(int traineeId) => string.Empty;
        public string GetTraineeLiveOutfitImageDirectory() => string.Empty;
        public string GetTraineeLiveOutfitImagePath(int baseCharacterId) => string.Empty;
        public string GetTraineeLiveOutfitReferenceImagePath(int baseCharacterId) => string.Empty;
        public string GetTraineeReferenceImageDirectory() => string.Empty;
        public string GetTraineeReferenceImagePath(int traineeId) => string.Empty;
        public string GetMaintenanceTraineeReferenceImageDirectory() => string.Empty;
        public string GetMaintenanceTraineeReferenceImagePath(int traineeId) => string.Empty;
        public string GetSupportCardTemplateDirectory(int supportCardId) => string.Empty;

        public void RaiseDatabaseLoaded()
        {
            IsLoaded = true;
            _databaseLoaded?.Invoke(this, EventArgs.Empty);
        }
    }
}
