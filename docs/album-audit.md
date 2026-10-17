# Album audit context (stage E)

These owner/admin routes read existing evidence. They do not approve a musical
identity, call providers, launch retries/builds, or verify audio. Existing A–D
contracts and `/api/albums` keep their behavior.

## Inventory and primary membership

`GET /api/review/album-audit/groups?limit=25&query=&status=&cursor=` returns
`AlbumAuditInventory`. Limits: 1–100 groups, 256 query characters, 8192 cursor
characters. Status is an optional enrichment enum name; either filter selects
whole units. Every active owned track, including duplicates, Pending, unbuilt,
and untagged rows, belongs to exactly one unit. Shared and deleted rows are absent.

Version `album-audit-v1` keys are `v1:d:<destination-folder>`, otherwise
`v1:f:<exact-source-parent>`, otherwise `v1:u:<song-id>`. Destination extraction
matches the unmerged library projection, including its slash-at-root behavior;
source parents use exact POSIX spelling. Destination paths are trimmed as in the
projection; no case folding, fuzzy name merge, edition merge, or CD inference.
Keys sort using PostgreSQL `COLLATE "C"`. Moving membership changes the relevant
key/revision; compare complete fresh inventories by member IDs to identify the
old unit disappearing and the replacement. Do not transfer a verdict by name.

IDs are bounded to 200 per unit. `memberCursor` continues at
`GET /api/review/album-audit/group-members?cursor=...` in pages of 200. Counts of
source/destination parents and the context read reference avoid unbounded path
arrays. The context returns exact paths for selected members. Candidate group
hints use the summary's exact displayed credit/album only, capped at eight with
an exact count and `candidatesComplete`. They are navigation suggestions, not
musical membership; a heterogeneous From may have additional identities. Context
performs full relationship discovery from the explicit chosen member IDs.

PostgreSQL aggregates the narrow navigation projection and its digest. Three
queries per inventory page, no library entity materialization or per-track detail
queries, no filesystem dependency. The revision includes primary membership,
paths, searchable labels, enrichment/build statuses; it excludes likes, plays,
and response time. The short REPEATABLE READ / READ ONLY transaction lives inside
the Npgsql execution strategy. Cursor protection uses the native persisted Data
Protection key ring with a separate purpose and binds owner, filters, revision,
and last key. A malformed/foreign/changed-filter cursor is 400; revision drift is
409 `inventory_changed`. Restart from a fresh first page after drift.

## Explicit program context

`POST /api/review/album-audit/context`:

```json
{"memberSongIds":[101,102],"reference":null,"cursor":null,"limit":25}
```

Select 1–200 unique positive owned active IDs in any order, across exact Froms
and destinations. This selection establishes neither edition nor completeness.
`members` reuses A's allowlisted original/current tags, credits, positions,
manual fields, audits, provider candidates, duplicate flags and copy observations.
Missing original capture differs from a captured null. Unknown discs remain null;
null/1 slot warnings are potential collisions, distinct from certain collisions.
Filesystem observations are dated statistics, `contentVerified=false`.

`related` pages contain at most 100 neighbors (default 25), with exact counts,
reasons and a context-bound continuation. Relationships are exact From, exact
destination parent, exact credit+album, release ID, and the transitive native
duplicate graph (including dismissed edges and keeper pointers). They never add
a target. Source-parent-only neighbors are indicative. All other relations are
blocking dependencies: if selected+blocking rows fit 200, **all their details**
appear in `blockingRelated`, even if outside the current related page. Otherwise
`planReady=false` and `planContext=null`; the blocking count remains exact and
its ID listing is explicitly bounded to 200. Review a narrower program rather
than bypassing its dependencies.

A selection in a flat From with 5000 unrelated tracks can therefore prepare B
while paging indicative neighbors separately. Historical A still refuses its
oversized whole-From packet. No old limit was raised.

`selectionComplete` concerns requested members; `relatedComplete` concerns the
current neighbor traversal. Neither implies a complete musical program.
`membershipRevision`, `evidenceRevision`, `relatedRevision`, and combined
`contextRevision` are owner-bound, sorted, independent of response time/ID order,
and canonicalize timestamps to PostgreSQL microseconds (C-R1). Stored related
metadata/evidence and newly related Inbox rows participate. An unrelated album
outside these relationships changes inventory pagination but not this context;
a new row in the same flat From prudently invalidates the context. A DB revision
is not a file content proof.

Canonical references are cached name-linked hypotheses. Every available entry
exposes `{kind:"canonicalAlbum",id,revision}`. To select one, send that exact
object. Invalid type is 400, unavailable through the owned selection is 404,
and a changed revision is 409 `reference_changed`. No URL import or silent
substitution. Cached canonical albums have the native normalized name key,
which can collapse edition distinctions; source counts, contested tracks and
program discrepancies remain visible, without claiming a selected reference is
the right edition. Multiple owned memberships, live/studio/bonus contradictions,
VA compilations and single-artist Best Of programs are preserved.

Facts report certain/potential position collisions, contradictory credits,
years/versions/totals, unmatched members, duration/disc disagreements and uncovered
reference positions. Correspondence uses an exact recording ID or explicit
known disc+position and always qualifies reference-edition uncertainty; titles
and scores never establish a match. Without a selected reference, completeness
is unknown. Equal recording IDs never remove an album membership.

Each response has a 4000 evidence-row / 2 MiB budget; selected+blocking evidence
and the related page share that budget. Oversized unpageable evidence returns
413 `evidence_too_large`, oversized selection 413 `scope_too_large`, without a
partial packet presented as complete.

## Existing B bridge

Use the returned `planContext.scope` and `planContext.contextRevision` unchanged
in `POST /api/review/plans/preview`, followed by the existing exact plan flow.
The additive scope is:

```json
{"froms":[],"targetSongIds":[101,102],"audit":{"version":"album-audit-v1","memberSongIds":[101,102],"reference":null}}
```

Only selected IDs are writable. B still restricts writes to real Matched or
NeedsReview tracks (explicit approval required for NeedsReview); Pending remains
readable and uses D for retries. Preview/apply re-read the bounded dependencies
and selected cached reference. Drift returns `context_changed` / `plan_stale`;
new identities or destinations introducing outside tracks require
`expand_context`. Existing B/C publication gates, explicit unknowns, non-target
protection, copy outbox and idempotent historical jobs remain in force. A missing
`audit` field uses the original reader; a null field is omitted on serialization
so historical request/revision bytes stay compatible. No migration is required.

OpenAPI operation names: `ListAlbumAuditGroups`, `ListAlbumAuditGroupMembers`,
`ReadAlbumAuditContext`; DTOs live in `Contracts/AlbumAudit.cs`. Tests use disposable
PostgreSQL and synthetic metadata only. Independent exact-image/native-auth
acceptance is a separate gate before integration or deployment.
