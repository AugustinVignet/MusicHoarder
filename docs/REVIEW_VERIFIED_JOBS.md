# Verified album review jobs (stage C)

Stage C extends exact plans with a durable, targeted copy job. It fixes the B-R1
race against the existing library builder. An Apply response acknowledges metadata,
not successful files. This task must pass independent Linux and full Production
image acceptance before use on an actual library or merge of C into improvement.

## API and durable receipt

All routes require the native Admin capability and ownership, including reads.

1. Read `/api/review/album-context`, then POST `/api/review/plans/preview` as in B.
   Preview now binds full SHA-256 content for selected source/current/previous/final
   paths, in addition to the contextual revision and file observations. Changed
   bytes with unchanged size/time stale the plan. Unreadable fingerprints block it.
2. POST `/api/review/plans/{id}/apply` with `revision` and an optional nonempty
   `requestId` UUID. Omitted requestId defaults to the plan UUID (legacy callers).
   The result retains `state: metadataApplied`, `copyState: awaitingVerifiedBuild`
   and adds `jobId`. Metadata, field audit, request key, job and exact track manifest
   commit together in a retryable serializable transaction.
3. GET `/api/review/jobs/{jobId}` for persisted state and per-track results.
4. POST `/api/review/jobs/{jobId}/retry` explicitly retries a stopped job. It queues
   the existing manifest; it never replays metadata or adds another job.
5. POST `/api/review/jobs/{jobId}/rollback` restores the recorded baseline. It
   retains the audit, old copies and new generation. A later correction or changed
   published bytes refuses rollback. Repeating a completed rollback is harmless.

A key acknowledged for one plan/revision cannot acknowledge another content.
Aliases for the same plan are durably reserved too. A plan has at most one job.
A replay during an active writer can receive HTTP 409 `writer_busy`; retry it
later. No metadata or file mutation is made by that refused Apply. A rolled-back
plan cannot be applied again; prepare a new preview instead.

B plans already applied before this migration can acquire a job on repeated
Apply if they still own every selected track. Their old metadata baseline was not
stored by B, so C explicitly refuses their metadata rollback. New C plans have
both snapshots. Old immutable B previews without content fingerprints/policy v2
must be prepared again if not yet applied.

## Concurrency and restart

`ReviewWriteGuard` is a dedicated, unpooled PostgreSQL session advisory lock,
shared by Apply, the legacy builder's entire per-track copy/tag/publish/cleanup,
verified jobs, retries and rollback. It is global **within the database**, including
owners and processes, and deliberately serializes publishers. Apply uses try-lock
and returns a bounded busy refusal. The native builder waits cancellably and
reloads its row after acquisition. The EF ReviewPlanId concurrency token remains
an additional defense, rather than the filesystem protection.

Workers poll indexed durable pending/running jobs every two seconds, take the same
gate and process only their exact IDs. A process exit releases its PostgreSQL
session lock. A new process recovers staging and persisted publication intent.
The owning connection is checked before publication. Deploy all cooperating
writers together; an old binary does not participate in this protocol. Do not
run two independent databases against the same destination tree. Uncoordinated
external writers/storage failures are not a distributed filesystem transaction.

The memory semaphore fallback exists only for non-PostgreSQL unit tests.
Production uses PostgreSQL and the real hosted worker. There is no RAM-only
queue whose loss can discard an acknowledged Apply.

## Timestamp persistence (C-R1)

Snapshots and public receipts represent UTC timestamps at PostgreSQL/Npgsql's
persisted microsecond precision, including dates captured before SaveChanges.
Snapshot reads apply the same normalization to legacy JSON with 100 ns fractions;
existing jobs need no migration or rewritten baseline. Both approval and prior
build dates keep their null/value meaning. Rollback restores that persisted value.

Comparison still includes the approval timestamp, owner/plan, exact metadata,
manual values and membership. There is no time tolerance: a change of one
persisted microsecond is a different approval and refuses publication/rollback.
Build status/date remain excluded from the later-correction check as before,
because publication itself updates them. Content/generation checks still apply.

Normal gate disposal explicitly unlocks and awaits PostgreSQL before closing the
dedicated session, so an immediate Apply replay cannot race socket-close cleanup.
An interrupted process still releases its locks on session loss. Active writers
continue to return writer_busy before any mutation.

## File states and evidence

A job is pending, running, needsAttention, completed, rollingBack or rolledBack.
Tracks record pending, staging, copied, tagged, verified, publishing, published, failed or
rolledBack, with attempts, stage/code, timestamps, manifest paths and proof JSON.
The song remains AwaitingReviewBuild until its published copy is verified.

