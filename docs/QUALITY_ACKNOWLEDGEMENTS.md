# AI flag acknowledgements and targeted regrading

The Inbox's AI-flagged queue (`GET /api/quality/songs?category=wrong-or-questionable`)
lists songs whose latest grade is Wrong or Questionable. A reviewer who has proven
such a flag false (for example by an excerpt or provider comparison) can record that
decision instead of changing tags to clear a badge.

## Acknowledge one exact grade

`POST /api/quality/songs/{id}/acknowledge` (Administer):

```json
{ "gradeId": 123, "reason": "Excerpt alignment confirms the recording", "evidenceReference": "cache:sha256:..." }
```

`gradedAtUtc` may name the grade instead of `gradeId`; one of them is required. The
grade must be the song's current one (`409 grade_not_current` otherwise, with
`currentGradeId`) and must be an AI flag (`409 not_flagged`). A reason is required.
Acknowledging the same grade again is idempotent.

The acknowledged song leaves the wrong-or-questionable list and the overview's
`aiFlaggedCount` (`acknowledgedFlagCount` counts them); `includeAcknowledged=true`
shows them again. List rows and `GET /api/quality/songs/{id}` expose `gradeId` and
the acknowledgement. The decision is bound to that grade row: any newer grade is
flagged again until reviewed. `DELETE /api/quality/songs/{id}/acknowledge` revokes it
(the row stays as history); `GET /api/quality/acknowledgements?includeRevoked=false`
pages them, with `isCurrentGrade` false once a newer grade replaced the acknowledged one.

## Regrade the open flags

`POST /api/quality/regrade-flagged` (Administer) with an optional body
`{ "songIds": [1, 2], "limit": 100 }` forces fresh grades for open (unacknowledged,
gradeable, non-duplicate) AI flags, worst first, at most `limit` (default 100, max 500)
per call. Explicit ids narrow the set; ids that are not open flags are returned in
`notFlagged` and never graded. The response reports `enqueued`, `songIds`, `remaining`
and `paidCalls`. **Each grade is a paid LLM call.** `POST /api/quality/songs/{id}/grade`
still grades one song synchronously.
