import { SingleFlightGuard, isQueryResult, type QueryResult } from "./protocol";

/**
 * Address navigation between heap drilldown views (issue #1168).
 *
 * Addresses are exact `0x` hexadecimal strings at every hop (CLI → host → webview → host → CLI).
 * Nothing in this module (or in the webview script it emits) converts an address to a JavaScript
 * number: a 64-bit address above 2^53 would silently lose precision.
 */

/** Views a webview may navigate to by address; each maps to a CLI `query` view that accepts `address`. */
export const NAVIGATION_VIEWS = ["object", "gcroot", "static-fields"] as const;
export type NavigationView = (typeof NAVIGATION_VIEWS)[number];

/** Views that read from the target / dump on demand and therefore need the High-risk acknowledgement. */
const READ_VIEWS: ReadonlySet<string> = new Set(["object", "gcroot"]);

/** Bounded history depth shared by the webview (entries and their cached results). */
export const MAX_NAVIGATION_HISTORY = 20;

const HEX_ADDRESS = /^0x[0-9a-fA-F]{1,16}$/;

/**
 * Validates an address received from a webview. Accepts only a string `0x` followed by 1–16 hex
 * digits that is not zero; returns it lower-cased, or `undefined` (numbers, decimal strings and
 * padded text are all rejected).
 */
export function parseHexAddress(value: unknown): string | undefined {
    if (typeof value !== "string" || !HEX_ADDRESS.test(value) || !/[1-9a-fA-F]/.test(value.slice(2))) {
        return undefined;
    }
    return value.toLowerCase();
}

export function isNavigationView(value: unknown): value is NavigationView {
    return typeof value === "string" && (NAVIGATION_VIEWS as readonly string[]).includes(value);
}

/** Everything the navigation handler needs from a panel controller; keeps this module free of `vscode`. */
export interface AddressNavigationHost {
    /** Handle of the current heap snapshot, or `undefined` when none is retained / its connection ended. */
    currentHeapHandle(): string | undefined;
    /** Asks the user to acknowledge a High-risk read; resolves `false` when declined. */
    acknowledge(view: "object" | "gcroot", address: string): Promise<boolean>;
    acknowledgeSensitiveValues(address: string): Promise<boolean>;
    query(handle: string, view: NavigationView, address: string, acknowledgeRisk: boolean, includeSensitiveValues?: boolean,
        arrayStartIndex?: number, arrayCount?: number): Promise<QueryResult>;
    post(message: Record<string, unknown>): void;
    copy(address: string): Promise<void>;
    log(text: string): void;
}

/** Validates and serves `copyAddress` / `navigateAddress` messages from one webview. */
export class AddressNavigationController {
    private readonly guard = new SingleFlightGuard();

    public constructor(private readonly host: AddressNavigationHost) {}

    /** Returns `true` when `message` was a navigation message (valid or not), so callers can stop dispatching. */
    public async handle(message: Record<string, unknown>): Promise<boolean> {
        if (message.type === "copyAddress") {
            const address = parseHexAddress(message.address);
            if (address !== undefined) {
                await this.host.copy(address);
            }
            return true;
        }
        if (message.type !== "navigateAddress") {
            return false;
        }

        const address = parseHexAddress(message.address);
        const view = message.view;
        const hasRange = message.arrayStartIndex !== undefined || message.arrayCount !== undefined;
        const start = message.arrayStartIndex === undefined ? 0 : message.arrayStartIndex;
        const count = message.arrayCount === undefined ? 8 : message.arrayCount;
        if (message.kind !== "heap" || address === undefined || !isNavigationView(view)
            || (message.includeSensitiveValues !== undefined && typeof message.includeSensitiveValues !== "boolean")
            || (message.includeSensitiveValues === true && view !== "object")
            || (hasRange && (view !== "object" || typeof start !== "number" || !Number.isInteger(start)
                || start < 0 || start > 2147483647 || typeof count !== "number" || !Number.isInteger(count) || count < 1 || count > 8))) {
            this.host.log("Ignored an invalid address navigation request from the webview.");
            return true;
        }

        const navigationId = typeof message.navigationId === "number" && Number.isSafeInteger(message.navigationId)
            && message.navigationId > 0 ? message.navigationId : undefined;
        const outcome = await this.guard.run(() => this.navigate(view, address, navigationId, message.includeSensitiveValues === true,
            hasRange && typeof start === "number" ? start : undefined, hasRange && typeof count === "number" ? count : undefined));
        if (!outcome.started) {
            this.fail(view, address, "Another address lookup is still running.", navigationId);
        }
        return true;
    }

