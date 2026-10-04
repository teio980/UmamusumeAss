using System.Collections.Concurrent;
using System.Diagnostics;
using UmamusumeWpfGui.Models;
using UmamusumeWpfGui.Services.Tasks;

namespace UmamusumeWpfGui.Services.Training;

/// <summary>
/// Connects post-training event recognition to the event actions already
/// defined by the URA screen profile and JSON pipeline.
/// </summary>
internal sealed class CareerEventHandler : ICareerEventHandler
{
    private static readonly (string ScreenId, string ActionId)[] Events =
    [
        ("event_choice", "event.choice_first"),
        ("training_event", "event.advance"),
        ("scenario_event", "event.advance"),
        ("career_intro_event", "event.advance"),
    ];

    private readonly IVisualPipelineRuntime _visualRuntime;
    private readonly ICareerFlowActionRunner _actions;
    private readonly CareerEventTitleRecognizer _eventTitleRecognizer;
    private readonly ConcurrentDictionary<string, Lazy<Task<GrayImage?>>> _templates =
        new(StringComparer.OrdinalIgnoreCase);

    public CareerEventHandler(
        IVisualPipelineRuntime visualRuntime,
        ICareerFlowActionRunner actions)
    {
        _visualRuntime = visualRuntime ?? throw new ArgumentNullException(nameof(visualRuntime));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _eventTitleRecognizer = new CareerEventTitleRecognizer(visualRuntime);
    }

    public async Task<CareerTrainingResult?> TryRecognizeAndHandleAsync(
        CareerFlowContext context)
    {
        var started = Stopwatch.GetTimestamp();
        string? observedEvent = null;
        string? observedEventId = null;
        var stableSamples = 0;
        var quietSamples = 0;

        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(45))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            GrayImage? frame;
            try
            {
                frame = await _visualRuntime.CaptureGrayAsync(
                        context.Connection,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not DateChangedInterruptionException and not DateChangedRecoveryException)
            {
                frame = null;
            }

            if (frame is not null)
            {
                (string ScreenId, string ActionId)? recognized = null;
                CareerEventTitleMatch? titleMatch = null;
                foreach (var candidate in Events)
                {
                    if (await MatchesAsync(frame, context.Pack, candidate.ScreenId,
                            context.CancellationToken).ConfigureAwait(false))
                    {
                        recognized = candidate;
                        break;
                    }

                    titleMatch = await _eventTitleRecognizer.RecognizeAsync(
                            frame, context.Pack, candidate.ScreenId, context.CancellationToken)
                        .ConfigureAwait(false);
                    if (titleMatch is not null)
                    {
                        recognized = (candidate.ScreenId, titleMatch.Event.OcrTitle!.ActionId);
                        break;
                    }
                }

                if (recognized is { } eventScreen)
                {
                    quietSamples = 0;
                    stableSamples = observedEvent == eventScreen.ScreenId
                        && observedEventId == titleMatch?.Event.EventId
                        ? stableSamples + 1
                        : 1;
                    observedEvent = eventScreen.ScreenId;
                    observedEventId = titleMatch?.Event.EventId;
                    if (stableSamples >= 2)
                    {
                        if (titleMatch is not null)
                        {
                            context = context with { Observation = titleMatch.Observation };
                            context.LogSink?.Add("Career Training",
                                $"Event '{titleMatch.Event.EventId}' recognized via OCR title: "
                                + $"'{titleMatch.Title}'; action '{eventScreen.ActionId}'.");
                        }
                        var result = await _actions.RunAsync(
                                context,
                                eventScreen.ScreenId,
                                eventScreen.ActionId,
                                titleMatch?.RunOptions)
                            .ConfigureAwait(false);
                        if (result is not null)
                            return result;

                        context.State.HasScenarioEvent = false;
                        return null;
                    }
                }
                else
                {
                    observedEvent = null;
                    observedEventId = null;
                    stableSamples = 0;
                    quietSamples++;
                    if (await MatchesAsync(frame, context.Pack, "training_result",
                            context.CancellationToken).ConfigureAwait(false)
                        || await MatchesAsync(frame, context.Pack, "career_main",
                            context.CancellationToken).ConfigureAwait(false)
                        || quietSamples >= 5)
                    {
                        return null;
                    }
                }
            }

            await _visualRuntime.DelayAsync(350, context.CancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> MatchesAsync(
        GrayImage frame,
        UraScenarioPack pack,
        string screenId,
        CancellationToken cancellationToken)
    {
        var screen = pack.ScreenProfile.Find(screenId);
        if (screen is null)
            return false;

        foreach (var templatePath in screen.Templates)
        {
            var template = await LoadTemplateAsync(pack, screen, templatePath, cancellationToken)
                .ConfigureAwait(false);
            if (template is null || !TemplateMatcher.Find(
                    frame,
                    template,
                    screen.Recognition.Roi,
                    screen.Recognition.TemplateThreshold,
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight).Found)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(screen.Recognition.RequiredTemplate))
                return true;

            var required = await LoadTemplateAsync(
                    pack,
                    screen,
                    screen.Recognition.RequiredTemplate,
                    cancellationToken)
                .ConfigureAwait(false);
            if (required is not null && TemplateMatcher.Find(
                    frame,
                    required,
                    screen.Recognition.RequiredTemplateRoi,
                    screen.Recognition.RequiredTemplateThreshold,
                    pack.ScreenProfile.ReferenceWidth,
                    pack.ScreenProfile.ReferenceHeight).Found)
            {
                return true;
            }
        }

        return false;
    }

    private Task<GrayImage?> LoadTemplateAsync(
        UraScenarioPack pack,
        UraScreenDefinition screen,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var path = screen.SourceDirectory is not null
            ? UraScenarioResourceResolver.Resolve(pack, screen, relativePath)
            : UraScenarioResourceResolver.Resolve(pack, relativePath);
        return _templates.GetOrAdd(path,
                key => new Lazy<Task<GrayImage?>>(() =>
                    _visualRuntime.LoadTemplateAsync(
                        key,
                        string.Empty,
                        CancellationToken.None)))
            .Value.WaitAsync(cancellationToken);
    }
}