For each target, C copies the original input to a deterministic hidden directory
`.musichoarder-review/<job>/<song>/candidate.<extension>` under the destination
folder. It flushes the file, writes exact reviewed tags, independently reads all
31 managed fields, then compares every audio stream using encoded packet hashes
and the fully decoded signal. No output audio is transcoded or normalized.
M4A keeps standard year-only date and full precision RELEASEDATE separately;
RELEASETYPE_PRIMARY preserves the independent primary type in all supported
formats. MP3, FLAC and Ogg Xiph/Opus use their native fields. Unknown stays unknown,
including a missing disc. M4A repeated dash atoms are repaired, not merely hidden
by the first native getter. ISRC on Opus is explicitly written/read through Xiph.

Before publication the worker rechecks the metadata snapshot, destination policy,
source and candidate hashes, live/retained path ownership and existing target
hash. It records publishing intent **before** renames. Existing same-path copies
are copied to `original.<extension>` and their full hash verified first. A
case-only move retains the old name in a separate case artifact before changing
its spelling. Older current/previous paths are never pruned. The manifest
protects those retained paths from the native builder too.

After rename the worker verifies the published file again, then acknowledges the
track and Done row together. A crash after rename is recognized by the candidate
hash and verified without copying/tagging again. Earlier interruptions rebuild
only that job's staging candidate. Disk/permission failures leave old copies and
persist needsAttention. Failed jobs need explicit retry; they do not hot-loop.

Audio proofs are stored by full content SHA-256 and verifier plus both tool
versions, not path/stat or Done. GET checks present contents and metadata and,
with the registered verifier, current native tags and current tool versions.
`copiesVerified` is false after relevant drift. `hasProof` denotes a recorded
historical proof; it is not current verification. `albumCompleteness: notAssessed`
does not make a musical completeness assertion: compare the established program
and every position in the album context separately. Truncated source material
that decodes cleanly cannot prove the intended musical program complete.

Staging/backup storage must have room for all retained generations. No automatic
cleanup is included. Filesystem moves and PostgreSQL commits are separate;
recovery is journaled, not a promise of a single atomic NAS/database commit.
A source unavailable/released before Apply is not silently substituted: the
copy reports source_changed_or_missing and retains the old library copies.

## Album memberships and rollback

C does not run global build scans, reconciliation, renumbering or duplicate
retirement. A recording can keep a separate place on a studio album and a
compilation. A duplicate-marked target requires the explicit per-track
`preserveAlbumMembership: true`; Apply clears its exclusion while keeping the
underlying duplicate edges and the before snapshot. Later automatic clustering
cannot retire a row bound to a review plan. Rollback restores its prior duplicate
state as well as metadata, approval, build status and copy pointers.

Position conflicts remain visible. A different path alone does not resolve a
true ambiguous position. Fully dismissed pair evidence across distinct paths
produces `distinctTakesRetained`, and distinct known editions in distinct folders
produce `distinctEditionsRetained`; these warnings permit both slots. Otherwise
albumPosition/possibleAlbumPosition remains a blocker, including unknown-disc
versus disc 1. Colliding destinations always block regardless of dismissal.

Rollback persists rollingBack intent, restores verified backup bytes while
retaining the replacement, then restores metadata and marks plan audit reverted
in one retryable transaction. The hosted worker resumes interrupted rollbacks.
Rollback never deletes a source or silently overwrites a later correction.
New-path replacement files may remain alongside the old path as retained
artifacts; removal is a separate explicit operation, not part of this stage.

## Schema and verification

Two additive migrations follow B: AddVerifiedReviewJobs and AddReviewRequestAliases
(85 migrations total from the 83-migration B schema). They add ReviewCopyJobs,
ReviewCopyTracks, ReviewAudioCache and ReviewCopyRequestKeys, with unique plan and
owner/request keys. No rewrite of B's migration. Down drops these new receipts
and does not undo files; do not use Down as an operational file rollback.

Regression classes: ReviewCopyJobTests, ReviewTagVerifierTests,
ReviewAudioVerifierTests, ReviewCopyTagRetagTests and the Copied/Tagged race cases
in LibraryBuilderServiceTests. PostgreSQL tests require an explicitly disposable
local MH_REVIEW_TEST_POSTGRES database; audio tool tests require ffmpeg/ffprobe.
All fixtures are synthetic. ENOSPC/rename denial and crash boundaries are injected
failures; full NAS power loss and Linux Production image remain independent
acceptance checks. See the private project's C delivery report for exact SHA,
patch hashes, commands, results and remaining qualification.

