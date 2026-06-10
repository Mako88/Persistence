namespace Persistence.Tests;

/// <summary>
/// Collection for tests that mutate process-global <c>PERSISTENCE_*</c> environment variables.
/// Several config tests set/clear the same env vars (e.g. <c>PERSISTENCE_SELECTEDMODEL</c>,
/// <c>PERSISTENCE_DATABASEDIRECTORY</c>) inside try/finally; xUnit would otherwise run their classes
/// in parallel and they'd clobber each other's env state. Sharing one collection with
/// <see cref="Xunit.CollectionDefinitionAttribute.DisableParallelization"/> serializes them.
/// </summary>
[CollectionDefinition("EnvironmentVariables", DisableParallelization = true)]
public sealed class EnvironmentVariablesCollection;
