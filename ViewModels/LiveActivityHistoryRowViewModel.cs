using System.Globalization;
using DSMonitor.Models;
using DSMonitor.Services;

namespace DSMonitor.ViewModels;

/// <summary>
/// One row in the Live Activity tab. Started uses the verified OpenCode
/// assistant message creation time when the exact message duration evidence
/// is available; otherwise it uses the record's own source timestamp (or a
/// neutral unknown marker). Duration is only the same-part tool start/end
/// duration when the source supplied a valid pair, and Reported Status keeps
/// the row's own exact/report-local value when usable.
/// Otherwise it reuses the job-level value resolved once for the Forge jobs
/// table cell, including a job-owned text fallback. Nothing here establishes
/// completion, success, or liveness.
/// </summary>
public sealed class LiveActivityHistoryRowViewModel
{
    private readonly ReportedStatusExtraction? _matchingReportedStatus;
    private readonly string _reportedStatusValue;
    private readonly OpenCodeMessageDuration? _messageDuration;

    /// <param name="jobReportedStatus">
    /// The job-level value resolved once by
    /// <see cref="ReportedStatusPresentation.ResolveJobReportedStatus"/> for
    /// this row's own job. A value owned by another job is never applied.
    /// </param>
    /// <param name="messageDuration">
    /// Exact OpenCode assistant-message duration for this row's structured
    /// message id, when the service confirmed an assistant type and a valid
    /// created/completed pair. Its Created value may supply Started for this
    /// exact message row. It is message scoped and never replaces a valid
    /// same-part tool duration.
    /// </param>
    public LiveActivityHistoryRowViewModel(
        LiveActivityHistoryEntry entry,
        ReportedStatusExtraction? reportedStatus,
        bool isCurrentActiveTarget,
        ReportedStatusPresentation.JobReportedStatus? jobReportedStatus = null,
        OpenCodeMessageDuration? messageDuration = null)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _messageDuration = messageDuration is not null &&
                           messageDuration.Duration >= TimeSpan.Zero &&
                           entry.Kind.Contains("text", StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(
                               messageDuration.MessageId,
                               entry.MessageId,
                               StringComparison.Ordinal)
            ? messageDuration
            : null;
        _matchingReportedStatus = Matches(reportedStatus, entry)
            ? reportedStatus
            : null;
        var rowStatus = ReportedStatusPresentation.ToDisplay(
            _matchingReportedStatus,
            _matchingReportedStatus?.Source.Text ?? entry.Text);
        if (rowStatus != ReportedStatusPresentation.Unavailable)
        {
            _reportedStatusValue = rowStatus;
        }
        else if (jobReportedStatus is { } jobStatus &&
                 string.Equals(jobStatus.JobId, entry.JobId, StringComparison.Ordinal))
        {
            _reportedStatusValue = jobStatus.Value;
        }
        else if (BelongsToSameJob(reportedStatus, entry))
        {
            _reportedStatusValue = ReportedStatusPresentation.ToDisplay(reportedStatus);
        }
        else
        {
            _reportedStatusValue = rowStatus;
        }

        IsCurrentActiveTarget = isCurrentActiveTarget;
    }

    public LiveActivityHistoryEntry Entry { get; }

    /// <summary>
    /// True only for the exact currently observed Doing target when Forge
    /// metadata explicitly reports RUNNING. It is not liveness proof.
    /// </summary>
    public bool IsCurrentActiveTarget { get; }

