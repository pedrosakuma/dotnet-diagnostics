import { ADDRESS_NAVIGATION_SCRIPT, ADDRESS_NAVIGATION_STYLES } from "./addressNavigation";

// Provided by the address-navigation script that is emitted next to this renderer in the webview.
declare function appendAddressValue(parent: HTMLElement, value: unknown, containerId: string): void;

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
    "unique-stacks": "Unique thread stacks",
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
        if (view === "deadlocks") {
            details.open = true;
            details.classList.add("query-view-deadlocks");
        }
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
    summary.textContent = isError ? label + " (unavailable)" : label;
    content.replaceChildren();
    if (isError) {
        appendMessage(content, String(resultOrMessage), "query-view-error-message");
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
    };

    // Columns named "... address" hold exact hex strings: linked/copyable, never reformatted as numbers.
    const fillCell = (cell: HTMLElement, columnName: string, value: unknown) => {
        if (/address$/i.test(columnName)) {
            appendAddressValue(cell, value, containerId);
        } else {
            cell.textContent = formatValue(value);
        }
    };

    const table = (
        title: string,
        items: unknown,
        columns: Array<[string, (row: Record<string, unknown>) => unknown]>,
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
        content.appendChild(section);
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
            table("Object", [object], [
                ["Type", row => row.typeFullName],
                ["Size", row => formatBytes(row.size)],
                ["Generation", row => row.generation],
                ["Segment", row => row.segmentKind],
                ["Address", row => row.address],
            ], "No object details were returned.");
            if (object.isString === true) {
                metrics([["String value", object.stringValue], ["Truncated", object.stringValueTruncated]]);
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
                table("Array sample", object.arraySample, [
                    ["Index", row => row.index],
                    ["Element type", row => row.typeFullName],
                    ["Value", row => row.value],
                    ["Element address", row => row.objectAddress],
                ], "No array elements were sampled.");
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
            table("Path from a GC root to the object", root.chain, [
                ["Type", frame => frame.typeFullName],
                ["Root kind", frame => frame.rootKind],
                ["Object address", frame => frame.objectAddress],
            ], "No path from a GC root was found.");
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
                    fieldTable(body, "Objects on the path", row.chain, [
                        ["Type", frame => frame.typeFullName],
                        ["Root kind", frame => frame.rootKind],
                        ["Object address", frame => frame.objectAddress],
                    ], "No chain was captured.");
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
                row => formatValue(row.threadCount) + " thread(s) · " + formatPercent(typeof row.threadPercentage === "number" ? row.threadPercentage * 100 : row.threadPercentage),
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
                    const frames = Array.isArray(row.canonicalFrames) ? row.canonicalFrames : [];
                    appendMessage(body, frames.map(frame => {
                        const item = frame && typeof frame === "object" ? frame as Record<string, unknown> : {};
                        return typeof item.displayName === "string" && item.displayName ? item.displayName : formatValue(item);
                    }).join("  →  "), "query-view-chain");
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
                    fieldTable(body, "Wait chain", row.links, [
                        ["Waiting thread", link => link.waitingThreadId],
                        ["Wait reason", link => link.waitReason],
                        ["Target", link => [link.targetKind, link.targetLabel].filter(Boolean).join(" · ")],
                        ["Owner", link => link.ownerThreadId],
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
            metrics([
                ["Observed exceptions", data.totalExceptions],
                ["Retained events", recent.length],
                ["Event cap", data.recentCap],
                ["Started", data.startedAt],
                ["Duration", data.duration],
                ["Process exited", data.processExited],
                ["Unhandled exception observed", data.unhandledExceptionObserved],
            ]);
            appendMessage(content, formatValue(result.summary), "query-view-note");
            table("Exception types (all observed events)", data.byType, [
                ["Exception type", row => row.exceptionType],
                ["Count", row => row.count],
            ], "No managed exceptions were observed. Start capture before reproducing the problem.");
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
            nested("Retained exception details", recent,
                row => formatValue(row.timestamp) + " · " + formatValue(row.exceptionType) + " · thread " + formatValue(row.threadId),
                (body, row) => {
                    appendMessage(body, String(row.exceptionMessage || ""), "query-view-note");
                    const stack = Array.isArray(row.managedStack) ? row.managedStack : [];
                    appendMessage(body, stack.length ? stack.map(formatValue).join("\n") : "No managed stack was captured for this event.", "query-view-chain");
                },
                "No retained exception details.");
            notes("Capture notes and retention limits", result.notes);
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
    details.query-view-nested { border-top:1px solid var(--vscode-panel-border); padding:.35rem 0; }
    details.query-view-nested > summary { cursor:pointer; font-weight:500; }
    .query-view-nested-body { padding:.35rem 0 .2rem .7rem; }
    .query-view-chain { font-family:var(--vscode-editor-font-family); overflow-wrap:anywhere; }
    .query-view-notes { border-left:3px solid var(--vscode-editorWarning-foreground); padding:.25rem .65rem; margin:.6rem 0; }
    .query-view-notes ul { margin:.25rem 0; padding-left:1.2rem; }
    .query-view-deadlocks { border-color:var(--vscode-editorError-foreground)!important; border-width:2px!important; }
    .query-view-deadlocks > summary { color:var(--vscode-editorError-foreground); }
    .query-view-error > summary, .query-view-error-message { color:var(--vscode-errorForeground); }
    .query-view-fallback { margin:.5rem 0; }
` + ADDRESS_NAVIGATION_STYLES;