    private async navigate(view: NavigationView, address: string, navigationId?: number, includeSensitiveValues = false,
        arrayStartIndex?: number, arrayCount?: number): Promise<void> {
        const handle = this.host.currentHeapHandle();
        if (handle === undefined) {
            this.fail(view, address, "No heap snapshot with a retained handle is available. Capture a heap snapshot first.", navigationId);
            return;
        }
        const needsAcknowledgement = READ_VIEWS.has(view);
        if (includeSensitiveValues && !await this.host.acknowledgeSensitiveValues(address)) {
            this.fail(view, address, "Cancelled: revealing sensitive values was declined.", navigationId);
            return;
        }
        if (needsAcknowledgement && !await this.host.acknowledge(view as "object" | "gcroot", address)) {
            this.fail(view, address, "Cancelled: the risk acknowledgement was declined.", navigationId);
            return;
        }
        if (this.host.currentHeapHandle() !== handle) {
            this.fail(view, address, "The heap context changed during acknowledgement; retry on the current snapshot.", navigationId);
            return;
        }
        try {
            const result = await this.host.query(handle, view, address, needsAcknowledgement, includeSensitiveValues, arrayStartIndex, arrayCount);
            if (!isQueryResult(result) || result.view !== view) {
                this.fail(view, address, "The CLI returned an unexpected response.", navigationId);
                return;
            }
            if (includeSensitiveValues && result.sensitiveValuesIncluded !== true) {
                this.fail(view, address, "This CLI did not confirm sensitive-value disclosure. Use a CLI build supporting per-object value reveal.", navigationId);
                return;
            }
            if (arrayStartIndex !== undefined) {
                const object = result.objectDetails as Record<string, unknown> | undefined;
                if (object?.isArray !== true || object.arrayStartIndex !== arrayStartIndex || object.arrayRequestedCount !== arrayCount) {
                    this.fail(view, address, "The CLI did not confirm the requested array range. Use a CLI build supporting array ranges.", navigationId);
                    return;
                }
            }
            this.host.post({ type: "navigation", view, address, result,
                ...(includeSensitiveValues ? { sensitiveValues: true } : {}), ...(navigationId ? { navigationId } : {}) });
        } catch (error) {
            this.fail(view, address, error instanceof Error ? error.message : String(error), navigationId);
        }
    }

    private fail(view: NavigationView, address: string, message: string, navigationId?: number): void {
        this.host.post({ type: "navigationError", view, address, message, ...(navigationId ? { navigationId } : {}) });
        this.host.log(`Address navigation '${view}' failed: ${message}`);
    }
}

/** Static markup for the breadcrumb/back/forward bar and its single result container. */
export const ADDRESS_NAVIGATION_HTML = `<section id="addressNavigation" class="address-navigation" aria-label="Address navigation" hidden>
        <div class="address-nav-bar">
          <strong>Object inspector</strong>
          <button id="addressNavClose" type="button" aria-label="Close object inspector">Close</button>
          <button id="addressNavBack" type="button" aria-label="Back" title="Back" disabled>&larr;</button>
          <button id="addressNavForward" type="button" aria-label="Forward" title="Forward" disabled>&rarr;</button>
          <nav aria-label="Address history"><ol id="addressBreadcrumb" class="address-breadcrumb"></ol></nav>
        </div>
        <div id="addressNavActions" class="address-nav-actions"></div>
        <p>Live Object / GC root lookups read the process again and may suspend it. Addresses can change after GC.
        Dump lookups read the dump; history reuses cached results.</p>
        <div id="addressNavStatus" class="address-nav-status" role="status"></div>
        <div id="addressQueryViews" class="query-views"></div>
      </section>`;

