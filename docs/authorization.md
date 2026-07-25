# Authorization (bearer scopes)

How the **MCP server** (`dotnet-diagnostics-mcp`) decides which tools a caller may
invoke. Every networked tool call is gated by a **bearer token** whose **scopes** are
checked before the tool runs.

> **Not affected:** the **CLI** (`dotnet-diagnostics-cli`) and the **BenchmarkDotNet
> diagnoser** run in-process with no HTTP transport — there is no bearer and no scope
> check. `--stdio` mode is likewise unscoped (the MCP client *is* the process owner).
> Scopes only matter for the **HTTP** transport.

## Scopes

Scope names are kebab-case and **stable — never renamed**. Each is a distinct trust
boundary. A tool declares its requirement with `[RequireScope]` / `[RequireAnyScope]`;
startup fails fast if any `[McpServerTool]` is missing one.

`tools/list` surfaces the same static requirement in each tool's native MCP
`_meta` object, under `_meta.dotnetDiagnostics.auth`:

```json
{
  "requiredScopes": ["dump-write", "ptrace"],
  "semantics": "all",
  "authorized": false
}
```

- `requiredScopes` mirrors the attribute values.
- `semantics` is `all` for `[RequireScope]` and `any` for `[RequireAnyScope]`.
- `authorized` is evaluated for the current bearer token, so clients can hide or
  gray out tools the caller cannot invoke before attempting `tools/call`.

This is advisory discovery metadata, not a bypass: `tools/call` still enforces
the same scopes, and dispatcher tools can apply narrower per-parameter or
per-handle checks at runtime.

### Primary scopes

