# Read-only audio verifier — provisional lot C component

This is a building block for the forthcoming verified-copy job, on provisional
B base `1b2ecb1`. It is internal, has no HTTP route or worker registration, and
changes no plan, copy path, source file or database. It does not declare an
album or copy complete: tag verification, authorization, staging/publication,
durable state, retries and rollback still belong to the remaining C workflow.

`ReviewAudioVerifier.VerifyAsync(source, candidate)` returns qualified evidence
or a fixed failure code/stage, never raw paths/decoder errors. It:

1. Identifies the current ffmpeg and ffprobe versions.
2. Hashes each complete file, checking size/time stability during the read.
3. Reads every audio stream's codec/rate/channels (bounded to 32; larger refuses).
4. Hashes all demuxed encoded audio streams without encoding output.
5. Decodes the entire input to an in-memory checksum stream, without resampling
   or channel folding. A nonzero exit, any error diagnostic, invalid JSON/hash,
   empty audio, missing stream or output over the cap is a failure.
6. Rehashes after inspection and at the successful comparison boundary to refuse
   a source/candidate changed while the other input was inspected.

Success requires matching stream layouts, encoded packet hashes and decoded
signal hashes. Re-encoding an identical signal still fails if the encoded audio
changes. Whole-file hashes may differ because tags/container metadata changed.
This checks fidelity of observed audio, not musical edition, release completeness
or whether a source ending on a valid frame boundary should have been longer.
A read-only source mount and the future publisher's own checks remain necessary;
no atomic transaction between files and PostgreSQL is implied.

An optional proof-store interface reuses only matching content SHA-256 and
verifier/tool versions. A fresh full-file hash is required before reuse; size,
time and path are insufficient. The tests supply an in-memory fake; **durable
cache persistence is not implemented at this checkpoint**. No default cache
claims survive a restart, and no copy state is marked Done/Verified.

`ReviewAudioCommandRunner` uses argument lists without a shell, drains both
pipes with a 32768-character cap each, rejects overflow, applies a five-minute
per-command timeout by default and kills the process tree on cancellation.
It uses the provided ffmpeg path and sibling ffprobe (or PATH). Missing tools,
timeouts and malformed/error output remain explicit. Neither media nor decoded
PCM is written by the verifier.

Real tests use copied synthetic MP3/FLAC/M4A fixtures, repeated retags, two-stream
AAC copies and an omitted second stream, metadata-only and empty audio. Simulated
process tests cover nonzero exit with a valid hash, exit zero with errors,
invalid JSON/hash, empty-stream hashes, layout/payload/signal changes, cache
invalidation and file mutation. A real temporary Unix process tests both-pipe
limits, timeout and cancellation. ffmpeg/ffprobe absence and Windows-only limits
produce explicit skips; install the tools for a zero-skip acceptance run.

```sh
dotnet test MusicHoarder.Api.Tests/MusicHoarder.Api.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~ReviewAudioVerifierTests|FullyQualifiedName~ReviewCopyTagRetagTests'
```

Before final C delivery: rebase/carry the components onto accepted B, connect the
durable job ledger and proof cache, verify all native tags and publication
failure cases, then run the full official API suite and independent image
acceptance. Passing these component tests alone does not qualify C.
