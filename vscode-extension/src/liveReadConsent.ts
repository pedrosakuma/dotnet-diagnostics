export type LiveReadOperation = "object" | "gcroot" | "instances";
export type ConsentChoice = "once" | "session" | "cancel";

export class LiveReadConsent {
    private scope?: object;
    private generation = 0;
    private readonly grants = new Set<LiveReadOperation>();
    private readonly history: Array<{ operation: string; decision: string; timestamp: string }> = [];

    public constructor(private readonly publish: (state: Record<string, unknown>) => void) {}

    public clear(): void {
        this.scope = undefined;
        this.generation++;
        this.grants.clear();
        this.history.length = 0;
        this.emit();
    }

    public revoke(): void {
        this.generation++;
        this.grants.clear();
        this.record("all live reads", "revoked");
    }

    public async acknowledge(
        operation: LiveReadOperation,
        currentScope: () => object | undefined,
        prompt: () => Promise<ConsentChoice>,
    ): Promise<boolean> {
        const scope = currentScope();
        if (!scope) return false;
        if (this.scope !== scope) {
            this.clear();
            this.scope = scope;
        }
        if (this.grants.has(operation)) {
            this.record(operation, "session grant used");
            return true;
        }
        const generation = this.generation;
        const choice = await prompt();
        if (generation !== this.generation || currentScope() !== scope) {
            this.record(operation, "cancelled: context changed or revoked");
            return false;
        }
        if (choice === "session") this.grants.add(operation);
        this.record(operation, choice === "session" ? "allowed for this snapshot session"
            : choice === "once" ? "allowed once" : "declined");
        return choice !== "cancel";
    }

    private record(operation: string, decision: string): void {
        this.history.push({ operation, decision, timestamp: new Date().toISOString() });
        if (this.history.length > 30) this.history.shift();
        this.emit();
    }

    private emit(): void {
        this.publish({ type: "liveReadConsent", grants: [...this.grants], history: [...this.history] });
    }
}

export const LIVE_READ_CONSENT_HTML = `<section id="liveReadConsents" class="live-read-permissions" aria-label="Live-read permissions">
  <div class="live-read-permission-bar">
    <p id="liveReadConsentStatus" role="status" aria-live="polite">Live target-read permissions: none remembered.</p>
    <button id="revokeLiveReads" type="button" aria-disabled="true" title="No remembered live-read permission.">Revoke remembered live reads</button>
  </div>
  <p class="live-read-permission-scope">Remembered grants apply separately to live type-instance, Object and GC root reads for this snapshot and CLI connection only. A live read can suspend the target and expose sensitive data; dump reads are offline, and navigating an already-loaded result is local. New heap captures and closed panels end these grants. Captures and exports need separate acknowledgement. Permission history retains the latest 30 decisions.</p>
  <details>
    <summary>Live-read permission history</summary>
    <ol id="liveReadConsentHistory"></ol>
  </details>
</section>`;

export const LIVE_READ_CONSENT_STYLES = `
.live-read-permissions{margin:.5rem 0}
.live-read-permission-bar{position:sticky;top:3rem;z-index:1;display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:.45rem;padding:.35rem 0;background:var(--vscode-editor-background);border-block:1px solid var(--vscode-panel-border)}
.live-read-permission-bar p{min-width:0;margin:0;overflow-wrap:anywhere}
.live-read-permission-bar button{min-height:2rem}
.live-read-permission-scope{margin:.25rem 0;color:var(--vscode-descriptionForeground);overflow-wrap:anywhere}
.live-read-permissions details{margin:.3rem 0}
.live-read-permissions li{overflow-wrap:anywhere}
@media(max-width:480px){.live-read-permission-bar{align-items:stretch}.live-read-permission-bar button{width:100%}}
`;

export const LIVE_READ_CONSENT_SCRIPT = `
document.getElementById('revokeLiveReads').addEventListener('click', () => vscode.postMessage({ type: 'revokeLiveReads' }));
function renderLiveReadConsent(message) {
  const root = document.getElementById('liveReadConsents');
  const grants = Array.isArray(message.grants) ? message.grants : [];
  const names = grants.map(name => name === 'gcroot' ? 'GC root' : name === 'instances' ? 'Type instances' : 'Object');
  const revoke = document.getElementById('revokeLiveReads');
  revoke.setAttribute('aria-disabled', String(!grants.length));
  revoke.title = grants.length ? '' : 'No remembered live-read permission.';
  document.getElementById('liveReadConsentStatus').textContent = names.length
    ? 'Live target-read permissions for this heap snapshot session: ' + names.join(', ') + ' remembered.'
    : 'Live target-read permissions: none remembered.';
  const history = document.getElementById('liveReadConsentHistory');
  history.replaceChildren();
  for (const entry of (Array.isArray(message.history) ? message.history : []).slice(-30)) {
    const item = document.createElement('li');
    item.textContent = entry.timestamp + ' | ' + entry.operation + ' | ' + entry.decision;
    history.appendChild(item);
  }
}
`;
