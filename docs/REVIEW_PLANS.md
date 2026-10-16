# Exact album metadata plans (lot B)

Read [ALBUM_REVIEW_CONTEXT.md](ALBUM_REVIEW_CONTEXT.md) first. The three new
routes require a signed-in user with `Administer`, just like native manual
review; every query still restricts songs and plans to that user's ownership.
The legacy single-track manual-review route keeps its existing semantics.

## API

1. `POST /api/review/album-context`: retain its exact scope and revision.
2. `POST /api/review/plans/preview`: persist the immutable request, snapshot and
   preview. Only `ReviewPlans` is written; no song, provider, builder or copy.
3. `GET /api/review/plans/{id}`: recover that immutable preview, including after
   an interrupted client session. This is not a job-progress endpoint.
4. `POST /api/review/plans/{id}/apply`, body `{"revision":"<preview revision>"}`:
   recheck dependencies and atomically apply the exact metadata with its audit.
   Repeating this applied plan/revision returns its receipt without another
   metadata change. An unrelated new plan is a separate musical decision.

Synthetic preview request (replace the revision by the context response):

```json
{
  "scope": { "froms": ["/source/example"], "targetSongIds": [1, 14] },
  "contextRevision": "<64-character context revision>",
  "reason": "Positions and edition checked against the synthetic booklet",
  "evidenceReferences": ["fixture:example-booklet"],
  "tracks": [
    { "songId": 1, "approve": false, "fields": { "DiscNumber": 2, "TrackNumber": 3 } },
    { "songId": 14, "approve": true, "fields": { "Year": null, "ReleaseDate": null } }
  ]
}
```

Only real live `Matched` or `NeedsReview` targets are accepted. NeedsReview
requires `approve:true`; Matched needs no artificial status reset. Every edited
ID must belong to the context's selected target IDs. Other context neighbors
are dependencies, never implicit mutation targets. To correct such a neighbor,
select it explicitly in the scope and `tracks`. An approve-only target is
allowed; its decision is recorded in the plan receipt without inventing a
field change. Evidence references are declared by the reviewer; the server
does not decide their musical validity.

`fields` keys are case-sensitive persisted names. Missing means unchanged;
a concrete value is an explicit approval even if equal; JSON `null` means clear
and intentionally unknown. Empty strings are rejected. No name correction
selects an edition, renumbers a neighbor or derives a MusicBrainz identity.

Supported keys:

```
Title Artist Artists AlbumArtist Album Year TrackNumber DiscNumber TotalDiscs
TotalTracks IsCompilation ReleaseTypePrimary ReleaseTypes MusicBrainzId
MusicBrainzReleaseId MusicBrainzReleaseGroupId ArtistMusicBrainzIds
AlbumArtistMusicBrainzId SpotifyId AcoustIdTrackId Isrc Genre ReleaseDate
OriginalReleaseDate Label CatalogNumber Upc Composer Copyright ArtistSort
AlbumArtistSort
```

Numbers are positive integers (year <=9999, positions/totals <=100000).
Strings are trimmed, nonempty, <=4096 characters and contain no control
characters. Dates are real `YYYY`, `YYYY-MM` or `YYYY-MM-DD` values. When editing
a date/year pair, Year must agree with a retained ReleaseDate: clear both to
make the year unknown. OriginalReleaseDate is independent. TrackNumber must
not exceed per-disc TotalTracks; DiscNumber must not exceed TotalDiscs. Artist
MusicBrainz IDs must align with the semicolon-separated Artists list.
ReleaseTypes must include a retained ReleaseTypePrimary when editing this pair;
semicolon lists must not contain empty members. Submitted MusicBrainz identities must be UUIDs. Unrelated historical dirty
IDs or inconsistent dates do not prevent a name-only correction: validation
checks the touched fields and their dependent pairs without silently fixing
unsubmitted fields.

The preview includes explicit field intents and before/after values, current
and planned paths, source parents, before/after album groups, selected/context
IDs, conflicts, filesystem observations, policy version and required work.
A potential null-disc/disc-1 collision blocks an affected plan without turning
null into 1. Conflicts only among untouched context rows do not mutate them or
block an unrelated target. Destination observations cannot certify audio or
copies. An occupied/unobservable planned destination blocks application unless
it is the same track's current exact destination. A new identity involving
owned rows outside the read scope returns `expand_context` with the extra IDs;
read those neighbors first. No library-wide client export is needed.

