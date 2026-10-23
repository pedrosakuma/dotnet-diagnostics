import { ADDRESS_NAVIGATION_SCRIPT, ADDRESS_NAVIGATION_STYLES } from "./addressNavigation";
import { EVIDENCE_NAVIGATION_SCRIPT, EVIDENCE_NAVIGATION_STYLES } from "./evidenceNavigation";
import { SNAPSHOT_NAVIGATION_SCRIPT } from "./snapshotNavigation";
import { EVIDENCE_VIEW_SWITCHER_SCRIPT, EVIDENCE_VIEW_SWITCHER_STYLES } from "./evidenceViewSwitcher";

declare function enhanceEvidenceTables(parent: HTMLElement): void;
declare function renderExceptionNavigation(parent: HTMLElement, events: Array<Record<string, unknown>>): void;
declare function applyHeapTypeFocus(): void;
declare function updateThreadNavigation(view: string, result: Record<string, unknown>): void;
declare function navigateThreadFromGroup(id: number, returnFocus?: HTMLElement): void;
declare function navigateThreadTo(id: number, returnPanel?: string, returnFocus?: HTMLElement): void;
declare function updateEvidenceViewSwitcher(container: HTMLElement): void;
declare let addressLiveQueriesAvailable: boolean;

// Provided by the address-navigation script that is emitted next to this renderer in the webview.
declare function appendAddressValue(parent: HTMLElement, value: unknown, containerId: string): void;
declare function navigateToAddress(address: string, view: string, includeSensitiveValues?: boolean, arrayStartIndex?: number, arrayCount?: number): void;

const queryViewLabels: Record<string, string> = {
    "roots-by-kind": "GC roots by kind",
    "finalizer-queue": "Finalizer queue",
    fragmentation: "Heap fragmentation",
    gchandles: "GC handles",
    async: "Async operations",
    timers: "Timers and tasks",
    alc: "AssemblyLoadContexts",
    "static-fields": "Static fields",
    "delegate-targets": "Delegate targets",
    "retention-paths": "Retention paths",
    "retained-exceptions": "Retained exceptions",
    object: "Object",
    gcroot: "GC root path",
    deadlocks: "Deadlock candidates",
    "unique-stacks": "Grouped stacks",
    "wait-chains": "Wait chains",
    threadpool: "Thread pool",
    "thread-statics": "Thread statics",
    exceptions: "Captured exceptions",
};

