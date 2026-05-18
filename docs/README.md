# Documentation

| File | What it covers |
|---|---|
| [`tool-reference.md`](./tool-reference.md) | Every MCP tool: parameters, returns, runtime requirements, examples |
| [`investigation-playbooks.md`](./investigation-playbooks.md) | Step-by-step recipes for common symptoms (slow, leaking, 5xx, slow HTTP, NativeAOT) |
| [`client-setup.md`](./client-setup.md) | Connecting to the server from the C# SDK, GUI MCP clients, and `curl` smoke tests |
| [`../deploy/k8s/README.md`](../deploy/k8s/README.md) | Sidecar topology for Kubernetes, including the required pod-level settings |

Planned but not yet written:

- `architecture.md` — high-level component map (Core vs Server, EventPipe pipeline)
- `nativeaot-support.md` — capability matrix and limitations (currently summarized inside `tool-reference.md` and `investigation-playbooks.md`)

