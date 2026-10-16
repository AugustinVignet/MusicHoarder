# Targeted retries (stage D)

D starts from the exact delivered C SHA. It includes B-R1+C+D for combined
acceptance; it does not mark those tasks accepted or merge improvement. The
private delivery report pins source/tree/patch and independent image checks.

## API

Every route requires native Administer and ownership. A request has a nonempty
UUID, 1..200 unique positive IDs and unique stages in the explicit order
fingerprint, enrichment, copies. Mixed-owner/deleted/synthetic selections are
refused in full before mutation. ID order is canonicalized. Same UUID/content
returns the existing job, different content returns 409. writer_busy refuses
without queuing/holding anything: retry the same UUID later.

POST `/api/review/retries` (synthetic example):

```json
{
  "requestId": "11111111-1111-4111-8111-111111111111",
  "songIds": [101, 102],
  "stages": ["fingerprint"],
  "allowProviders": false,
  "recomputeSuccessful": false,
  "respectRetryAfter": true
}
```

GET `/api/review/retries/{id}` returns persistent job/items, stage, state, code,
input hash and timestamps. Jobs are pending/running/completed/needsAttention;
items additionally support failed, skipped, blocked, deferred, reviewRequired
and qualificationRequired. A restart resumes pending/running work. A committed
stage receipt is recovered without executing it again.

GET `/api/review/failures?stage=fingerprint&offset=0&limit=25` pages failures and
unresolved source warnings, with hasMore. GET `/api/review/tracks/{id}/stages`
returns current states, attempts, reason, date, optional deadline, input hash,
candidate evidence and persistent source warning. GET
`/api/review/tracks/{id}/attempts?offset=0&limit=25` returns append-only history
including job link and exit code. Limits are 1..100, owner-scoped and stable.

After a failed/deferred job, submit a **new UUID** for only the desired IDs and
stages. Replaying an old UUID intentionally does not perform another attempt.
Fresh valid fingerprints are skipped unless recomputeSuccessful is explicit;
input hash, validator version, native fingerprint/duration and audio receipt
must match. Other tracks and valid provider results are never reset.

Enrichment needs an explicit stage and allowProviders=true. Only configured
native identification providers for these IDs may run; no global queue or build
trigger. respectRetryAfter keeps deadlines visible as deferred. False explicitly
makes only selected non-Matched attempts due, preserving their candidates/history
and every Matched attempt. Automatic lyrics fetching is not chained.

Copies need copyJobId for an owned stopped C job. All selected IDs must belong
to its exact manifest. Pending/running C jobs are refused. D invokes just these
tracks, preserving unselected successful/failed records. It never queues all of
C or invents/reapplies a metadata plan. C retains its staging, tag/audio proof,
backup, publication and restart safeguards.

## Automation fence

Fingerprint-only work does not invoke providers, duplicates or copies/builds.
Songs.AutomationHeld is persisted for selected IDs so native discovery cannot
silently run the omitted steps after a successful fingerprint. Native enrichment,
lyrics, builders, consolidation, index resets and duplicate flag projection honor
it. The hold is also an EF concurrency token. C's publication gate and PostgreSQL
per-song locks coordinate already-active writers/providers. The complete ID set
uses one unpooled connection, not one server session per selected song.

Holds remain after completion. POST `/api/review/retries/resume-automation`
requires explicit IDs and **all** downstream permissions:

```json
{
  "songIds": [101, 102],
  "allowProviders": true,
  "allowBuild": true,
  "allowDuplicateDetection": true
}
```

It refuses unfinished jobs, source drift, stale/missing fingerprint evidence or
unresolved source warnings. It permits normal automation for those rows rather
than performing those stages itself. C's manual plan/approval still protects its
songs. No warning-clear or exception accepting a code-3 candidate is provided.

## Source quality and fpcalc

The adapter uses a shell-free bounded process launcher with concurrent capped
pipes, timeout and cancellation/child cleanup. Require unambiguous JSON object,
finite positive bounded duration and nonempty compressed fingerprint decodable
by the native Chromaprint decoder. Invalid JSON, arbitrary strings, zero/invalid
duration, missing output/tool and timeout are failures, with bounded reason codes.

A nonzero exit **including 3**, or diagnostics even at zero exit, leaves a valid
candidate qualificationRequired and Result null. No general exit-code exception.
The source is also fully checked by C's audio verifier, its native duration
compared, and full content hashed before/after. Metadata-only ID3 has no usable
audio and is failed, never successful. Source drift refuses the result.

Candidates/exit codes and source warnings persist by input hash. Identification
or restart never erases a source warning. Separately authorized name-based
identification may run, but the native targeted entry excludes AcoustID without
a fresh successful fingerprint and no unresolved warning. A failed/unqualified
predecessor blocks downstream steps in the same request. The observed audio
bytes do not prove musical identity or completeness; a valid frame-boundary cut
may still lack intended music. No transcode or source deletion is added.

## Storage, compatibility and qualification

AddDurableTargetedRetries follows C (85 -> 86 migrations), adding
TargetedRetryJobs/Items, PipelineStageStates/Attempts and AutomationHeld. Request,
holds and exact items commit in one transaction owned by Npgsql's retry strategy.
A unique job/song/stage attempt receipt supports uncertain-commit recovery.

The historical fingerprint worker replaces its RAM-only failure cache with the
same durable journal and excludes known failed/qualified inputs after restart.
Its usual successful pipeline remains for non-held songs. Old global routes stay
compatible but do not become targeted routes; use the new API for selected
failures. No reasons lost before D are invented/backfilled. Existing provider
and C-copy histories remain canonical; the new journal records observed
fingerprinting and explicitly requested stages.

Down removes D state/holds and may re-enable automation: it is not an operational
rollback. Test snapshot restoration or use explicit stage/automation operations.
All writers must use the matching protocol and database. No production restart,
network-provider probe, paid AI call or global retry belongs to developer tests.

Regression tests use PostgreSQL, synthetic files, simulated identification
providers and the real hosted HTTP worker in Production mode. Real fpcalc tests
cover a generated FLAC and the same file truncated by 100 bytes: valid JSON and
fingerprint with exit 3 stays unusable. Injected/process-fixture tests cover
invalid outputs, timeout, cancellation and interruption at receipt boundaries.

Required test environment: disposable local MH_REVIEW_TEST_POSTGRES, ffmpeg and
ffprobe, MH_TEST_FPCALC or fpcalc on PATH. Record missing-tool skips. Exact Linux
and Production image/native-auth acceptance remain independent; record actual
fpcalc/decoder versions rather than assuming this development host's versions.
