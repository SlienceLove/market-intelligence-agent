# IMA to Dify Knowledge Base Sync SOP

This is a repeatable manual export/import procedure for moving substantive IMA
notes into the Dify knowledge base. It is intentionally manual because IMA has
no direct Dify integration in the current phase.

## Current project decision

- IMA note connectivity is deferred and is not a prerequisite for Phase 2.
- Phase 2 retrieval, routing, and import validation use the five formally
  imported market-department files already verified in Dify.
- When prepared files need to be added before IMA connectivity is resumed,
  skip the IMA export steps and directly upload `.md`, Word `.doc/.docx`, or
  `.pdf` files to the appropriate Dify knowledge base.
- This document remains a future reference for resuming the IMA export/import
  procedure; it does not claim that an end-to-end IMA run has completed.

## Trigger and Preconditions

- A batch of organized IMA notes is ready to move to Dify.
- The Dify `market-intelligence` workspace and target knowledge bases exist.
- The operator can sign in to IMA and Dify.
- The target Dify knowledge base has a configured embedding model.
- Use a local temporary directory for exports. Do not store exported notes in
  Git, and remove the temporary directory after verification.

## Procedure

### 1. Export notes from IMA

1. Open `https://ima.qq.com` and enter the source workspace or notebook.
2. Select 5-10 notes that belong in the high-priority knowledge bases.
3. Export as Markdown when the notes are text-first. Use Word (`.doc/.docx`)
   or PDF (`.pdf`) when the export contains important images or Markdown
   formatting is incomplete.
4. Download and extract the export archive into a temporary local directory.
5. Record the batch identifier and note count in the verification record below;
   do not record note contents or sensitive source metadata here.

### 2. Inspect and classify the export

1. Open at least two exported files and confirm that the text is readable,
   correctly encoded, and not only an image.
2. Split files by the existing Dify topic knowledge-base names.
3. Put notes whose topic cannot be determined in the `Unclassified` landing
   knowledge base. Use the exact display name configured in Dify.
4. If a note is image-only, route it through the OCR capability planned for
   Phase 4 before importing it. Do not silently import an empty document.

### 3. Import into Dify

1. In Dify, open `Knowledge` and select the topic knowledge base, or the
   `Unclassified` landing base for unresolved notes.
2. Choose `Add file` and upload only the files for that knowledge base.
   Prepared `.md`, Word `.doc/.docx`, and `.pdf` files can be uploaded directly;
   IMA connectivity is not required for this step.
3. Keep the default chunking settings initially. Change them only when a
   retrieval test demonstrates a quality problem.
4. Wait until every uploaded document shows `Completed` before testing.
5. If indexing fails, capture the error in the operational ticket, fix the
   source file or model configuration, and retry the failed document only.

### 4. Verify retrieval

1. Open the knowledge base's retrieval test panel.
2. Search for a distinctive phrase known to occur in one imported note.
3. Confirm that the returned chunk contains recognizable content from that
   note. For classified bases, the result should come from the selected topic.
4. Treat the batch as failed if no result is returned, the result is unrelated,
   or indexing is not `Completed`.

### 5. Clean up and mark the source

1. Delete the local temporary export directory after verification.
2. In IMA, mark the successfully migrated notes with the team's agreed
   `Synced to Dify` label or equivalent metadata.
3. Keep a migration log outside Git containing the batch identifier, operator,
   destination knowledge base, count, and verification result.
4. If a batch failed, do not mark the IMA notes as synced. Record the failure
   reason and retry after correction.

## Verification Record

Fill this section after a real end-to-end run. Do not include note text,
credentials, server addresses, or API keys.

- Batch identifier:
- Run date:
- Operator:
- Source note count:
- Destination knowledge base(s):
- Indexed document count:
- Retrieval phrase used:
- Retrieval result: Deferred; Phase 2 uses the existing five-file validation baseline
- IMA notes marked as synced: Not applicable while IMA connectivity is deferred
- Temporary export removed: Pending first external run

## Troubleshooting

- Garbled text: retry the IMA export as `.doc/.docx` or `.pdf` and inspect the
  extracted text before importing.
- Image-only content: use OCR before import; an empty extracted document is not
  an acceptable successful migration.
- No retrieval result: confirm indexing is complete, the embedding model is
  available, and the query uses an exact phrase from the note.
- Wrong topic result: remove the document from the incorrect base, classify it,
  and re-import it into the correct base.
- Duplicate result: check the migration log and IMA sync label before retrying.
