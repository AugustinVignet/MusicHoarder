# M4A repeated retag — provisional lot C component

This component is developed on `task-review-verified-jobs` in a separate
checkout, temporarily based on unaccepted B commit `1b2ecb1`. It must be carried
onto the accepted B integration and rechecked before final C delivery.

The synthetic regression counts physical `----` parents and their data values,
not just the first list returned by a getter. Before the fix all three raw-atom
cases fail; the earlier first-value-only check also reveals a second artist ID
lost on three retags. The full native-tag selection then passes 56 tests.

The helper replaces all occurrences of each managed mean/name pair, including
old duplicate parents. It uses one parent with all data children, preserves
unrelated tags, handles empty values and rejects an excessive malformed set
explicitly. It avoids the plural setter that can duplicate a parent. Behavior
was reproduced with the project's TagLibSharp 2.3.0 binary; the
[upstream AppleTag source](https://github.com/mono/taglib-sharp/blob/main/src/TaglibSharp/Mpeg4/AppleTag.cs)
explains why getters and the plural setter can hide or repeat values.

Tests exercise two/three/five writes, both artist identities, release types,
changes between 1/3/70/0 collaborators and repair of the old physical structure.
Existing MP3/FLAC/M4A and manual-unknown tag tests remain required. Only copies
of synthetic fixtures are written. No source, production instance, plan route,
migration, queue or worker is changed by this component.

```sh
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~ReviewCopyTagRetagTests|FullyQualifiedName~TagLibLibraryTagWriterTests|FullyQualifiedName~ManualReviewTagWriterTests|FullyQualifiedName~ReviewPlanTagWriterTests'
```

The complete C workflow (durable/idempotent job, staging, tag/audio verification,
publication, failure recovery and rollback) is not delivered by this fix.
