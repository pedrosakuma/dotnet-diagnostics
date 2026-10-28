import {
    INVESTIGATION_EVIDENCE_KINDS,
    classifyInvestigationTemporalRelation,
    type InvestigationEvidenceItem,
    type InvestigationEvidenceKind,
    type InvestigationTemporalRelation,
} from "./investigationEvidence";

const KIND_LABELS: Record<InvestigationEvidenceKind, string> = {
    cpu: "CPU",
    allocation: "Allocation",
    off_cpu: "Off-CPU",
    contention: "Contention",
    threadpool: "ThreadPool",
    gc: "GC",
    requests: "Requests",
};

export interface InvestigationLensEvidence {
    kind: InvestigationEvidenceKind;
    label: string;
    state: "observed" | "unavailable";
    summary: string;
    window: string;
    backend: string;
    quality: string;
    retention: string;
    sensitive: boolean;
}

export interface InvestigationLensRelation {
    left: InvestigationEvidenceKind;
    right: InvestigationEvidenceKind;
    relation: InvestigationTemporalRelation;
}

export interface InvestigationLensAction {
    kind: InvestigationEvidenceKind;
    label: string;
}

export interface InvestigationLensVisualization {
    title: string;
    first: string;
    relation: InvestigationTemporalRelation;
    second: string;
    nextDecision: string;
}

export interface InvestigationLensModel {
    headline: string;
    target: string;
    evidence: InvestigationLensEvidence[];
    relations: InvestigationLensRelation[];
    observed: string[];
    inferred: string[];
    unavailable: string[];
    limitations: string[];
    actions: InvestigationLensAction[];
    visualization?: InvestigationLensVisualization;
}

