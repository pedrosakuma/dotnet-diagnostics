# Explicit comparison-field selection

`advisory-comparison-fields-v1` is a separately declared, file-only selection
policy for an existing Phase-A response. It is not a repair of that response
and does not change the strict follow-up protocol.

The case motivating it is documented in
[`advisory-llm-followup-02-results.md`](advisory-llm-followup-02-results.md):
case 05 returned a bounded JSON response but omitted `alternatives`. Its
original schema/extraction failure remains immutable.

## Selected fields

The existing Phase-B candidate builder consumes only:

- `/hypotheses/<index>/text`;
- `/hypotheses/<index>/evidenceLocations`;
- `/uncertainty`;
- `/nextDiagnosticQuestion`.

It does not consume observations, alternatives or structured abstention.
The selector reads the exact selected values, records their source pointers,
and assigns controller-only claim IDs. It neither requires nor fabricates
source IDs. Missing, wrongly typed, duplicated or oversized selected fields
fail closed. Zero explicitly supplied hypotheses remain valid.

Unused standard fields are recorded by their actual JSON kind or `Missing`.
Thus an omitted `/alternatives` remains `Missing`, not an invented empty array.
No values from these fields, their availability markers, or source schema
statuses are forwarded to the comparator. Unknown or unresolved citations
remain unchanged, with mechanical resolution recorded separately.

`PrepareComparisonSubset` returns the candidate pair, source mapping, exact
prompt, raw/normalized response hashes, prompt hash and selection metadata.
It reuses the existing bounded extraction helpers, candidate construction and
follow-up B template. It never executes a model or declares the full source
schema compliant.

## Required controller boundaries

A real comparison requires a separately frozen controller manifest binding the
source protocol, follow-up plan, source run/case hashes, retained raw response,
projection and original A prompt, source format failure, subset seal, B prompt,
model/CLI, code/assembly and unchanged byte/time bounds.

The selected subset must be create-new sealed before invocation. For the
case-05 continuation, the permitted new budget is **one B and zero A calls**,
with original-first candidate order retained and no retry or fallback.
The comparator receives the same evidence and v1 rubric as the other
follow-up-02 primary comparisons, not any later proposed rubric improvements.

The strict `RunFollowupAsync` workflow still rejects the missing required
array. A separately recorded subset comparison does not rewrite that run or
retroactively make it technically complete. Any result must be reported as a
new comparison with explicit source provenance, not imported as human review
or converted into diagnostic ground truth.

The separately executed case-05 outcome is recorded in
[`advisory-case05-subset-results.md`](advisory-case05-subset-results.md).
