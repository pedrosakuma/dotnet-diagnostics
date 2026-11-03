# Canonical assessment validation reliability gate

Issue [#1372](https://github.com/pedrosakuma/dotnet-diagnostics/issues/1372)
replaces the operational evaluator's fail-fast unknown-property rejection with
bounded validation of the complete canonical assessment contract. This is an
evaluator reliability change only. It does not modify or rerun Protocols 1-9,
select an allocation field package, or authorize the blocked work in #1350.

## Retained evidence boundary

The original rejected Protocol 9 response is not available as an issue
artifact. The issue-artifact endpoint returned an empty inventory. The
implementation therefore uses only the privacy-safe evidence retained in the
merged Protocol 9 report:

- JSON path: `$.claims[0].posture`;
- value kind: `String`;
- raw response SHA-256 and exact UTF-8 byte count;
- assistant-content SHA-256 and exact UTF-8 byte count; and
- bounded canonical replay byte count, redaction, and truncation state.

Tests reproduce that path and value kind with synthetic content. They do not
claim to reconstruct the unavailable response body.

## Canonical projection

Scoring projects only these reviewed fields:

- root: `claims`, `uncertainty`, `uncertaintyRationale`,
  `unsupportedCertaintyFindings`, `usefulNextSteps`,
  `unhelpfulNextSteps`, and `summary`;
- claim: `index`, `support`, and `rationale`;
- reviewed claim alias: `text`, only at `$.claims[*].text`;
- unsupported-certainty finding: `claimIndex`, `category`, and `rationale`.

The frozen typed root addition `nextSteps` remains unscored and must still be
an array of strings. Other bounded properties on the root, claim objects, and
unsupported-certainty finding objects are annotation metadata. They are
ignored only after every canonical required-field, type, enum, index,
uniqueness, alias-conflict, and contradiction rule passes.

Canonical next steps remain strings. Objects inside `usefulNextSteps` or
`unhelpfulNextSteps` are not annotation containers: the object and its
properties fail validation. This avoids inventing a new next-step object
contract or alias.

## Explicit bounds and strict failures

Assessment JSON is validated under all of these limits:

- transport-configured UTF-8 byte limit;
- maximum JSON depth of 32;
- maximum retained structural diagnostics of 64; and
- maximum serialized diagnostic metadata of 8,192 bytes.

Validation scans the complete bounded document and records the total
diagnostic and additional-property counts. Retained diagnostics include JSON
path, property name, and value kind only. Values are never retained in
diagnostic metadata. Non-canonical property names are replaced by deterministic
SHA-256-derived tokens; canonical names and the retained `posture` shape remain
readable. When either the count or byte limit is reached, the inventory is
marked truncated while preserving a canonical failure diagnostic when the
budget permits.

Exact duplicate names, case-only collisions, case-insensitive canonical or
alias shadows, malformed JSON, size/depth violations, missing fields, invalid
types/enums/indices, duplicate claim/finding identities, alias conflicts, and
contradictory next-step classifications remain strict failures. No fuzzy
matching, semantic inference, retry, or success-shaped fallback is used.

## Replay, scoring, privacy, and accounting

Failure replay preserves bounded diagnostic property names so the documented
`$.claims[0].posture` shape replays deterministically while string values and
non-diagnostic property names remain redacted. Oversized, over-depth, malformed,
or non-canonicalizable responses retain only a bounded redacted envelope.

Successful assessments expose content-free annotation metadata separately from
the canonical scoring fields. Deterministic tests serialize the complete
scoring projection and prove it is byte-identical with and without ignored
annotations. Serialized ignored-annotation diagnostics, failure diagnostics,
and replay JSON are byte-counted exactly once in retained artifact accounting;
hashes and UTF-8 byte counts remain exact.
