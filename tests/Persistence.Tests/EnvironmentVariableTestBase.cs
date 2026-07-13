namespace Persistence.Tests;

/// <summary>
/// Base class for config tests that read <c>PERSISTENCE_*</c> environment variables.
/// <para>
/// These tests run inside the live app's container, which legitimately sets
/// <c>PERSISTENCE_UIMODE</c>, <c>PERSISTENCE_CONFIGPATH</c>, <c>PERSISTENCE_CONTAINER_*</c>, etc.
/// The <c>dotnet test</c> process inherits those, and env vars override both file fixtures and
/// code defaults — so without isolation a test that writes <c>UiMode=Tui</c> to a temp file still
/// reads back <c>Api</c> from the ambient environment. That override precedence is exactly the
/// behaviour these tests verify, so the fix is isolation, not deleting the coverage.
/// </para>
/// <para>
/// The constructor snapshots and clears every ambient <c>PERSISTENCE_*</c> variable so each test
/// starts from a clean environment; <see cref="Dispose"/> restores the exact snapshot afterwards.
/// Tests that set their own <c>PERSISTENCE_*</c> vars (in try/finally) still work — they now do so
/// against a known-empty baseline. The enclosing "EnvironmentVariables" collection already
/// disables parallelization, so these process-global mutations never race.
/// </para>
/// </summary>
public abstract class EnvironmentVariableTestBase : IDisposable
{
    private const string Prefix = "PERSISTENCE_";

    private readonly Dictionary<string, string?> snapshot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Snapshots and clears all ambient <c>PERSISTENCE_*</c> environment variables so each test
    /// starts from a clean, predictable environment.
    /// </summary>
    protected EnvironmentVariableTestBase()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                snapshot[key] = entry.Value as string;
                Environment.SetEnvironmentVariable(key, null);
            }
        }
    }

    /// <summary>
    /// Restores the ambient <c>PERSISTENCE_*</c> environment exactly as it was before the test ran.
    /// </summary>
    public void Dispose()
    {
        foreach (var (key, value) in snapshot)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        GC.SuppressFinalize(this);
    }
}