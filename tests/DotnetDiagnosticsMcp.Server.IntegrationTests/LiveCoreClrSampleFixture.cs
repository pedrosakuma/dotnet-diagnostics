namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Boots a single <c>CoreClrSample</c> instance for the lifetime of a test class. The spawn /
/// readiness / teardown logic lives in the shared <see cref="LiveCoreClrSampleFixtureBase"/>; this
/// thin subclass keeps the concrete fixture type inside this assembly so <c>IClassFixture&lt;T&gt;</c>
/// consumers bind to a project-local type.
/// </summary>
public sealed class LiveCoreClrSampleFixture : LiveCoreClrSampleFixtureBase;
