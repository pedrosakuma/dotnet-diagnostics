export interface TargetProcess {
    processId: number;
    operatingSystem: string;
    processArchitecture: string;
    runtimeVersion: string;
    managedEntrypointAssemblyName?: string | null;
}

export interface CounterValue {
    provider: string;
    name: string;
    displayName: string;
    value: number;
    kind: string;
    unit?: string | null;
}

export interface ProtocolFrame {
    type: string;
    [key: string]: unknown;
}

export function parseProcessList(json: string): TargetProcess[] {
    const envelope: unknown = JSON.parse(json);
    if (!isRecord(envelope) || !Array.isArray(envelope.data)) {
        throw new Error("The CLI process-list response did not contain a data array.");
    }

    return envelope.data.flatMap((value: unknown) => {
        if (!isRecord(value)
            || typeof value.processId !== "number"
            || !Number.isSafeInteger(value.processId)
            || value.processId <= 0) {
            return [];
        }

        return [{
            processId: value.processId,
            operatingSystem: optionalString(value.operatingSystem) ?? "unknown OS",
            processArchitecture: optionalString(value.processArchitecture) ?? "unknown architecture",
            runtimeVersion: optionalString(value.runtimeVersion) ?? "unknown runtime",
            managedEntrypointAssemblyName: optionalString(value.managedEntrypointAssemblyName),
        }];
    });
}

export function parseProtocolFrame(line: string): ProtocolFrame {
    const value: unknown = JSON.parse(line);
    if (!isRecord(value) || typeof value.type !== "string") {
        throw new Error("The CLI emitted a protocol frame without a string type.");
    }

    return value as ProtocolFrame;
}

export function describeStreamCompatibilityError(output: string): string {
    const trimmed = output.trim();
    if (!trimmed.startsWith("{") || trimmed.split(/\r?\n/).length > 8 || trimmed.length > 600) {
        return "The configured dotnet-diagnostics-cli did not start the JSONL stream protocol. It may be an older CLI build without live-stream support. Update/build the CLI with `stream --protocol jsonl`, then set the machine-scoped `dotnetDiagnostics.cliPath` to that executable.";
    }

    return "The configured dotnet-diagnostics-cli returned output that is not a valid JSONL protocol frame. Check the CLI executable and its version.";
}

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

function optionalString(value: unknown): string | null {
    return typeof value === "string" && value.length > 0 ? value : null;
}
