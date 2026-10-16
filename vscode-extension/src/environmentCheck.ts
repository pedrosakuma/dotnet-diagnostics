/**
 * Environment check for the extension: decides whether the configured dotnet-diagnostics-cli can be
 * used, and turns every failure into actionable guidance. This module has no `vscode` dependency so
 * it can be unit tested directly; `onboarding.ts` binds it to the VS Code host. It never installs or
 * updates tools and never collects diagnostics; it only runs `stream --protocol jsonl` (hello
 * handshake only) and `doctor --json` (host-only environment readiness).
 */

export const REQUIRED_PROTOCOL_VERSION = 2;
export const MIN_CLI_VERSION = "0.29.0";
export const MIN_FULL_FEATURES_CLI_VERSION = "0.31.0";
// `dotnet tool install` has no version-range syntax, so install the latest stable release.
export const INSTALL_COMMAND = "dotnet tool install -g dotnet-diagnostics-cli";
export const DEFAULT_CLI_PATH = "dotnet-diagnostics-cli";
export const UPDATE_COMMAND = "dotnet tool update -g dotnet-diagnostics-cli";
export const WALKTHROUGH_ID = "pedrosakuma.dotnet-diagnostics#dotnetDiagnostics.gettingStarted";

export interface CliRunResult {
    exitCode: number | null;
    stdout: string;
    stderr: string;
    /** Set when the process could not be spawned (for example `ENOENT`). */
    spawnError?: string;
    timedOut?: boolean;
    outputLimitExceeded?: boolean;
}

export type CliRunner = (cliPath: string, args: string[], stdin?: string) => Promise<CliRunResult>;

export type PreflightStatus = "Ok" | "Degraded" | "Blocked" | "NotApplicable";

export interface DoctorCheck {
    id: string;
    title: string;
    status: PreflightStatus;
    reason: string;
    remediation?: string;
    affectedTools: string[];
}

export interface DoctorReport {
    os: string;
    overall: PreflightStatus;
    hasBlocker: boolean;
    checks: DoctorCheck[];
}

export type EnvironmentIssueKind =
    | "cli-missing"
    | "runtime-missing"
    | "cli-outdated"
    | "extension-outdated"
    | "feature-compatibility"
    | "cli-failed"
    | "environment-blocked"
    | "environment-degraded";

export interface EnvironmentIssue {
    kind: EnvironmentIssueKind;
    severity: "error" | "warning";
    message: string;
    guidance: string;
    /** Command the user may run themselves; it is only shown or copied, never executed. */
    command?: string;
}

export interface EnvironmentReport {
    cliPath: string;
    ready: boolean;
    protocolVersion?: number;
    cliVersion?: string;
    doctor?: DoctorReport;
    issues: EnvironmentIssue[];
}

const STATUSES: readonly string[] = ["Ok", "Degraded", "Blocked", "NotApplicable"];