export function buildInvestigationLensModel(items: readonly InvestigationEvidenceItem[]): InvestigationLensModel {
    const byKind = new Map(items.map(item => [item.descriptor.kind, item]));
    const availableItems = INVESTIGATION_EVIDENCE_KINDS
        .map(kind => byKind.get(kind))
        .filter((item): item is InvestigationEvidenceItem => item !== undefined);
    const verified = availableItems.filter(item => item.descriptor.target.identityStatus === "verified");
    const target = verified.length > 0
        ? `PID ${verified[0].descriptor.target.processId} · process start ${verified[0].descriptor.target.processStartedAt}`
        : availableItems.length > 0
            ? `PID ${availableItems[0].descriptor.target.processId} · process identity unverified`
            : "No target evidence retained";

    const evidence = INVESTIGATION_EVIDENCE_KINDS.map(kind => {
        const item = byKind.get(kind);
        if (!item) {
            return {
                kind,
                label: KIND_LABELS[kind],
                state: "unavailable" as const,
                summary: "No retained evidence for this target generation.",
                window: "Unavailable",
                backend: "Unavailable",
                quality: "Unavailable",
                retention: "Unavailable",
                sensitive: false,
            };
        }
        const descriptor = item.descriptor;
        const start = descriptor.window.start ?? "unknown";
        const end = descriptor.window.end ?? "unknown";
        const drilldown = descriptor.retention.drilldown;
        return {
            kind,
            label: KIND_LABELS[kind],
            state: "observed" as const,
            summary: `${descriptor.collectorKind} projection retained from an independent capture.`,
            window: `${descriptor.window.kind}: ${start} to ${end} · ${descriptor.window.completion}`,
            backend: `${descriptor.backend.kind} · ${descriptor.backend.source}`,
            quality: descriptor.quality.status === "reported"
                ? `Reported by ${descriptor.quality.source}`
                : "Collector quality unknown",
            retention: drilldown.status === "available"
                ? "Projection retained · specialist drilldown currently available"
                : `Projection retained · specialist drilldown ${drilldown.status}`
                    + (drilldown.unavailableReason ? ` (${drilldown.unavailableReason})` : ""),
            sensitive: descriptor.mayContainSensitiveData,
        };
    });

    const relations: InvestigationLensRelation[] = [];
    for (let left = 0; left < availableItems.length; left++) {
        for (let right = left + 1; right < availableItems.length; right++) {
            relations.push({
                left: availableItems[left].descriptor.kind,
                right: availableItems[right].descriptor.kind,
                relation: classifyInvestigationTemporalRelation(
                    availableItems[left].descriptor,
                    availableItems[right].descriptor,
                ),
            });
        }
    }

    const observed = availableItems.map(item =>
        `${KIND_LABELS[item.descriptor.kind]} evidence is retained as a bounded response copy; `
        + `${item.descriptor.window.kind} window and ${item.descriptor.backend.kind} backend are source-reported.`);
    const unavailable = INVESTIGATION_EVIDENCE_KINDS
        .filter(kind => !byKind.has(kind))
        .map(kind => `${KIND_LABELS[kind]} evidence is unavailable for this target generation.`);
    const inferred: string[] = [];
    let visualization: InvestigationLensVisualization | undefined;

    const threadPool = byKind.get("threadpool");
    const requests = byKind.get("requests");
    const threadPoolStarvation = threadPool ? positiveMetric(threadPool.projection, "Confirmed starvation adjustments") : undefined;
    const longRequests = requests ? positiveMetric(requests.projection, "Long-running") : undefined;
    if (threadPool && requests && threadPoolStarvation === true && longRequests === true) {
        const relation = classifyInvestigationTemporalRelation(threadPool.descriptor, requests.descriptor);
        const sameVerifiedTarget = sameVerifiedTargetIdentity(threadPool, requests);
        if (sameVerifiedTarget && (relation === "adjacent" || relation === "none")) {
            inferred.push("ThreadPool starvation evidence and long requests were observed in separate windows "
                + "of the same process. This corroborates a recurring symptom under the reproduced workload.");
            visualization = {
                title: "Next diagnostic decision",
                first: "Observed ThreadPool starvation adjustments",
                relation,
                second: "Observed long requests",
                nextDecision: "Repeat the workload with a more narrowly aligned measurement; do not assign causality.",
            };
        } else if (sameVerifiedTarget && relation === "reported-intersection") {
            inferred.push("ThreadPool starvation evidence and long requests were reported in intersecting wall-clock "
                + "windows, but independent captures do not establish observed overlap.");
            visualization = {
                title: "Next diagnostic decision",
                first: "Observed ThreadPool starvation adjustments",
                relation,
                second: "Observed long requests",
                nextDecision: "Use a controlled repeat or one collector-owned correlation surface before linking the symptoms.",
            };
        } else {
            inferred.push("Abstained: ThreadPool and request symptoms are present, but target identity or comparable "
                + "reported windows do not support a combined conclusion.");
        }
    } else if (threadPool || requests) {
        inferred.push("Abstained: retained ThreadPool/request evidence does not contain both a positive starvation "
            + "signal and positive long-request signal.");
    } else if (availableItems.length > 0) {
        inferred.push("Abstained: the retained subset supports specialist inspection, not a cross-evidence conclusion.");
    } else {
        inferred.push("Abstained: no evidence has been admitted. The lens never starts captures automatically.");
    }

    const headline = visualization
        ? "Recurring ThreadPool pressure and long-request symptoms need a controlled, better-aligned repeat."
        : availableItems.length === 0
            ? "No multi-evidence conclusion is available."
            : `${availableItems.length} of 7 evidence kinds are retained; no root-cause verdict is supported.`;

    return {
        headline,
        target,
        evidence,
        relations,
        observed,
        inferred,
        unavailable,
        limitations: [
            "Independent captures are never presented as observed overlap or causal evidence.",
            "EventPipe sessions can overlap only with startup/stop skew, independent quality and no shared readiness point.",
            "Windows kernel ETW captures are treated as sequential because one global kernel session gate spans capture lifetime.",
            "Linux perf CPU and off-CPU captures are treated as sequential while JIT-map ownership is shared.",
            "Request Activities remain exclusive with the extension live session and external DiagnosticSource coexistence is not established.",
            "Reported-intersection, adjacent, none and unknown describe reported windows only; none proves causality or continuous coverage.",
        ],
        actions: availableItems.map(item => ({
            kind: item.descriptor.kind,
            label: `Open ${KIND_LABELS[item.descriptor.kind]} workspace`,
        })),
        ...(visualization ? { visualization } : {}),
    };
}

