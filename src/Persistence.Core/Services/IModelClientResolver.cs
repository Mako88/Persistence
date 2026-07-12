namespace Persistence.Services;

/// <summary>
/// Resolves the <see cref="IModelClient"/> for the currently-active model profile
/// (<see cref="Config.IAppConfig.Provider"/>). Because the active profile can change at runtime (the
/// <c>set_model</c> command), the client is resolved per turn rather than injected once — so a switch
/// takes effect on the next turn without restarting the process.
/// </summary>
public interface IModelClientResolver
{
    /// <summary>Returns the model client for the active provider/model profile.</summary>
    IModelClient Resolve();
}
