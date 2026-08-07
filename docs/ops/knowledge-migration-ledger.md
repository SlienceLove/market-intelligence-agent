# Knowledge Migration Ledger

This ledger is a non-sensitive recording template for knowledge-base intake and
revalidation. It must not contain source text, answer text, credentials,
customer data, real server addresses, or secret-bearing URLs.

## Recording Rules

- Use one row per import batch and add a retry row only for a failed or
  reprocessed item.
- Use a stable internal `batch_id`; do not put source text in the identifier.
- Record counts, statuses, test identifiers, and failure reasons only.
- Record the source synchronization marker only after indexing and retrieval
  gates pass.
- A batch is accepted only when every file is `Completed` and the required
  retrieval checks pass.
- The five-file Phase 2 baseline is an existing imported batch. Its original
  external batch identifier is not reconstructed in this repository.

## Current Non-Sensitive Snapshot

| Scope | Dataset | Document count | Index state | Retrieval state | Decision |
|---|---|---:|---|---|---|
| Phase 1 formal validation baseline | `产品与解决方案` | 5 | All `Completed` | Baseline and P1 route checks passed | Keep as P1; no duplicate import |
| Current P2 topic dataset | `行业与市场研究` | 0 | No documents | Positive P2-hit check deferred | Do not upload a test placeholder |
| P3 landing dataset | `待分类` | 0 | No documents | Empty-P3 fallback passed | Keep empty until real unclassified material arrives |
| Deferred source path | IMA | Not imported in this phase | Not applicable | Not applicable | Resume only as a separate approved task |

## Batch Entry Template

| Field | Value |
|---|---|
| `batch_id` | `BATCH-YYYYMMDD-NN` |
| Collection date | `YYYY-MM-DD` |
| Source system | `file_delivery`, `IMA_export`, or another approved label |
| Organizer | Internal role or approved operator identifier |
| Reviewer/approver | Internal role or approved operator identifier |
| Proposed topic | Existing dataset name or `待分类` |
| Proposed priority | `P1`, `P2`, or `P3` |
| Target dataset | Exact Dify dataset name |
| File count | Integer |
| Duplicate check | `clear`, `duplicate_rejected`, `superseded_archived`, or `review_required` |
| Import result | `not_started`, `imported`, `partial`, `failed`, or `rejected` |
| Index result | `pending`, `indexing`, `completed`, or `failed` |
| Retrieval test IDs | Non-sensitive IDs only |
| Retrieval conclusion | `passed`, `failed`, or `deferred` |
| Migration result | `accepted`, `retry_required`, `archived`, or `rejected` |
| Source sync marker | `not_applicable`, `pending`, or `applied_after_acceptance` |
| Failure reason | Short operational reason, or `none` |
| Review date | `YYYY-MM-DD` |
| Notes | No source text or credentials |

## Operating Sequence

1. Create the batch row before uploading.
2. Confirm topic, priority, approval, and duplicate status.
3. Import only to the selected existing dataset.
4. Update import and indexing status until every file is `Completed` or the
   batch is explicitly marked failed.
5. Run the required retrieval checks and record their identifiers and outcome.
6. Apply the source synchronization marker only for an accepted batch.
7. For a retry, preserve the original failure row and add the retry result.

## Current Follow-Up

The next positive P2 entry can be added only when a real, topic-classified P2
file is delivered and approved. Until then, the empty P2/P3 states and the
deferred IMA path are expected baseline conditions, not failed migrations.

The controlled P2 branch reuse check used the existing P1 baseline only inside
the Workflow draft. It was not an import, did not create a ledger batch, and did
not change the snapshot above.
