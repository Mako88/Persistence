using Autofac.Features.Indexed;
using Persistence.Config;
using Persistence.Data.Entities;
using Persistence.Data.Repositories;
using Persistence.DI;
using Persistence.Events;
using Persistence.Notifications;
using Persistence.Services;

namespace Persistence.Runtime;

/// <summary>
/// Processes a single conversational turn. Persists the user's input, calls the model,
/// parses the structured response, dispatches to the appropriate action handler, and
/// loops if the digital colleague requests continuation. Handles parse failures by
/// feeding the error back to the model when iterations remain, or displaying the raw
/// output when they don't. Enforces a configurable iteration cap to prevent runaway loops.
/// </summary>
[Singleton]
public class TurnHandler : ITurnHandler
{
    private readonly IWorkingContextRepository workingContextRepo;
    private readonly ITagRepository tagRepo;
    private readonly IActionLogRepository actionLogRepo;
    private readonly ISessionContext sessionContext;
    private readonly IModelClient modelClient;
    private readonly IModelResponseParser responseParser;
    private readonly IPromptBuilder promptBuilder;
    private readonly IIndex<ModelAction, IActionHandler> actionHandlers;
    private readonly IEventBus eventBus;
    private readonly IAppConfig config;

    /// <summary>
    /// Constructor
    /// </summary>
    public TurnHandler(
        IWorkingContextRepository workingContextRepo,
        ITagRepository tagRepo,
        IActionLogRepository actionLogRepo,
        ISessionContext sessionContext,
        IModelClient modelClient,
        IModelResponseParser responseParser,
        IPromptBuilder promptBuilder,
        IIndex<ModelAction, IActionHandler> actionHandlers,
        IEventBus eventBus,
        IAppConfig config)
    {
        this.workingContextRepo = workingContextRepo;
        this.tagRepo = tagRepo;
        this.actionLogRepo = actionLogRepo;
        this.sessionContext = sessionContext;
        this.modelClient = modelClient;
        this.responseParser = responseParser;
        this.promptBuilder = promptBuilder;
        this.actionHandlers = actionHandlers;
        this.eventBus = eventBus;
        this.config = config;
    }

    /// <summary>
    /// Executes a full turn — persists the user input, calls the model in a loop
    /// dispatching to action handlers, and stops when the digital colleague yields
    /// or the iteration cap is reached
    /// </summary>
    public async Task ExecuteTurnAsync(string input, CancellationToken ct = default)
    {
        var context = await workingContextRepo.GetByIdAsync(sessionContext.WorkingContextId, ct);

        if (context == null)
        {
            return;
        }

        await PersistUserMessageAsync(context, input);

        var availableTags = await tagRepo.GetAllRootAsync();
        var iteration = 0;
        var hasResponded = false;

        while (iteration < config.MaxActionIterations)
        {
            iteration++;

            var (prompt, systemPrompt) = promptBuilder.Build(context, availableTags, iteration, config.MaxActionIterations);
            var rawOutput = await modelClient.CompleteAsync(prompt, systemPrompt, ct);
            var response = responseParser.Parse(rawOutput);

            if (!response.ParsedSuccessfully)
            {
                if (iteration < config.MaxActionIterations)
                {
                    AddParseErrorFeedback(context, rawOutput);
                    continue;
                }

                // Out of iterations — show the raw output and an error message
                await eventBus.PublishAsync(this, new DigitalColleagueReplied(rawOutput));
                await eventBus.PublishAsync(this, new DigitalColleagueReplied(
                    "[Turn ended: could not parse a valid structured response]"));
                break;
            }

            await DispatchActionAsync(context, response, ct);

            if (response.Action == ModelAction.RespondToUser)
            {
                hasResponded = true;
            }

            if (!response.Continue)
            {
                if (!hasResponded)
                {
                    await eventBus.PublishAsync(this, new DigitalColleagueReplied(
                        "[Turn completed — no response to user]"));
                }

                break;
            }

            if (iteration >= config.MaxActionIterations)
            {
                await eventBus.PublishAsync(this, new DigitalColleagueReplied(
                    "[Turn ended: maximum action iterations reached]"));
            }
        }

        // Persist all context changes from this turn in a single save.
        // Transient fragment types (ActionResponse, ScratchPad) are automatically
        // skipped by SaveSubEntitiesAsync.
        await workingContextRepo.SaveAsync(context, ct: ct);
    }

    // ── Private ──────────────────────────────────────────────────

    /// <summary>
    /// Dispatches the model's response to the appropriate action handler, logs the
    /// action automatically, and injects an error ActionResponse fragment if the
    /// handler throws
    /// </summary>
    private async Task DispatchActionAsync(WorkingContextEntity context, ModelResponse response, CancellationToken ct)
    {
        if (!actionHandlers.TryGetValue(response.Action, out var handler))
        {
            throw new InvalidOperationException($"No handler registered for action: {response.Action}");
        }

        try
        {
            await handler.HandleAsync(context, response.Data, ct);

            await actionLogRepo.LogAsync(
                response.Action.ToString(),
                payload: response.Data?.ToJsonString(),
                result: "success");
        }
        catch (Exception ex)
        {
            AddErrorResponse(context, response.Action, ex);

            await actionLogRepo.LogAsync(
                response.Action.ToString(),
                payload: response.Data?.ToJsonString(),
                result: $"error: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds the user's message to the working context as a ChatMessage fragment
    /// and saves the context (which cascades to the fragment and junction table)
    /// </summary>
    private async Task PersistUserMessageAsync(WorkingContextEntity context, string input)
    {
        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ChatMessage,
            Status = ContextFragmentStatus.Active,
            Content = input,
            Notes = "user",
            Importance = 1.0f,
            Confidence = 1.0f,
            Weight = 1.0f,

            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });

        await workingContextRepo.SaveAsync(context);
    }

    /// <summary>
    /// Adds a transient ActionResponse fragment telling the digital colleague that
    /// its previous output could not be parsed, so it can correct the format
    /// </summary>
    private void AddParseErrorFeedback(WorkingContextEntity context, string rawOutput)
    {
        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ActionResponse,
            Status = ContextFragmentStatus.Active,
            Content = $"Your previous response could not be parsed as valid structured JSON. " +
                      $"Please respond with a valid JSON object containing \"action\", \"continue\", and \"data\" properties. " +
                      $"Your raw output was:\n{rawOutput}",
            Importance = 1.0f,
            Confidence = 1.0f,
            Weight = 1.0f,

            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Adds a transient ActionResponse fragment with error details to the working context
    /// </summary>
    private void AddErrorResponse(WorkingContextEntity context, ModelAction action, Exception ex)
    {
        context.AddFragment(new WeightedContextFragment
        {
            FragmentType = ContextFragmentType.ActionResponse,
            Status = ContextFragmentStatus.Active,
            Content = $"Error executing {action}: {ex.Message}",
            Importance = 1.0f,
            Confidence = 1.0f,
            Weight = 1.0f,

            CreatedUtc = DateTimeOffset.UtcNow,
            LastModifiedUtc = DateTimeOffset.UtcNow,
        });
    }
}