Bounds: the existing context limits (200 tracks, 4000 evidence rows, 2 MiB)
remain whole-request refusals, including the known large flat-From limit.
Plans accept 1..200 unique targets, a reason <=1024 characters and 1..16 proof
references of <=1024 characters each. Serialized request plus preview is at
most 2 MiB; excess returns 413 with no partial plan persisted.

## Concurrency, audit and automatic work

Both transactions execute entirely inside the configured EF execution
strategy, with serializable PostgreSQL isolation. Apply reloads the saved
request and context, never takes replacement fields from the client. It binds
the context revision (tags, membership, evidence, duplicate decisions, cached
programs and copy observations), final destinations/policy, and source/final
file size/time observations. Context drift returns 409 `plan_stale` with exact
JSON paths and expected/actual values. Policy/file drift returns the old and
new previews. An expected preview revision mismatch returns 409; inaccessible
selections remain unavailable, with no partial mutation. Files can change
outside a database transaction; lot C must verify content before publication.

Each submitted field uses the existing `SongMetadataChanges` source
`ManualReview`, including explicit null and equal-value approvals, extended
with actor, reason, proof references and plan revision. Active earlier manual
values omitted from a subsequent plan are carried forward to its new approval
stamp. Historical audits remain intact. The plan preserves its original
request/snapshot/preview and application time.

`Songs.ReviewPlanId` binds the resulting metadata and is an EF concurrency
token: a worker that loaded an old row cannot silently save over a newly
applied decision. Bound rows keep exact metadata through canonical correction,
identity reconciliation and provider merge; generic healer/consolidator and
builder selection exclude them. Per-track build rechecks the binding. Explicit
native unlock releases it; ordinary automatic resets do not. Reindexing a
changed source updates size/time/index observations while retaining the exact
metadata and held copies; C must revalidate the changed audio before copying.

Native tag writing respects the exact identity and deliberate unknowns,
including dates, IDs, artists, totals and compilation; unknown IsCompilation
uses the existing nonnullable false storage plus an authoritative null audit.
The context returns null and Apple `cpil` is removed rather than recording a
false assertion. No album-artist/ARTISTS fallback refills an intentionally
unknown value. MB IDs absent from a bound exact plan clear stale source IDs;
legacy unreviewed songs keep their conservative behavior. Spotify/AcoustID
native fields use `SPOTIFY_TRACK_ID` and `Acoustid Id` (Xiph `ACOUSTID_ID`).
MP3/Xiph detailed dates follow the existing writer; M4A standard date remains
year-only. This does not claim repeated M4A ARTISTS atoms are fixed (lot C).

## Copy state, migration and rollback

Applying B reports `metadataApplied` and `copyState:awaitingVerifiedBuild`.
Affected songs have `LibraryBuildStatus.AwaitingReviewBuild` (5). Current and
previous copy paths, build timestamps and old files are retained. B creates no
copy job and never reports a copy as rebuilt or verified. Stage C will add the
verified durable copy workflow; do not use B alone for the real review pilot.

Migration `20261016211342_AddExactReviewPlans` adds one table, one nullable song
binding and four nullable audit columns. Existing migrations are unchanged.
No automatic musical backfill or requeue. Test on a disposable/restored database
with the normal migration startup. Down removes B's plan state and audit
extensions, so preserve a database backup and receipts first. It leaves edited
tags and legacy audit columns intact; it does not reverse musical decisions or
restore copies. Prefer restoring the pre-test database and previous image for
an isolated acceptance rollback; do not downgrade a production database
containing applied plans blindly.

Tests live in `MusicHoarder.Api.Tests/Endpoints/ReviewPlanTests.cs` and
`Library/ReviewPlanTagWriterTests.cs`, with the PostgreSQL context fixture.
Set `MH_REVIEW_TEST_POSTGRES` to an explicitly disposable local server with
`Database=mh_review_test`. Each test creates/drops its own random database.
Absent configuration produces explicit PostgreSQL skips. Never use the real
connection, source mounts or provider credentials. Standard command:

```sh
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~ReviewPlan|FullyQualifiedName~AlbumReviewContext|FullyQualifiedName~ManualReview'
```

Run the official whole API suite after the targeted checks. Independent
acceptance must build and launch the exact task SHA before any improvement
merge, with the normal authentication/persistence composition and a restored
isolated database. Library and watcher of the real instance remain untouched.
