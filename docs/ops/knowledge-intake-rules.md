# Knowledge Intake Rules

This is the first non-sensitive revision of the Phase 2 knowledge-intake
rules. It describes routing priority, approval, import quality gates, and
exception handling. It must not contain source text, credentials, customer
data, real server addresses, or secret-bearing URLs.

## Scope and Current Baseline

- IMA note connectivity is deferred and is not a prerequisite for this
  workflow or its acceptance.
- Future prepared material may be imported directly as `.md`, `.doc`, `.docx`,
  or `.pdf` after the same intake checks.
- The five formally imported files in `产品与解决方案` are the Phase 2 P1
  validation baseline. They are already indexed as `Completed` and have
  passed retrieval checks.
- `行业与市场研究` remains the P2 dataset and currently has zero documents.
- `待分类` is the P3 landing dataset and currently has zero documents. Do not
  upload a placeholder file to make a test pass.

## Destination Rules

| Material state | Destination | Required decision | Notes |
|---|---|---|---|
| Topic is clear, content is parseable, source and date are traceable | Existing matching topic dataset | Topic owner confirms classification | Do not create a duplicate topic dataset |
| Current, business-critical, approved reference material | Existing dataset with `priority=P1` metadata | Business owner approval | P1 is a routing priority, not a reason to create a new dataset |
| Topic is classified but not approved for P1 | Existing topic dataset with `priority=P2` | Topic owner confirms classification | P2 material must not be routed through the P1 filter |
| Topic or source is not yet clear, or review is incomplete | `待分类` with `priority=P3` or pending status | Knowledge-base operator records the pending review | Retrieval must identify the material as pending review; no automatic P1/P2 promotion |
| Question requires current or real-time information | Tavily route | No knowledge-base import for the question | Web evidence must retain title, URL, summary, and retrieval-time context |
| Duplicate or superseded file | Reject or archive the duplicate | Operator records the comparison and decision | Do not upload a second copy before the check |
| Empty file or image-only file without usable OCR text | Hold outside the dataset | Operator requests a parseable source or OCR result | Do not count it as an imported document |
| Sensitive material not approved for this workspace | Approved restricted location or reject | Business owner and access-control owner decide | Do not place it in a shared dataset |
| Expired or unverifiable material | Archive or reject | Topic owner records the reason | Do not use it as current P1 evidence |

## Required Metadata

For an approved topic import, record the fields available in Dify without
copying source content into this repository:

- `priority`: `P1`, `P2`, or `P3`;
- `topic`: the exact existing topic dataset name;
- `status`: `approved`, `pending`, `archived`, or another explicitly recorded
  operational state;
- built-in document identity and upload/index timestamps where available.

P1 requires business-owner confirmation. A document must not be promoted only
because retrieval returns a high score or because it appears in a useful
answer.

## Intake and Quality Gates

1. Register the source system, collection date, organizer, reviewer, proposed
   topic, and proposed priority before upload.
2. Check the source identifier and available file metadata for duplicates and
   superseded versions.
3. Select the existing destination dataset. Use `待分类` only when the topic
   or review state cannot yet be determined.
4. Import the file and apply the approved metadata. Do not mix P1 and lower
   priority material in the P1 retrieval filter.
5. Wait for every file in the batch to reach `Completed`. A partial or failed
   batch is not an accepted batch.
6. Run at least two non-sensitive retrieval checks for an accepted batch,
   including a distinctive phrase check where the source owner can provide
   one. Record only the test identifier, status, and conclusion.
7. Add the indexing and retrieval result to the migration ledger. Apply a
   source synchronization marker only after the quality gates pass.

The current five-file P1 baseline has already passed these gates. The empty P2
and P3 datasets remain valid states and do not require placeholder documents.

## Failure and Retry Rules

- Retry only the failed file or failed indexing operation; do not re-import a
  whole successful batch by default.
- Keep the failure reason, document identifier, and retry status in the
  ledger. Do not mark a failed item as synchronized.
- For parsing failure, prefer a parseable `.md`, Word, or PDF version. Keep
  image-only material outside the dataset until OCR or a text source is
  available.
- For incorrect topic or priority, stop the affected route, correct the
  metadata or destination, re-index, and repeat retrieval validation.
- For a suspected duplicate, preserve the existing accepted copy until the
  replacement is indexed and verified.
- For a workflow routing regression, restore the last verified Workflow
  version before changing source documents.
- For a Tavily failure, keep it as a Web failure. Never treat the failure as a
  knowledge-base hit and never store a plugin key in the ledger.

## Review and Change Control

The minimum operating cadence is weekly collection, topic review, deduplication,
batch import, and retrieval sampling. Urgent imports may be handled separately
but cannot skip approval, metadata, indexing, or ledger recording. IMA material
joins this cadence only after the deferred export/import step is resumed.

This first revision is based on the completed P1 hit, empty-P3 fallback,
Web-intent, Web-failure, and normalization-failure checks. A draft-only
controlled P2 branch reuse using the existing P1 baseline also passed, but it
does not change the P2 dataset state or prove real P2 coverage. The positive
P2-dataset hit remains an explicit follow-up because that dataset is empty; no
test document may be created to close that gap.

| Date | Revision | Evidence basis | Open item |
|---|---|---|---|
| 2026-08-06 | Initial Phase 2 revision | Existing five-file P1 baseline and R1, R3-R9 route checks | Repeat the positive P2-hit check when real P2 material exists |