    public string StartedText => (_messageDuration?.Created ?? Entry.SourceTimestamp) is { } timestamp
        ? timestamp.ToLocalTime().ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture)
        : "—";

    public string StartedTooltip => _messageDuration is { } messageDuration
        ? $"Verified OpenCode assistant message time.created {messageDuration.Created:O}; " +
          $"Forge source timestamp {Entry.SourceTimestamp?.ToString("O") ?? "N/A"}; " +
          $"observed {Entry.ObservedAt:O}. Job {Entry.JobId}; session {Entry.SessionId}; " +
          $"message {Entry.MessageId}. Message-scoped creation time, not this part's " +
          "observation/completion time."
        : Entry.SourceTimestamp is { } timestamp
        ? $"Source timestamp {timestamp:O}; observed {Entry.ObservedAt:O}. " +
          $"Job {Entry.JobId}; session {Entry.SessionId}; message {Entry.MessageId}; " +
          $"part {Entry.PartId}; source {Entry.SourcePath} at byte {Entry.ByteOffset}. " +
          "Observed record time only; not proof of current execution or completion."
        : $"N/A — source timestamp unavailable; observed {Entry.ObservedAt:O}. " +
          "Unknown timestamps are ordered after known timestamps. No timestamp was invented.";

    public string DurationText => Entry.ObservedDuration is { } duration
        ? FormatDuration(duration)
        : _messageDuration is { } messageDuration
            ? FormatDuration(messageDuration.Duration)
            : "N/A";

    /// <summary>
    /// Numeric sort value for the Duration column. A valid same-part tool
    /// duration wins; otherwise the exact OpenCode message duration is used.
    /// Missing evidence sorts after every known value.
    /// </summary>
    public double DurationSortSeconds =>
        Entry.ObservedDuration?.TotalSeconds ??
        _messageDuration?.Duration.TotalSeconds ??
        double.PositiveInfinity;

    public string DurationTooltip => Entry.ObservedDuration is { } duration
        ? $"Observed same-part tool duration {FormatDurationDetail(duration)}. " +
          FormatToolStartEnd() +
          " This is not a job duration, execution-liveness proof, or completion evidence."
        : _messageDuration is { } messageDuration
            ? "OpenCode assistant message duration " +
              FormatMessageDurationDetail(messageDuration.Duration) +
              $" (time.created {messageDuration.Created:O}, " +
              $"time.completed {messageDuration.Completed:O}; message {messageDuration.MessageId}). " +
              "Message scoped; not this part's duration, job duration, execution-liveness proof, " +
              "or completion evidence."
            : "N/A — no reliable same-part start/end timing or exact OpenCode assistant message " +
              "duration for this activity; no job duration or inferred timing is used.";

    /// <summary>
    /// Complete bounded display text. WPF applies ellipsis from available width;
    /// this value is never character-truncated by presentation code.
    /// </summary>
    public string ReportedStatusText => _reportedStatusValue;

    /// <summary>The same shared presentation value shown in the cell.</summary>
    public string ReportedStatusTooltip => ReportedStatusText;

    public string Provenance =>
        $"{Entry.Kind}; job {Entry.JobId}; session {Entry.SessionId}; message {Entry.MessageId}; " +
        $"part {Entry.PartId}; source {Entry.SourcePath} at byte {Entry.ByteOffset}; " +
        $"source time {Entry.SourceTimestamp?.ToString("O") ?? "N/A"}; observed {Entry.ObservedAt:O}.";

    private string FormatToolStartEnd()
    {
        if (Entry.DurationStartUnixMilliseconds is not { } start ||
            Entry.DurationEndUnixMilliseconds is not { } end)
        {
            return string.Empty;
        }

        try
        {
            var startTime = DateTimeOffset.FromUnixTimeMilliseconds(start);
            var endTime = DateTimeOffset.FromUnixTimeMilliseconds(end);
            return $"Source state.time start {startTime:O}, end {endTime:O}. ";
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }

    private static bool Matches(
        ReportedStatusExtraction? extraction,
        LiveActivityHistoryEntry entry)
    {
        if (extraction is null)
        {
            return false;
        }

        var source = extraction.Source;
        return string.Equals(source.JobId, entry.JobId, StringComparison.Ordinal) &&
               string.Equals(source.SessionId, entry.SessionId, StringComparison.Ordinal) &&
               string.Equals(source.MessageId, entry.MessageId, StringComparison.Ordinal) &&
               string.Equals(source.PartId, entry.PartId, StringComparison.Ordinal) &&
               string.Equals(source.SourcePath, entry.SourcePath, StringComparison.Ordinal) &&
               source.ByteOffset == entry.ByteOffset;
    }

    private static bool BelongsToSameJob(
        ReportedStatusExtraction? extraction,
        LiveActivityHistoryEntry entry) =>
        extraction is not null &&
        string.Equals(extraction.Source.JobId, entry.JobId, StringComparison.Ordinal);

    private static string FormatMessageDurationDetail(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";
        }

        return FormatDurationDetail(duration);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{(int)duration.TotalSeconds}s";
    }

    private static string FormatDurationDetail(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return duration.TotalSeconds >= 1
            ? duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s"
            : duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";
    }
}
