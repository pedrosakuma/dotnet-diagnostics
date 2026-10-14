import { spawn } from "node:child_process";
import type { CliRunResult, CliRunner } from "./environmentCheck";

export interface CliRunnerTimeouts {
    /** Time before the child is asked to terminate. */
    timeoutMs: number;
    /** Extra time after the polite kill before the child is force-killed. */
    graceMs: number;
    /** Extra time after the force kill before the run is settled even without a `close` event. */
    settleMs: number;
    maxOutputChars?: number;
}

const DEFAULT_TIMEOUTS: CliRunnerTimeouts = { timeoutMs: 20_000, graceMs: 2_000, settleMs: 2_000 };

/**
 * Runs the CLI with a bounded lifetime, optionally writing `stdin` then closing it. Never rejects and
 * always settles: a child that ignores the polite kill is force-killed, and if it still produces no
 * `close` the run settles anyway as timed out. All timers and listeners are released on settle.
 */
export function createCliRunner(timeouts: CliRunnerTimeouts = DEFAULT_TIMEOUTS): CliRunner {
    return (cliPath, args, stdin) => new Promise<CliRunResult>(resolve => {
        let stdout = "";
        let stderr = "";
        let settled = false;
        let timedOut = false;
        const timers: NodeJS.Timeout[] = [];
        const child = spawn(cliPath, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });

        const finish = (result: CliRunResult): void => {
            if (settled) {
                return;
            }
            settled = true;
            timers.forEach(clearTimeout);
            child.removeAllListeners();
            child.stdout.removeAllListeners();
            child.stderr.removeAllListeners();
            child.stdin.removeAllListeners();
            child.stdout.destroy();
            child.stderr.destroy();
            child.stdin.on("error", () => undefined);
            child.stdin.destroy();
            child.on("error", () => undefined);
            resolve(result);
        };

        timers.push(setTimeout(() => {
            timedOut = true;
            child.kill("SIGTERM");
            timers.push(setTimeout(() => {
                child.kill("SIGKILL");
                timers.push(setTimeout(() => finish({ exitCode: null, stdout, stderr, timedOut: true }), timeouts.settleMs));
            }, timeouts.graceMs));
        }, timeouts.timeoutMs));

        const append = (chunk: string, channel: "stdout" | "stderr"): void => {
            if (timeouts.maxOutputChars !== undefined
                && stdout.length + stderr.length + chunk.length > timeouts.maxOutputChars) {
                child.kill("SIGKILL");
                finish({ exitCode: null, stdout: "", stderr: "", outputLimitExceeded: true });
            } else if (channel === "stdout") {
                stdout += chunk;
            } else {
                stderr += chunk;
            }
        };
        child.stdout.setEncoding("utf8").on("data", (chunk: string) => append(chunk, "stdout"));
        child.stderr.setEncoding("utf8").on("data", (chunk: string) => append(chunk, "stderr"));
        child.stdin.on("error", () => undefined);
        child.on("error", error => finish({ exitCode: null, stdout, stderr, spawnError: error.message }));
        child.on("close", code => finish({ exitCode: code, stdout, stderr, timedOut }));
        child.stdin.end(stdin ?? "");
    });
}

export const runCli: CliRunner = createCliRunner();
