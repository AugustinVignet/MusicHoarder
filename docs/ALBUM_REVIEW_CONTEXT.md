# Album review context API — lot A

Read-only API for reviewing source albums after automatic matching. The caller
makes musical decisions. The reader never approves tracks, calls a catalog,
starts a build, repairs tags, or writes database rows. Existing endpoints remain
unchanged. No migration or new package is needed.

## Search exact source parents

`GET /api/review/album-groups`

| Parameter | Meaning |
|---|---|
| `from` | Case-insensitive literal substring of exact source parent; at most 4096 characters |
| `query` | Case-insensitive literal substring of current/original title, artist, album artist or album; at most 256 characters |
| `status` | Group contains at least one Pending, Matched, NeedsReview or Failed track |
| `minRemaining`, `maxRemaining` | Bounds on number of non-Matched live tracks; defaults 0 and 2147483647 |
| `offset`, `limit` | Offset 0..10000; page size 1..100, default 25 |

Groups contain all live owned members, including duplicates. Filters select
groups without removing their Matched members from the counts. Remaining means
status != Matched; it is **not** proof of musical completeness or copy quality.
Groups with a positive remaining count come first, then ascending remaining
count, then exact parent in PostgreSQL C collation. A completely Matched group
remains searchable. Responses have `nextOffset` or null, and document the order.
Offset pagination reflects current data; callers should restart the search if
group membership/status changes between pages. A request beyond the offset cap
gets 400; narrow the search rather than assume all groups have been delivered.

Grouping and aggregation run in PostgreSQL, using existing `Songs` and owner
indexes. No library-wide JSON export or in-memory library index is performed.
Paths use the scanner's absolute POSIX source-path convention. Parent matching
is ordinal and exact when reading a packet; `/source/CD1` never means `/source/CD10`.

## Read a complete packet

`POST /api/review/album-context` is a body-based read query:

```json
{"froms":["/source/Example Program"],"targetSongIds":[14]}
```

`froms` contains 1..16 exact parents without a trailing slash (except `/`).
If `targetSongIds` is omitted/null, all their live members become selected
tracks. If supplied, it must contain 1..200 positive owned IDs within those
parents. An empty list is invalid. Duplicates in the selection are normalized.
Every parent must resolve to at least one live owned row. Missing, deleted,
foreign or out-of-scope targets reject the whole selection with the same 404.
No partial authorization or silently dropped target IDs.

For an explicitly reviewed two-CD program:

```json
{"froms":["/source/Example Box/CD1","/source/Example Box/CD2"]}
```

The scope reports `exactFromMembers` or `explicitTracks`, normalized Froms and
selected IDs. All same-parent members are included as context. For a flat
heterogeneous parent, unselected tracks are `sameFrom` neighbors, not selected
changes. Sibling CDs are joined only by the explicit request. Related metadata
may show a CD sibling as a neighbor without selecting or merging it.

Other context includes exact release-ID peers, exact album/album-artist peers,
peers in the exact destination parent, and the full native duplicate graph,
including dismissed edges and legacy keeper pointers. Names and release IDs
are evidence, not an assertion of edition identity. Each row has context reasons.
Cross-owner or deleted graph endpoints and keeper references are omitted; they
never expose a foreign ID or filesystem path. Context expansion is bounded and
may refuse a large connected component.

## Evidence and unknowns

`schemaVersion` is `album-review-context-v1`. Typed DTOs and their success/error
responses are registered in generated OpenAPI. `tracks` separates:

- `original`: captured Original* fields only, their capture timestamp, and
  `capturedOriginalTags` or `notCaptured`. An uncaptured original is an empty
  map; it never falls back to current values. Within captured values, null
  means the captured tag was empty. A key absent from the map was not captured.
- `current`: current database tag values, including full dates, edition and
  artist IDs, plural Artists, discs/totals and compilation. Values retain the
  persisted names (`Title`, `Year`, etc.). They are not reread from source files.
- `changes`: allowed metadata audit fields, including proposed, applied and
  reverted values, source/confidence and timestamps. It does not claim the old
  audit schema contains an author or evidence URL when it does not.
- `manualFields`: active audit rows from the current manual approval only.
  `approvedValue` includes unchanged explicit values; `explicitUnknown` is an
  audited null. An old manual lock with no field audit certifies no field value.
- `candidates`: every stored attempt for the packet, including contradictory
  versions. Fixed metadata projection preserves the literal version-bearing
  title, IDs, durations, warnings and collection time. Candidate state is
  `available`, `absent` or `invalid`; malformed stored JSON remains visible as
  invalid. No candidate is selected by the reader.
