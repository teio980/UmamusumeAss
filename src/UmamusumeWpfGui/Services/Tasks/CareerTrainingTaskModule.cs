using System.Text.Json.Nodes;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services;
using UmamusumeWpfGui.ViewModels.Tasks;

namespace UmamusumeWpfGui.Services.Tasks;

/// <summary>Career profile editor and persistence adapter; Core owns execution.</summary>
public sealed class CareerTrainingTaskModule : IGrassTaskModule
{
    private readonly IUmaDatabaseService _umaDatabase;
    private readonly ILocalizationService _localizationService;

    public CareerTrainingTaskModule(
        IUmaDatabaseService umaDatabase,
        ILocalizationService localizationService)
    {
        _umaDatabase = umaDatabase ?? throw new ArgumentNullException(nameof(umaDatabase));
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        Settings = new CareerTrainingTaskSettingsViewModel(_umaDatabase, _localizationService);
    }

    public GrassTaskDefinition Definition { get; } = new(
        "career-training", "GrassTaskCareerTraining", "GrassTaskCareerTrainingDescription",
        "Career Training", "Run a Core career training task");

    public CareerTrainingTaskSettingsViewModel Settings { get; }

    object IGrassTaskModule.Settings => Settings;

    public JsonObject ExportSettings() => CareerTaskSettingsSerializer.Export(Settings);

    public void ImportSettings(JsonObject settings) => CareerTaskSettingsSerializer.Import(Settings, settings);

    public IGrassTaskModule CreateInstance() => new CareerTrainingTaskModule(_umaDatabase, _localizationService);
}
