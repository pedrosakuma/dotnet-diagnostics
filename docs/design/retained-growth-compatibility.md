# Retained-growth compatibility and migration

Issue [#1354](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1354)
adds an explicit repeated-pooling identity to
`dotnet-diagnostics/retained-growth-evidence/v1`. The evidence schema remains
additive v1; the nested identity is versioned independently as
`dotnet-diagnostics/retained-growth-compatibility/v1`.

## Compatibility identity

Every completed process-run pair that may enter repeated median, MAD, or
bootstrap calculations declares:

- runtime name/version, architecture, backend, target type, capture settings,
  process-run independence, and the capture-time comparison axis;
- retained-byte quantity `retained-shallow-bytes`, unit `bytes`;
- retained-instance quantity `retained-instances`, unit `instances`;
- aggregation `paired-delta-then-window-median`;
- normalization `absolute-over-declared-observation-opportunity`;
- scenario and scenario version;
- workload and workload version;
- canonical caller-declared workload parameters;
- workload phase;
- positive operation count and positive finite elapsed opportunity in seconds.

Both quantities remain absolute selected-type shallow totals and signed
current-minus-baseline deltas. They are not divided by operations or elapsed
time. The normalization metadata states that the absolute delta is comparable
only under the declared, matching observation opportunity.

Compatibility is checked before repeated pooling. Runtime/capture mismatches
inside one pair still make the pair evidence unavailable. Cross-run workload,
phase, opportunity, quantity, or other repeated-identity mismatches preserve
the exact baseline/current values and signed deltas for that pair, but set
`repeatedPoolingAvailability` to `incompatible` and exclude it from the repeated
population.

## Bootstrap provenance

Available repeated evidence records:

- the ordered accepted-input SHA-256;
- the canonical compatibility-identity SHA-256;
- distinct retained-byte and retained-instance seed SHA-256 values;
- PRNG `xoshiro256-star-star-v1`;
- quantile rule `linear-r7-v1`;
- resample count, confidence level, method version, and evidence schema.

The seed derivation includes the versioned compatibility identity. Identical
deltas collected under different workload parameters, phases, or observation
opportunities therefore do not share bootstrap provenance.

## Legacy migration

Old `retained-growth-evidence/v1` JSON remains readable. Existing runtime,
backend, type, quantity-representation, capture, independence, and comparison
fields are preserved. New quantity, workload, phase, observation-opportunity,
and compatibility-schema fields remain absent; readers must not synthesize
them from current defaults.

Legacy or partially identified pairs keep exact single-pair evidence when the
underlying captures are otherwise valid. They report
`repeatedPoolingAvailability: "incompatible"`, contribute no accepted repeated
pairs, and make repeated estimates unavailable with
`required_repeated_compatibility_identity_missing_or_mismatched`. To regain
repeated estimates, collect or submit fresh process-run pairs with explicit
`retained-growth-compatibility/v1` identity.

Failed and aborted attempts remain in denominators. Allocation traffic remains
separate from retained quantities. Root/path availability and observer effects
remain independent evidence dimensions. Neither single-pair nor repeated
output produces a leak, regression, health, or no-leak verdict.

## Surface boundary

Core owns the compatibility checks and calculations. The standalone CLI accepts
and emits the identity in JSON/JSONL and does not infer missing values. VS Code
continues to consume CLI JSONL and performs no statistics in TypeScript. The
objective contract is not exposed through MCP, adds no MCP tool, and contributes
zero bytes to the registered MCP catalog.
