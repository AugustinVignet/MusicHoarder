using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Auth.EndpointFilters;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Quality;
using MusicHoarder.Api.Settings;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// AI library-quality grading + the raw data export used to debug bad algorithm decisions.
/// All reads go through the request-scoped (owner-filtered) DbContext, so a user only ever sees
/// their own songs and grades.
/// </summary>
public static class QualityEndpoints
{
    private static readonly EnrichmentStatus[] GradeableStatuses =
        [EnrichmentStatus.Matched, EnrichmentStatus.NeedsReview];

    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/quality/overview", GetOverview)
            .WithName("GetQualityOverview")
            .WithSummary("Library-wide AI quality rollup: verdict counts, average score, top issues, AI-flagged count, worst offenders, per-directory breakdown.")
            .WithTags("Quality");

        app.MapGet("/api/quality/directories", GetDirectories)
            .WithName("GetQualityDirectories")
            .WithSummary("Per-directory AI quality rollups.")
            .WithTags("Quality");

        app.MapGet("/api/quality/songs", GetSongs)
            .WithName("ListQualitySongs")
            .WithSummary("Graded songs classified into flagged / silent-failure / verified-clean, by verdict, or wrong-or-questionable (the Inbox's AI-flagged queue), worst first, paged.")
            .WithTags("Quality");

        app.MapGet("/api/quality/songs/{id:int}", GetSongGrade)
            .WithName("GetSongQualityGrade")
            .WithSummary("Latest AI quality grade for one song, plus its grade history count.")
            .WithTags("Quality");

        app.MapPost("/api/quality/songs/{id:int}/grade", GradeSong)
            .WithName("GradeSongQuality")
            .WithSummary("Grade one song now (forces a fresh LLM call even if unchanged).")
            .WithTags("Quality");

        app.MapPost("/api/quality/grade-all", GradeAll)
            .WithName("GradeAllQuality")
            .WithSummary("Enqueue every gradeable song for AI grading (skips unchanged).")
            .WithTags("Quality");

        app.MapPost("/api/quality/grade-outdated", GradeOutdated)
            .WithName("GradeOutdatedQuality")
            .WithSummary("Enqueue only songs whose latest grade is outdated (prompt version or model changed since it was graded).")
            .WithTags("Quality");

        app.MapPost("/api/quality/grade-directory", GradeDirectory)
            .WithName("GradeDirectoryQuality")
            .WithSummary("Enqueue every gradeable song under a source directory for AI grading.")
            .WithTags("Quality");

        app.MapPost("/api/quality/songs/{id:int}/acknowledge", AcknowledgeGrade).RequireAdmin()
            .WithName("AcknowledgeSongQualityGrade")
            .WithSummary("Record that the song's current AI flag is a false positive, bound to that exact grade. A newer grade flags it again.")
            .WithTags("Quality");

        app.MapDelete("/api/quality/songs/{id:int}/acknowledge", RevokeAcknowledgement).RequireAdmin()
            .WithName("RevokeSongQualityAcknowledgement")
            .WithSummary("Undo the song's active false-positive acknowledgements; the AI flag shows again.")
            .WithTags("Quality");

        app.MapGet("/api/quality/acknowledgements", ListAcknowledgements).RequireAdmin()
            .WithName("ListSongQualityAcknowledgements")
            .WithSummary("False-positive acknowledgements, newest first, paged.")
            .WithTags("Quality");

        app.MapPost("/api/quality/regrade-flagged", RegradeFlagged).RequireAdmin()
            .WithName("RegradeFlaggedQuality")
            .WithSummary("Force a fresh grade for the AI-flagged, unacknowledged songs (optionally only explicit ids), worst first, bounded. Each grade is a paid LLM call.")
            .WithTags("Quality");

        app.MapGet("/api/quality/progress", GetProgress)
            .WithName("GetQualityProgress")
            .WithSummary("Current AI grading run progress.")
            .WithTags("Quality");

