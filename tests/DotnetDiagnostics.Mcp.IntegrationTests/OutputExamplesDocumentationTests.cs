using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetDiagnostics.Core.Safety;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Mcp.Tools;
using FluentAssertions;

namespace DotnetDiagnostics.Mcp.IntegrationTests;

/// <summary>
/// Stable contract guardrails for <c>docs/output-examples.md</c>. Metric values, timestamps,
/// process ids, handles, and array lengths are intentionally not snapshot-tested.
/// </summary>
public sealed class OutputExamplesDocumentationTests
{
    [Fact]
    public void OutputExamples_DocumentRepresentativeDiscriminators()
    {
        var doc = ReadOutputExamples();
        var eventKinds = new[] { "counters", "gc", "exceptions", "threadpool", "contention" };
        var sampleKinds = new[] { "cpu", "allocation" };

        foreach (var kind in eventKinds)
        {
            DiagnosticOperationCatalog.CollectEventsKinds.All.Should().Contain(kind);
            doc.Should().Contain($"\"kind\": \"{kind}\"");
        }

        foreach (var kind in sampleKinds)
        {
            DiagnosticOperationCatalog.CollectSampleKinds.All.Should().Contain(kind);
            doc.Should().Contain($"\"kind\": \"{kind}\"");
        }

        doc.Should().Contain("\"view\": \"triage\"");
        doc.Should().Contain("\"tool\": \"collect_events\"");
    }

    [Fact]
    public void OutputExamples_DocumentStableTriageBatchAndSafetyFields()
    {
        var doc = ReadOutputExamples();

        AssertDocumentedProperties<TriageResult>(
            doc,
            nameof(TriageResult.Verdict),
            nameof(TriageResult.Severity),
            nameof(TriageResult.SecondaryVerdicts),
            nameof(TriageResult.TopIndicators),
            nameof(TriageResult.ModelVersion),
            nameof(TriageResult.Assessment),
            nameof(TriageResult.ObservedSignals),
            nameof(TriageResult.Hypotheses));
        AssertDocumentedProperties<TriageIndicator>(
            doc,
            nameof(TriageIndicator.Name),
            nameof(TriageIndicator.Value),
            nameof(TriageIndicator.Unit),
            nameof(TriageIndicator.Score),
            nameof(TriageIndicator.Level));
        AssertDocumentedProperties<CollectEventsEnvelope>(
            doc,
            nameof(CollectEventsEnvelope.Kind),
            nameof(CollectEventsEnvelope.Counters),
            nameof(CollectEventsEnvelope.Gc),
            nameof(CollectEventsEnvelope.Exceptions),
            nameof(CollectEventsEnvelope.ThreadPool),
            nameof(CollectEventsEnvelope.Contention));
        AssertDocumentedProperties<CollectSampleEnvelope>(
            doc,
            nameof(CollectSampleEnvelope.Kind),
            nameof(CollectSampleEnvelope.Cpu),
            nameof(CollectSampleEnvelope.Allocation));
        AssertDocumentedProperties<CollectBatchReport>(
            doc,
            nameof(CollectBatchReport.ProcessId),
            nameof(CollectBatchReport.DurationSeconds),
            nameof(CollectBatchReport.Results),
            nameof(CollectBatchReport.Gen2Evidence));
        AssertDocumentedProperties<CollectBatchEntryResult>(
            doc,
            nameof(CollectBatchEntryResult.Tool),
            nameof(CollectBatchEntryResult.Kind),
            nameof(CollectBatchEntryResult.Summary),
            nameof(CollectBatchEntryResult.Data),
            nameof(CollectBatchEntryResult.Handle),
            nameof(CollectBatchEntryResult.Error));
        AssertDocumentedProperties<InvocationSafetyDescriptor>(
            doc,
            nameof(InvocationSafetyDescriptor.RiskLevel),
            nameof(InvocationSafetyDescriptor.TargetImpact),
            nameof(InvocationSafetyDescriptor.DataExposure),
            nameof(InvocationSafetyDescriptor.SideEffects),
            nameof(InvocationSafetyDescriptor.ApprovalPolicy),
            nameof(InvocationSafetyDescriptor.Reason),
            nameof(InvocationSafetyDescriptor.Mitigations));
        AssertDocumentedProperties<InvocationSafetyApproval>(
            doc,
            nameof(InvocationSafetyApproval.Status),
            nameof(InvocationSafetyApproval.Message),
            nameof(InvocationSafetyApproval.AcknowledgementArgument),
            nameof(InvocationSafetyApproval.RequiredAcknowledgement));
        AssertDocumentedProperties<InvocationSafetyAcknowledgement>(
            doc,
            nameof(InvocationSafetyAcknowledgement.Operation),
            nameof(InvocationSafetyAcknowledgement.Arguments),
            nameof(InvocationSafetyAcknowledgement.Safety),
            nameof(InvocationSafetyAcknowledgement.ChildSafety));
        AssertDocumentedProperties<InvocationSafetyChildDescriptor>(
            doc,
            nameof(InvocationSafetyChildDescriptor.Operation),
            nameof(InvocationSafetyChildDescriptor.Arguments),
            nameof(InvocationSafetyChildDescriptor.Safety));

        var batchSection = Section(doc, "## Concurrent collection — `collect_batch`");
        batchSection.Should().Contain("\"childSafety\"");
        batchSection.Should().Contain("\"operation\": \"collect_events\"");
        batchSection.Should().Contain("\"arguments\": { \"kind\": \"counters\" }");
        batchSection.Should().Contain("\"arguments\": { \"kind\": \"gc\" }");
        batchSection.Should().Contain("\"arguments\": { \"kind\": \"exceptions\" }");
    }

    [Theory]
    [InlineData(InvocationSafetyApprovalStatus.AcknowledgementRequired, "acknowledgement-required")]
    [InlineData(InvocationSafetyApprovalStatus.HumanApprovalRequired, "human-approval-required")]
    [InlineData(InvocationSafetyApprovalStatus.Declined, "declined")]
    [InlineData(InvocationSafetyApprovalStatus.Failed, "failed")]
    public void OutputExamples_DocumentSafetyApprovalStatuses(
        InvocationSafetyApprovalStatus status,
        string serializedValue)
    {
        JsonSerializer.Serialize(status).Should().Be($"\"{serializedValue}\"");
        ReadOutputExamples().Should().Contain($"\"status\": \"{serializedValue}\"");
    }

    private static void AssertDocumentedProperties<T>(string doc, params string[] propertyNames)
    {
        var properties = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .ToDictionary(
                static property => property.Name,
                static property =>
                    property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                StringComparer.Ordinal);

        foreach (var propertyName in propertyNames)
        {
            properties.Should().ContainKey(propertyName);
            doc.Should().Contain($"\"{properties[propertyName]}\"");
        }
    }

    private static string ReadOutputExamples()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetDiagnostics.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test output must be beneath the repository root");
        return File.ReadAllText(Path.Combine(directory!.FullName, "docs", "output-examples.md"));
    }

    private static string Section(string document, string heading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        var end = document.IndexOf("\n---\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return document[start..end];
    }
}
