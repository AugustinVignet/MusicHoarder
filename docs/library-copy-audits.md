# Existing library copy audits (stage F)

F checks existing source/copy pairs with C's native tag and full-audio verifiers.
It does not rebuild, retag, rename, stage, approve, enrich, download or choose a
musical identity. Only the three new technical audit tables and the shared
content-proof cache can change. A technically faithful copy does not establish
that its source was assigned to the correct recording or album.

## Routes and ownership

All routes require native authentication, admin capability, and ownership of the
selected active rows/job. Shared-only and deleted tracks are unavailable. IDs
resolve exact paths from the database; request DTOs contain no client file paths,
and unknown top-level request properties are rejected.

Create an audit from E's returned `planContext`:

```http
POST /api/review/copy-audits
Content-Type: application/json
```

```json
{
  "requestId":"11111111-1111-4111-8111-111111111111",
  "scope":{
    "froms":[],
    "targetSongIds":[101,102],
    "audit":{"version":"album-audit-v1","memberSongIds":[101,102],"reference":null}
  },
  "contextRevision":"<fresh 64-character revision returned by E>"
}
```

The response is 202, `CopyAuditJobResult`, and a Location for the job. Select
1–200 unique positive IDs; order is normalized. Every selected member is checked,
including a Pending/unbuilt row that will receive an explicit technical result.
No earlier C job or fake metadata plan is required. Enqueue validates the entire
owned/live selection and fresh E context in a retry-owned short transaction;
there is no partial job on a foreign, deleted or stale selection.

Same owner + UUID + normalized request returns the same durable job, including
after restart or later filesystem changes. It is a historical replay, **not a
fresh physical check**. Different content under that key returns 409
`request_key_reused`. A new physical observation needs a new UUID and fresh scope.
Request-key advisory serialization precedes the transaction so concurrent double
clicks do not create duplicate logical attempts.

- `GET /api/review/copy-audits/{id}?afterSongId=0&limit=25` pages immutable target
  IDs in ascending order. Limit 1–100; the next ID is `nextAfterSongId`. The scope,
  complete bounded `auditedSongIds`, exact counts, timestamps, wait reason,
  next attempt, active attempts and owned backlog are included. State is live
  and dated; completed receipts remain historical. GET performs database reads
  only, with no file stat/hash, tool execution, or queued work.
- `GET /api/review/copy-audits/{id}/tracks/{songId}/attempts?afterAttempt=0&limit=25`
  pages retained attempt history, also bounded to 100. An interrupted physical
  attempt remains visible; recovery does not overwrite a completed receipt.
- `POST /api/review/copy-audits/{id}/retry` accepts
  `{"requestId":"<new UUID>","songIds":[101]}`. It creates a **child job** for
  only explicit issues/unverifiable/stale IDs of a stopped uncanceled parent.
  Parent receipts and other tracks remain unchanged. Repeat the same retry key
  to recover that child. Verified IDs require a new full audit rather than a
  failure retry. The child retains the explicit program selection and obtains
  its current technical context; a changed pinned reference requires a new E
  selection. No old reference revision is silently substituted.
- `POST /api/review/copy-audits/{id}/cancel` durably cancels pending work and
  cooperatively stops a running tool. Already completed receipts are retained.
  There is no rollback because F changed no musical file or metadata.

Errors use existing structured review errors: 400 invalid input/page, 401/403
auth/capability, 404 foreign/unavailable object, 409 stale context/request-key
conflict/busy retry, 413 oversized scope/evidence. Public results never include
raw provider payloads, lyrics, acoustic fingerprints, stderr or exception text.

## What a receipt proves

For each target the worker reads current DB tags/manual rules and exact source
and destination paths, compares the persisted expected DB revision, and checks
configured source/download roots and destination-root case spelling. Root aliases are resolved; descendant
symlinks must stay inside the physical configured root. Missing copies, unbuilt
tracks and missing sources have separate codes. Directory checks stream names
rather than materializing a large source folder.

C's independent tag reader verifies all editable native tags, including plural
artists, credits, disc/track totals, release dates/IDs and explicit unknowns.
Legacy copies can expose missing extension tags when the DB claims a value;
F reports these as issues and does not write them. An unknown DB value remains
unknown. A wrong tag is an issue, rather than automatically a concurrent change;
a later change to the observed tag set is stale.

The audio engine checks tool versions, hashes full current container contents,
probes all audio streams, hashes encoded streams and strictly decodes every
stream to PCM for comparison. No trimming, loudness normalization, resampling,
partial fpcalc proof or silent acceptance of decoder errors. Corrupt identical
files and metadata-only files cannot become verified through binary equality.

