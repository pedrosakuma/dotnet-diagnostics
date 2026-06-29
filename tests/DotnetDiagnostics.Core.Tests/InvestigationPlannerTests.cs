using System.Collections.Generic;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Investigation;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnostics.Core.Tests;

public class InvestigationPlannerTests
{
    private readonly InvestigationPlanner _planner = new();

    [Fact]
    public void Plan_DefaultsToColdMode_WhenNoHypothesisOrBaseline()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Symptom: "high latency"));

        plan.Mode.Should().Be(InvestigationMode.Cold);
        plan.NextStep.ToolName.Should().Be("collect_events", "cold investigations must start with vitals");
        plan.NextStep.StepNumber.Should().Be(1);
        plan.AllSteps.Should().HaveCountGreaterThan(1);
        plan.EarlyStopConditions.Should().Contain(e => e.ConditionId == "max-tool-calls-reached");
        plan.BaselineComparisons.Should().BeNull();
    }

    [Fact]
    public void Plan_PicksWarmMode_WhenBaselineProvided_AndEmitsComparisons()
    {
        var baseline = new BaselineHandle(
            InvestigationId: "inv-prev",
            SnapshotAt: DateTimeOffset.UtcNow.AddHours(-1),
            KeyMetrics: new Dictionary<string, double> { ["cpu_pct"] = 23.4, ["gen2_count"] = 0.5 });

        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Baseline: baseline));

        plan.Mode.Should().Be(InvestigationMode.Warm);
        plan.NextStep.StepId.Should().Be("vitals-delta");
        plan.BaselineComparisons.Should().NotBeNull().And.HaveCount(2);
        plan.BaselineComparisons!.Select(c => c.MetricName).Should().BeEquivalentTo(new[] { "cpu_pct", "gen2_count" });
    }

    [Theory]
    [InlineData("lock contention on Cart.Checkout", "collect_events", "lock-events")]
    [InlineData("memory leak in payment service", "collect_events", "memory-vitals")]
    [InlineData("threadpool starvation after release", "collect_events", "tp-vitals")]
    [InlineData("exception storm from validation", "collect_events", "exception-collect")]
    [InlineData("hot CPU on Regex matching", "collect_events", "cpu-vitals")]
    [InlineData("cold start regression on startup", "collect_events", "startup-vitals")]
    public void Plan_RoutesHypothesisByKeyword(string hypothesis, string expectedTool, string expectedStepId)
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Hypothesis: hypothesis));

        plan.Mode.Should().Be(InvestigationMode.Hypothesis);
        plan.NextStep.ToolName.Should().Be(expectedTool);
        plan.NextStep.StepId.Should().Be(expectedStepId);
        plan.EarlyStopConditions.Should().Contain(e => e.ConditionId == "hypothesis-confirmed");
        plan.EarlyStopConditions.Should().Contain(e => e.ConditionId == "hypothesis-refuted");
    }

    [Fact]
    public void Plan_UnknownHypothesis_FallsBackToVitals()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 1234, Hypothesis: "weird mystery thing"));

        plan.Mode.Should().Be(InvestigationMode.Hypothesis);
        plan.NextStep.ToolName.Should().Be("collect_events");
        plan.NextStep.StepId.Should().Be("vitals");
    }

    [Fact]
    public void Plan_HypothesisWinsOverBaseline_WhenBothProvided()
    {
        var baseline = new BaselineHandle("inv-prev", DateTimeOffset.UtcNow, new Dictionary<string, double>());
        var plan = _planner.Plan(new InvestigationRequest(1234, Hypothesis: "lock contention", Baseline: baseline));

        plan.Mode.Should().Be(InvestigationMode.Hypothesis);
    }

    [Fact]
    public void Plan_HonorsCustomConstraints()
    {
        var plan = _planner.Plan(new InvestigationRequest(
            1234,
            Symptom: "latency",
            Constraints: new InvestigationConstraints(MaxToolCalls: 3, DumpRequiresApproval: false, MaxDumpType: "Triage")));

        plan.Constraints.MaxToolCalls.Should().Be(3);
        plan.Constraints.DumpRequiresApproval.Should().BeFalse();
        plan.Constraints.MaxDumpType.Should().Be("Triage");
    }

    [Fact]
    public void Plan_RejectsInvalidProcessId()
    {
        var act = () => _planner.Plan(new InvestigationRequest(ProcessId: 0));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Plan_AllBranchTargets_ResolveToStepOrTerminal_AcrossModes()
    {
        var baseline = new BaselineHandle("inv-base", DateTimeOffset.UtcNow, new Dictionary<string, double> { ["cpu_pct"] = 10 });
        var plans = new[]
        {
            _planner.Plan(new InvestigationRequest(1234, Symptom: "latency")),
            _planner.Plan(new InvestigationRequest(1234, Baseline: baseline)),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "lock contention on x")),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "memory leak")),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "cpu hot path")),
            _planner.Plan(new InvestigationRequest(1234, Hypothesis: "weird mystery thing")),
        };

        foreach (var plan in plans)
        {
            var validTargets = new HashSet<string>(plan.AllSteps.Select(s => s.StepId)
                .Concat(plan.Terminals.Select(t => t.TerminalId)));
            foreach (var step in plan.AllSteps)
            {
                foreach (var branch in step.Branches)
                {
                    validTargets.Should().Contain(branch.NextStepId,
                        $"branch '{branch.Condition}' in step '{step.StepId}' (mode={plan.Mode}) must point to a real step or terminal");
                }
            }
        }
    }

    [Fact]
    public void Plan_LockEventsStep_EmitsContentionKeywordAsLong()
    {
        var plan = _planner.Plan(new InvestigationRequest(1234, Hypothesis: "lock contention on Foo"));
        var lockStep = plan.AllSteps.First(s => s.StepId == "lock-events");

        lockStep.ToolParams.Should().ContainKey("keywords");
        var kw = lockStep.ToolParams["keywords"];
        kw.Should().BeOfType<long>("collect_event_source.keywords is typed `long` — string would fail schema validation");
        ((long)kw!).Should().Be(0x4000L);
    }

    [Fact]
    public void Plan_DumpTerminal_IsAlwaysApprovalGated_RegardlessOfConstraints()
    {
        var plan = _planner.Plan(new InvestigationRequest(
            1234,
            Hypothesis: "memory leak in cache",
            Constraints: new InvestigationConstraints(DumpRequiresApproval: false)));

        var dump = plan.Terminals.First(t => t.TerminalId == "dump-heap");
        dump.RequiresApproval.Should().BeTrue(
            "dumps must remain approval-gated even when global flag is off — Mini still pauses production");
    }

    [Fact]
    public void Plan_AcceptsCustomIdFactory_ForDeterministicSnapshotTests()
    {
        var seq = 0;
        var planner = new InvestigationPlanner(idFactory: () => $"inv-test-{++seq}");

        var first = planner.Plan(new InvestigationRequest(1234, Symptom: "latency"));
        var second = planner.Plan(new InvestigationRequest(1234, Symptom: "latency"));

        first.InvestigationId.Should().Be("inv-test-1");
        second.InvestigationId.Should().Be("inv-test-2");
    }

    // ───────────────────────────── executable next-action / playbook (#468) ─────────────────────────────

    [Fact]
    public void Plan_ColdMode_EmitsExecutableNextAction_FilledWithPidAndKind()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 4242, Symptom: "high cpu"));

        plan.NextAction.Should().NotBeNull("the planner must surface a one-click executable call");
        plan.NextAction!.NextTool.Should().Be("collect_events");
        plan.NextAction.Priority.Should().Be(NextActionHintPriority.High, "the immediate call is the highest-priority hint");
        plan.NextAction.SuggestedArguments.Should().NotBeNull();
        plan.NextAction.SuggestedArguments!["processId"].Should().Be(4242, "the pid must be substituted into the call");
        plan.NextAction.SuggestedArguments!["kind"].Should().Be("counters");

        // NextAction mirrors NextStep — same tool + identical filled arguments.
        plan.NextAction.NextTool.Should().Be(plan.NextStep.ToolName);
        plan.NextAction.SuggestedArguments.Should().BeEquivalentTo(plan.NextStep.ToolParams);
    }

    [Fact]
    public void Plan_ColdMode_Playbook_ChainsVitalsThenCpuSampleThenDrilldown()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 4242, Symptom: "high cpu"));

        plan.Playbook.Should().NotBeNull();
        plan.Playbook!.Count.Should().BeInRange(2, 4, "a playbook is the next 2-4 chained calls");
        plan.Playbook[0].Should().BeSameAs(plan.NextAction, "the playbook leads with the immediate next-action");

        var tools = plan.Playbook.Select(p => p.NextTool).ToArray();
        tools.Should().ContainInOrder(new[] { "collect_events", "collect_sample", "query_snapshot" },
            "the cold happy-path is vitals → cpu sample → drill the sample handle");

        // The drilldown references the sample's handle via a ${stepId.handle} placeholder.
        var drilldown = plan.Playbook.First(p => p.NextTool == "query_snapshot");
        drilldown.SuggestedArguments.Should().NotBeNull();
        drilldown.SuggestedArguments!["handle"].Should().Be("${cpu-sample.handle}");
        drilldown.SuggestedArguments!["view"].Should().Be("call-tree");
        drilldown.SuggestedArguments!["topN"].Should().Be(25);
    }

    [Fact]
    public void Plan_MemoryHypothesis_Playbook_EndsWithApprovalGatedDump()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 777, Hypothesis: "memory leak in cache"));

        plan.NextAction!.NextTool.Should().Be("collect_events");
        plan.NextAction.SuggestedArguments!["processId"].Should().Be(777);

        var tools = plan.Playbook!.Select(p => p.NextTool).ToArray();
        tools.Should().ContainInOrder(new[] { "collect_events", "collect_events", "collect_process_dump" },
            "the memory path is vitals → gc events → heap dump");

        var dump = plan.Playbook!.First(p => p.NextTool == "collect_process_dump");
        dump.SuggestedArguments!["processId"].Should().Be(777);
        dump.Reason.Should().Contain("approval-gated", "the dump step must flag that it needs confirmation");
    }

    [Fact]
    public void Plan_LockHypothesis_Playbook_DrillsTheSampleHandle()
    {
        var plan = _planner.Plan(new InvestigationRequest(ProcessId: 99, Hypothesis: "lock contention on Cart.Checkout"));

        var tools = plan.Playbook!.Select(p => p.NextTool).ToArray();
        tools.Should().ContainInOrder(new[] { "collect_events", "collect_sample", "query_snapshot" },
            "the lock path is contention events → cpu sample → drill the sample handle");

        var drilldown = plan.Playbook!.First(p => p.NextTool == "query_snapshot");
        drilldown.SuggestedArguments!["handle"].Should().Be("${lock-sample.handle}");
    }

    [Fact]
    public void Plan_Playbook_IsNeverLongerThanFour_AndStartsWithNextStep()
    {
        var plans = new[]
        {
            _planner.Plan(new InvestigationRequest(1, Symptom: "latency")),
            _planner.Plan(new InvestigationRequest(1, Hypothesis: "lock contention on x")),
            _planner.Plan(new InvestigationRequest(1, Hypothesis: "memory leak")),
            _planner.Plan(new InvestigationRequest(1, Hypothesis: "cpu hot path")),
            _planner.Plan(new InvestigationRequest(1, Baseline: new BaselineHandle("inv", DateTimeOffset.UtcNow, new Dictionary<string, double> { ["cpu_pct"] = 1 }))),
        };

        foreach (var plan in plans)
        {
            plan.Playbook.Should().NotBeNull();
            plan.Playbook!.Count.Should().BeInRange(1, 4);
            plan.Playbook[0].NextTool.Should().Be(plan.NextStep.ToolName);
            plan.Playbook[0].SuggestedArguments.Should().BeEquivalentTo(plan.NextStep.ToolParams);
        }
    }
}