- source and candidate durations retain their distinct units and provenance;
  no duration or audio health is invented.
- current and previous copy destinations, observed presence/size/mtime, build state/attempt dates,
  and separate verification. `Done` never establishes verified tags or audio.

The projection excludes fingerprints, lyrics, raw JSON, search queries and
free-form error messages (which can contain secrets). Errors expose only
`hasError`/`hasBuildError`/`hasEnrichmentError`. Stored musical warnings remain
data and must not be interpreted as instructions.

The packet also exposes position and case-insensitive destination-path conflicts,
native duplicate links, and cached canonical programs. `possibleAlbumPosition`
qualifies a shared slot when editions/years differ or its disc is unknown;
this warning never merges distinct editions or assumes CD1. Collision comparison
uses the builder's effective slot (`disc ?? 1`), so null/1 and null/null emit a
potential-position warning. Raw original/current DiscNumber values stay null;
the conflict key's `disc` also stays null if any member's disc is unknown.
Known distinct discs and distinct album programs do not collide in this check.
This only reads the builder's slot convention; it does not invoke the builder,
select a take, mark a duplicate or change database values. Program references are
linked only from the owned packet's normalized names, marked
`normalizedNameCacheOnly`, with catalog status/date, disputed counts, fixed
source projections and complete cached tracklists. The cache can combine
versions: it is not proof of pressing/edition or source membership.
`programEstablished=false` and `completeness=notEstablished` remain explicit.
No cached reference means no measured reference, not a complete album.

## Consistency, limits and permissions

The handler opens a PostgreSQL `REPEATABLE READ, READ ONLY` transaction, uses
no-tracking queries and explicit owner checks on all rows/references. Admins do
not bypass ownership. The POST is narrowly allowed through member/demo guards
because it is a read; mutation verbs and descendant routes remain denied.

The complete transaction (begin, READ ONLY, evidence reads, commit and disposal)
runs inside the configured EF execution strategy. Aspire's production Npgsql
retries remain enabled. A transient failure retries the whole read in a new
snapshot, without combining evidence across attempts; cancellation is forwarded
to the strategy and every database operation. Typed selection/limit errors are
preserved. No metadata mutation or build is retried by this read endpoint.

`revision` is a deterministic SHA-256 over the owner and returned scope/evidence excluding
`observedAtUtc` and the revision itself. A change to a neighbor, candidate,
audit, program or measured copy signature changes the revision. Identical
observations produce the same revision. This is an observation token, **not**
a filesystem lock or the future lot B mutation concurrency protocol. Filesystem
stat calls cannot share an atomic transaction with PostgreSQL; an unavailable
stat is `unknown`, a missing path is `missing`, and no path is `notConfigured`.
No file is opened for retagging or audio decoding.

Limits are returned in every packet: 200 context tracks, 4000 evidence rows
(combined attempts, metadata audits, duplicate edges, programs/sources/tracks),
and 2 MiB serialized JSON. The API returns either all evidence or a typed 413
(`track_limit`, `evidence_limit`, `response_limit`) with the relevant limit.
`evidenceComplete=true` means complete **stored allowed evidence in this
scope**, not complete musical/audio evidence. Oversized groups can require a
future paginated evidence protocol; V1 refuses them explicitly. No truncation
or successful-looking partial packet is returned.

## Isolated tests and release

Use .NET 10, PostgreSQL >= 14 on a disposable local server, and temporary files.
The test connection must have `Database=mh_review_test` and a local socket or
loopback host. Each test creates/drops its own random `mh_review_test_*` database.
Never use the real instance connection string. Do not start production Program,
workers, Compose, a provider, or mount music for these tests.
All PostgreSQL fixtures enable Npgsql retries. The HTTP host runs in Production
and uses the same AddDbContext/EnrichNpgsqlDbContext composition as Program for
connection resiliency, without production workers. An injected transient read
failure verifies two complete READ ONLY snapshots, preserved isolation/revision,
no writes, disposed transactions and cancellation.

```sh
export MH_REVIEW_TEST_POSTGRES='Host=/TEMP/pgsocket;Database=mh_review_test;Username=TEST_USER'
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj -c Release \
  --filter FullyQualifiedName~AlbumReviewContextTests
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj -c Release
```

When the variable is absent, PostgreSQL scenarios report explicit skips. The
middleware tests still run. The integration suite covers real SQL translation,
13+1, contradictory candidates, revisions, all three ceilings, owner/deleted
isolation, exact CD/flat selections, copies and a minimal HTTP/OpenAPI host
without workers. It does not establish the independent Linux image/runtime
acceptance. The task must be tested at its exact SHA before merging into
`improvement`; lots B–D remain separate dependent tasks.