Cache keys include full content SHA256 and C verifier/ffmpeg/ffprobe version.
Every cache lookup starts with a new full content hash, even at identical size
and mtime. Cache hits can avoid decoding; they do not avoid NAS reads. C and F
share the same cache and atomic PostgreSQL upserts, including concurrent native
readers. Cache proofs retain the time of their successful decoding; a receipt
separately dates the new physical content observation.

The engine also keeps non-proof hashes read before decoding. If decoding fails
because a file changed mid-read, F can still distinguish that drift from an
unchanged corrupt file; neither hash is admitted as a successful decode proof.

Before acknowledgment, the worker repeats owner/DB/context/reference checks,
independent tag readback and full file hashes. File or relevant DB/scope drift
produces stale, including a new related Inbox member or a changed selected
reference. A stale target keeps the job's scope stale; earlier successful
receipts are retained as evidence of their earlier revisions, not proof of the
new album. `contentObservedAtUtc` dates the physical hash observation;
`finishedAtUtc` dates the receipt. These are observations, not continuous
monitoring of subsequent external changes.

Receipts contain expected/observed DB and context revisions, stage results and
field names, source/copy observations and proofs, verifier version, dates,
cache-hit flags, bytes actually hashed and decode-attempt counts. Audio
`contentVerified` alone does not mean tags/program identity are correct: the
whole track must be `verified`. Metrics sum persisted attempt receipts;
`metricsCoverage` explicitly qualifies I/O lost before acknowledgment during a
process/database interruption. Logical attempt counts and history remain durable.

## Durability, concurrency and load

Job states: `queued`, `running`, `completed`, `completedWithIssues`, `canceled`.
Track states: `queued`, `running`, `retryWaiting`, then `verified`, `issues`,
`unverifiable`, `stale`, or `canceled`. Ordinary per-track failures do not abort
other tracks. Temporary access/I/O/tool failures use at most three attempts,
with persisted 5- and 20-second backoffs. Busy publishers defer the audit without
starting a physical attempt. A recent cached negative directory-availability
observation defers reads; F never calls the monitor's side-effecting probe or
starts discovery. Historical files in the configured download root are allowed
without starting or enabling downloads. Interrupted process attempts can be resumed;
physical decoding may repeat after a crash, so no exactly-once I/O is promised.

The native API hosted worker processes one track per shared publication-gate
acquisition, releasing it before the next track. It coordinates with the current
legacy builder and B/C, including two API instances. There is no album-wide
transaction or F gate held across the whole queue. Source/copy audio processing
is serial and bounded. The worker is independent of browser/session lifetimes;
old queued jobs remain eligible after days and API restarts.

A claim is persisted before physical work. The final short receipt transaction
uses the original unpooled PostgreSQL session that still owns the publication
gate, with a claim check. It cannot silently reconnect and publish a success
after losing that session. A negative conditional transition can record
`storage_or_lease_lost` and a retry time; a database outage can prevent even that
write, in which case the prior durable claim resumes on recovery. Cancellation
is polled every 250 ms and propagated to C's process runner, which kills the
process tree. Tool timeout is five minutes and per-track cancellation budget is
15 minutes. File operations use host/mount I/O semantics; independent environment
acceptance must also exercise NAS outage/access timing. No mount or production
watcher configuration is changed by this feature.

System DbContexts without a user accessor have a separate compiled-model key
from anonymous HTTP/background accessors. They have different captured tenant
filter semantics and must not share a cached model after a fresh process start.
The worker's read-only context uses explicit owner predicates and no fabricated
HTTP identity. Its snapshot transactions remain inside the Npgsql retry strategy.

## Schema and validation

Additive migration `20261017152841_AddDurableCopyAudits`, after schema 86, creates
`CopyAuditJobs`, `CopyAuditTracks`, `CopyAuditAttempts` and technical indexes.
No published migration, Song column, old C outbox/snapshot or provider queue is
rewritten. Historical results have no Song FK, so removing a library row does
not erase its receipt. Fresh and schema-86-restored databases are tested.

OpenAPI operations: `CreateCopyAudit`, `GetCopyAudit`, `RetryCopyAudit`,
`CancelCopyAudit`, `ListCopyAuditAttempts`. DTOs: `Contracts/CopyAudit.cs`.
Regression fixtures use disposable PostgreSQL and generated/silent synthetic
audio, including FLAC/MP3/M4A, read-only files, same-size/mtime changes, actual
process death/recovery, cancellation, two workers, stale references, and native
C checks. Failure injection exercises unavailable tools/storage without faking a
successful audio proof. Exact Production image/native-auth and usual-instance
NAS behavior remain separate independent acceptance gates before integration.
