import { spawn } from "node:child_process";
import * as vscode from "vscode";
import {
    INSTALL_COMMAND,
    UPDATE_COMMAND,
    WALKTHROUGH_ID,
    runEnvironmentCheckFlow,
    type CliRunResult,
    type CliRunner,
    type EnvironmentHost,
} from "./environmentCheck";

const CLI_TIMEOUT_MS = 20_000;

/** Runs the CLI with a timeout, optionally writing `stdin` then closing it. Never rejects. */
export const runCli: CliRunner = (cliPath, args, stdin) => new Promise<CliRunResult>(resolve => {
    let stdout = "";
    let stderr = "";
    let settled = false;
    let timedOut = false;
    const finish = (result: CliRunResult): void => {
        if (!settled) {
            settled = true;
            clearTimeout(timer);
            resolve(result);
        }
    };
    const child = spawn(cliPath, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    const timer = setTimeout(() => {
        timedOut = true;
        child.kill();
    }, CLI_TIMEOUT_MS);
    child.stdout.setEncoding("utf8").on("data", (chunk: string) => { stdout += chunk; });
    child.stderr.setEncoding("utf8").on("data", (chunk: string) => { stderr += chunk; });
    child.stdin.on("error", () => undefined);
    child.on("error", error => finish({ exitCode: null, stdout, stderr, spawnError: error.message }));
    child.on("close", code => finish({ exitCode: code, stdout, stderr, timedOut }));
    child.stdin.end(stdin ?? "");
});

function createHost(output: vscode.OutputChannel): EnvironmentHost {
    return {
        getCliPath: () => vscode.workspace.getConfiguration("dotnetDiagnostics").get<string>("cliPath", "dotnet-diagnostics-cli").trim()
            || "dotnet-diagnostics-cli",
        run: runCli,
        notify: async (severity, message, actions) => {
            const show = severity === "error"
                ? vscode.window.showErrorMessage
                : severity === "warning" ? vscode.window.showWarningMessage : vscode.window.showInformationMessage;
            return (await show(message, ...actions)) as (typeof actions)[number] | undefined;
        },
        copyToClipboard: text => Promise.resolve(vscode.env.clipboard.writeText(text)),
        openWalkthrough: async () => {
            await vscode.commands.executeCommand("workbench.action.openWalkthrough", WALKTHROUGH_ID, false);
        },
        openSettings: async () => {
            await vscode.commands.executeCommand("workbench.action.openSettings", "dotnetDiagnostics.cliPath");
        },
        log: line => output.appendLine(line),
    };
}

/** Registers the onboarding commands. Install/update commands are only shown or copied, never executed. */
export function registerOnboarding(context: vscode.ExtensionContext, output: vscode.OutputChannel): void {
    const host = createHost(output);
    context.subscriptions.push(
        vscode.commands.registerCommand("dotnetDiagnostics.checkEnvironment", () => runEnvironmentCheckFlow(host).then(() => undefined)),
        vscode.commands.registerCommand("dotnetDiagnostics.copyInstallCommand", async () => {
            await host.copyToClipboard(INSTALL_COMMAND);
            const choice = await vscode.window.showInformationMessage(
                `Copied: ${INSTALL_COMMAND}\nRun it in a terminal yourself (to update an existing CLI use: ${UPDATE_COMMAND}), then recheck. The extension never installs tools.`,
                "Recheck",
            );
            if (choice === "Recheck") {
                await runEnvironmentCheckFlow(host);
            }
        }),
        vscode.commands.registerCommand("dotnetDiagnostics.openGettingStarted", () => host.openWalkthrough()),
    );
}
