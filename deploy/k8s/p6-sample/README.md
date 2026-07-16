# p6-sample — two replicas of CoreClrSample for the kind integration test

These manifests are loaded by `.github/workflows/kind-integration.yml` and back
the final acceptance bullet of issue #20:

> Integration test that spins up two replicas of the sample target, attaches to
> a specific one by label, runs diagnostics through the orchestrator, and
> confirms the target carries the chosen replica's command-line discriminator.

Each Deployment runs **one** CoreClrSample Pod tagged with a discriminating
`p6-target={a,b}` label so the test can:

1. Call `list_orchestrator(kind="pods")` with `labelSelector=app=p6-sample,p6-target=a`.
2. Call `attach_to_pod` against the single matching Pod.
3. Proxy `inspect_process(view="list")` through the returned handle and confirm
   the surfaced PID's command line contains `--p6-target=a` (replica `b` carries
   `--p6-target=b`).

Both Pods carry the `diagnostics.dotnet.io/prepared=true` label, mount a shared
`/tmp` emptyDir, and run as UID/GID 10001 — matching `central-target.yaml`'s
recipe so the orchestrator's ephemeral-container attach path is the production
one.

The image tag (`coreclr-sample:p6`) is built and `kind load`-ed by the workflow;
no GHCR push happens.
