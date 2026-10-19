const { spawn, execFile } = require("node:child_process");
const { promisify } = require("node:util");
const { createInterface } = require("node:readline");
const { performance } = require("node:perf_hooks");
const path = require("node:path");

const execFileAsync = promisify(execFile);
const root = path.resolve(__dirname, "..");
const sampleDll = path.join(root, "samples", "CoreClrSample", "bin", "Release", "net10.0", "CoreClrSample.dll");
const cliDll = path.join(root, "src", "DotnetDiagnostics.Cli", "bin", "Release", "net10.0", "dotnet-diagnostics.dll");
const port = Number(process.env.LIVE_EVENTS_PORT || 5197);
const rate = Number(process.env.LIVE_EVENTS_RATE || 11_000);
const durationSeconds = Number(process.env.LIVE_EVENTS_DURATION_SECONDS || 330);
const acceptanceRun = durationSeconds >= 330;
const warmupSeconds = acceptanceRun ? 30 : 0;
const acceptanceRate = acceptanceRun ? 10_000 : 0;
const blockStdoutSeconds = Number(process.env.LIVE_EVENTS_BLOCK_STDOUT_SECONDS || 0);
const blockedStdoutRun = blockStdoutSeconds > 0;
const baseUrl = `http://127.0.0.1:${port}`;

if (process.platform !== "win32") {
    console.error("This acceptance runner currently samples per-process RSS with Windows PowerShell.");
    process.exit(2);
}

function median(values) {
    if (values.length === 0) return null;
    const ordered = [...values].sort((left, right) => left - right);
    const middle = Math.floor(ordered.length / 2);
    return ordered.length % 2 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2;
}

function percentile(values, fraction) {
    if (values.length === 0) return null;
    const ordered = [...values].sort((left, right) => left - right);
    return ordered[Math.min(ordered.length - 1, Math.ceil(fraction * ordered.length) - 1)];
}

async function waitForHttp(url, process, timeoutMs = 30_000) {
    const deadline = Date.now() + timeoutMs;
    while (Date.now() < deadline) {
        if (process.exitCode !== null) throw new Error(`Sample exited with code ${process.exitCode}.`);
        try {
            const response = await fetch(url);
            if (response.ok) return;
        } catch {}
        await new Promise(resolve => setTimeout(resolve, 250));
    }
    throw new Error(`Sample did not become ready at ${url}.`);
}

async function readWorkingSet(processId) {
    const command = `(Get-Process -Id ${processId} -ErrorAction SilentlyContinue).WorkingSet64`;
    try {
        const { stdout } = await execFileAsync("powershell.exe", ["-NoProfile", "-Command", command], { timeout: 5000 });
        const bytes = Number(stdout.trim());
        if (Number.isFinite(bytes) && bytes > 0) return bytes;
        throw new Error(`Get-Process returned no working-set value for PID ${processId}.`);
    } catch (error) {
        throw new Error(`Unable to sample working set for PID ${processId}: ${error.message}`, { cause: error });
    }
}

async function fetchForPhase(phase, url) {
    try {
        return await fetch(url);
    } catch (error) {
        const cause = error instanceof Error && "cause" in error ? error.cause : undefined;
        const detail = cause instanceof Error ? cause.message : error instanceof Error ? error.message : String(error);
        throw new Error(`${phase} request failed (${url}): ${detail}`, { cause: error });
    }
}

