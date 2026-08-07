# Phase 2 Workflow Routing Runbook

This runbook is the non-sensitive execution record for `phase2-knowledge-routing`.
It contains no credentials, source text, customer data, or real server address.

## Permission preflight

The operator needs:

- an initialized Dify account with an active license;
- dataset edit/management permission for metadata fields and document metadata;
- application edit permission for Workflow draft, debug, and publish operations.

Dify 1.16.1 source verification shows that Knowledge Retrieval supports
metadata filtering modes `disabled`, `automatic`, and `manual`, with manual
conditions and comparison operators passed into dataset retrieval.

## Browser/debug host note

The Studio page and its Socket.IO collaboration channel must use the same
hostname. If the page is opened on `127.0.0.1` while the collaboration channel
resolves to `localhost`, the socket can be rejected and Studio may remain
blocked by `同步数据中`. For the temporary local environment, open the same
Workflow route on the configured `localhost` host so the existing authenticated
browser session and collaboration channel share a host. This is a browser
session workaround only; no permanent startup or deployment configuration was
changed.

## Knowledge metadata setup

In `产品与解决方案`:

1. Open dataset metadata management.
2. Create or reuse these non-sensitive fields: `priority`, `topic`, `status`.
3. Set all five existing documents to:
   - `priority = P1`
   - `topic = 产品与解决方案`
   - `status = approved`
4. Do not upload, duplicate, or delete the five documents.
5. Confirm every document remains `Completed` and retrieval still returns a
   recognizable chunk.

`行业与市场研究` remains P2 and currently has 0 documents. The empty
`待分类` landing dataset now exists with document count `0`; do not upload a
placeholder document.

## Workflow draft

Create a draft named `phase2-knowledge-routing` with these connections:

```text
Start
  -> Normalize Question
  -> Web Intent IF
       true  -> Tavily Search -> Normalize Web -> Answer from Web
       false -> Retrieve P1 (priority = P1)
                  -> Normalize P1 -> P1 Hit IF
       true  -> End - P1 (evidence)
                       false -> Retrieve P2 Topics
                                  -> Normalize P2 -> P2 Hit IF
                                       true  -> End - P2 (evidence)
                                       false -> Retrieve P3 Unclassified
                                                  -> Normalize P3 -> P3 Hit IF
                                                       true  -> End - P3 (evidence, pending review)
                                                       false -> Tavily Search
                                                                  -> Normalize Web
                                                                  -> End - Web (evidence with URL)
```

### Retrieval node boundaries

| Node | Dataset selection | Metadata filter | Top K | Score threshold |
|---|---|---|---:|---|
| Retrieve P1 | `产品与解决方案` | `priority = P1` | 3 | Disabled, keep Phase 1 baseline |
| Retrieve P2 Topics | `行业与市场研究` | None required | 3 | Disabled, keep Phase 1 baseline |
| Retrieve P3 Unclassified | `待分类` if created | None required | 3 | Disabled, keep Phase 1 baseline |

P1 hit must not connect to P2, P3, or Tavily. P2 hit must not connect to P3.
Only an empty/invalid result from all knowledge branches may reach Tavily.

## Draft record

- Workflow name: `phase2-knowledge-routing`
- Published version: `phase2-routing-v1` (`2026-08-06 06:53:00` Dify version timestamp)
- Draft graph: 22 nodes / 22 edges
- Metadata fields created/verified: `priority`, `topic`, `status`; all five P1 documents rechecked
- Five P1 documents rechecked: `Completed`; `priority=P1`, `topic=产品与解决方案`, `status=approved`
- P3 dataset status/count: `待分类` exists; `0` documents
- Debug result: R1, R2-C, R3, R4, R5, R6, R7, R8, and R9 passed; R2 real-P2-dataset coverage remains pending because the P2 dataset is empty
- Publish result: Published; Phase 2 validation used evidence-output mode because no LLM channel was available at that validation point. A later 2026-08-06 Phase 3 preflight observed the configured `Gptpro` provider and default text model; this does not change the published Phase 2 graph or its historical validation result.
- Live P2-04 recheck: after aligning the Studio host, the collaboration socket
  completed its handshake, the loading overlay disappeared, and the published
  version endpoint returned the 22-node/22-edge graph.
- Live route traces: empty input was rejected before `draft/run`; direct Web
  input executed `Normalize Question -> Web Intent IF -> Tavily Search ->
  Normalize Web -> WEB HIT IF -> END - WEB`; a non-hit input executed P1,
  P2, P3, and then Tavily. No source text or query was recorded here.
- Latest P1-hit regression: an existing P1-file keyword produced HTTP 200 SSE
  and a `succeeded` workflow. The observed path was `Start -> Normalize Question
  -> Web Intent IF -> Retrieve P1 -> Normalize P1 -> P1 Hit IF -> End - P1`;
  `Normalize P1` returned `hit=true`, `count=3`, and `quality_note=ok`.
  P2, P3, and Tavily were not executed.
- Controlled P2-branch reuse: in draft only, the P1 filter was temporarily set
  to a non-matching value and `Retrieve P2 Topics` temporarily read the
  existing five-file P1 dataset. The HTTP 200 SSE run followed `P1 miss -> P2
  hit -> End - P2`; `Normalize P2` returned `hit=true`, `count=3`, and
  `quality_note=ok`. The draft was restored to its original hash and graph;
  the published version was not changed. This is branch validation, not real
  P2 dataset acceptance.
- Version history: `phase2-routing-v1` remains the latest published version;
  the previous published version was restored successfully, then the restored
  draft was re-published with the same marker and comment. The new published
  graph is still 22 nodes / 22 edges and has the same hash as the verified
  baseline.
- Rollback smoke check: `draft/run` returned HTTP 200 as an SSE stream and
  finished with `succeeded`; the observed path was `Start -> Normalize Question
  -> Invalid Query IF -> Web Intent IF -> Tavily Search -> Normalize Web ->
  Web Hit IF -> END - WEB`.
- R9 controlled fault check: a temporary malformed JSON input was injected into
  the P1 normalizer in draft only; it returned `hit=false`, `quality_note=empty`,
  and `failure_reason=empty_result`, then the route continued through P2, P3,
  and Tavily. The draft was restored immediately and its hash, code, node count,
  and edge count matched the published baseline.

## Rollback rule

Save the draft before each structural change. If a metadata update or node
connection causes a regression, restore the previous draft/version; do not
delete source documents as a first recovery action.
