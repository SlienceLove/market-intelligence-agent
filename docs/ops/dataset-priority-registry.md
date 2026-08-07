# Dataset Priority Registry

This registry contains only non-sensitive dataset metadata and priority
decisions. It must not contain credentials, source text, customer data, or
real server addresses.

## Current decision

- `P1` is a logical priority and requires business-owner approval.
- The business owner confirmed on 2026-08-06 that all five files in
  `产品与解决方案` are P1.
- `行业与市场研究` remains P2.
- The empty `P3`/`待分类` landing dataset exists with zero documents. Keep it
  empty until a real unclassified file arrives.
- Do not create a duplicate dataset merely to represent a priority name.

## Registry

| Dataset display name | Logical level | Topic | Content state | Owner/approver | Effective date | Notes |
|---|---|---|---|---|---|---|
| `行业与市场研究` | P2 | 行业与市场研究 | Approved topic dataset; currently queryable | Pending team assignment | 2026-08-06 | Existing dataset; no duplicate created |
| `产品与解决方案` | P1 | 产品与解决方案 | Five files indexed as `Completed`; retrieval baseline passed | Business owner confirmed | 2026-08-06 | Use existing dataset; assign `priority=P1` metadata to all five files |
| `待分类` | P3 | Unclassified | Empty landing dataset; 0 documents | Knowledge-base operator | 2026-08-06 | Created for the P3 retrieval node; do not upload a placeholder document |

## P1 decision record

- Decision: all five validated files in `产品与解决方案` are P1.
- Decision date: 2026-08-06.
- Approval source: user/business-owner confirmation in the project thread.
- No separate P1 dataset is required while metadata filtering is available.

## Metadata-filter and permission check

- Dify 1.16.1 source verification: Knowledge Retrieval supports
  `disabled`/`automatic`/`manual` metadata filtering modes and passes filtering
  conditions into dataset retrieval.
- The Knowledge Retrieval UI exposes metadata conditions and comparison
  operators; use a manual `priority = P1` condition for the P1 branch.
- Dataset metadata management requires an authenticated, initialized account,
  an active license, and dataset edit/management RBAC permissions.
- Workflow draft/publish requires application edit permission.
- Live account permission confirmation remains a Dify UI checkpoint; do not
  store a token in this repository.
- P2-02 UI operation checklist: `docs/ops/phase2-workflow-routing-runbook.md`.
- If the live UI rejects metadata operations, use an explicitly approved
  `P1-产品与解决方案` dataset only after re-indexing and retrieval validation;
  do not duplicate the current dataset by assumption.
