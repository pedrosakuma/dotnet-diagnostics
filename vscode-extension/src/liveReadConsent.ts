export type LiveReadOperation = "object" | "gcroot";
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

export const LIVE_READ_CONSENT_HTML = `<details id="liveReadConsents">
<summary>Live-read permissions: none remembered</summary>
<p>Remembered permissions apply separately to Object and GC root reads for this heap snapshot and CLI connection only.
Reads can suspend the target and expose sensitive data. A new heap capture or closed panel ends these grants.
Captures and exports still need their own acknowledgement.</p>
<button id="revokeLiveReads" type="button">Revoke remembered live reads</button>
<p id="liveReadConsentStatus" role="status"></p>
<ol id="liveReadConsentHistory"></ol>
</details>`;

export const LIVE_READ_CONSENT_SCRIPT = `
document.getElementById('revokeLiveReads').addEventListener('click', () => vscode.postMessage({ type: 'revokeLiveReads' }));
function renderLiveReadConsent(message) {
  const root = document.getElementById('liveReadConsents');
  const grants = Array.isArray(message.grants) ? message.grants : [];
  const names = grants.map(name => name === 'gcroot' ? 'GC root' : 'Object');
  root.querySelector('summary').textContent = 'Live-read permissions: ' + (names.length ? names.join(', ') + ' remembered' : 'none remembered');
  document.getElementById('liveReadConsentStatus').textContent = names.length
    ? 'Active for the current heap snapshot session only: ' + names.join(', ') : 'No remembered live-read permission.';
  const history = document.getElementById('liveReadConsentHistory');
  history.replaceChildren();
  for (const entry of (Array.isArray(message.history) ? message.history : []).slice(-30)) {
    const item = document.createElement('li');
    item.textContent = entry.timestamp + ' | ' + entry.operation + ' | ' + entry.decision;
    history.appendChild(item);
  }
}
`;