function createFrameReader(child) {
    const waiters = [];
    const pendingFrames = [];
    const stats = {
        observations: 0,
        eventBuckets: new Map(),
        latestQuality: undefined,
        maxFrameBytes: 0,
        receiptLatencySamplesMs: [],
        truncatedObservations: 0,
        malformedFrames: 0,
        terminal: undefined,
    };

    function publish(frame) {
        const waiterIndex = waiters.findIndex(waiter => waiter.predicate(frame));
        if (waiterIndex >= 0) {
            const [waiter] = waiters.splice(waiterIndex, 1);
            clearTimeout(waiter.timer);
            waiter.resolve(frame);
        } else if (frame.type !== "observation" && frame.type !== "quality") {
            if (pendingFrames.length >= 32) {
                throw new Error("CLI emitted more than 32 unmatched control frames.");
            }
            pendingFrames.push(frame);
        }
    }

    const lines = createInterface({ input: child.stdout });
    lines.on("line", line => {
        let frame;
        try {
            frame = JSON.parse(line);
        } catch (error) {
            stats.malformedFrames++;
            console.error(`Invalid CLI JSONL frame (${Buffer.byteLength(line, "utf8")} UTF-8 bytes): ${error.message}`);
            return;
        }
        stats.maxFrameBytes = Math.max(stats.maxFrameBytes, Buffer.byteLength(line, "utf8"));
        if (frame.type === "observation" && frame.kind === "event_source"
            && frame.provider === "CoreClrSample.LiveEventStress") {
            stats.observations++;
            if (frame.payloadTruncated) stats.truncatedObservations++;
            const elapsedSeconds = (performance.now() - stats.startedAt) / 1000;
            const bucket = Math.floor(elapsedSeconds);
            stats.eventBuckets.set(bucket, (stats.eventBuckets.get(bucket) || 0) + 1);
            if (elapsedSeconds >= warmupSeconds && stats.observations % 100 === 0
                && stats.receiptLatencySamplesMs.length < 10_000) {
                const receivedAt = Date.parse(frame.receivedAt);
                if (Number.isFinite(receivedAt)) {
                    stats.receiptLatencySamplesMs.push(Math.max(0, Date.now() - receivedAt));
                }
            }
        } else if (frame.type === "quality") {
            stats.latestQuality = frame;
        } else if (frame.type === "terminal") {
            stats.terminal = frame;
        }
        publish(frame);
    });

    return {
        stats,
        waitFor(predicate, timeoutMs = 30_000) {
            const index = pendingFrames.findIndex(predicate);
            if (index >= 0) return Promise.resolve(pendingFrames.splice(index, 1)[0]);
            return new Promise((resolve, reject) => {
                const waiter = {
                    predicate,
                    resolve,
                    timer: setTimeout(() => {
                        const index = waiters.indexOf(waiter);
                        if (index >= 0) waiters.splice(index, 1);
                        reject(new Error("Timed out waiting for a CLI protocol frame."));
                    }, timeoutMs),
                };
                waiters.push(waiter);
            });
        },
    };
}