function renderQueryView(containerId: string, view: string, resultOrMessage: unknown, isError: boolean): void {
    const container = document.getElementById(containerId);
    if (!container) return;

    const labels = queryViewLabels;
    const label = labels[view] || view;
    const result = resultOrMessage && typeof resultOrMessage === "object"
        ? resultOrMessage as Record<string, unknown>
        : {};
    const elementId = containerId + "-" + view;
    let details = document.getElementById(elementId) as HTMLDetailsElement | null;
    if (!details) {
        details = document.createElement("details");
        details.id = elementId;
        details.className = "query-view";
        const summary = document.createElement("summary");
        summary.textContent = label;
        const content = document.createElement("div");
        content.className = "query-view-content";
        details.append(summary, content);
        container.appendChild(details);
    }

    const summary = details.querySelector("summary");
    const content = details.querySelector(".query-view-content") as HTMLDivElement | null;
    if (!summary || !content) return;

    details.classList.toggle("query-view-error", isError);
    const hasCandidates = view === "deadlocks" && !isError && Array.isArray(result.deadlocks) && result.deadlocks.length > 0;
    details.classList.toggle("query-view-deadlocks", hasCandidates);
    if (hasCandidates) details.open = true;
    summary.textContent = isError ? label + " (unavailable)" : label;
    content.replaceChildren();
    if (isError) {
        appendMessage(content, String(resultOrMessage), "query-view-error-message");
        if (containerId === "threadQueryViews") updateThreadNavigation(view, {});
        if (containerId === "heapQueryViews" && view === "retention-paths") applyHeapTypeFocus();
        if (containerId === "heapQueryViews" || containerId === "threadQueryViews") updateEvidenceViewSwitcher(container);
        return;
    }

    const metadata = [];
    if (typeof result.origin === "string") metadata.push(result.origin);
    if (typeof result.processId === "number") metadata.push("PID " + result.processId);
    if (typeof result.capturedAt === "string") metadata.push(new Date(result.capturedAt).toLocaleString());
    if (metadata.length) appendMessage(content, metadata.join(" · "), "query-view-provenance");

    const metrics = (items: Array<[string, unknown]>, parent: HTMLElement = content) => {
        const populated = items.filter(([, value]) => value !== undefined && value !== null);
        if (!populated.length) return;
        const grid = document.createElement("div");
        grid.className = "query-view-metrics";
        for (const [name, value] of populated) {
            const card = document.createElement("div");
            card.className = "query-view-metric";
            const labelNode = document.createElement("span");
            labelNode.className = "query-view-metric-label";
            labelNode.textContent = name;
            const valueNode = document.createElement("strong");
            valueNode.textContent = formatValue(value);
            card.append(labelNode, valueNode);
            grid.appendChild(card);
        }
        parent.appendChild(grid);
        return grid;
    };

    // Columns named "... address" hold exact hex strings: linked/copyable, never reformatted as numbers.
    const fillCell = (cell: HTMLElement, columnName: string, value: unknown) => {
        if (/address$/i.test(columnName)) {
            appendAddressValue(cell, value, containerId);
        } else if (containerId === "threadQueryViews" && document.getElementById("threadExplorer")
            && /^(?:managed|waiting|owner) thread$/i.test(columnName)
            && typeof value === "number" && Number.isSafeInteger(value) && value >= 0) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "heap-type-link";
            button.textContent = "Thread " + value;
            button.addEventListener("click", () => navigateThreadTo(value,
                containerId === "threadQueryViews" ? "threadQueryViews-" + view : undefined, button));
            cell.appendChild(button);
        } else {
            cell.textContent = formatValue(value);
        }
    };

    const table = (
        title: string,
        items: unknown,
        columns: Array<[string, (row: Record<string, unknown>) => unknown]>,
        emptyText: string,
        parent: HTMLElement = content,
    ) => {
        const section = document.createElement("section");
        section.className = "query-view-section";
        const heading = document.createElement("h4");
        heading.textContent = title;
        section.appendChild(heading);
        const rows = Array.isArray(items) ? items : [];
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "query-view-empty";
            empty.textContent = emptyText;
            section.appendChild(empty);
            parent.appendChild(section);
            return;
        }
        const wrapper = document.createElement("div");
        wrapper.className = "query-view-table-wrap";
        const tableNode = document.createElement("table");
        tableNode.className = "query-view-table";
        const thead = document.createElement("thead");
        const headerRow = document.createElement("tr");
        for (const [columnName] of columns) {
            const header = document.createElement("th");
            header.scope = "col";
            header.textContent = columnName;
            headerRow.appendChild(header);
        }
        thead.appendChild(headerRow);
        tableNode.appendChild(thead);
        const tbody = document.createElement("tbody");
        for (const item of rows) {
            const rowObject = item && typeof item === "object"
                ? item as Record<string, unknown>
                : {};
            const row = document.createElement("tr");
            for (const [columnName, getValue] of columns) {
                const cell = document.createElement("td");
                fillCell(cell, columnName, getValue(rowObject));
                row.appendChild(cell);
            }
            tbody.appendChild(row);
        }
        tableNode.appendChild(tbody);
        wrapper.appendChild(tableNode);
        section.appendChild(wrapper);
        parent.appendChild(section);
    };

    const nested = (
        title: string,
        items: unknown,
        headingFor: (item: Record<string, unknown>, index: number) => string,
        renderBody: (body: HTMLDivElement, item: Record<string, unknown>) => void,
        emptyText: string,
    ) => {
        const section = document.createElement("section");
        section.className = "query-view-section";
        const heading = document.createElement("h4");
        heading.textContent = title;
        section.appendChild(heading);
        const rows = Array.isArray(items) ? items : [];
        if (!rows.length) {
            const empty = document.createElement("p");
            empty.className = "query-view-empty";
            empty.textContent = emptyText;
            section.appendChild(empty);
            content.appendChild(section);
            return;
        }
        rows.forEach((item, index) => {
            const rowObject = item && typeof item === "object"
                ? item as Record<string, unknown>
                : {};
            const row = document.createElement("details");
            row.className = "query-view-nested";
            const rowSummary = document.createElement("summary");
            rowSummary.textContent = headingFor(rowObject, index);
            const body = document.createElement("div");
            body.className = "query-view-nested-body";
            renderBody(body, rowObject);
            row.append(rowSummary, body);
            section.appendChild(row);
        });
        content.appendChild(section);
    };

    const notes = (title: string, values: unknown) => {
        if (!Array.isArray(values) || values.length === 0) return;
        const section = document.createElement("section");
        section.className = "query-view-notes";
        const heading = document.createElement("h4");
        heading.textContent = title;
        const list = document.createElement("ul");
        for (const value of values) {
            const item = document.createElement("li");
            item.textContent = formatValue(value);
            list.appendChild(item);
        }
        section.append(heading, list);
        content.appendChild(section);
    };

    const fieldTable = (
        parent: HTMLElement,
        title: string,
        rows: unknown,
        columns: Array<[string, (row: Record<string, unknown>) => unknown]>,
        emptyText: string,
    ) => {
        const section = document.createElement("section");
        section.className = "query-view-section";
        const heading = document.createElement("h4");
        heading.textContent = title;
        section.appendChild(heading);
        const values = Array.isArray(rows) ? rows : [];
        if (!values.length) {
            const empty = document.createElement("p");
            empty.className = "query-view-empty";
            empty.textContent = emptyText;
            section.appendChild(empty);
        } else {
            const wrapper = document.createElement("div");
            wrapper.className = "query-view-table-wrap";
            const tableNode = document.createElement("table");
            tableNode.className = "query-view-table";
            const thead = document.createElement("thead");
            const headerRow = document.createElement("tr");
            for (const [columnName] of columns) {
                const header = document.createElement("th");
                header.scope = "col";
                header.textContent = columnName;
                headerRow.appendChild(header);
            }
            thead.appendChild(headerRow);
            tableNode.appendChild(thead);
            const tbody = document.createElement("tbody");
            for (const value of values) {
                const rowObject = value && typeof value === "object"
                    ? value as Record<string, unknown>
                    : {};
                const row = document.createElement("tr");
                for (const [columnName, getValue] of columns) {
                    const cell = document.createElement("td");
                    fillCell(cell, columnName, getValue(rowObject));
                    row.appendChild(cell);
                }
                tbody.appendChild(row);
            }
            tableNode.appendChild(tbody);
            wrapper.appendChild(tableNode);
            section.appendChild(wrapper);
        }
        parent.appendChild(section);
    };

    const renderChain = (chain: unknown) => Array.isArray(chain)
        ? chain.map(frame => {
            const item = frame && typeof frame === "object" ? frame as Record<string, unknown> : {};
            const kind = item.rootKind ? " [" + formatValue(item.rootKind) + "]" : "";
            return formatValue(item.typeFullName) + kind;
        }).join("  →  ")
        : "No chain was captured.";
    const referencePath = (parent: HTMLElement, chain: unknown) => {
        if (!Array.isArray(chain) || chain.length === 0) return;
        const heading = document.createElement("h4");
        heading.textContent = "Explore root → object";
        parent.appendChild(heading);
        let branch = parent;
        for (const frame of chain.slice(0, 64)) {
            const item = asRecord(frame);
            const list = document.createElement("ul");
            list.className = "reference-path";
            const row = document.createElement("li");
            const disclosure = document.createElement("details");
            disclosure.open = true;
            const label = document.createElement("summary");
            label.textContent = formatValue(item.typeFullName) + (item.rootKind ? " [" + formatValue(item.rootKind) + "]" : "");
            const address = document.createElement("div");
            appendAddressValue(address, item.objectAddress, containerId);
            disclosure.append(label, address);
            row.appendChild(disclosure);
            list.appendChild(row);
            branch.appendChild(list);
            branch = disclosure;
        }
        if (chain.length > 64) appendMessage(parent, "Path display capped at 64 frames; remaining frames omitted.", "query-view-note");
    };

    switch (view) {
        case "roots-by-kind": {
            const rows = Array.isArray(result.rootsByKind) ? result.rootsByKind : [];
            metrics([["Root records", rows.reduce((sum, row) => sum + numericField(row, "rootCount"), 0)]]);
            table("Root categories", rows, [
                ["Root kind", row => row.rootKind],
                ["Roots", row => row.rootCount],
                ["Distinct targets", row => row.distinctTargetObjects],
                ["Direct bytes", row => formatBytes(row.directlyReferencedBytes)],
                ["Pinned", row => row.pinnedRootCount],
                ["Interior", row => row.interiorRootCount],
            ], "No root categories were recorded.");
            break;
        }
        case "finalizer-queue":
            table("Objects awaiting finalization", result.finalizableObjects, [
                ["Type", row => row.typeFullName],
                ["Instances", row => row.instanceCount],
                ["Bytes", row => formatBytes(row.totalBytes)],
            ], "No objects were reported on the finalizer queue.");
            break;
        case "fragmentation":
            table("Heap segments (most free bytes first)", result.segments, [
                ["Generation", row => row.generation],
                ["Heap", row => row.logicalHeap],
                ["Free", row => formatBytes(row.freeBytes)],
                ["Free %", row => formatPercent(row.freePercent)],
                ["Used", row => formatBytes(row.usedBytes)],
                ["Segment size", row => formatBytes(row.length)],
                ["Objects", row => row.objectCount],
            ], "No heap segments were recorded.");
            break;
        case "static-fields":
            table("Static references", result.staticFields, [
                ["Declaring type", row => row.containingTypeFullName],
                ["Field", row => row.fieldName],
                ["Referenced type", row => row.valueTypeFullName],
                ["Direct bytes", row => formatBytes(row.directlyReferencedBytes)],
                ["Value address", row => row.valueAddress],
            ], "No static reference fields were found.");
            break;
        case "object": {
            const object = asRecord(result.objectDetails);
            if (result.sensitiveValuesIncluded === false) {
                appendMessage(content, "Metadata-only: string, field and array values are intentionally redacted. Reveal values of this object requires separate sensitive-data consent.", "query-view-note");
            }
            table("Object", [object], [
                ["Type", row => row.typeFullName],
                ["Size", row => formatBytes(row.size)],
                ["Generation", row => row.generation],
                ["Segment", row => row.segmentKind],
                ["Address", row => row.address],
            ], "No object details were returned.");
            if (object.isString === true) {
                const preview = metrics([["String preview", object.stringValue ?? "Unavailable"], ["Preview truncated", object.stringValueTruncated]]);
                const actions = document.createElement("div");
                actions.className = "object-value-actions";
                preview?.querySelector(".query-view-metric")?.appendChild(actions);
            } else {
                const actions = document.createElement("div");
                actions.className = "object-value-actions";
                content.appendChild(actions);
            }
            if (Array.isArray(object.fields)) {
                table("Fields", object.fields, [
                    ["Field", row => row.name],
                    ["Field type", row => row.typeFullName],
                    ["Value", row => row.value],
                    ["Referenced address", row => row.objectAddress],
                    ["Referenced type", row => row.referencedTypeFullName],
                ], "No fields were reported.");
            }
            if (object.isArray === true) {
                metrics([["Array length", object.arrayLength]]);
                const start = typeof object.arrayStartIndex === "number" ? object.arrayStartIndex : undefined;
                const count = typeof object.arrayRequestedCount === "number" ? object.arrayRequestedCount : undefined;
                const length = typeof object.arrayLength === "number" ? object.arrayLength : undefined;
                if (start !== undefined && count !== undefined && length !== undefined) {
                    const end = Math.min(start + count, length);
                    appendMessage(content, `Array range [${start}, ${end}) of ${length} elements (zero-based, end exclusive). `
                        + `${length - (end - start)} elements outside this range are not inspected. Missing rows inside the range are read failures, not empty entries.`, "query-view-note");
                    if (containerId === "addressQueryViews" && typeof object.address === "string" && length > 0) {
                        const address = object.address;
                        const controls = document.createElement("div");
                        controls.className = "controls";
                        const read = (index: number) => navigateToAddress(address, "object", false, index, count);
                        for (const [label, index, disabled] of [
                            ["Previous array range", Math.max(0, start - count), start === 0],
                            ["Next array range", end, end >= length],
                        ] as Array<[string, number, boolean]>) {
                            const button = document.createElement("button");
                            button.type = "button";
                            button.setAttribute("data-live-follow-up", "true");
                            button.textContent = label;
                            button.disabled = disabled || !addressLiveQueriesAvailable;
                            button.addEventListener("click", () => read(index));
                            controls.appendChild(button);
                        }
                        const label = document.createElement("label");
                        label.textContent = "Start index ";
                        const indexInput = document.createElement("input");
                        indexInput.type = "number";
                        indexInput.min = "0";
                        indexInput.max = String(length - 1);
                        indexInput.step = "1";
                        indexInput.value = String(start);
                        indexInput.setAttribute("data-live-follow-up", "true");
                        indexInput.disabled = !addressLiveQueriesAvailable;
                        label.appendChild(indexInput);
                        controls.appendChild(label);
                        const go = document.createElement("button");
                        go.type = "button";
                        go.setAttribute("data-live-follow-up", "true");
                        go.textContent = "Read array range";
                        go.disabled = !addressLiveQueriesAvailable;
                        const readIndex = () => {
                            const value = indexInput.value.trim();
                            const index = Number(value);
                            if (!value || !Number.isInteger(index) || index < 0 || index >= length) {
                                indexInput.setCustomValidity(`Enter an integer from 0 to ${length - 1}.`);
                                indexInput.reportValidity();
                                return;
                            }
                            indexInput.setCustomValidity("");
                            read(index);
                        };
                        go.addEventListener("click", readIndex);
                        indexInput.addEventListener("input", () => indexInput.setCustomValidity(""));
                        indexInput.addEventListener("keydown", event => {
                            if (event.key === "Enter") { event.preventDefault(); readIndex(); }
                        });
                        controls.appendChild(go);
                        content.appendChild(controls);
                        appendMessage(content, "Reading another range is a new read, metadata-only by default. Live-read permission and sensitive-value consent remain separate.", "query-view-note");
                    }
                }
                const structs = Array.isArray(object.arraySample) ? object.arraySample.filter(item => {
                    const element = asRecord(item);
                    return Array.isArray(element.fields);
                }) : [];
                if (structs.length) {
                    nested("Array elements", object.arraySample, element =>
                        `[${element.index}] ${element.typeFullName} — ${formatValue(element.value)}`, (body, element) => {
                            table("Fields", element.fields, [
                                ["Field path", row => row.name],
                                ["Field type", row => row.typeFullName],
                                ["Value", row => row.value],
                                ["Referenced address", row => row.objectAddress],
                                ["Referenced type", row => row.referencedTypeFullName],
                            ], "No fields were returned; check inspection warnings for invalid elements or expansion limits.", body);
                        }, "No value-type element fields were returned.");
                    appendMessage(content, "Structs live inline inside the array, so they have no independent object address. Dotted field paths expand nested structs; only actual managed references are navigable. Array slots are storage, not necessarily active collection entries.", "query-view-note");
                } else {
                    table("Array sample", object.arraySample, [
                        ["Index", row => row.index],
                        ["Element type", row => row.typeFullName],
                        ["Value", row => row.value],
                        ["Element address", row => row.objectAddress],
                    ], "No array elements were sampled.");
                }
            }
            notes("Inspection warnings", object.warnings);
            appendMessage(content, readProvenance(result.origin), "query-view-note");
            break;
        }
        case "gcroot": {
            const root = asRecord(result.gcRoot);
            metrics([
                ["Type", root.typeFullName],
                ["Path length", Array.isArray(root.chain) ? root.chain.length : undefined],
                ["Truncated", root.truncated],
            ]);
            if (!Array.isArray(root.chain) || root.chain.length === 0)
                appendMessage(content, "No path from a GC root was found.", "query-view-empty");
            referencePath(content, root.chain);
            notes("Inspection warnings", root.warnings);
            appendMessage(content, readProvenance(result.origin), "query-view-note");
            break;
        }
        case "delegate-targets":
            table("Delegate subscriptions", result.delegateTargets, [
                ["Publisher / declaring type", row => row.declaringTypeFullName],
                ["Method", row => row.methodName],
                ["Target type", row => row.targetTypeFullName || (row.isStaticTarget ? "Static target" : "Unknown")],
                ["Subscribers", row => row.subscriberCount],
            ], "No delegate targets were found.");
            break;
        case "retention-paths":
            nested("Paths from roots to retained objects", result.retentionPaths,
                row => formatValue(row.targetTypeFullName) + " · " + formatAddress(row.targetObjectAddress),
                (body, row) => {
                    appendMessage(body, renderChain(row.chain), "query-view-chain");
                    fieldTable(body, "Target object", [row], [
                        ["Type", target => target.targetTypeFullName],
                        ["Target address", target => target.targetObjectAddress],
                    ], "No target object was recorded.");
                    referencePath(body, row.chain);
                    if (row.truncated === true) appendMessage(body, "Path shortened for this response.", "query-view-note");
                },
                "No paths match the selected type filter.");
            metrics([["Matching paths", result.totalRetentionPaths], ["Omitted", result.omittedRetentionPaths]]);
            break;
        case "retained-exceptions":
            nested("Retained exception types", result.retainedExceptions,
                row => formatValue(row.typeFullName) + " · " + formatValue(row.instanceCount) + " instance(s)",
                (body, row) => {
                    metrics([["Type", row.typeFullName], ["Instances", row.instanceCount], ["Heap bytes", formatBytes(row.totalBytes)]], body);
                    const samples = Array.isArray(row.samples) ? row.samples : [];
                    fieldTable(body, "Bounded message samples", samples, [
                        ["Message", sample => sample.message || "(no message)"],
                        ["HResult", sample => formatHResult(sample.hResult)],
                        ["Truncation", sample => sample.messageTruncated === true ? "truncated" : "complete"],
                    ], "No message samples were retained.");
                },
                "No live exception objects were found.");
            appendMessage(content, "Exception messages are bounded samples and may contain application data.", "query-view-note");
            break;
        case "gchandles": {
            const handles = asRecord(result.gcHandles);
            metrics([["Total handles", handles.totalHandles]]);
            const buckets = Array.isArray(handles.byKind) ? handles.byKind : [];
            nested("Handles by kind", buckets,
                row => formatValue(row.kind) + " · " + formatValue(row.count),
                (body, row) => {
                    metrics([["Handles", row.count], ["Direct bytes", formatBytes(row.retainedBytes)]], body);
                    fieldTable(body, "Top target types", row.topTypes, [
                        ["Type", type => type.typeFullName],
                        ["Handles", type => type.count],
                        ["Direct bytes", type => formatBytes(type.retainedBytes)],
                    ], "No target types were recorded.");
                },
                "No GC handles were recorded.");
            notes("Collection notes", handles.notes);
            break;
        }
        case "async":
            nested("Pending async operations", result.asyncOperations,
                row => formatValue(row.stateMachineTypeFullName) + " · state " + formatValue(row.state),
                (body, row) => {
                    metrics([
                        ["Awaiter", row.awaiterTypeFullName],
                        ["Direct size", formatBytes(row.directSizeBytes)],
                        ["Task ID", row.taskId],
                        ["Task type", row.taskTypeFullName],
                        ["Continuation", row.continuationObjectTypeFullName],
                    ], body);
                    const frames = Array.isArray(row.stack) ? row.stack : [];
                    fieldTable(body, "Reconstructed async chain", frames, [
                        ["State machine", frame => frame.stateMachineTypeFullName],
                        ["State", frame => frame.state],
                        ["Awaiter", frame => frame.awaiterTypeFullName],
                        ["Task ID", frame => frame.taskId],
                    ], "No continuation chain was reconstructed.");
                },
                "No pending async state machines were found.");
            break;
        case "timers": {
            const timers = asRecord(result.timers);
            metrics([
                ["Timers", timers.totalTimers],
                ["Tasks", timers.totalTasks],
                ["Task completion sources", timers.totalTaskCompletionSources],
            ]);
            table("Timer callbacks", timers.timersByCallback, [
                ["Callback method", row => [row.declaringTypeFullName, row.methodName].filter(Boolean).join(".") || "Unknown"],
                ["Target type", row => row.callbackTargetTypeFullName || "Static / unresolved"],
                ["Timer type", row => row.timerTypeFullName],
                ["Instances", row => row.count],
                ["Canceled", row => row.isCanceled],
            ], "No timer callbacks were recorded.");
            table("Tasks by type", timers.tasksByType, [
                ["Type", row => row.typeFullName],
                ["Count", row => row.count],
                ["Bytes", row => formatBytes(row.totalBytes)],
            ], "No tasks were recorded.");
            table("Task completion sources by type", timers.taskCompletionSourcesByType, [
                ["Type", row => row.typeFullName],
                ["Count", row => row.count],
                ["Bytes", row => formatBytes(row.totalBytes)],
            ], "No task completion sources were recorded.");
            notes("Collection notes", timers.notes);
            break;
        }
        case "alc": {
            const alc = asRecord(result.assemblyLoadContexts);
            metrics([
                ["Contexts", alc.totalContexts],
                ["Collectible", alc.collectibleContexts],
                ["Suspected leaks", alc.suspectedLeakedCollectibleContexts],
            ]);
            nested("Assembly load contexts", alc.contexts,
                row => (row.name ? formatValue(row.name) : "Unnamed context") +
                    (row.suspectedLeak === true ? " · suspected leak" : ""),
                (body, row) => {
                    metrics([
                        ["Collectible", row.isCollectible],
                        ["Default context", row.isDefault],
                        ["Assemblies", row.assemblyCount],
                        ["Live objects", row.liveObjectCount],
                        ["Live bytes", formatBytes(row.liveBytes)],
                        ["Retention target", row.retentionTargetTypeFullName],
                    ], body);
                    fieldTable(body, "Loaded assemblies", row.assemblies, [
                        ["Assembly", assembly => assembly.assemblyName],
                        ["Live objects", assembly => assembly.liveObjectCount],
                        ["Live bytes", assembly => formatBytes(assembly.liveBytes)],
                        ["Module", assembly => assembly.moduleName],
                    ], "No assembly details were recorded.");
                    if (row.retentionPath && typeof row.retentionPath === "object") {
                        appendMessage(body, "Retention: " + renderChain(asRecord(row.retentionPath).chain), "query-view-chain");
                    }
                    notes("Context notes", row.notes);
                },
                "No assembly load contexts were found.");
            notes("Collection notes", alc.notes);
            break;
        }
        case "deadlocks":
            nested("Inferred cycle candidates", result.deadlocks,
                row => formatValue(row.classification || "Inferred cycle candidate") +
                    " · " + formatValue(row.confidence) + " confidence",
                (body, row) => {
                    fieldTable(body, "Threads in cycle", row.cycleMembers, [
                        ["Managed thread", member => member.threadId],
                        ["OS thread", member => member.osThreadId],
                        ["State", member => member.state],
                        ["Wait reason", member => member.inferredWaitReason],
                        ["Top method", member => member.topFrameMethod],
                    ], "No member details.");
                    fieldTable(body, "Waiter → owner lock edges", row.lockChain, [
                        ["Waiting thread", edge => edge.waitingThreadId],
                        ["Owner thread", edge => edge.ownerThreadId],
                        ["Lock type", edge => edge.lockObjectTypeFullName || edge.lockKind],
                        ["Lock address", edge => edge.lockObjectAddress],
                        ["Confidence", edge => edge.confidence],
                    ], "No lock-edge details.");
                    fieldTable(body, "Suggested next steps", row.recommendedCommands, [
                        ["Command", command => command.command],
                        ["Purpose", command => command.purpose],
                    ], "No follow-up commands were suggested.");
                    appendMessage(body, "Candidate inferred from a snapshot; validate the wait-for edges before treating it as a confirmed deadlock.", "query-view-note");
                },
                "No inferred deadlock cycle candidates were found.");
            break;
        case "unique-stacks":
            nested("Stack groups", result.uniqueStacks,
                row => formatValue(row.threadCount) + " thread(s) · " + formatPercent(typeof row.threadPercentage === "number" ? row.threadPercentage * 100 : row.threadPercentage)
                    + (!Array.isArray(row.canonicalFrames) || row.canonicalFrames.length === 0 ? " · Stack unavailable" : ""),
                (body, row) => {
                    metrics([
                        ["Threads", row.threadCount],
                        ["Share", formatPercent(typeof row.threadPercentage === "number" ? row.threadPercentage * 100 : row.threadPercentage)],
                        ["Wait reason", row.inferredWaitReason],
                    ], body);
                    fieldTable(body, "Threads in group", row.sampleThreads, [
                        ["Managed thread", thread => thread.managedThreadId],
                        ["OS thread", thread => thread.osThreadId],
                    ], "No representative thread IDs.");
                    appendMessage(body, "Only representative thread IDs are returned; this is not the full group membership. "
                        + "Select a managed thread to inspect its captured details without a new read.", "query-view-note");
                    const table = body.querySelector("tbody");
                    const members = Array.isArray(row.sampleThreads) ? row.sampleThreads : [];
                    Array.from(table?.children ?? []).forEach((threadRow, index) => {
                        const member = asRecord(members[index]);
                        const id = member.managedThreadId;
                        const cell = threadRow.querySelector("td");
                        if (!cell || typeof id !== "number" || !Number.isSafeInteger(id) || id < 0) return;
                        const button = document.createElement("button");
                        button.type = "button";
                        button.className = "heap-type-link";
                        button.textContent = "Thread " + id;
                        button.addEventListener("click", () => navigateThreadFromGroup(id, button));
                        cell.replaceChildren(button);
                    });
                    const frames = Array.isArray(row.canonicalFrames) ? row.canonicalFrames : [];
                    if (!frames.length) {
                        appendMessage(body, "No managed stack frames were returned for this group. The capture does not specify why; "
                            + "this does not mean these threads have no stack or share the same execution path.", "query-view-note");
                    }
                    const stack = document.createElement("ol");
                    stack.className = "query-view-stack-frames";
                    for (const frame of frames) {
                        const item = frame && typeof frame === "object" ? frame as Record<string, unknown> : {};
                        const frameNode = document.createElement("li");
                        frameNode.textContent = typeof item.displayName === "string" && item.displayName
                            ? item.displayName : formatValue(item);
                        stack.appendChild(frameNode);
                    }
                    if (frames.length) {
                        const order = document.createElement("p");
                        order.className = "query-view-note";
                        order.textContent = "Top of stack first.";
                        body.append(order, stack);
                    }
                },
                "No stack groups matched the requested threshold.");
            break;
        case "wait-chains": {
            const waitChains = asRecord(result.waitChains);
            metrics([
                ["Threads", waitChains.threadCount],
                ["Wait edges", waitChains.edgeCount],
                ["Cycle candidates", waitChains.inferredCycleCandidateCount],
                ["Open chains", waitChains.openChainCount],
                ["ThreadPool starvation", waitChains.threadPoolStarved],
            ]);
            nested("Ranked wait chains", waitChains.chains,
                row => "Thread " + formatValue(row.rootThreadId) + " · " +
                    formatValue(row.length) + " hop(s) · " + formatValue(row.terminalKind),
                (body, row) => {
                    metrics([
                        ["Blocked threads", row.blockedThreadCount],
                        ["Cycle", row.isCycle],
                        ["Inferred cycle candidate", row.isInferredCycleCandidate],
                        ["Confidence", row.inferenceConfidence],
                    ], body);
                    fieldTable(body, "Root thread", [{ managedThreadId: row.rootThreadId }], [
                        ["Managed thread", link => link.managedThreadId],
                    ], "No root thread ID was returned.");
                    fieldTable(body, "Wait chain", row.links, [
                        ["Waiting thread", link => link.waitingThreadId],
                        ["Wait reason", link => link.waitReason],
                        ["Target", link => [link.targetKind, link.targetLabel].filter(Boolean).join(" · ")],
                        ["Owner thread", link => link.ownerThreadId],
                        ["Confidence", link => link.confidence],
                    ], "No wait links were recorded.");
                    notes("Chain caveats", row.notes);
                },
                "No wait chains were detected.");
            notes("Analyzer caveats", waitChains.notes);
            break;
        }
        case "threadpool": {
            const pool = asRecord(result.threadPool);
            metrics([
                ["Initialized", pool.initialized],
                ["Portable ThreadPool", pool.usingPortableThreadPool],
                ["Windows ThreadPool", pool.usingWindowsThreadPool],
                ["CPU utilization", pool.cpuUtilization === undefined ? undefined : formatPercent(pool.cpuUtilization)],
                ["Pending work items", pool.pendingWorkItems],
            ]);
            const workers = asRecord(pool.workers);
            const iocp = asRecord(pool.iocp);
            table("Worker threads", [workers], [
                ["Current", row => row.current],
                ["Active", row => row.active],
                ["Idle", row => row.idle],
                ["Retired", row => row.retired],
                ["Minimum", row => row.min],
                ["Maximum", row => row.max],
            ], "Worker-thread counts are unavailable.");
            table("I/O completion threads", [iocp], [
                ["Current", row => row.current],
                ["Idle", row => row.idle],
                ["Minimum", row => row.min],
                ["Maximum", row => row.max],
                ["Current limit", row => row.currentLimit],
            ], "I/O completion counts are unavailable.");
            const queues = asRecord(pool.queues);
            metrics([["Global queue", queues.globalQueueLength]]);
            table("Global queues", queues.globalQueues, [
                ["Queue", row => row.name],
                ["Items", row => row.queueLength],
                ["Index", row => row.queueIndex],
            ], "No global queue details were recorded.");
            table("Worker-local queues", queues.localQueues, [
                ["Managed thread", row => row.managedThreadId],
                ["OS thread", row => row.osThreadId],
                ["Items", row => row.queueLength],
                ["Queue index", row => row.queueIndex],
            ], "No worker-local queue details were recorded.");
            notes("Collection notes", pool.notes);
            break;
        }
        case "exceptions": {
            const data = asRecord(result.data);
            const recent = Array.isArray(data.recent) ? data.recent.map(asRecord) : [];
            const startedAt = typeof data.startedAt === "string" ? data.startedAt : undefined;
            const startedAtDate = startedAt ? new Date(startedAt) : undefined;
            const scope = document.createElement("details");
            scope.className = "exception-capture-scope";
            const scopeLabel = document.createElement("summary");
            scopeLabel.textContent = "Capture totals and scope";
            scope.appendChild(scopeLabel);
            const headline = document.createElement("p");
            headline.className = "exception-capture-summary";
            headline.textContent = formatValue(data.totalExceptions) + " observed · " + recent.length + " retained";
            content.appendChild(headline);
            const beforeMetadata = new Set(Array.from(content.children));
            metrics([
                ["Observed exceptions", data.totalExceptions],
                ["Retained events", recent.length],
                ["Event cap", data.recentCap],
                ["Started", startedAtDate && Number.isFinite(startedAtDate.getTime())
                    ? startedAtDate.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "medium" })
                    : startedAt],
                ["Duration", data.duration],
                ["Process exited", data.processExited],
                ["Unhandled exception observed", data.unhandledExceptionObserved],
            ]);
            if (startedAt) {
                const exactTimestamp = document.createElement("details");
                exactTimestamp.className = "query-view-timestamp";
                const exactTimestampLabel = document.createElement("summary");
                exactTimestampLabel.textContent = "Exact start time";
                const exactTimestampValue = document.createElement("code");
                exactTimestampValue.textContent = startedAt;
                exactTimestamp.append(exactTimestampLabel, exactTimestampValue);
                content.appendChild(exactTimestamp);
            }
            appendMessage(content, formatValue(result.summary), "query-view-note");
            table("Exception types (all observed events)", data.byType, [
                ["Exception type", row => row.exceptionType],
                ["Count", row => row.count],
            ], "No managed exceptions were observed. EventPipe readiness is not reported, so a one-time failure may have happened outside the capture window. Keep the workload running or repeat the problem throughout a new capture.");
            const messages = new Map<string, Record<string, unknown>>();
            for (const event of recent) {
                const key = JSON.stringify([event.exceptionType, event.exceptionMessage]);
                const existing = messages.get(key);
                if (existing) {
                    existing.count = Number(existing.count) + 1;
                    if (String(event.timestamp) < String(existing.first)) existing.first = event.timestamp;
                    if (String(event.timestamp) > String(existing.last)) existing.last = event.timestamp;
                } else {
                    messages.set(key, { exceptionType: event.exceptionType, message: event.exceptionMessage,
                        count: 1, first: event.timestamp, last: event.timestamp });
                }
            }
            table("Top messages (retained events only)", [...messages.values()].sort((a, b) => Number(b.count) - Number(a.count)), [
                ["Exception type", row => row.exceptionType],
                ["Message (potentially sensitive)", row => row.message],
                ["Retained count", row => row.count],
                ["First retained", row => row.first],
                ["Last retained", row => row.last],
            ], "No retained exception messages.");
            const aggregates = document.createElement("details");
            const aggregateLabel = document.createElement("summary");
            aggregateLabel.textContent = "Type counts and retained-message aggregates";
            aggregates.appendChild(aggregateLabel);
            for (const child of Array.from(content.children))
                if (child.classList.contains("query-view-section")) aggregates.appendChild(child);
            content.appendChild(aggregates);
            for (const child of Array.from(content.children))
                if (!beforeMetadata.has(child)) scope.appendChild(child);
            const notRetained = typeof data.totalExceptions === "number" && Number.isFinite(data.totalExceptions)
                ? Math.max(0, data.totalExceptions - recent.length) : 0;
            const captureNotes = Array.isArray(result.notes) ? result.notes : [];
            if (notRetained > 0 || captureNotes.length) {
                const alert = document.createElement("details");
                alert.className = "exception-capture-notes";
                const caption = document.createElement("summary");
                caption.textContent = [
                    notRetained > 0 ? notRetained + " observations not retained" : "",
                    captureNotes.length ? captureNotes.length + " collection / retention note(s)" : "",
                ].filter(Boolean).join(" · ");
                alert.appendChild(caption);
                for (const note of captureNotes) appendMessage(alert, String(note), "query-view-note");
                content.appendChild(alert);
            }
            renderExceptionNavigation(content, recent);
            content.appendChild(scope);
            break;
        }
        case "thread-statics": {
            const statics = asRecord(result.threadStatics);
            const threads = Array.isArray(statics.threads) ? statics.threads : [];
            metrics([["Type", statics.typeFullName], ["Threads with values", threads.length]]);
            nested("Values by managed thread", threads,
                row => "Managed thread " + formatValue(row.managedThreadId),
                (body, row) => fieldTable(body, "Thread-static fields", row.fields, [
                    ["Field", field => field.name],
                    ["Initialized", field => field.isInitialized],
                    ["Value preview", field => field.valuePreview],
                ], "No thread-static fields were found on this thread."),
                "No thread-static fields were found for this type.");
            notes("Resolution notes", statics.warnings);
            break;
        }
        default:
            renderUnknownView(content, result);
            break;
    }

    const quality = asRecord(result.quality);
    const limitations = Array.isArray(quality.limitations)
        ? quality.limitations.map(item => {
            const limitation = item && typeof item === "object" ? item as Record<string, unknown> : {};
            return limitation.detail;
        })
        : [];
    notes("Evidence limitations", limitations);
    enhanceEvidenceTables(content);
    if (containerId === "heapQueryViews" && view === "retention-paths") applyHeapTypeFocus();
    if (containerId === "threadQueryViews" && ["unique-stacks", "wait-chains", "deadlocks"].includes(view))
        updateThreadNavigation(view, result);
    if (containerId === "heapQueryViews" || containerId === "threadQueryViews") updateEvidenceViewSwitcher(container);
}

