using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services;





public sealed class GrassTaskCatalog : IGrassTaskCatalog
{
    private readonly List<IGrassTaskModule> _modules = [];

    public static GrassTaskCatalog CreateEmpty() => new();

    public IReadOnlyList<IGrassTaskModule> Modules => _modules;

    public void Register(IGrassTaskModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (_modules.Any(item => item.Definition.Id == module.Definition.Id))
        {
            throw new InvalidOperationException(
                $"Grass task '{module.Definition.Id}' is already registered.");
        }

        _modules.Add(module);
    }
}





public sealed class DefaultGrassTaskCatalog : IGrassTaskCatalog
{
    private readonly GrassTaskCatalog _catalog = new();

    public DefaultGrassTaskCatalog(
        StartGameTaskModule startGameTaskModule,
        TeamRaceTaskModule teamRaceTaskModule,
        DailyRaceTaskModule dailyRaceTaskModule,
        CareerTrainingTaskModule careerTrainingTaskModule,
        MailCollectionTaskModule mailCollectionTaskModule,
        MissionCollectionTaskModule missionCollectionTaskModule,
        ShopTaskModule shopTaskModule)
    {
        ArgumentNullException.ThrowIfNull(startGameTaskModule);
        ArgumentNullException.ThrowIfNull(teamRaceTaskModule);
        ArgumentNullException.ThrowIfNull(dailyRaceTaskModule);
        ArgumentNullException.ThrowIfNull(careerTrainingTaskModule);
        ArgumentNullException.ThrowIfNull(mailCollectionTaskModule);
        ArgumentNullException.ThrowIfNull(missionCollectionTaskModule);
        ArgumentNullException.ThrowIfNull(shopTaskModule);
        _catalog.Register(startGameTaskModule);
        _catalog.Register(teamRaceTaskModule);
        _catalog.Register(dailyRaceTaskModule);
        _catalog.Register(careerTrainingTaskModule);
        _catalog.Register(mailCollectionTaskModule);
        _catalog.Register(missionCollectionTaskModule);
        _catalog.Register(shopTaskModule);
    }

    public IReadOnlyList<IGrassTaskModule> Modules => _catalog.Modules;
}