async function main() {
    const sample = spawn("dotnet", [sampleDll, "--urls", baseUrl], { cwd: root, stdio: ["ignore", "ignore", "inherit"] });
    const cli = { current: undefined };
    let sampler;
    let startedAt = 0;
    let stdoutPausedAt = 0;
    let stdoutPausedDurationMs = null;

    try {
        await waitForHttp(`${baseUrl}/weatherforecast`, sample);

        cli.current = spawn("dotnet", [cliDll, "stream", "--protocol", "jsonl"], {
            cwd: root,
            stdio: ["pipe", "pipe", "inherit"],
        });
        const reader = createFrameReader(cli.current);
        const { stats } = reader;
        cli.current.once("error", error => console.error(`CLI process error: ${error.message}`));

        cli.current.stdin.write(`${JSON.stringify({ type: "hello", protocolVersion: 2 })}\n`);
        const hello = await reader.waitFor(frame => frame.type === "hello");
        if (hello.protocolVersion !== 2 || !hello.supportedLiveKinds?.includes("event_source")) {
            throw new Error("CLI did not negotiate event_source support.");
        }

        const [cliBaselineRss, sampleBaselineRss] = await Promise.all([
            readWorkingSet(cli.current.pid),
            readWorkingSet(sample.pid),
        ]);
        cli.current.stdin.write(`${JSON.stringify({
            type: "start",
            requestId: "live-events-acceptance",
            processId: sample.pid,
            kinds: [{
                kind: "event_source",
                providers: [{ name: "CoreClrSample.LiveEventStress", eventLevel: 4, keywords: "-1" }],
                observationCapacity: 256,
                acknowledgeRisk: "high",
            }],
        })}\n`);
        const started = await reader.waitFor(frame => frame.type === "started" || frame.type === "error");
        if (started.type === "error") throw new Error(`CLI rejected start: ${started.message}`);

        const quietReceivedBefore = stats.observations;
        if (!blockedStdoutRun) await new Promise(resolve => setTimeout(resolve, 5_000));
        const quietEventDelta = stats.observations - quietReceivedBefore;
        if (blockedStdoutRun && blockStdoutSeconds <= durationSeconds) {
            throw new Error("Blocked-stdout duration must exceed the workload duration so stop is sent while stdout is paused.");
        }
        startedAt = performance.now();
        stats.startedAt = startedAt;
        if (blockedStdoutRun) {
            stdoutPausedAt = performance.now();
            cli.current.stdout.pause();
        }

        const rssSamples = [];
        const generatedSamples = [];
        const samplingErrors = [];
        let sampling = false;
        sampler = setInterval(async () => {
            if (sampling) return;
            sampling = true;
            try {
                const elapsedSeconds = (performance.now() - startedAt) / 1000;
                const statusResponse = await fetchForPhase("workload status", `${baseUrl}/live-events/status`);
                if (!statusResponse.ok) throw new Error(`Workload status returned HTTP ${statusResponse.status}.`);
                const [status, cliRss, sampleRss] = await Promise.all([
                    statusResponse.json(),
                    readWorkingSet(cli.current.pid),
                    readWorkingSet(sample.pid),
                ]);
                generatedSamples.push({ elapsedSeconds, generatedEvents: status.generatedEvents, running: status.running });
                rssSamples.push({ elapsedSeconds, cliRss, sampleRss });
                const minute = Math.floor(elapsedSeconds / 60);
                if (minute > 0 && Math.abs((elapsedSeconds % 60) - 1) < 0.6) {
                    console.log(JSON.stringify({
                        progressSeconds: Math.round(elapsedSeconds),
                        observedEvents: stats.observations,
                        generatedEvents: status.generatedEvents,
                        cliRssBytes: cliRss,
                        sampleRssBytes: sampleRss,
                    }));
                }
                if (elapsedSeconds > 0 && Math.abs((elapsedSeconds % 30) - 1) < 0.6) {
                    console.log(`Soak running at ${Math.round(elapsedSeconds)}s; received=${stats.observations}.`);
                }
            } catch (error) {
                const message = error instanceof Error ? error.message : String(error);
                samplingErrors.push(message);
                console.error(`Acceptance sample failed: ${message}`);
            } finally {
                sampling = false;
            }
        }, 10_000);
        sampler.unref();

        const stressStartResponse = await fetchForPhase("event generator start",
            `${baseUrl}/live-events/stress/start?rate=${rate}&seconds=${durationSeconds}&payloadChars=16`);
        if (!stressStartResponse.ok) {
            throw new Error(`Stress workload start returned HTTP ${stressStartResponse.status}.`);
        }
        let workloadStatus = await stressStartResponse.json();
        while (workloadStatus.running) {
            await new Promise(resolve => setTimeout(resolve, 1_000));
            const statusResponse = await fetchForPhase("event generator status", `${baseUrl}/live-events/status`);
            if (!statusResponse.ok) throw new Error(`Workload status returned HTTP ${statusResponse.status}.`);
            workloadStatus = await statusResponse.json();
            if (workloadStatus.error) throw new Error(`Stress workload failed: ${workloadStatus.error}`);
        }
        const workload = {
            requestedEventsPerSecond: rate,
            requestedDurationSeconds: durationSeconds,
            generatedEvents: workloadStatus.generatedEvents,
            elapsedSeconds: workloadStatus.elapsedSeconds,
            achievedEventsPerSecond: workloadStatus.achievedEventsPerSecond,
        };
        const truncatedBefore = stats.truncatedObservations;
        let oversizedPayloadWasReportedTruncated = false;
        if (!blockedStdoutRun) {
            const oversizedResponse = await fetchForPhase("oversized payload", `${baseUrl}/live-events/oversized?payloadChars=20000`);
            if (!oversizedResponse.ok) throw new Error("Oversized payload fixture failed.");
            const oversizedDeadline = Date.now() + 20_000;
            while (Date.now() < oversizedDeadline && stats.truncatedObservations === truncatedBefore) {
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            oversizedPayloadWasReportedTruncated = stats.truncatedObservations > truncatedBefore;
        }
        await new Promise(resolve => setTimeout(resolve, 5_000));
        const finalElapsedSeconds = (performance.now() - startedAt) / 1000;
        const finalStatusResponse = await fetchForPhase("final workload status", `${baseUrl}/live-events/status`);
        if (!finalStatusResponse.ok) throw new Error(`Final workload status returned HTTP ${finalStatusResponse.status}.`);
        const [finalStatus, finalCliRss, finalSampleRss] = await Promise.all([
            finalStatusResponse.json(),
            readWorkingSet(cli.current.pid),
            readWorkingSet(sample.pid),
        ]);
        generatedSamples.push({
            elapsedSeconds: Math.min(durationSeconds, finalElapsedSeconds),
            generatedEvents: finalStatus.generatedEvents,
            running: finalStatus.running,
        });
        if (finalCliRss || finalSampleRss) {
            rssSamples.push({
                elapsedSeconds: Math.min(durationSeconds, finalElapsedSeconds),
                cliRss: finalCliRss,
                sampleRss: finalSampleRss,
            });
        }

        const stopSentAt = performance.now();
        cli.current.stdin.write(`${JSON.stringify({ type: "stop", sessionId: started.sessionId })}\n`);
        if (blockedStdoutRun) {
            const pauseRemainingMs = (blockStdoutSeconds * 1000) - (performance.now() - startedAt);
            if (pauseRemainingMs > 0) await new Promise(resolve => setTimeout(resolve, pauseRemainingMs));
            cli.current.stdout.resume();
            stdoutPausedDurationMs = performance.now() - stdoutPausedAt;
        }
        const terminal = await reader.waitFor(frame => frame.type === "terminal", 30_000);
        const stopToTerminalMs = performance.now() - stopSentAt;
        cli.current.stdin.end();
        await new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error("CLI did not exit after terminal.")), 10_000);
            cli.current.once("exit", () => { clearTimeout(timer); resolve(); });
        });

        const generatedRate = workload.generatedEvents / workload.elapsedSeconds;
        const measuredGenerated = generatedSamples.filter(sample => sample.elapsedSeconds >= warmupSeconds
            && sample.elapsedSeconds < durationSeconds).map(sample => sample.generatedEvents);
        const measuredGeneratedRates = [];
        generatedSamples.sort((left, right) => left.elapsedSeconds - right.elapsedSeconds);
        for (let index = 1; index < generatedSamples.length; index++) {
            const previous = generatedSamples[index - 1], current = generatedSamples[index];
            if (previous.elapsedSeconds >= warmupSeconds && current.elapsedSeconds <= durationSeconds) {
                measuredGeneratedRates.push((current.generatedEvents - previous.generatedEvents)
                    / (current.elapsedSeconds - previous.elapsedSeconds));
            }
        }
        const firstMeasuredMinute = rssSamples.filter(sample => sample.elapsedSeconds >= warmupSeconds
            && sample.elapsedSeconds < warmupSeconds + 60);
        const finalMeasuredMinute = rssSamples.filter(sample => sample.elapsedSeconds >= durationSeconds - 60
            && sample.elapsedSeconds <= durationSeconds);
        const firstCliMinuteMedian = median(firstMeasuredMinute.map(sample => sample.cliRss).filter(Boolean));
        const finalCliMinuteMedian = median(finalMeasuredMinute.map(sample => sample.cliRss).filter(Boolean));
        const maxCliRss = Math.max(0, ...rssSamples.map(sample => sample.cliRss || 0));
        const cliRssWithinBaselineBudget = cliBaselineRss !== null
            && maxCliRss <= cliBaselineRss + (128 * 1024 * 1024);
        const cliFinalMinuteTrendWithinBudget = firstCliMinuteMedian !== null && finalCliMinuteMedian !== null
            && finalCliMinuteMedian <= firstCliMinuteMedian + (16 * 1024 * 1024);
        const receivedCountsByMinute = Array.from({ length: Math.ceil((durationSeconds - warmupSeconds) / 60) }, (_, minute) => {
            const from = warmupSeconds + (minute * 60);
            const to = Math.min(durationSeconds, from + 60);
            let count = 0;
            for (let second = from; second < to; second++) count += stats.eventBuckets.get(second) || 0;
            return { fromSeconds: from, toSeconds: to, receivedEvents: count, receivedPerSecond: count / (to - from) };
        });
        const report = {
            environment: {
                platform: process.platform,
                arch: process.arch,
                node: process.version,
                dotnet: "See dotnet --info for SDK/runtime details.",
                rateRequested: rate,
                durationSeconds,
                warmupSeconds,
                targetPid: sample.pid,
                cliPid: cli.current.pid,
            },
            workload,
            observedEvents: stats.observations,
            receivedCountsByMinute,
            generatedRateAfterWarmup: measuredGeneratedRates.length
                ? { minimum: Math.min(...measuredGeneratedRates), median: median(measuredGeneratedRates), samples: measuredGeneratedRates.length }
                : null,
            cliReceiptToHostReadLatencyMsP95: percentile(stats.receiptLatencySamplesMs, 0.95),
            cliReceiptLatencySamplePolicy: "Every 100th received observation, capped at 10,000 samples.",
            malformedFrameCount: stats.malformedFrames,
            maxObservationFrameBytes: stats.maxFrameBytes,
            terminalQuality: stats.latestQuality ?? null,
            quietEventDelta: quietEventDelta,
            oversizedFixturePayloadCharacters: blockedStdoutRun ? null : 20_000,
            oversizedPayloadWasReportedTruncated,
            blockedStdoutSeconds: blockedStdoutRun ? blockStdoutSeconds : null,
            stdoutPausedDurationMs,
            stopToTerminalMs: blockedStdoutRun ? stopToTerminalMs : null,
            rssBytes: {
                cliBaselineBeforeStream: cliBaselineRss,
                sampleBaseline: sampleBaselineRss,
                firstMeasuredMinuteCliMedian: firstCliMinuteMedian,
                finalMeasuredMinuteCliMedian: finalCliMinuteMedian,
                maxCliRss,
                firstMeasuredMinuteSampleMedian: median(firstMeasuredMinute.map(sample => sample.sampleRss).filter(Boolean)),
                finalMeasuredMinuteSampleMedian: median(finalMeasuredMinute.map(sample => sample.sampleRss).filter(Boolean)),
                samples: rssSamples.length,
            },
            cliRssWithinBaselineBudget,
            cliFinalMinuteTrendWithinBudget,
            samplingErrors,
            harnessRssBytes: process.memoryUsage().rss,
            measuredGeneratedSampleCount: measuredGenerated.length,
        };
        console.log(JSON.stringify(report, null, 2));

        const passed = generatedRate >= acceptanceRate
            && (!acceptanceRun || (measuredGeneratedRates.length >= 29
                && Math.min(...measuredGeneratedRates) >= acceptanceRate))
            && stats.maxFrameBytes <= 16 * 1024
            && quietEventDelta === 0
            && (blockedStdoutRun ? (stats.latestQuality?.cliDispatchQueueDropped > 0)
                : oversizedPayloadWasReportedTruncated)
            && (blockedStdoutRun || receivedCountsByMinute.every(minute => minute.receivedPerSecond > 0))
            && stats.malformedFrames === 0
            && (!blockedStdoutRun || stopToTerminalMs <= 5_000)
            && (!acceptanceRun || (cliRssWithinBaselineBudget && cliFinalMinuteTrendWithinBudget))
            && samplingErrors.length === 0
            && (terminal.droppedObservations ?? 0) >= 0;
        if (!passed) process.exitCode = 1;
    } finally {
        if (sampler) clearInterval(sampler);
        if (cli.current && cli.current.exitCode === null) {
            cli.current.kill();
            await new Promise(resolve => cli.current.once("exit", resolve));
        }
        if (sample.exitCode === null) {
            sample.kill();
            await new Promise(resolve => sample.once("exit", resolve));
        }
    }
}

main().catch(error => {
    console.error(error.stack || error);
    process.exitCode = 1;
});