## Development validation (2026-10-17)

The complete API suite on macOS arm64 / .NET 10.0.402 / PostgreSQL 14.17 /
ffmpeg+ffprobe 7.1.1 completed 3,542 cases: 3,541 passed, one failed, zero skipped.
The only failure is the already-reproduced macOS baseline Web Push private/public
key rejection test. Stage C adds 67 passing cases over B, including the earlier
28 component cases. No Linux/image qualification is implied by these results.
The private delivery report pins the final source SHA and exported patch.

## MP3 compatibility and retained-job diagnostics

The tag writer does not introduce an ID3v1 block when the source did not have
one. Existing ID3v1 tags remain supported. This matters when a partial final MPEG
frame makes a newly appended tag appear inside ffmpeg's last encoded packet.
Encoded-payload and full decoded-PCM checks remain mandatory.

ID3v2.4 is selected per tag for native multi-value fields. Unmapped three-byte
ID3v2.2 frames are preserved as valid PRIV frames, with their complete rendered
v2.2 bytes and distinct owners under the `id3v2.2` namespace; they are not emitted
as malformed v2.4 frame headers or discarded. Existing four-byte unknown frames
and private metadata remain intact. The original source is never retagged.

Background targeted copy retries resolve their job with both its ID and explicit
owner, including when no HTTP user exists in the hosted worker. A foreign or
missing job still returns `job_not_found`. This does not release AutomationHeld
or authorize another stage or metadata replay.

Snapshot failures remain strict. Failure-only structured diagnostics identify
the check, before/after sizes and UTC timestamp ticks, and whether content changed;
they omit paths and hashes. Public failure codes, receipts and audio-cache policy
are unchanged. A later stable read does not explain an earlier fresh-copy failure.


### MP3 boundary-tag verification

For a single MP3 audio stream in an `.mp3` file, the encoded payload proof hashes
all file bytes outside recognized leading ID3v2.2/2.3/2.4 tags and the exact
128-byte trailing ID3v1 block. This avoids treating a changed ID3v1 tag as changed
audio when ffmpeg includes it in a partial final MPEG packet. The ID3v2.4 footer
is validated against its header; invalid versions, reserved flags, impossible
sizes and ambiguous boundaries fail verification. Unclassified leading bytes,
interior markers and unknown trailers remain included in the hash.

Strict tool-error handling, complete PCM decoding, stream-layout comparison and
source/candidate stability checks still run. Other codecs and multiple audio
streams retain their existing encoded-stream verification. No audio is repaired,
transcoded or omitted. The `review-audio-v2-id3-boundaries` proof version prevents
reuse of earlier algorithm proofs while preserving their historical receipts.
Existing retained copy jobs can be retried without replaying metadata approval.

The boundary layout follows the [ID3v2.4 structure specification](https://id3.org/id3v2.4.0-structure).
Regression coverage includes real retagged partial-frame MP3s with existing
ID3v1 tags, changed valid audio, malformed boundaries and old proof-cache entries.

## Releasing a historical destination reservation

A finished job keeps reserving every path it touched (destination, retained backup,
old destination and old previous path) so that its rollback can always restore the
old generation. After that old generation has been removed on purpose, the path can
stay blocked forever: another song planned to that exact path fails publication with
`destination_held_by_another_track`.

`POST /api/review/destination-reservations/release` (Administer) lifts such a
reservation explicitly:

```json
{ "requestId": "11111111-1111-4111-8111-111111111111",
  "destinationPath": "/music/destination/Example/2000 - Example/01 - Example.flac",
  "reason": "Old generation removed after verification; slot needed by another track" }
```

It is refused unless all of these hold: no file exists at the path
(`destination_file_present`), no active song is built there (`destination_in_use`),
every job referencing it is finished (`job_not_finished`), no other owner holds it
(`foreign_reservation`) and something historical actually reserves it
(`reservation_not_found`). The writer gate is held (`writer_busy` otherwise).

The durable receipt lists the released jobs — those whose reference is a past
generation (old/retained path, or a destination they published) — and the songs whose
`PreviousDestinationPath` pointed there, which is cleared. A job that merely targets
the path keeps its claim. Publication and the legacy builder then ignore only those
recorded references; any other job still blocks. Rolling back a released job is
refused with `destination_reservation_released` before anything is touched. Reusing
the request UUID returns the same receipt; another path under it is `request_key_reused`.
`GET /api/review/destination-reservations/releases` lists receipts, newest first.