export const ADDRESS_NAVIGATION_STYLES = `
    .address-navigation { position:fixed; top:0; right:0; bottom:0; width:min(36rem, calc(100vw - 2rem)); overflow:auto; z-index:20; background:var(--vscode-editor-background); border:1px solid var(--vscode-focusBorder); padding:.5rem .7rem; box-shadow:0 0 1rem var(--vscode-widget-shadow); }
    .address-navigation[hidden] { display:none; }
    .address-nav-bar { display:flex; align-items:center; gap:.4rem; flex-wrap:wrap; }
    .address-breadcrumb { display:flex; flex-wrap:wrap; gap:.25rem; list-style:none; margin:0; padding:0; }
    .address-breadcrumb button[aria-current="step"] { font-weight:700; }
    .address-nav-actions { display:flex; gap:.4rem; margin:.4rem 0; }
    .address-nav-status { color:var(--vscode-descriptionForeground); font-size:.85rem; }
    .object-value-actions { margin:.4rem 0; }
    .object-value-actions button { white-space:normal; text-align:left; }
    .address-value { display:inline-flex; align-items:center; gap:.35rem; flex-wrap:wrap; }
    button.address-link { background:none; border:none; padding:0; color:var(--vscode-textLink-foreground); cursor:pointer; font-family:var(--vscode-editor-font-family); text-decoration:underline; }
    button.address-copy { padding:0 .35rem; font-size:.75rem; }
`;

/**
 * Webview-side navigation, concatenated after the query-view renderer. Plain JavaScript on
 * purpose: it is emitted verbatim into the page. History is a bounded list (see
 * `MAX_NAVIGATION_HISTORY`); each entry caches only its own already-bounded result, so back/forward
 * never re-reads a live target, and only the current entry is ever in the DOM.
 */