        app.MapGet("/api/quality/export/songs/{id:int}", ExportSong)
            .WithName("ExportSongQuality")
            .WithSummary("Download the full dossier + grade for one song (for feeding to an agent).")
            .WithTags("Quality");

        app.MapGet("/api/quality/export/directory", ExportDirectory)
            .WithName("ExportDirectoryQuality")
            .WithSummary("Download dossiers + grades for every song under a source directory.")
            .WithTags("Quality");

        app.MapGet("/api/quality/export/library", ExportLibrary)
            .WithName("ExportLibraryQuality")
            .WithSummary("Download dossiers + grades for the whole library.")
            .WithTags("Quality");

        return app;
    }

    private record GradeRowDto(
        int SongId,
        string SourcePath,
        string FileName,
        string? Artist,
        string? Title,
        string? Album,
        int Score,
        SongQualityVerdict Verdict,
        string? Summary,
        string? IssuesJson,
        string? EnrichmentStatusAtGrade,
        string? DestinationPathPreview,
        DateTime GradedAtUtc,
        int PromptVersion,
        string? Model,
        int GradeId);

    private static async Task<List<GradeRowDto>> LoadGradeRowsAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        return await db.SongQualityGrades.LatestPerSong()
            .Join(db.Songs.Where(s => s.DeletedAtUtc == null),
                g => g.SongId, s => s.Id,
                (g, s) => new GradeRowDto(
                    s.Id, s.SourcePath, s.FileName, s.Artist, s.Title, s.Album,
                    g.Score, g.Verdict, g.Summary, g.IssuesJson,
                    g.EnrichmentStatusAtGrade, g.DestinationPathPreview, g.GradedAtUtc,
                    g.PromptVersion, g.Model, g.Id))
            .ToListAsync(ct);
    }

    // Grade ids an active (not revoked) false-positive acknowledgement covers.
    private static async Task<HashSet<int>> AcknowledgedGradeIdsAsync(MusicHoarderDbContext db, CancellationToken ct)
        => (await db.SongGradeAcknowledgements.Where(a => a.RevokedAtUtc == null).Select(a => a.GradeId).ToListAsync(ct)).ToHashSet();

    // The Inbox's AI-flagged queue: a wrong/questionable latest grade nobody acknowledged as a false positive.
    private static bool IsOpenAiFlag(GradeRowDto r, HashSet<int> acknowledged)
        => VerdictSeverity.IsAiFlagged(r.Verdict) && !acknowledged.Contains(r.GradeId);

    private static string DirectoryOf(string sourcePath)
        => Path.GetDirectoryName(sourcePath)?.Replace('\\', '/') ?? "/";

    private static object ToWorstOffender(GradeRowDto r) => new
    {
        songId = r.SongId,
        fileName = r.FileName,
        sourcePath = r.SourcePath,
        artist = r.Artist,
        title = r.Title,
        album = r.Album,
        score = r.Score,
        verdict = r.Verdict.ToString(),
        summary = r.Summary,
        issues = GradingIssueJson.Parse(r.IssuesJson),
        enrichmentStatusAtGrade = r.EnrichmentStatusAtGrade,
        destinationPathPreview = r.DestinationPathPreview,
        gradedAtUtc = r.GradedAtUtc,
    };

    // The worst-offender shape plus the workbench bucket, for the AI-quality master list.
    private static object ToSongRow(GradeRowDto r, string currentModel, bool acknowledged) => new
    {
        songId = r.SongId,
        gradeId = r.GradeId,
        acknowledged,
        fileName = r.FileName,
        sourcePath = r.SourcePath,
        artist = r.Artist,
        title = r.Title,
        album = r.Album,
        score = r.Score,
        verdict = r.Verdict.ToString(),
        summary = r.Summary,
        issues = GradingIssueJson.Parse(r.IssuesJson),
        enrichmentStatusAtGrade = r.EnrichmentStatusAtGrade,
        destinationPathPreview = r.DestinationPathPreview,
        gradedAtUtc = r.GradedAtUtc,
        bucket = QualityBuckets.Classify(r.EnrichmentStatusAtGrade, r.Verdict).Name(),
        isOutdated = GradeFreshness.IsSongGradeOutdated(r.PromptVersion, r.Model, currentModel),
    };

    // Maps the `category` query value to a predicate over the latest grade rows.
    private static bool MatchesCategory(GradeRowDto r, string category) => category switch
    {
        "flagged" => QualityBuckets.Classify(r.EnrichmentStatusAtGrade, r.Verdict) == QualityBucket.Flagged,
        "silent" => QualityBuckets.Classify(r.EnrichmentStatusAtGrade, r.Verdict) == QualityBucket.Silent,
        "verified" => QualityBuckets.Classify(r.EnrichmentStatusAtGrade, r.Verdict) == QualityBucket.Verified,
        "wrong" => r.Verdict == SongQualityVerdict.Wrong,
        "questionable" => r.Verdict == SongQualityVerdict.Questionable,
        "good" => r.Verdict == SongQualityVerdict.Good,
        "excellent" => r.Verdict == SongQualityVerdict.Excellent,
        "ungradeable" => r.Verdict == SongQualityVerdict.Ungradeable,
        "wrong-or-questionable" => VerdictSeverity.IsAiFlagged(r.Verdict),
        _ => true, // "all" / unknown
    };

    // Worst first: Wrong → Questionable → Good → Excellent → Ungradeable (see VerdictSeverity), then
    // lowest score, then most recently graded.
    private static IOrderedEnumerable<GradeRowDto> OrderWorstFirst(IEnumerable<GradeRowDto> rows) => rows
        .OrderBy(r => VerdictSeverity.WorstFirstRank(r.Verdict))
        .ThenBy(r => r.Score)
        .ThenByDescending(r => r.GradedAtUtc);

    private static object AggregateToDto(QualityAggregate agg) => new
    {
        graded = agg.Graded,
        averageScore = agg.AverageScore,
        verdicts = new
        {
            excellent = agg.Verdicts.Excellent,
            good = agg.Verdicts.Good,
            questionable = agg.Verdicts.Questionable,
            wrong = agg.Verdicts.Wrong,
            ungradeable = agg.Verdicts.Ungradeable,
        },
        topIssues = agg.TopIssues.Select(i => new { code = i.Code, count = i.Count }),
    };

    internal static async Task<IResult> GetOverview(
        MusicHoarderDbContext db, IOptionsMonitor<QualityGradingOptions> options, CancellationToken ct)
    {
        var rows = await LoadGradeRowsAsync(db, ct);
        var currentModel = options.CurrentValue.Model;

        var gradeableTotal = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate && GradeableStatuses.Contains(s.EnrichmentStatus))
            .CountAsync(ct);

        var agg = QualityRollup.Aggregate(rows.Select(r => new QualityRollup.GradeRow(r.Verdict, r.Score, r.IssuesJson)));

        var worst = OrderWorstFirst(rows)
            .Take(50)
            .Select(ToWorstOffender)
            .ToList();

        var directories = BuildDirectoryRollups(rows).Take(200).ToList();

        // Bucket the latest grades for the workbench's three "needs attention" cards.
        int flagged = 0, silent = 0, verified = 0;
        foreach (var r in rows)
        {
            switch (QualityBuckets.Classify(r.EnrichmentStatusAtGrade, r.Verdict))
            {
                case QualityBucket.Flagged: flagged++; break;
                case QualityBucket.Silent: silent++; break;
                case QualityBucket.Verified: verified++; break;
            }
        }

        var outdatedCount = rows.Count(r => GradeFreshness.IsSongGradeOutdated(r.PromptVersion, r.Model, currentModel));

        // The Inbox's "AI flagged" count: exactly what the wrong-or-questionable category lists, so a
        // badge never has to count the capped worst-offender list.
        var acknowledged = await AcknowledgedGradeIdsAsync(db, ct);
        var aiFlaggedCount = rows.Count(r => IsOpenAiFlag(r, acknowledged));
        var acknowledgedFlagCount = rows.Count(r => VerdictSeverity.IsAiFlagged(r.Verdict) && acknowledged.Contains(r.GradeId));

        return Results.Ok(new
        {
            gradeableTotal,
            coverage = gradeableTotal == 0 ? 0d : Math.Round((double)rows.Count / gradeableTotal, 3),
            library = AggregateToDto(agg),
            flaggedCount = flagged,
            silentFailureCount = silent,
            verifiedCleanCount = verified,
            outdatedCount,
            aiFlaggedCount,
            acknowledgedFlagCount,
            worstOffenders = worst,
            directories,
        });
    }

    /// <summary>
    /// Graded songs for the AI-quality workbench master list, filtered by <paramref name="category"/>
    /// (flagged / silent / verified, a verdict bucket, "wrong-or-questionable", or "all"), ordered
    /// worst-first, paged. "wrong-or-questionable" leaves out acknowledged false positives unless
    /// <paramref name="includeAcknowledged"/> is set.
    /// </summary>
    // category/skip/take are optional with defaults — minimal API treats a non-nullable `int`
    // query param as REQUIRED and 400s when it's absent, so they must have defaults here.
    internal static async Task<IResult> GetSongs(
        MusicHoarderDbContext db,
        IOptionsMonitor<QualityGradingOptions> options,
        CancellationToken ct,
        string? category = null,
        int skip = 0,
        int take = 100,
        bool includeAcknowledged = false)
    {
        var cat = string.IsNullOrWhiteSpace(category) ? "all" : category.Trim().ToLowerInvariant();
        var pageSize = take <= 0 ? 100 : Math.Min(take, 500);
        var offset = Math.Max(skip, 0);
        var currentModel = options.CurrentValue.Model;

        var rows = await LoadGradeRowsAsync(db, ct);

        var acknowledged = await AcknowledgedGradeIdsAsync(db, ct);
        var hideAcknowledged = cat == "wrong-or-questionable" && !includeAcknowledged;

        var filtered = OrderWorstFirst(rows.Where(r => MatchesCategory(r, cat)
            && !(hideAcknowledged && acknowledged.Contains(r.GradeId)))).ToList();

        var items = filtered.Skip(offset).Take(pageSize)
            .Select(r => ToSongRow(r, currentModel, acknowledged.Contains(r.GradeId))).ToList();

        return Results.Ok(new { total = filtered.Count, skip = offset, take = pageSize, items });
    }

    private static IEnumerable<object> BuildDirectoryRollups(List<GradeRowDto> rows)
    {
        return rows
            .GroupBy(r => DirectoryOf(r.SourcePath))
            .Select(grp =>
            {
                var agg = QualityRollup.Aggregate(
                    grp.Select(r => new QualityRollup.GradeRow(r.Verdict, r.Score, r.IssuesJson)));
                var worstInDir = VerdictSeverity.WorstOf(grp.Select(r => r.Verdict));
                return (
                    directory: grp.Key,
                    agg,
                    worstVerdict: worstInDir,
                    wrongCount: agg.Verdicts.Wrong);
            })
            // Surface the most problematic directories first: most wrong tracks, then lowest average.
            .OrderByDescending(d => d.wrongCount)
            .ThenBy(d => d.agg.AverageScore ?? 1000d)
            .Select(d => (object)new
            {
                directory = d.directory,
                rollup = AggregateToDto(d.agg),
                worstVerdict = d.worstVerdict.ToString(),
                wrongCount = d.wrongCount,
            });
    }

    private static async Task<IResult> GetDirectories(MusicHoarderDbContext db, CancellationToken ct)
    {
        var rows = await LoadGradeRowsAsync(db, ct);
        return Results.Ok(new { directories = BuildDirectoryRollups(rows).ToList() });
    }

    private static async Task<IResult> GetSongGrade(
        int id, MusicHoarderDbContext db, IOptionsMonitor<QualityGradingOptions> options, CancellationToken ct)
    {
        var grade = await db.SongQualityGrades
            .Where(g => g.SongId == id)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (grade is null)
            return Results.Ok(new { songId = id, graded = false });

        var historyCount = await db.SongQualityGrades.CountAsync(g => g.SongId == id, ct);
        var acknowledgement = await db.SongGradeAcknowledgements
            .Where(a => a.GradeId == grade.Id && a.RevokedAtUtc == null)
            .Select(a => new { a.Reason, a.EvidenceReference, a.CreatedAtUtc })
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            songId = id,
            graded = true,
            gradeId = grade.Id,
            acknowledgement,
            score = grade.Score,
            verdict = grade.Verdict.ToString(),
            summary = grade.Summary,
            issues = GradingIssueJson.Parse(grade.IssuesJson),
            model = grade.Model,
            promptVersion = grade.PromptVersion,
            enrichmentStatusAtGrade = grade.EnrichmentStatusAtGrade,
            destinationPathPreview = grade.DestinationPathPreview,
            durationMs = grade.DurationMs,
            gradedAtUtc = grade.GradedAtUtc,
            isOutdated = GradeFreshness.IsSongGradeOutdated(grade.PromptVersion, grade.Model, options.CurrentValue.Model),
            historyCount,
        });
    }

    private static async Task<IResult> GradeSong(
        int id, MusicHoarderDbContext db, IQualityGradingService grader,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        // Ownership check via the filtered context — a cross-tenant id 404s here before grading.
        var exists = await db.Songs.AnyAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (!exists)
            return Results.NotFound(new { message = $"Song {id} not found." });

        var result = await grader.GradeSongAsync(id, force: true, ct);

        if (result.Outcome == GradeOutcome.NotConfigured)
            return NotConfiguredProblem();

        if (result.Outcome is GradeOutcome.Failed)
            return Results.Problem(
                title: "Grading failed", detail: result.Error, statusCode: 502,
                extensions: new Dictionary<string, object?> { ["errorCode"] = result.ErrorCode });

        var g = result.Grade;
        return Results.Ok(new
        {
            songId = id,
            outcome = result.Outcome.ToString(),
            score = g?.Score,
            verdict = g?.Verdict.ToString(),
            summary = g?.Summary,
            issues = GradingIssueJson.Parse(g?.IssuesJson),
            model = g?.Model,
            destinationPathPreview = g?.DestinationPathPreview,
            durationMs = g?.DurationMs,
            gradedAtUtc = g?.GradedAtUtc,
        });
    }

    private static async Task<IResult> GradeAll(
        MusicHoarderDbContext db, QualityGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate && GradeableStatuses.Contains(s.EnrichmentStatus))
            .Select(s => s.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count });
    }

    private static async Task<IResult> GradeOutdated(
        MusicHoarderDbContext db, QualityGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        var currentModel = options.CurrentValue.Model;

        // Songs whose latest grade is outdated (prompt version or model changed). force:false is fine —
        // the grader's own staleness check still re-grades on a version/model mismatch.
        var ids = await db.SongQualityGrades.LatestPerSong()
            .WhereOutdated(currentModel)
            .Join(
                db.Songs.Where(s => s.DeletedAtUtc == null && !s.IsDuplicate
                    && GradeableStatuses.Contains(s.EnrichmentStatus)),
                g => g.SongId, s => s.Id, (g, s) => s.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count });
    }

    internal record AcknowledgeGradeRequest(int? GradeId, DateTime? GradedAtUtc, string? Reason, string? EvidenceReference);

    internal static async Task<IResult> AcknowledgeGrade(
        int id, AcknowledgeGradeRequest request, MusicHoarderDbContext db, ICurrentUserAccessor user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Results.BadRequest(new { code = "reason_required", message = "Explain why the AI flag is a false positive." });
        if (request.Reason.Length > 2048 || request.EvidenceReference?.Length > 1024)
            return Results.BadRequest(new { code = "too_long", message = "Reason is limited to 2048 and evidence to 1024 characters." });
        if (request.GradeId is null && request.GradedAtUtc is null)
            return Results.BadRequest(new { code = "grade_required", message = "Name the exact grade (gradeId or gradedAtUtc) being acknowledged." });

        var exists = await db.Songs.AnyAsync(s => s.Id == id && s.DeletedAtUtc == null, ct);
        if (!exists)
            return Results.NotFound(new { message = $"Song {id} not found." });
        var latest = await db.SongQualityGrades.Where(g => g.SongId == id)
            .OrderByDescending(g => g.GradedAtUtc).ThenByDescending(g => g.Id).FirstOrDefaultAsync(ct);
        if (latest is null)
            return Results.NotFound(new { code = "no_grade", message = $"Song {id} has no AI grade." });
        // Microsecond tolerance: PostgreSQL keeps microseconds, clients echo what the list returned.
        var sameTime = request.GradedAtUtc is not { } at || Math.Abs((at.ToUniversalTime() - latest.GradedAtUtc).Ticks) < 10;
        if ((request.GradeId is { } gradeId && gradeId != latest.Id) || !sameTime)
            return Results.Conflict(new { code = "grade_not_current", message = "A newer grade exists; review it before acknowledging.", currentGradeId = latest.Id });
        if (!VerdictSeverity.IsAiFlagged(latest.Verdict))
            return Results.Conflict(new { code = "not_flagged", message = "The current grade is not an AI flag." });

        var ack = await db.SongGradeAcknowledgements.SingleOrDefaultAsync(a => a.GradeId == latest.Id, ct);
        // EF assigns a temporary key on Add, so newness is tracked explicitly rather than read from Id.
        var created = ack is null;
        if (ack is null)
        {
            ack = new SongGradeAcknowledgement { OwnerUserId = latest.OwnerUserId, SongId = id, GradeId = latest.Id };
            db.SongGradeAcknowledgements.Add(ack);
        }
        if (created || ack.RevokedAtUtc is not null)
        {
            ack.Reason = request.Reason.Trim();
            ack.EvidenceReference = request.EvidenceReference?.Trim();
            ack.ActorUserId = user.UserId;
            ack.CreatedAtUtc = DateTime.UtcNow;
            ack.RevokedAtUtc = null;
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(AcknowledgementDto(ack, latest.Id));
    }

    internal static async Task<IResult> RevokeAcknowledgement(int id, MusicHoarderDbContext db, CancellationToken ct)
    {
        var active = await db.SongGradeAcknowledgements.Where(a => a.SongId == id && a.RevokedAtUtc == null).ToListAsync(ct);
        if (active.Count == 0)
            return Results.NotFound(new { code = "no_acknowledgement", message = $"Song {id} has no active acknowledgement." });
        foreach (var ack in active) ack.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { songId = id, revoked = active.Count });
    }

    internal static async Task<IResult> ListAcknowledgements(
        MusicHoarderDbContext db, CancellationToken ct, bool includeRevoked = false, int skip = 0, int take = 100)
    {
        var query = db.SongGradeAcknowledgements.Where(a => includeRevoked || a.RevokedAtUtc == null);
        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id)
            .Skip(Math.Max(skip, 0)).Take(take <= 0 ? 100 : Math.Min(take, 500)).ToListAsync(ct);
        var songIds = page.Select(a => a.SongId).Distinct().ToArray();
        var current = await db.SongQualityGrades.LatestPerSong().Where(g => songIds.Contains(g.SongId))
            .Select(g => new { g.SongId, g.Id }).ToListAsync(ct);
        return Results.Ok(new
        {
            total,
            items = page.Select(a => AcknowledgementDto(a, current.FirstOrDefault(c => c.SongId == a.SongId)?.Id)).ToList(),
        });
    }

    private static object AcknowledgementDto(SongGradeAcknowledgement a, int? currentGradeId) => new
    {
        id = a.Id,
        songId = a.SongId,
        gradeId = a.GradeId,
        reason = a.Reason,
        evidenceReference = a.EvidenceReference,
        createdAtUtc = a.CreatedAtUtc,
        revokedAtUtc = a.RevokedAtUtc,
        // False once a newer grade replaced the acknowledged one: that newer grade is flagged again.
        isCurrentGrade = currentGradeId == a.GradeId,
    };

    internal record RegradeFlaggedRequest(int[]? SongIds, int? Limit);

    internal const int DefaultRegradeBatch = 100;
    internal const int MaxRegradeBatch = 500;

    /// <summary>Open AI flags to re-grade, worst first: wrong/questionable latest grade, not acknowledged,
    /// still gradeable. Explicit ids narrow the set; ids outside it are reported, never graded.</summary>
    internal static async Task<(List<int> Selected, int Remaining, List<int> NotFlagged)> SelectRegradeTargetsAsync(
        MusicHoarderDbContext db, int[]? songIds, int? limit, CancellationToken ct)
    {
        var rows = await LoadGradeRowsAsync(db, ct);
        var acknowledged = await AcknowledgedGradeIdsAsync(db, ct);
        var gradeable = (await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate && GradeableStatuses.Contains(s.EnrichmentStatus))
            .Select(s => s.Id).ToListAsync(ct)).ToHashSet();
        var open = OrderWorstFirst(rows.Where(r => IsOpenAiFlag(r, acknowledged) && gradeable.Contains(r.SongId)))
            .Select(r => r.SongId).ToList();
        var notFlagged = new List<int>();
        if (songIds is { Length: > 0 })
        {
            var wanted = songIds.ToHashSet();
            notFlagged = wanted.Except(open).Order().ToList();
            open = open.Where(wanted.Contains).ToList();
        }
        var batch = Math.Clamp(limit ?? DefaultRegradeBatch, 1, MaxRegradeBatch);
        return (open.Take(batch).ToList(), Math.Max(open.Count - batch, 0), notFlagged);
    }

    private static async Task<IResult> RegradeFlagged(
        RegradeFlaggedRequest? request, MusicHoarderDbContext db, QualityGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        var (selected, remaining, notFlagged) = await SelectRegradeTargetsAsync(db, request?.SongIds, request?.Limit, ct);
        // force: a fresh LLM call even when the dossier is unchanged — each one is billed.
        channel.EnqueueRange(selected, force: true);
        return Results.Ok(new { enqueued = selected.Count, songIds = selected, remaining, notFlagged, paidCalls = selected.Count });
    }

    private record GradeDirectoryRequest(string Path);

    private static async Task<IResult> GradeDirectory(
        GradeDirectoryRequest request, MusicHoarderDbContext db, QualityGradingChannel channel,
        IOptionsMonitor<QualityGradingOptions> options, IRuntimeSettingsService runtimeSettings,
        CancellationToken ct)
    {
        if (!options.CurrentValue.IsConfigured)
            return NotConfiguredProblem();
        if (!(await runtimeSettings.GetAsync(ct)).QualityGradingEnabled)
            return GradingDisabledProblem();

        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new { message = "path is required." });

        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null && !s.IsDuplicate
                && GradeableStatuses.Contains(s.EnrichmentStatus)
                && s.SourcePath.StartsWith(request.Path))
            .Select(s => s.Id)
            .ToListAsync(ct);

        channel.EnqueueRange(ids, force: false);
        return Results.Ok(new { enqueued = ids.Count, path = request.Path });
    }

    private static async Task<IResult> GetProgress(
        QualityGradingProgressTracker tracker, IOptionsMonitor<QualityGradingOptions> options,
        IRuntimeSettingsService runtimeSettings, CancellationToken ct)
    {
        var configured = options.CurrentValue.IsConfigured;
        var enabled = (await runtimeSettings.GetAsync(ct)).QualityGradingEnabled;
        var err = tracker.GetLastError();
        // Suppress the error surface entirely while grading is disabled, so turning it off in
        // Settings makes the failure banners disappear.
        object? lastError = enabled && err is not null
            ? new { code = err.Code, message = err.Message, atUtc = err.AtUtc }
            : null;

        var state = tracker.GetCurrent();
        if (state is null)
            return Results.Ok(new { active = false, aiGradingConfigured = configured, aiGradingEnabled = enabled, lastError });

        return Results.Ok(new
        {
            active = !state.IsComplete,
            aiGradingConfigured = configured,
            aiGradingEnabled = enabled,
            lastError,
            runId = state.RunId,
            total = state.Total,
            processed = state.Processed,
            graded = state.Graded,
            skipped = state.Skipped,
            failed = state.Failed,
            isComplete = state.IsComplete,
            startedAt = state.StartedAt,
            completedAt = state.CompletedAt,
        });
    }

    private static IResult NotConfiguredProblem() =>
        Results.Problem(
            title: "AI grading not configured",
            detail: "Set QualityGrading:ApiKey (and optionally BaseUrl/Model) to enable grading.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult GradingDisabledProblem() =>
        Results.Problem(
            title: "AI grading disabled",
            detail: "AI quality grading is turned off in Settings. Enable it to grade songs.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

    // --- Export ---

    private static async Task<object?> BuildExportItemAsync(
        int songId, MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        var song = await db.Songs
            .Include(s => s.ProviderAttempts)
            .FirstOrDefaultAsync(s => s.Id == songId && s.DeletedAtUtc == null, ct);
        if (song is null) return null;

        var changes = await db.SongMetadataChanges.Where(c => c.SongId == songId).ToListAsync(ct);
        var dossier = factory.Build(song, changes);

        var grade = await db.SongQualityGrades
            .Where(g => g.SongId == songId)
            .OrderByDescending(g => g.GradedAtUtc)
            .FirstOrDefaultAsync(ct);

        return new { dossier, grade = GradeExport(grade) };
    }

    private static object? GradeExport(SongQualityGrade? g) => g is null ? null : new
    {
        score = g.Score,
        verdict = g.Verdict.ToString(),
        summary = g.Summary,
        issues = GradingIssueJson.Parse(g.IssuesJson),
        model = g.Model,
        promptVersion = g.PromptVersion,
        enrichmentStatusAtGrade = g.EnrichmentStatusAtGrade,
        destinationPathPreview = g.DestinationPathPreview,
        gradedAtUtc = g.GradedAtUtc,
    };

    private static async Task<IResult> ExportSong(
        int id, MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        var item = await BuildExportItemAsync(id, db, factory, ct);
        if (item is null) return Results.NotFound(new { message = $"Song {id} not found." });
        return Results.Ok(item);
    }

    private static async Task<IResult> ExportDirectory(
        string path, MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Results.BadRequest(new { message = "path query parameter is required." });

        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null && s.SourcePath.StartsWith(path))
            .OrderBy(s => s.SourcePath)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var items = new List<object>();
        foreach (var sid in ids)
        {
            var item = await BuildExportItemAsync(sid, db, factory, ct);
            if (item is not null) items.Add(item);
        }

        return Results.Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            scope = "directory",
            path,
            count = items.Count,
            items,
        });
    }

    private static async Task<IResult> ExportLibrary(
        MusicHoarderDbContext db, IQualityDossierFactory factory, CancellationToken ct)
    {
        var ids = await db.Songs
            .Where(s => s.DeletedAtUtc == null)
            .OrderBy(s => s.SourcePath)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var items = new List<object>();
        foreach (var sid in ids)
        {
            var item = await BuildExportItemAsync(sid, db, factory, ct);
            if (item is not null) items.Add(item);
        }

        return Results.Ok(new
        {
            generatedAtUtc = DateTime.UtcNow,
            scope = "library",
            count = items.Count,
            items,
        });
    }
}
