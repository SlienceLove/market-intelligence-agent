# Phase 2 Baseline Checklist

This is a non-sensitive baseline record for Phase 2. It must not contain
credentials, real server addresses, source document text, or customer data.

## Scope decision

- IMA note connectivity is deferred and is not a Phase 2 dependency.
- Document-hit tests use the five formally imported market-department files
  already verified in Dify. Do not upload new test documents.
- Future prepared files may be uploaded directly as `.md`, Word `.doc/.docx`,
  or `.pdf`.

## Baseline inventory

| Item | Expected baseline | Evidence available on 2026-08-06 | Live check | Status |
|---|---|---|---|---|
| Dify platform | Dify 1.16.1 and `market-intelligence` workspace available | `docs/ops/dify-deployment.md` records successful initialization | Authenticated Dify UI session confirmed by the operator; workspace, Knowledge and Workflow controls are available for the Phase 2 operations | Confirmed |
| Topic dataset 1 | `行业与市场研究` exists and is queryable | `docs/PROGRESS.md` records creation and retrieval validation | User confirmed in Dify UI on 2026-08-06 | Confirmed |
| Topic dataset 2 | `产品与解决方案` exists and is queryable | `docs/PROGRESS.md` records creation and retrieval validation | User confirmed in Dify UI on 2026-08-06 | Confirmed |
| Five formal files | All five show `Completed` and have retrieval evidence | `docs/PROGRESS.md` records five successful indexing/retrieval checks | User confirmed all five are `Completed` in Dify UI on 2026-08-06 | Confirmed |
| P3 dataset | `待分类` dataset existence and document count recorded | Empty landing dataset is recorded in the routing runbook and registry | User confirmed `待分类` exists and contains 0 documents; no placeholder file was uploaded | Confirmed |
| Web search | Tavily smoke test returns a result | `docs/PROGRESS.md` records successful smoke test | Phase 2 direct-Web and knowledge-miss fallback paths were exercised in the published Workflow | Confirmed |

## Non-sensitive test record template

Record only IDs, paths, status, duration, and failure reason. Do not paste
question text, answer text, source snippets, credentials, or URLs containing
secrets.

| ID | Test category | Expected path | Actual path | Result | Duration/status | Failure reason |
|---|---|---|---|---|---|---|
| R1 | Existing five-file document hit / P1 candidate | P1 hit; stop lower-priority retrieval | Normalize Question -> Web Intent IF -> Retrieve P1 -> Normalize P1 (`hit=true`, `count=3`, `quality_note=ok`) -> P1 Hit IF -> End - P1; P2/P3/Tavily not executed | Passed | HTTP 200 SSE; workflow succeeded | None |
| R2 | Existing five-file topic hit | P1 miss → P2 hit; do not query P3 | P1 miss → empty P2 → empty P3 → Tavily fallback | Deferred | P2 dataset has 0 documents | Add real P2 material later and repeat the positive-hit check; do not upload a test document |
| R2-C | Controlled P2 branch reuse using the existing P1 baseline | P1 miss → P2 hit; do not query P3 | Draft-only P1 non-match filter → P2 retrieval from the existing five-file dataset → P2 hit → End - P2 | Passed (controlled) | HTTP 200 SSE; `hit=true`, `count=3`, `quality_note=ok`; workflow succeeded | Validates branch and short-circuit behavior only; does not replace the real P2 dataset hit |
| R3 | P3 empty or pending dataset | P1/P2 miss → P3 result/empty → Web fallback | P1 miss → P2 miss → P3 empty → Tavily fallback | Passed | Empty-P3 path observed | None |
| R4 | Explicit web or time-sensitive request | Direct Tavily; no knowledge retrieval | Normalize → Web intent → Tavily → normalized Web evidence | Passed | Direct-Web path observed | None |
| R5 | No knowledge-base evidence | P1/P2/P3 miss → Tavily fallback | P1 miss → P2 miss → P3 miss → Tavily fallback | Passed | Knowledge-miss fallback observed | None |
| R6 | Current-status request | Direct Tavily; preserve source URL metadata | Time-sensitive intent → Tavily → title/URL/summary output | Passed | Direct-Web path and URL metadata observed | None |
| R7 | Empty/whitespace input | Invalid-query response; no retrieval or Web call | Normalize → invalid query → terminal response | Passed | No-retrieval path observed | None |
| R8 | Tavily failure or empty result | Actionable failure response; no fabricated answer | Tavily failure → Web failure response | Passed | Failure branch observed and restored | None |
| R9 | Retrieval normalization failure | `hit=false`; follow failure path | Controlled malformed JSON in `Normalize P1` → `hit=false` / `empty_result` → P2/P3/Tavily fallback | Passed | HTTP 200 SSE; workflow succeeded; baseline restored | None |
| R10 | Duplicate source-file import | Keep one copy; record cleanup and revalidation | Not applicable; no new or duplicate file was imported | N/A | No duplicate operation performed | None |
| R11 | Published workflow restore and re-publish | Restore the verified version, publish the restored draft, then run a smoke check | Restore `phase2-routing-v1` → publish `phase2-routing-v1` → `draft/run` Web path | Passed | HTTP 200; SSE run succeeded | None |

## P2-00 completion gate

- [x] Dify UI baseline confirmation is available from the operator.
- [x] Both topic datasets and the five-file `Completed` status are rechecked.
- [x] P3 dataset existence and current document count (0) are recorded.
- [x] Non-sensitive test and result-record templates are prepared.
- [x] P2-01 priority registration can start without inventing dataset or file
      facts.

**Current status:** Complete based on the repository evidence and the user's
2026-08-06 authenticated Dify UI confirmation. R1, R2-C, and R3–R9 passed;
R2's positive real-P2-dataset case is deferred until real P2 material exists,
R10 is not applicable to this round, and R11 passed after restoring and
re-publishing the verified workflow version. The controlled R2-C draft change
was removed and the baseline hash and graph were restored before closing the
check.
No credential was read or stored in the repository.