export const ADDRESS_NAVIGATION_SCRIPT = `
const MAX_ADDRESS_HISTORY = ${MAX_NAVIGATION_HISTORY};
const addressLinkContainers = new Set(['heapQueryViews', 'addressQueryViews']);
const addressViewLabels = { 'object': 'Object', 'gcroot': 'GC root path', 'static-fields': 'Referencing statics' };
const addressNavigationState = { entries: [], index: -1, sequence: 0 };
let addressInspectorClosed = false;
let addressInspectorOpener;
let revealedObject;
let addressLiveQueriesAvailable = true;

function isValidAddress(value) {
  return typeof value === 'string' && /^0x[0-9a-fA-F]{1,16}$/.test(value) && /[1-9a-fA-F]/.test(value.slice(2));
}

function postAddressMessage(message) {
  if (typeof vscode !== 'undefined' && vscode) vscode.postMessage(message);
}

function setAddressLiveQueriesAvailable(available) {
  addressLiveQueriesAvailable = available;
  for (const containerId of ['heapQueryViews', 'addressQueryViews', 'addressNavigation']) {
    const container = document.getElementById(containerId);
    if (!container) continue;
    for (const control of [...container.querySelectorAll('button'), ...container.querySelectorAll('input')]) {
      const addressLink = control.classList.contains('address-link');
      const followUp = control.hasAttribute('data-live-follow-up');
      if (!addressLink && !followUp) continue;
      control.disabled = !available;
      control.title = available
        ? addressLink ? 'Inspect the object at ' + control.textContent : ''
        : 'Live heap connection unavailable; this snapshot is display-only.';
    }
  }
  if (!available) {
    const status = document.getElementById('addressNavStatus');
    if (status) status.textContent = 'Display-only: the owning CLI connection is closed. Live object and GC-root reads are disabled.';
  }
}

function appendAddressValue(parent, value, containerId) {
  if (!isValidAddress(value)) {
    // Numbers (including unsafe JavaScript integers) are flagged by formatAddress and never linked.
    parent.textContent = formatAddress(value);
    return;
  }
  const wrapper = document.createElement('span');
  wrapper.className = 'address-value';
  if (addressLinkContainers.has(containerId)) {
    const link = document.createElement('button');
    link.type = 'button';
    link.className = 'address-link';
    link.textContent = value;
    link.disabled = !addressLiveQueriesAvailable;
    link.title = addressLiveQueriesAvailable ? 'Inspect the object at ' + value
      : 'Live heap connection unavailable; this snapshot is display-only.';
    link.setAttribute('aria-label', 'Inspect the object at ' + value);
    link.addEventListener('click', () => navigateToAddress(value, 'object'));
    wrapper.appendChild(link);
  } else {
    const code = document.createElement('code');
    code.textContent = value;
    wrapper.appendChild(code);
  }
  const copy = document.createElement('button');
  copy.type = 'button';
  copy.className = 'address-copy';
  copy.textContent = 'Copy';
  copy.title = 'Copy address ' + value;
  copy.setAttribute('aria-label', 'Copy address ' + value);
  copy.addEventListener('click', () => postAddressMessage({ type: 'copyAddress', address: value }));
  wrapper.appendChild(copy);
  parent.appendChild(wrapper);
}

function navigateToAddress(address, view, includeSensitiveValues = false, arrayStartIndex, arrayCount) {
  if (!isValidAddress(address) || !addressViewLabels[view]) return;
  if (!addressLiveQueriesAvailable) {
    const status = document.getElementById('addressNavStatus');
    if (status) status.textContent = 'The owning CLI connection is closed. This snapshot is display-only; live follow-up reads are unavailable.';
    return;
  }
  address = address.toLowerCase();
  const state = addressNavigationState;
  revealedObject = undefined;
  const inspector = document.getElementById('addressNavigation');
  if (!inspector || inspector.hidden) addressInspectorOpener = document.activeElement;
  addressInspectorClosed = false;
  state.entries.length = state.index + 1;
  const navigationId = ++state.sequence;
  state.entries.push({ address: address, view: view, status: 'loading', navigationId, includeSensitiveValues, arrayStartIndex, arrayCount });
  if (state.entries.length > MAX_ADDRESS_HISTORY) state.entries.splice(0, state.entries.length - MAX_ADDRESS_HISTORY);
  state.index = state.entries.length - 1;
  postAddressMessage({ type: 'navigateAddress', kind: 'heap', view: view, address: address, navigationId,
    ...(includeSensitiveValues ? { includeSensitiveValues: true } : {}),
    ...(arrayStartIndex !== undefined ? { arrayStartIndex, arrayCount: arrayCount ?? 8 } : {}) });
  renderAddressNavigation();
  document.getElementById('addressNavClose')?.focus();
}

function moveAddressHistory(index) {
  const state = addressNavigationState;
  if (index < 0 || index >= state.entries.length || index === state.index) return;
  revealedObject = undefined;
  state.index = index;
  renderAddressNavigation();
}

function resetAddressNavigation() {
  revealedObject = undefined;
  addressNavigationState.entries.length = 0;
  addressNavigationState.index = -1;
  renderAddressNavigation();
}

function handleAddressNavigationMessage(message) {
  if (message.type !== 'navigation' && message.type !== 'navigationError') return false;
  const entries = addressNavigationState.entries;
  for (let i = entries.length - 1; i >= 0; i--) {
    const entry = entries[i];
    if (entry.status === 'loading' && entry.view === message.view && entry.address === message.address
        && entry.navigationId === message.navigationId) {
      if (message.type === 'navigation') {
        if (message.sensitiveValues === true || entry.includeSensitiveValues) {
          entry.status = 'hidden';
          if (i === addressNavigationState.index && !addressInspectorClosed)
            revealedObject = { navigationId: entry.navigationId, result: message.result };
        } else {
          entry.status = 'ready';
          entry.result = message.result;
        }
      } else {
        entry.status = 'error';
        entry.error = String(message.message || 'The lookup failed.');
      }
      break;
    }
  }
  renderAddressNavigation();
  return true;
}

function renderAddressNavigation() {
  const bar = document.getElementById('addressNavigation');
  if (!bar) return;
  const state = addressNavigationState;
  const entry = state.entries[state.index];
  if (entry) postAddressMessage({ type: 'navigationSelection', view: entry.view, address: entry.address, navigationId: entry.navigationId,
    ready: entry.status === 'ready',
    history: state.entries.map(item => ({ view: item.view, address: item.address, navigationId: item.navigationId })) });
  bar.hidden = state.entries.length === 0 || addressInspectorClosed;
  const back = document.getElementById('addressNavBack');
  const forward = document.getElementById('addressNavForward');
  if (back) back.disabled = state.index <= 0;
  if (forward) forward.disabled = state.index >= state.entries.length - 1;

  const breadcrumb = document.getElementById('addressBreadcrumb');
  if (breadcrumb) {
    breadcrumb.replaceChildren();
    state.entries.forEach((item, index) => {
      const crumb = document.createElement('li');
      const button = document.createElement('button');
      button.type = 'button';
      button.textContent = addressViewLabels[item.view] + ' ' + item.address
        + (item.arrayStartIndex !== undefined ? ' [from ' + item.arrayStartIndex + ']' : '');
      if (index === state.index) button.setAttribute('aria-current', 'step');
      button.addEventListener('click', () => moveAddressHistory(index));
      crumb.appendChild(button);
      breadcrumb.appendChild(crumb);
    });
  }

  const actions = document.getElementById('addressNavActions');
  if (actions) {
    actions.replaceChildren();
    if (entry) {
      for (const view of ['object', 'gcroot', 'static-fields']) {
        if (view === entry.view) continue;
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('data-live-follow-up', 'true');
        button.textContent = addressViewLabels[view];
        button.disabled = !addressLiveQueriesAvailable;
        button.addEventListener('click', () => navigateToAddress(entry.address, view));
        actions.appendChild(button);
      }
    }
  }

  const status = document.getElementById('addressNavStatus');
  if (status) {
    const detail = !entry ? '' : entry.status === 'loading'
      ? 'Looking up ' + entry.address + '…'
      : entry.status === 'error' ? '' : addressViewLabels[entry.view] + ' for ' + entry.address;
    status.textContent = !addressLiveQueriesAvailable && entry
      ? 'Display-only: the owning CLI connection is closed. ' + detail
      : detail;
  }

  const results = document.getElementById('addressQueryViews');
  if (results) {
    results.replaceChildren();
    if (entry && revealedObject?.navigationId === entry.navigationId) {
      const warning = document.createElement('p');
      warning.textContent = 'Sensitive previews revealed for this object only (strings up to 256 characters; arrays up to 8 elements; fields up to 256 entries and value-type depth 3). Field and element string previews may be shortened at 256 characters. Not cached in navigation history. Closing or navigating away hides them. This read may differ from the original snapshot.';
      results.appendChild(warning);
      renderQueryView('addressQueryViews', 'object', revealedObject.result, false);
      const details = document.getElementById('addressQueryViews-object');
      if (details) details.open = true;
    } else if (entry && entry.status === 'hidden') {
      const note = document.createElement('p');
      note.textContent = 'Sensitive previews are not cached in history. Reveal again to read this object.';
      results.appendChild(note);
    } else if (entry && entry.status === 'ready') {
      renderQueryView('addressQueryViews', entry.view, entry.result, false);
      const details = document.getElementById('addressQueryViews-' + entry.view);
      if (details) details.open = true;
    } else if (entry && entry.status === 'error') {
      renderQueryView('addressQueryViews', entry.view, entry.error, true);
      const details = document.getElementById('addressQueryViews-' + entry.view);
      if (details) details.open = true;
    }
    if (entry?.view === 'object') {
      const target = results.querySelector('.object-value-actions') || results;
      const reveal = document.createElement('button');
      reveal.type = 'button';
      const revealed = revealedObject?.navigationId === entry.navigationId;
      reveal.textContent = revealed ? 'Hide values' : 'Reveal values of this object';
      reveal.title = 'Applies to string, field and array previews of this object, within read limits. Requires separate sensitive-data consent.';
      reveal.setAttribute('data-live-follow-up', 'true');
      reveal.disabled = !addressLiveQueriesAvailable || entry.status === 'loading';
      reveal.addEventListener('click', () => {
        if (revealed) { revealedObject = undefined; renderAddressNavigation(); }
        else navigateToAddress(entry.address, 'object', true, entry.arrayStartIndex, entry.arrayCount);
      });
      target.appendChild(reveal);
    }
  }
}

function initAddressNavigation() {
  const close = document.getElementById('addressNavClose');
  const dismiss = () => {
    revealedObject = undefined;
    document.getElementById('addressQueryViews')?.replaceChildren();
    addressInspectorClosed = true;
    const bar = document.getElementById('addressNavigation');
    if (bar) bar.hidden = true;
    addressInspectorOpener?.focus();
  };
  if (close) close.addEventListener('click', dismiss);
  document.getElementById('addressNavigation')?.addEventListener('keydown', event => {
    if (event.key === 'Escape') { event.preventDefault(); dismiss(); }
  });
  const back = document.getElementById('addressNavBack');
  const forward = document.getElementById('addressNavForward');
  if (back) back.addEventListener('click', () => moveAddressHistory(addressNavigationState.index - 1));
  if (forward) forward.addEventListener('click', () => moveAddressHistory(addressNavigationState.index + 1));
}
initAddressNavigation();
`;