function isRecord(value: unknown): value is Record<string, unknown> {
    return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Parses the `doctor --json` envelope (`{ summary, hints, data: PreflightReport }`). Throws on a malformed shape. */
export function parseDoctorOutput(stdout: string): DoctorReport {
    let envelope: unknown;
    try {
        envelope = JSON.parse(stdout);
    } catch {
        throw new Error("doctor output is not valid JSON.");
    }
    const data = isRecord(envelope) ? envelope.data : undefined;
    if (!isRecord(data) || typeof data.os !== "string" || !STATUSES.includes(data.overall as string) || !Array.isArray(data.checks)) {
        throw new Error("doctor output does not contain a preflight report.");
    }

    const checks: DoctorCheck[] = [];
    for (const raw of data.checks) {
        if (!isRecord(raw) || typeof raw.id !== "string" || typeof raw.title !== "string"
            || !STATUSES.includes(raw.status as string) || typeof raw.reason !== "string") {
            continue;
        }
        checks.push({
            id: raw.id,
            title: raw.title,
            status: raw.status as PreflightStatus,
            reason: raw.reason,
            remediation: typeof raw.remediation === "string" && raw.remediation.trim() ? raw.remediation : undefined,
            affectedTools: Array.isArray(raw.affectedTools) ? raw.affectedTools.filter((t): t is string => typeof t === "string") : [],
        });
    }
    return {
        os: data.os,
        overall: data.overall as PreflightStatus,
        hasBlocker: data.hasBlocker === true || data.overall === "Blocked",
        checks,
    };
}

export type HandshakeOutcome =
    | { kind: "ok"; protocolVersion: number; cliVersion?: string }
    | { kind: "unsupported"; supportedVersions: number[] }
    | { kind: "invalid" };

/** Interprets the first stdout line of `stream --protocol jsonl` after a `hello` request. */
export function parseHandshakeOutput(stdout: string): HandshakeOutcome {
    const firstLine = stdout.split(/\r?\n/).find(line => line.trim().length > 0);
    if (!firstLine) {
        return { kind: "invalid" };
    }
    let frame: unknown;
    try {
        frame = JSON.parse(firstLine);
    } catch {
        return { kind: "invalid" };
    }
    if (!isRecord(frame)) {
        return { kind: "invalid" };
    }
    if (frame.type === "hello" && typeof frame.protocolVersion === "number") {
        const reportedVersion = typeof frame.cliVersion === "string" && frame.cliVersion.length <= 100
            ? frame.cliVersion.trim()
            : undefined;
        return frame.protocolVersion === REQUIRED_PROTOCOL_VERSION
            ? {
                kind: "ok",
                protocolVersion: frame.protocolVersion,
                cliVersion: reportedVersion && parseVersion(reportedVersion) ? reportedVersion : undefined,
            }
            : { kind: "unsupported", supportedVersions: [frame.protocolVersion] };
    }
    if (frame.type === "error" && frame.code === "protocol_version_unsupported") {
        const supported = Array.isArray(frame.supportedVersions)
            ? frame.supportedVersions.filter((v): v is number => typeof v === "number")
            : [];
        return { kind: "unsupported", supportedVersions: supported };
    }
    return { kind: "invalid" };
}

const RUNTIME_MISSING = /hostfxr|You must install or update \.NET|framework.*(was )?not found|DOTNET_ROOT|No frameworks were found/i;
const MISSING_EXECUTABLE = /ENOENT|not found|cannot find the file|no such file/i;

function missingCliIssue(cliPath: string, detail: string): EnvironmentIssue {
    return {
        kind: "cli-missing",
        severity: "error",
        message: `dotnet-diagnostics-cli was not found at '${cliPath}' (${detail}).`,
        guidance: `Install the CLI yourself (the extension never installs tools), or set the machine-scoped dotnetDiagnostics.cliPath to an existing executable. CLI ${MIN_CLI_VERSION} or later supports protocol v2; use ${MIN_FULL_FEATURES_CLI_VERSION} or later for the full extension feature set.`,
        command: INSTALL_COMMAND,
    };
}

function runtimeMissingIssue(detail: string): EnvironmentIssue {
    return {
        kind: "runtime-missing",
        severity: "error",
        message: `The CLI was found but could not start because a compatible .NET runtime is missing. ${detail}`.trim(),
        guidance: "The framework-dependent CLI requires a .NET 10 runtime. Install the runtime, set DOTNET_ROOT if it lives in a custom location, or point dotnetDiagnostics.cliPath at a self-contained CLI executable.",
    };
}

function failedIssue(what: string, run: CliRunResult): EnvironmentIssue {
    const detail = (run.timedOut ? "timed out" : run.stderr.trim() || run.stdout.trim() || `exit code ${String(run.exitCode)}`).slice(0, 300);
    return {
        kind: "cli-failed",
        severity: "error",
        message: `${what} failed: ${detail}`,
        guidance: "Run the same command in a terminal to see the full output, and check the dotnetDiagnostics.cliPath setting.",
    };
}

function classifyStartFailure(cliPath: string, run: CliRunResult): EnvironmentIssue | undefined {
    if (run.spawnError) {
        return MISSING_EXECUTABLE.test(run.spawnError)
            ? missingCliIssue(cliPath, run.spawnError)
            : { ...failedIssue("Starting the CLI", { ...run, stderr: run.spawnError }) };
    }
    const text = `${run.stderr}\n${run.stdout}`;
    if (RUNTIME_MISSING.test(text)) {
        return runtimeMissingIssue(text.trim().split(/\r?\n/)[0] ?? "");
    }
    return undefined;
}

/**
 * The global `dotnet tool update` only replaces the default `dotnet-diagnostics-cli` on PATH. For a
 * custom cliPath it would update a different executable, so give path-specific guidance instead.
 */
function outdatedFix(cliPath: string, minimumVersion = MIN_CLI_VERSION): Pick<EnvironmentIssue, "guidance" | "command"> {
    return cliPath === DEFAULT_CLI_PATH
        ? {
            guidance: "Update the CLI yourself with the command below, then recheck. The Getting Started walkthrough explains each step.",
            command: UPDATE_COMMAND,
        }
        : {
            guidance: `Update or rebuild the CLI at '${cliPath}' using whatever installed it (the global 'dotnet tool update' would not change this executable), or point dotnetDiagnostics.cliPath at a CLI ${minimumVersion} or later. The Getting Started walkthrough explains each step.`,
        };
}

interface ParsedVersion {
    major: number;
    minor: number;
    patch: number;
    prerelease: string | undefined;
}

function parseVersion(value: string | undefined): ParsedVersion | undefined {
    const match = value?.match(/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/);
    if (!match) {
        return undefined;
    }
    const [major, minor, patch] = match.slice(1, 4).map(Number);
    if (![major, minor, patch].every(Number.isSafeInteger)) {
        return undefined;
    }
    return { major, minor, patch, prerelease: match[4] };
}

function isVersionAtLeast(version: ParsedVersion, minimum: ParsedVersion): boolean {
    for (const part of ["major", "minor", "patch"] as const) {
        if (version[part] !== minimum[part]) {
            return version[part] > minimum[part];
        }
    }
    return version.prerelease === undefined || minimum.prerelease !== undefined;
}

function featureCompatibilityIssue(cliPath: string, cliVersion: string | undefined): EnvironmentIssue | undefined {
    const version = parseVersion(cliVersion);
    const minimum = parseVersion(MIN_FULL_FEATURES_CLI_VERSION)!;
    if (version && isVersionAtLeast(version, minimum)) {
        return undefined;
    }

    const knownVersion = version
        ? `CLI ${cliVersion} supports protocol v2 and live counters/GC pauses. Earlier capture, address navigation and export features depend on its version; allocation explorer, sensitive-value reveal and array-range navigation require CLI ${MIN_FULL_FEATURES_CLI_VERSION} or later.`
        : `CLI supports protocol v2, but its version is unavailable or unrecognized; support for diagnostic captures, heap/thread drilldowns, address navigation, exports, allocation explorer and value/range reads could not be verified. Use CLI ${MIN_FULL_FEATURES_CLI_VERSION} or later for the full extension feature set.`;
    return {
        kind: "feature-compatibility",
        severity: "warning",
        message: knownVersion,
        ...outdatedFix(cliPath, MIN_FULL_FEATURES_CLI_VERSION),
    };
}

function doctorIssues(doctor: DoctorReport): EnvironmentIssue[] {
    const issues: EnvironmentIssue[] = [];
    for (const check of doctor.checks) {
        if (check.status !== "Blocked" && check.status !== "Degraded") {
            continue;
        }
        const blocked = check.status === "Blocked";
        const affects = check.affectedTools.length > 0 ? ` Affects: ${check.affectedTools.join(", ")}.` : "";
        issues.push({
            kind: blocked ? "environment-blocked" : "environment-degraded",
            severity: blocked ? "error" : "warning",
            message: `${check.title}: ${check.reason}${affects}`,
            guidance: check.remediation
                ?? (check.id === "socket-uid"
                    ? "Run VS Code (and the CLI) as the same user as the target process."
                    : "Review the CLI doctor output for this check."),
        });
    }
    return issues;
}

/**
 * Runs the protocol handshake then `doctor --json` against `cliPath`. The handshake comes first
 * because it is the authoritative compatibility contract (CLI >= 0.29.0 speaks JSONL protocol v2);
 * older CLIs either answer `protocol_version_unsupported` or do not know `stream` at all.
 */
export async function checkEnvironment(cliPath: string, run: CliRunner, targetPid?: number): Promise<EnvironmentReport> {
    const hello = `${JSON.stringify({ type: "hello", protocolVersion: REQUIRED_PROTOCOL_VERSION })}\n`;
    const handshakeRun = await run(cliPath, ["stream", "--protocol", "jsonl"], hello);

    const startFailure = classifyStartFailure(cliPath, handshakeRun);
    if (startFailure) {
        return { cliPath, ready: false, issues: [startFailure] };
    }

    const handshake = parseHandshakeOutput(handshakeRun.stdout);
    if (handshake.kind === "unsupported") {
        const newer = handshake.supportedVersions.length > 0
            && handshake.supportedVersions.every(version => version > REQUIRED_PROTOCOL_VERSION);
        const issue: EnvironmentIssue = newer
            ? {
                kind: "extension-outdated",
                severity: "error",
                message: `The CLI only supports protocol version ${handshake.supportedVersions.join(", ")}; this extension speaks version ${REQUIRED_PROTOCOL_VERSION}.`,
                guidance: "Update the .NET Diagnostics extension, or install a CLI release that supports protocol v2.",
            }
            : {
                kind: "cli-outdated",
                severity: "error",
                message: `Protocol mismatch: the CLI supports protocol version ${handshake.supportedVersions.join(", ") || "unknown"}, but this extension requires version ${REQUIRED_PROTOCOL_VERSION} (CLI ${MIN_CLI_VERSION} or later).`,
                ...outdatedFix(cliPath),
            };
        return { cliPath, ready: false, issues: [issue] };
    }
    if (handshake.kind === "invalid") {
        const outdated: EnvironmentIssue = {
            kind: "cli-outdated",
            severity: "error",
            message: `Protocol mismatch: '${cliPath}' did not answer the JSONL protocol handshake. It is probably a CLI older than ${MIN_CLI_VERSION} without 'stream --protocol jsonl', or not dotnet-diagnostics-cli at all.`,
            ...outdatedFix(cliPath),
        };
        return { cliPath, ready: false, issues: [handshakeRun.exitCode === null || handshakeRun.timedOut ? failedIssue("The protocol handshake", handshakeRun) : outdated] };
    }

    const doctorRun = await run(cliPath, targetPid === undefined ? ["doctor", "--json"] : ["doctor", "--json", "--pid", String(targetPid)]);
    const doctorStartFailure = classifyStartFailure(cliPath, doctorRun);
    if (doctorStartFailure) {
        return { cliPath, ready: false, protocolVersion: handshake.protocolVersion, issues: [doctorStartFailure] };
    }

    // doctor exits 1 on a hard blocker but still writes the JSON envelope, so parse regardless of the exit code.
    let doctor: DoctorReport;
    try {
        doctor = parseDoctorOutput(doctorRun.stdout);
    } catch {
        return { cliPath, ready: false, protocolVersion: handshake.protocolVersion, issues: [failedIssue("'doctor --json'", doctorRun)] };
    }

    const cliVersion = handshake.cliVersion;
    const compatibilityIssue = featureCompatibilityIssue(cliPath, cliVersion);
    const issues = [...doctorIssues(doctor), ...(compatibilityIssue ? [compatibilityIssue] : [])];
    return {
        cliPath,
        ready: !doctor.hasBlocker,
        protocolVersion: handshake.protocolVersion,
        cliVersion,
        doctor,
        issues,
    };
}

/** One-line headline for a report. */
export function summarizeReport(report: EnvironmentReport): string {
    if (report.ready) {
        const warnings = report.issues.length;
        const version = report.cliVersion
            ? `CLI ${report.cliVersion}${report.issues.some(issue => issue.kind === "feature-compatibility") ? "" : " supports the full extension feature set"} (protocol v${String(report.protocolVersion)})`
            : `CLI speaks protocol v${String(report.protocolVersion)}`;
        const targetUnverified = report.doctor?.checks.some(check => check.id === "socket-uid" && check.status === "NotApplicable") ?? false;
        const scope = targetUnverified
            ? " Host-only check: permissions for a specific target process (socket UID) are not verified until you start a capture on it."
            : "";
        return warnings === 0
            ? `Host environment ready: ${version} and the host checks passed.${scope}`
            : `Host environment ready with ${warnings} warning(s): ${version}.${scope}`;
    }
    return report.issues.find(issue => issue.severity === "error")?.message
        ?? report.issues[0]?.message
        ?? "Environment is not ready.";
}

export type EnvironmentAction = "Copy Install Command" | "Copy Update Command" | "Open Getting Started" | "Open Settings" | "Recheck";

export interface EnvironmentHost {
    getCliPath(): string;
    run: CliRunner;
    /** Shows a notification and resolves with the clicked button label, if any. */
    notify(severity: "info" | "warning" | "error", message: string, actions: EnvironmentAction[]): Promise<EnvironmentAction | undefined>;
    copyToClipboard(text: string): Promise<void>;
    openWalkthrough(): Promise<void>;
    openSettings(): Promise<void>;
    log(line: string): void;
}

/** Actions offered for a report; install/update commands are only ever copied, never run. */
export function actionsForReport(report: EnvironmentReport): EnvironmentAction[] {
    const actions: EnvironmentAction[] = [];
    const first = report.issues.find(issue => issue.severity === "error")
        ?? report.issues.find(issue => issue.kind === "feature-compatibility")
        ?? report.issues[0];
    if (first?.kind === "cli-missing") {
        actions.push("Copy Install Command", "Open Settings", "Open Getting Started");
    } else if (first?.kind === "cli-outdated") {
        actions.push(...(first.command ? ["Copy Update Command" as const] : ["Open Settings" as const]), "Open Getting Started");
    } else if (first?.kind === "runtime-missing" || first?.kind === "cli-failed") {
        actions.push("Open Settings", "Open Getting Started");
    } else if (first?.kind === "extension-outdated") {
        actions.push("Open Getting Started");
    } else if (first?.kind === "feature-compatibility") {
        actions.push(...(first.command ? ["Copy Update Command" as const] : ["Open Settings" as const]), "Open Getting Started");
    }
    if (!report.ready || report.issues.some(issue => issue.kind === "feature-compatibility")) {
        actions.push("Recheck");
    }
    return actions;
}

/** Runs the check, reports the result, and re-checks while the user asks to. Returns the last report. */
export async function runEnvironmentCheckFlow(host: EnvironmentHost): Promise<EnvironmentReport> {
    for (;;) {
        const report = await checkEnvironment(host.getCliPath(), host.run);
        for (const issue of report.issues) {
            host.log(`[${issue.severity}] ${issue.message} ${issue.guidance}`);
        }
        const body = report.issues.length === 0
            ? summarizeReport(report)
            : [summarizeReport(report), ...report.issues.slice(report.ready ? 0 : 1).map(issue => issue.message), report.issues.find(i => i.severity === "error")?.guidance ?? report.issues[0].guidance]
                .filter((line, index, all) => line && all.indexOf(line) === index)
                .join(" ");
        const severity = report.ready ? (report.issues.length === 0 ? "info" : "warning") : "error";
        const choice = await host.notify(severity, body, actionsForReport(report));

        const command = report.issues.find(issue => issue.command)?.command;
        switch (choice) {
            case "Copy Install Command":
            case "Copy Update Command": {
                const text = choice === "Copy Install Command" ? INSTALL_COMMAND : (command ?? UPDATE_COMMAND);
                await host.copyToClipboard(text);
                const next = await host.notify("info", `Copied: ${text}\nRun it in a terminal yourself, then recheck. The extension does not run it for you.`, ["Recheck"]);
                if (next !== "Recheck") {
                    return report;
                }
                break;
            }
            case "Open Getting Started":
                await host.openWalkthrough();
                return report;
            case "Open Settings":
                await host.openSettings();
                return report;
            case "Recheck":
                break;
            default:
                return report;
        }
    }
}