function readProvenance(origin: unknown): string {
    return typeof origin === "string" && origin.toLowerCase() === "live"
        ? "Read live from the target process when requested; contents may differ from the captured snapshot."
        : "Read from the dump file.";
}

function appendMessage(parent: HTMLElement, text: string, className: string): void {
    const element = document.createElement("p");
    element.className = className;
    element.textContent = text;
    parent.appendChild(element);
}

function asRecord(value: unknown): Record<string, unknown> {
    return value && typeof value === "object" ? value as Record<string, unknown> : {};
}

function formatValue(value: unknown): string {
    if (value === undefined || value === null || value === "") return "—";
    if (typeof value === "boolean") return value ? "Yes" : "No";
    if (typeof value === "number") return Number.isFinite(value) ? new Intl.NumberFormat().format(value) : "—";
    if (typeof value === "string") return value;
    if (Array.isArray(value)) return value.map(formatValue).join(", ");
    return "[details]";
}

function numericField(value: unknown, key: string): number {
    const field = asRecord(value)[key];
    return typeof field === "number" && Number.isFinite(field) ? field : 0;
}

function formatBytes(value: unknown): string {
    if (typeof value !== "number" || !Number.isFinite(value)) return "—";
    const units = ["B", "KB", "MB", "GB", "TB"];
    let size = value;
    let unit = 0;
    while (Math.abs(size) >= 1024 && unit < units.length - 1) {
        size /= 1024;
        unit++;
    }
    return new Intl.NumberFormat(undefined, { maximumFractionDigits: unit === 0 ? 0 : 1 }).format(size) + " " + units[unit];
}