| Scope | Grants | Representative tools |
|---|---|---|
| `read-counters` | Process discovery + aggregated counters (`/proc` reads, bounded numeric EventCounters). No per-event data, no ptrace. | `inspect_process` (all views), `collect_events(kind="counters")` |
| `eventpipe` | EventPipe sessions + the handles their collectors mint. Exposes exception messages, allocation type names, activity/EventSource payloads. | `collect_events` (gc, exceptions, threadpool, contention, db, …), `collect_sample` (cpu / off_cpu / allocation), `query_snapshot` over those handles |
| `heap-read` | Read-only heap walks (type graphs, retention chains, addresses). | `inspect_heap(source="dump")`; **`inspect_heap(source="live")` additionally requires `ptrace`** |
| `ptrace` | Authorization for sensitive live-memory/attach operations. Live ClrMD readers additionally need `CAP_SYS_PTRACE` (Linux) / debug privilege (Windows). `collect_process_dump` carries this bearer scope as defense in depth but writes through diagnostic IPC and does not itself require Linux `CAP_SYS_PTRACE`. | `collect_thread_snapshot`, `capture_method_bytes`, `inspect_heap(source="live")` (+`heap-read`), `collect_process_dump` (+`dump-write`) |
| `dump-write` | Writes a full process dump (entire address space, zero redaction) to disk. **The single most dangerous scope.** Requires the separate `ptrace` bearer authorization scope as defense in depth; this does not imply a Linux kernel ptrace requirement for dump capture. | `collect_process_dump` (also needs `confirm=true` — see [below](#per-call-confirmation)) |
| `investigation-export` | Read-only meta/planning tools + drilldown over already-collected handles. | `start_investigation`, `export_investigation_summary`, `compare_to_baseline`, `query_snapshot(view="call-tree")` |
| `orchestrator-list` | Enumerate pods the orchestrator may see. Pure discovery. | `list_orchestrator(kind="pods")` |
| `orchestrator-attach` | Mutating Kubernetes calls that create ephemeral debug containers. | `attach_to_pod`, `detach_from_pod` |
| `azure-discovery` | Enumerate .NET workload candidates in an Azure subscription. | `discover_azure` |

The unified dispatcher tools span two scopes and authorize with **any** of them:
`collect_events` and `query_snapshot` accept `read-counters` **or** `eventpipe`;
`list_orchestrator` accepts `orchestrator-list` **or** `orchestrator-attach`. The
per-kind / per-view branch then tightens to the exact scope the requested operation
needs.

### Modifier scopes

Modifier scopes are checked with **literal membership** (`HasExplicitScope`) — the `*`
root pseudo-scope does **not** auto-grant them. An operator must layer each one on
deliberately.

| Modifier | Unlocks |
|---|---|
| `sensitive-heap-read` | Raw string contents / field-value previews (otherwise redacted) on heap + EventSource surfaces. |
| `sensitive-parameter-read` | `collect_sample(kind="method-params")` plus `query_snapshot(view="events")` over `method-params-capture` handles. Literal scope only; still requires `Diagnostics:AllowMethodParameterCapture=true` and `includeSensitiveValues=true` per call. |
| `eventsource-any` | `collect_events(kind="event_source")` against non-allowlisted providers (`unsafeProvider=true`). |
| `symbols-remote` | Remote symbol servers (`srv*http(s)://…`) outside the configured allowlist. |
| `orchestrator-admin` | List / operate on investigation handles minted by **other** MCP sessions. |
| `module-bytes-read` | `get_bytes` — stream raw PE / PDB / dump bytes out of the pod. Literal; gates the tool entirely. |

### The `*` (root) pseudo-scope

A token granted `*` resolves to the union of every **primary** scope (but **not** the
modifier scopes above). Used by `--stdio` / loopback defaults and the legacy
`MCP_BEARER_TOKEN` (see [Backward compatibility](#backward-compatibility)).

## Default policy by transport

| Transport | Default token resolution |
|---|---|
| **stdio** (`--stdio`) | Synthetic in-memory token with `*` scope. The MCP client is the process owner; no bearer ever crosses a network. |
| **Loopback HTTP** (`127.0.0.1` / `[::1]`) | Configured `Auth:BearerTokens` if present, else legacy `MCP_BEARER_TOKEN` → `*`. Developer ergonomics; unreachable from outside the host. |
| **Non-loopback HTTP** | `Auth:BearerTokens` is **required** — each entry must declare a non-empty scope set. Legacy `MCP_BEARER_TOKEN` is accepted but logs a deprecation `Warning`; a future release removes that fallback and refuses to start without scoped bearers. |

## Bearer tokens (config)

### `appsettings.json`

```json
{
  "Auth": {
    "BearerTokens": [
      { "Name": "dashboard",          "Token": "8f5e0c1a…", "Scopes": ["read-counters"] },
      { "Name": "oncall-investigator", "Token": "2c9447aa…", "Scopes": ["read-counters", "eventpipe", "heap-read", "investigation-export"] },
      { "Name": "platform-ops",        "Token": "…",         "Scopes": ["*"] }
    ]
  }
}
```

Env-var binder equivalent (ASP.NET Core rules — env overrides file):
`Auth__BearerTokens__0__Name=dashboard`, `Auth__BearerTokens__0__Token=…`,
`Auth__BearerTokens__0__Scopes__0=read-counters`, …

### Helm `values.yaml`

```yaml
mcp:
  bearerTokens:
    - name: dashboard
      valueFrom: { secretKeyRef: { name: mcp-bearer-tokens, key: dashboard } }
      scopes: [read-counters]
    - name: oncall-investigator
      valueFrom: { secretKeyRef: { name: mcp-bearer-tokens, key: oncall } }
      scopes: [read-counters, eventpipe, heap-read, investigation-export]
    - name: platform-ops
      valueFrom: { secretKeyRef: { name: mcp-bearer-tokens, key: platform-ops } }
      scopes: ["*"]
```

The chart expands each entry to the `Auth__BearerTokens__<i>__*` env shape above, sourcing
`Token` from the referenced Secret. Recommended Secret layout is a single multi-key
`Opaque` Secret (`stringData: { dashboard: …, oncall: …, platform-ops: … }`); token
*names* are non-sensitive and appear in logs, only values are secret. Cloud Run maps the
same env shape via `--set-secrets` / `--set-env-vars`. See
[`deploy/helm/README.md`](../deploy/helm/README.md) for the chart specifics.

## OIDC / JWT issuers (claims → scopes)

Instead of (or alongside) static opaque bearers, the HTTP transport can validate **OIDC
JWTs** so callers authenticate with a platform/workload identity and no shared secret. The
JWT path and the opaque path coexist: a JWT-shaped bearer is routed to OIDC validation, any
other bearer to the opaque `BearerTokenRegistry`.

A validated JWT maps onto the **same scope model** above:

- **Scopes** come from the token's scope claim — `scp` or `scope` by default, overridable
  with `MCP_OIDC_SCOPE_CLAIM`. Space-delimited values are split; each becomes a scope and is
  checked exactly like an opaque token's scopes (so a JWT must carry e.g. `eventpipe` to call
  `collect_events`). There is no implicit `*` for JWTs — grant scopes explicitly.
- **Granted scopes** (`MCP_OIDC_GRANTED_SCOPES`, or per-provider `grantedScopes`) let the
  operator assign MCP scopes server-side to any token that passes that issuer's
  audience + required-claim checks. Use it for trusted identities whose tokens carry **no**
  scope claim — e.g. a Kubernetes projected ServiceAccount token. Granted scopes are unioned
  with any scope-claim values; pin the identity with `requiredClaims` and keep the grant
  least-privilege.
- **Identity gating** uses `MCP_OIDC_REQUIRED_CLAIMS_JSON` — a JSON object mapping a claim to
  `null` (claim must be present), a string, or an array of allowed strings. Use it to pin the
  caller (`azp`/`client_id`/`sub`) so only your workload identity is accepted.
- **Principal name** (for audit logs) resolves from the first of `preferred_username`,
  `client_id`, `azp`, `appid`, `sub`.

Single issuer:

```bash
export MCP_OIDC_ISSUER="https://login.microsoftonline.com/<tenant-id>/v2.0"
export MCP_OIDC_AUDIENCE="api://dotnet-diagnostics-mcp"
export MCP_OIDC_REQUIRED_CLAIMS_JSON='{"azp":"<workload-identity-client-id>"}'
```

Multiple trusted issuers (e.g. a cloud workload identity **and** an in-cluster projected SA
token) — keep `MCP_OIDC_ISSUER`/`MCP_OIDC_AUDIENCE` for the first and add the rest via
`MCP_OIDC_PROVIDERS_JSON` (array of `{ issuer, audience, scopeClaim?, requiredClaims? }`):

```bash
export MCP_OIDC_PROVIDERS_JSON='[
  {
    "issuer": "https://kubernetes.default.svc.cluster.local",
    "audience": "dotnet-diagnostics-mcp",
    "requiredClaims": { "sub": "system:serviceaccount:diag:investigator" }
  }
]'
```

Each JWT is validated against every configured issuer in turn (standard OIDC metadata
discovery + signing-key validation); the first match wins, and a token matching no issuer is
rejected with the `401 {"kind":"unauthenticated"}` envelope. Provider-by-provider managed /
workload-identity recipes (Azure Workload Identity, AWS IRSA, GCP WIF, Kubernetes projected
SA) live in [`docs/client-setup.md`](./client-setup.md#managed--workload-identity-recipes-http-transport).

## Per-call confirmation

`collect_process_dump` requires explicit **human approval** on top of its
`dump-write` + `ptrace` scopes (defense in depth — a dump is irreversible and unbounded).
Approval is obtained one of two ways, capability-gated per client:

- **Native MCP Elicitation (preferred, issue #425).** When the client advertised the
  `elicitation` capability, the server **always** issues an `elicitation/create` request
  previewing the dump (PID / dump type / output path / disk-cost + heap-contents warning) with a
  single boolean `approve` field, and writes the dump only on an explicit approve — even if the
  caller also passed `confirm=true`. A decline writes nothing and returns an `approval_declined`
  envelope that deliberately does **not** invite a `confirm=true` retry; `confirm=true` cannot
  bypass a human decline on a capable client. The round-trip is stateless (no server-side
  pending-approval store).
- **`confirm=true` fallback.** Clients that did not negotiate elicitation keep the legacy
  contract: without `confirm=true` the tool returns a structured
  `{ "kind": "confirmation_required", targetPid, dumpType, outputDirectory }` envelope and
  writes nothing — no attach, no `createdump`.

No other tool takes `confirm` or elicits approval: adding it to read-only tools would train
callers to set it reflexively and destroy its signal.

## Drilldown over handles

`query_snapshot` reads from handles minted by a collector. The tool itself accepts
`read-counters` **or** `eventpipe`, then **re-applies the exact scope the originating
collector required** at runtime, keyed on the handle kind — so a handle minted under
`eventpipe` still demands `eventpipe` at query time even though the tool entry is broader.

On top of that, specific `(handle origin, view)` pairs require a **modifier** scope: e.g.
the `retention-paths` view on either a live or a dump heap snapshot requires
`sensitive-heap-read` (it can transitively expose managed-string contents). Reaching an
investigation handle minted by a **different** MCP session requires `orchestrator-admin`.

## Backward compatibility

| Situation | Behaviour |
|---|---|
| Only `MCP_BEARER_TOKEN`, stdio / loopback | Resolves to a synthetic `{ Name: "legacy", Scopes: ["*"] }`. No warning. |
| Only `MCP_BEARER_TOKEN`, non-loopback | Accepted but logs a deprecation `Warning`; a future release refuses it (use `Auth:BearerTokens`). |
| Both `MCP_BEARER_TOKEN` and `Auth:BearerTokens` | The scoped registry wins; the legacy variable is ignored with a `Warning`. |

Two content allowlists remain independent policies (a caller without the matching modifier
scope can still use allowlisted entries): `Diagnostics:EventSourceAllowlist` (vs
`eventsource-any`) and `Diagnostics:SymbolServerAllowlist` (vs `symbols-remote`). The old
deployment-wide `Diagnostics:AllowSensitiveHeapValues` flag is superseded by the
`sensitive-heap-read` scope.