function positiveMetric(projection: unknown, label: string): boolean | undefined {
    if (!isRecord(projection) || !Array.isArray(projection.metrics)) return undefined;
    const metric = projection.metrics.find(candidate => isRecord(candidate) && candidate.label === label);
    if (!isRecord(metric)) return undefined;
    return typeof metric.value === "number" && Number.isFinite(metric.value) ? metric.value > 0 : undefined;
}

function sameVerifiedTargetIdentity(left: InvestigationEvidenceItem, right: InvestigationEvidenceItem): boolean {
    return left.descriptor.target.identityStatus === "verified"
        && right.descriptor.target.identityStatus === "verified"
        && left.descriptor.target.processId === right.descriptor.target.processId
        && left.descriptor.target.processStartedAt === right.descriptor.target.processStartedAt;
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

export const INVESTIGATION_LENS_STYLES = `
    .investigation-headline { font-size:1.05rem; font-weight:600; margin:.6rem 0 .25rem; }
    .investigation-target, .investigation-disclosure { color:var(--vscode-descriptionForeground); }
    .investigation-grid { display:grid; grid-template-columns:minmax(0,1fr) minmax(0,1fr); gap:.65rem; }
    .investigation-card { border:1px solid var(--vscode-panel-border); border-radius:4px; padding:.65rem; min-width:0; }
    .investigation-card h3 { margin:.1rem 0 .45rem; }
    .investigation-card p, .investigation-card li { overflow-wrap:anywhere; }
    .investigation-state { font-weight:600; text-transform:capitalize; }
    .investigation-unavailable { color:var(--vscode-descriptionForeground); }
    .investigation-visual { display:grid; grid-template-columns:minmax(0,1fr) auto minmax(0,1fr); gap:.5rem; align-items:center; }
    .investigation-visual > * { min-width:0; overflow-wrap:anywhere; }
    .investigation-relation { border:1px solid var(--vscode-panel-border); border-radius:999px; padding:.25rem .45rem; text-align:center; }
    .investigation-actions { display:flex; gap:.4rem; flex-wrap:wrap; }
    @media (max-width:600px) {
      .investigation-grid, .investigation-visual { grid-template-columns:1fr; }
      .investigation-relation { border-radius:4px; }
      .investigation-actions { flex-direction:column; align-items:stretch; }
    }
    @media (forced-colors:active) {
      .investigation-card, .investigation-relation { border-color:CanvasText; }
    }
`;

export const INVESTIGATION_LENS_HTML = `
  <section id="resultInvestigation" class="result-tab-panel" role="tabpanel" aria-labelledby="resultTabInvestigation" hidden>
    <div id="investigationHeadline" class="investigation-headline" role="status">No multi-evidence conclusion is available.</div>
    <div id="investigationTarget" class="investigation-target"></div>
    <section id="investigationVisualization" class="investigation-card" aria-label="Decision-changing evidence relationship" hidden></section>
    <div class="investigation-grid">
      <section class="investigation-card" aria-labelledby="investigationEvidenceHeading">
        <h3 id="investigationEvidenceHeading">Evidence by kind</h3>
        <div id="investigationEvidence"></div>
      </section>
      <section class="investigation-card" aria-labelledby="investigationInterpretationHeading">
        <h3 id="investigationInterpretationHeading">Observed, inferred and unavailable</h3>
        <div id="investigationInterpretation"></div>
      </section>
    </div>
    <section class="investigation-card" aria-labelledby="investigationTimingHeading">
      <h3 id="investigationTimingHeading">Reported temporal relations</h3>
      <div id="investigationRelations"></div>
    </section>
    <section class="investigation-card" aria-labelledby="investigationLimitationsHeading">
      <h3 id="investigationLimitationsHeading">Skew, coexistence and interpretation limits</h3>
      <ul id="investigationLimitations"></ul>
    </section>
    <section class="investigation-card" aria-labelledby="investigationActionsHeading">
      <h3 id="investigationActionsHeading">Specialist workspaces</h3>
      <div id="investigationActions" class="investigation-actions"></div>
      <button id="clearInvestigation" type="button">Clear investigation lens</button>
    </section>
    <p class="investigation-disclosure">Memory-only bounded projection; not saved, exported or restored. Evidence can contain sensitive application data.</p>
  </section>
`;

export const INVESTIGATION_LENS_SCRIPT = `
    const investigationEvidence = document.getElementById('investigationEvidence');
    const investigationInterpretation = document.getElementById('investigationInterpretation');
    const investigationRelations = document.getElementById('investigationRelations');
    const investigationLimitations = document.getElementById('investigationLimitations');
    const investigationActions = document.getElementById('investigationActions');
    const investigationVisualization = document.getElementById('investigationVisualization');
    const investigationTab = document.getElementById('resultTabInvestigation');
    const investigationLabels = {
      cpu:'CPU', allocation:'Allocation', off_cpu:'Off-CPU', contention:'Contention',
      threadpool:'ThreadPool', gc:'GC', requests:'Requests'
    };
    function investigationList(title, values, state) {
      const section = document.createElement('section');
      const heading = document.createElement('h4');
      heading.textContent = title;
      section.appendChild(heading);
      const list = document.createElement('ul');
      list.dataset.state = state;
      for (const value of values) {
        const item = document.createElement('li');
        item.textContent = value;
        list.appendChild(item);
      }
      section.appendChild(list);
      return section;
    }
    function renderInvestigationLens(model) {
      investigationTab.hidden = false;
      document.getElementById('investigationHeadline').textContent = model.headline;
      document.getElementById('investigationTarget').textContent = model.target;
      investigationEvidence.replaceChildren();
      for (const evidence of model.evidence) {
        const details = document.createElement('details');
        const summary = document.createElement('summary');
        summary.textContent = evidence.label + ' · ' + evidence.state;
        summary.className = evidence.state === 'unavailable' ? 'investigation-unavailable' : 'investigation-state';
        details.appendChild(summary);
        for (const text of [evidence.summary, evidence.window, evidence.backend, evidence.quality, evidence.retention,
          evidence.sensitive ? 'May contain sensitive application data.' : 'No additional sensitivity flag reported.']) {
          const paragraph = document.createElement('p');
          paragraph.textContent = text;
          details.appendChild(paragraph);
        }
        investigationEvidence.appendChild(details);
      }
      investigationInterpretation.replaceChildren(
        investigationList('Observed', model.observed, 'observed'),
        investigationList('Inferred', model.inferred, 'inferred'),
        investigationList('Unavailable', model.unavailable, 'unavailable'));
      investigationRelations.replaceChildren();
      if (!model.relations.length) {
        investigationRelations.textContent = 'No comparable evidence pair is retained.';
      } else {
        const list = document.createElement('ul');
        for (const relation of model.relations) {
          const item = document.createElement('li');
          item.textContent = investigationLabels[relation.left] + ' / ' + investigationLabels[relation.right]
            + ': ' + relation.relation;
          list.appendChild(item);
        }
        investigationRelations.appendChild(list);
      }
      investigationLimitations.replaceChildren();
      for (const limitation of model.limitations) {
        const item = document.createElement('li');
        item.textContent = limitation;
        investigationLimitations.appendChild(item);
      }
      investigationActions.replaceChildren();
      for (const action of model.actions) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = action.label;
        button.dataset.investigationKind = action.kind;
        button.addEventListener('click', () => {
          selectResultTab('Workspace');
          setWorkspaceEvidence(action.kind);
          document.getElementById('workspaceEvidenceStatus').focus?.();
        });
        investigationActions.appendChild(button);
      }
      investigationVisualization.replaceChildren();
      investigationVisualization.hidden = !model.visualization;
      if (model.visualization) {
        const heading = document.createElement('h3');
        heading.textContent = model.visualization.title;
        const visual = document.createElement('div');
        visual.className = 'investigation-visual';
        for (const [text, className] of [
          [model.visualization.first, ''], [model.visualization.relation, 'investigation-relation'],
          [model.visualization.second, '']
        ]) {
          const node = document.createElement('div');
          node.textContent = text;
          node.className = className;
          visual.appendChild(node);
        }
        const next = document.createElement('p');
        next.textContent = model.visualization.nextDecision;
        investigationVisualization.append(heading, visual, next);
      }
    }
    document.getElementById('clearInvestigation').addEventListener('click',
      () => vscode.postMessage({ type:'clearInvestigationEvidence' }));
`;