function formatPercent(value: unknown): string {
    return typeof value === "number" && Number.isFinite(value)
        ? new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value) + "%"
        : "—";
}

function formatAddress(value: unknown): string {
    if (value === undefined || value === null || value === "") return "—";
    if (typeof value === "string" && /^0x[0-9a-f]+$/i.test(value)) return value;
    // Addresses are strings on the wire; any number (even a safe integer) violates that contract and is never reformatted.
    if (typeof value === "number") return "Unavailable: numeric address (imprecise JavaScript integer)";
    return String(value);
}

function formatHResult(value: unknown): string {
    if (typeof value !== "number" || !Number.isFinite(value)) return "—";
    return "0x" + (value >>> 0).toString(16).toUpperCase().padStart(8, "0");
}

function renderUnknownView(parent: HTMLElement, result: Record<string, unknown>): void {
    const skipped = new Set(["handle", "view", "origin", "processId", "capturedAt"]);
    const entries = Object.entries(result).filter(([key]) => !skipped.has(key));
    if (!entries.length) {
        appendMessage(parent, "No structured data was returned for this view.", "query-view-empty");
        return;
    }
    const table = document.createElement("table");
    table.className = "query-view-table query-view-fallback";
    for (const [key, value] of entries) {
        const row = document.createElement("tr");
        const heading = document.createElement("th");
        heading.scope = "row";
        heading.textContent = key;
        const cell = document.createElement("td");
        cell.textContent = formatValue(value);
        row.append(heading, cell);
        table.appendChild(row);
    }
    parent.appendChild(table);
}

export const QUERY_VIEW_RENDERER_SCRIPT =
    EVIDENCE_NAVIGATION_SCRIPT + "\n" +
    SNAPSHOT_NAVIGATION_SCRIPT + "\n" +
    EVIDENCE_VIEW_SWITCHER_SCRIPT + "\n" +
    "const queryViewLabels = " + JSON.stringify(queryViewLabels) + ";\n" +
    renderQueryView.toString() + "\n" +
    appendMessage.toString() + "\n" +
    readProvenance.toString() + "\n" +
    asRecord.toString() + "\n" +
    formatValue.toString() + "\n" +
    numericField.toString() + "\n" +
    formatBytes.toString() + "\n" +
    formatPercent.toString() + "\n" +
    formatAddress.toString() + "\n" +
    formatHResult.toString() + "\n" +
    renderUnknownView.toString() + "\n" +
    ADDRESS_NAVIGATION_SCRIPT;

export const QUERY_VIEW_STYLES = `
    #captureHeapResult { container-type:inline-size; }
    .heap-workspace { display:grid; grid-template-columns:minmax(16rem, 2fr) minmax(24rem, 3fr); gap:1rem; align-items:start; margin-top:.6rem; }
    .heap-type-list, .heap-detail-pane { min-width:0; max-height:70vh; overflow:auto; scrollbar-gutter:stable; }
    .heap-detail-pane { border:1px solid var(--vscode-panel-border); padding:.6rem; }
    .heap-type-list td:first-child { overflow-wrap:anywhere; }
    .heap-type-list button[aria-current="true"] { font-weight:700; text-decoration:underline; }
    #openHeapDetails { margin-bottom:.5rem; }
    #backToHeapTypes { display:none; }
    @container (max-width: 850px) {
        .heap-workspace { display:block; }
        .heap-type-list, .heap-detail-pane { max-height:none; overflow:visible; }
        .heap-workspace[data-focused="true"] .heap-type-list { display:none; }
        .heap-workspace[data-focused="false"] .heap-detail-pane { display:none; }
        #backToHeapTypes { display:inline-block; }
    }
    .query-views { margin-top:.8rem; }
    details.query-view { border:1px solid var(--vscode-panel-border); border-radius:3px; margin-bottom:.6rem; background:var(--vscode-sideBar-background); }
    details.query-view > summary { cursor:pointer; padding:.55rem .7rem; font-weight:600; }
    details.query-view[open] > summary { border-bottom:1px solid var(--vscode-panel-border); }
    .query-view-content { padding:.25rem .7rem .7rem; }
    .query-view-provenance, .query-view-note, .query-view-empty { color:var(--vscode-descriptionForeground); font-size:.85rem; }
    .query-view-provenance { margin:.5rem 0; }
    .query-view-metrics { display:grid; grid-template-columns:repeat(auto-fit,minmax(8rem,1fr)); gap:.5rem; margin:.6rem 0; }
    .query-view-metric { display:flex; flex-direction:column; gap:.2rem; padding:.5rem .6rem; border:1px solid var(--vscode-panel-border); border-radius:3px; min-width:0; }
    .query-view-metric-label { color:var(--vscode-descriptionForeground); font-size:.75rem; }
    .query-view-metric strong { overflow-wrap:anywhere; }
    .query-view-section { margin:.75rem 0; }
    .query-view-section h4, .query-view-notes h4 { margin:.4rem 0; font-size:.9rem; }
    .query-view-table-wrap { overflow-x:auto; }
    table.query-view-table { border-collapse:collapse; width:100%; font-size:.85rem; }
    .query-view-table th, .query-view-table td { text-align:left; vertical-align:top; padding:.35rem .5rem; border-bottom:1px solid var(--vscode-panel-border); overflow-wrap:anywhere; }
    .query-view-table th { color:var(--vscode-descriptionForeground); font-weight:600; }
    .query-view-timestamp { margin:.25rem 0 .6rem; color:var(--vscode-descriptionForeground); font-size:.85rem; }
    .query-view-timestamp summary { cursor:pointer; }
    .query-view-timestamp code { display:block; margin-top:.25rem; overflow-wrap:anywhere; user-select:text; }
    details.query-view-nested { border-top:1px solid var(--vscode-panel-border); padding:.35rem 0; }
    details.query-view-nested > summary { cursor:pointer; font-weight:500; }
    .query-view-nested-body { padding:.35rem 0 .2rem .7rem; }
    .query-view-chain { font-family:var(--vscode-editor-font-family); overflow-wrap:anywhere; }
    .query-view-stack-frames { padding-left:1.5rem; }
    .query-view-stack-frames li { margin:.15rem 0; overflow-wrap:anywhere; }
    .thread-search { display:flex; flex-wrap:wrap; align-items:center; gap:.4rem; color:var(--vscode-descriptionForeground); }
    .thread-search input { flex:1 1 12rem; min-width:0; }
    .thread-secondary { font-size:.85em; color:var(--vscode-descriptionForeground); }
    .thread-explorer-table { width:100%; table-layout:fixed; border-collapse:collapse; margin:.5rem 0; }
    .thread-explorer-table th:first-child { width:7rem; }
    .thread-explorer-table th:nth-child(2) { width:7rem; }
    .thread-explorer-table th, .thread-explorer-table td { text-align:left; vertical-align:top; padding:.4rem; overflow-wrap:anywhere; }
    .thread-top-frame, .thread-stack { font-family:var(--vscode-editor-font-family,monospace); font-size:.9em; }
    .thread-top-frame-preview { display:-webkit-box; -webkit-box-orient:vertical; -webkit-line-clamp:2; overflow:hidden; overflow-wrap:anywhere; }
    .thread-disclosure { background:none; border:none; color:var(--vscode-foreground); padding:0; font:inherit; text-align:left; }
    .thread-disclosure::before { content:'\\25B8'; display:inline-block; width:1em; }
    .thread-disclosure[aria-expanded="true"]::before { content:'\\25BE'; }
    .thread-disclosure:hover { background:var(--vscode-list-hoverBackground); }
    .thread-disclosure:focus-visible { outline:1px solid var(--vscode-focusBorder); outline-offset:2px; }
    .thread-expanded { background:var(--vscode-list-inactiveSelectionBackground); }
    .thread-inline-stack { padding:.2rem .4rem .6rem; border-bottom:1px solid var(--vscode-panel-border); min-width:0; }
    .thread-stack { padding-left:2rem; }
    .thread-stack li { white-space:pre-wrap; overflow-wrap:anywhere; margin:.35rem 0; }
    .thread-technical { font-size:.85em; color:var(--vscode-descriptionForeground); margin-top:.5rem; }
    @media(max-width:600px) {
      .thread-explorer-table th:first-child { width:5.5rem; }
      .thread-explorer-table th:nth-child(2) { width:5rem; }
    }
    .reference-path { list-style:none; padding-left:.8rem; border-left:1px solid var(--vscode-panel-border); }
    .reference-path summary { cursor:pointer; overflow-wrap:anywhere; }
    .query-view-notes { border:1px solid var(--vscode-editorWarning-foreground); padding:.25rem .65rem; margin:.6rem 0; }
    .query-view-notes ul { margin:.25rem 0; padding-left:1.2rem; }
    .query-view-deadlocks { border-color:var(--vscode-editorWarning-foreground)!important; border-width:2px!important; }
    .query-view-deadlocks > summary { color:var(--vscode-editorWarning-foreground); }
    .query-view-error > summary, .query-view-error-message { color:var(--vscode-errorForeground); }
    .query-view-fallback { margin:.5rem 0; }
` + ADDRESS_NAVIGATION_STYLES + EVIDENCE_NAVIGATION_STYLES + EVIDENCE_VIEW_SWITCHER_STYLES;
