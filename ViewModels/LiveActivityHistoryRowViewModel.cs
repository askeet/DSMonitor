using System.Globalization;
using DSMonitor.Models;
using DSMonitor.Services;

namespace DSMonitor.ViewModels;

/// <summary>
/// One row in the Live Activity tab. Started uses the verified OpenCode
/// assistant message creation time when the exact message duration evidence is
/// available, otherwise a validated tool <c>state.time.start</c>, otherwise the
/// record's own source timestamp (or a neutral unknown marker). Duration is the
/// same-part tool start/end duration, an exact step start/finish span, or the
/// exact message duration for text; nothing else is inferred. Reported Status
/// is the exact tool name/status pair, the step finish reason, or the row's own
/// exact/report-local value.
/// Otherwise it reuses the job-level value resolved once for the Forge jobs
/// table cell, including a job-owned text fallback. Nothing here establishes
/// completion, success, or liveness.
/// </summary>
public sealed class LiveActivityHistoryRowViewModel
{
    private readonly ReportedStatusExtraction? _matchingReportedStatus;
    private readonly string _reportedStatusValue;
    private readonly OpenCodeMessageDuration? _messageDuration;
    private readonly LiveActivityStepPair? _step;
    private readonly DateTimeOffset? _observedNow;
    private readonly bool _isActiveStep;

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
    /// same-part tool duration or a step span.
    /// </param>
    /// <param name="step">
    /// Pairing decision derived from the retained bounded history. It is null
    /// for text/tool rows and always carries the physical records as
    /// provenance; it never stands in for job completion.
    /// </param>
    /// <param name="isActiveStep">
    /// True only when this unpaired step start is the newest observed lifecycle
    /// record of a running job while monitoring is active. Anything else
    /// renders a neutral duration.
    /// </param>
    public LiveActivityHistoryRowViewModel(
        LiveActivityHistoryEntry entry,
        ReportedStatusExtraction? reportedStatus,
        bool isCurrentActiveTarget,
        ReportedStatusPresentation.JobReportedStatus? jobReportedStatus = null,
        OpenCodeMessageDuration? messageDuration = null,
        LiveActivityStepPair? step = null,
        DateTimeOffset? observedNow = null,
        bool isActiveStep = false)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _step = step;
        _observedNow = observedNow;
        _isActiveStep = isActiveStep;
        _messageDuration = messageDuration is not null &&
                           messageDuration.Duration >= TimeSpan.Zero &&
                           KindOf == LiveActivityKind.Text &&
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

    /// <summary>Exact activity class; a textual fallback exists only for legacy entries.</summary>
    public LiveActivityKind ActivityKind => KindOf;

    private LiveActivityKind KindOf => LiveActivityHistoryProjection.ResolveKind(Entry);

    public bool IsTextActivity => KindOf == LiveActivityKind.Text;

    public bool IsToolActivity => KindOf == LiveActivityKind.Tool;

    public bool IsStepActivity =>
        KindOf is LiveActivityKind.StepStart or LiveActivityKind.StepFinish;

    /// <summary>True only for an exact <c>state.status</c> completion.</summary>
    public bool IsSuccessfulToolActivity =>
        IsToolActivity && Entry.ToolOutcome == LiveActivityToolOutcome.Completed;

    /// <summary>True only for an exact <c>state.status</c> error.</summary>
    public bool IsErrorToolActivity =>
        IsToolActivity && Entry.ToolOutcome == LiveActivityToolOutcome.Failed;

    /// <summary>
    /// Exact tool display value: the bounded source name and the exact source
    /// status. No command, input, or output data is included.
    /// </summary>
    public string ToolStatusText =>
        $"{(string.IsNullOrWhiteSpace(Entry.ToolName) ? "Tool" : Entry.ToolName)} · " +
        $"{(string.IsNullOrWhiteSpace(Entry.ToolStatus) ? "status unavailable" : Entry.ToolStatus)}";

    /// <summary>
    /// Step display value: a paired row shows the exact finish reason only
    /// (`Step: tool-calls`, `Step: stop`, or `Step: finished`). An unmatched
    /// finish is exactly `Step: unmatched finish`. An unmatched start is
    /// `Step: in progress` only for a verified active job and `Step: unfinished`
    /// otherwise. A record that cannot be paired unambiguously is
    /// `Step: ambiguous`. No raw text is used.
    /// </summary>
    public string StepStatusText => _step?.State switch
    {
        LiveActivityStepState.Paired => "Step: " + StepReason,
        LiveActivityStepState.UnmatchedFinish => "Step: unmatched finish",
        LiveActivityStepState.UnmatchedStart =>
            _isActiveStep ? "Step: in progress" : "Step: unfinished",
        LiveActivityStepState.Ambiguous => "Step: ambiguous",
        _ => IsStepActivity && _step is null ? "Step: recorded" : "Step: started"
    };

    /// <summary>Pairing state of this row; <see cref="LiveActivityStepState.NotStep"/> for other kinds.</summary>
    public LiveActivityStepState StepState => _step?.State ?? LiveActivityStepState.NotStep;

    /// <summary>
    /// True only for the exact currently observed Doing target when Forge
    /// metadata explicitly reports RUNNING. It is not liveness proof.
    /// </summary>
    public bool IsCurrentActiveTarget { get; }

    public string StartedText =>
        (IsToolActivity ? ToolStartUnixMilliseconds() : null) is { } toolStart
            ? FormatStarted(toolStart)
            : (_messageDuration?.Created ?? Entry.SourceTimestamp) is { } timestamp
                ? timestamp.ToLocalTime().ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "—";

    /// <summary>Timestamp used to order all Activity History categories newest first.</summary>
    public DateTimeOffset? StartedSortTimestamp
    {
        get
        {
            if (IsToolActivity && Entry.ToolStartUnixMilliseconds is { } toolStart)
            {
                try
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(toolStart);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Invalid tool timing has no sortable Started value.
                }
            }

            return _messageDuration?.Created ?? Entry.SourceTimestamp;
        }
    }

    public string StartedTooltip
    {
        get
        {
            if (IsToolActivity && ToolStartUnixMilliseconds() is { } toolStart)
            {
                var startTime = FormatUnixMilliseconds(toolStart);
                return $"Validated tool state.time.start {startTime}; " +
                       $"Forge source timestamp {Entry.SourceTimestamp?.ToString("O") ?? "N/A"}; " +
                       $"observed {Entry.ObservedAt:O}. Job {Entry.JobId}; session {Entry.SessionId}; " +
                       $"message {Entry.MessageId}; part {Entry.PartId}. " +
                       "Same-part tool start evidence only; not job duration or completion.";
            }

            if (_messageDuration is { } messageDuration)
            {
                return $"Verified OpenCode assistant message time.created {messageDuration.Created:O}; " +
                       $"Forge source timestamp {Entry.SourceTimestamp?.ToString("O") ?? "N/A"}; " +
                       $"observed {Entry.ObservedAt:O}. Job {Entry.JobId}; session {Entry.SessionId}; " +
                       $"message {Entry.MessageId}. Message-scoped creation time, not this part's " +
                       "observation/completion time.";
            }

            return Entry.SourceTimestamp is { } timestamp
                ? $"Source timestamp {timestamp:O}; observed {Entry.ObservedAt:O}. " +
                  $"Job {Entry.JobId}; session {Entry.SessionId}; message {Entry.MessageId}; " +
                  $"part {Entry.PartId}; source {Entry.SourcePath} at byte {Entry.ByteOffset}. " +
                  "Observed record time only; not proof of current execution or completion."
                : $"N/A — source timestamp unavailable; observed {Entry.ObservedAt:O}. " +
                  "Unknown timestamps are ordered after known timestamps. No timestamp was invented.";
        }
    }

    public string DurationText =>
        ResolveDuration() is { } duration ? FormatDuration(duration) : "N/A";

    /// <summary>
    /// Numeric sort value for the Duration column. A paired step span, a valid
    /// same-part tool duration, or the exact OpenCode message duration wins in
    /// that order. Missing evidence sorts after every known value.
    /// </summary>
    public double DurationSortSeconds =>
        ResolveDuration()?.TotalSeconds ?? double.PositiveInfinity;

    public string DurationTooltip
    {
        get
        {
            if (IsStepActivity)
            {
                return StepDurationTooltip();
            }

            return Entry.ObservedDuration is { } duration
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
        }
    }

    /// <summary>
    /// Complete bounded display text. WPF applies ellipsis from available width;
    /// this value is never character-truncated by presentation code.
    /// </summary>
    public string ReportedStatusText =>
        IsToolActivity ? ToolStatusText
        : IsStepActivity ? StepStatusText
        : _reportedStatusValue;

    /// <summary>The same shared presentation value shown in the cell.</summary>
    public string ReportedStatusTooltip => ReportedStatusText;

    public string Provenance
    {
        get
        {
            var baseValue =
                $"{Entry.Kind}; job {Entry.JobId}; session {Entry.SessionId}; message {Entry.MessageId}; " +
                $"part {Entry.PartId}; source {Entry.SourcePath} at byte {Entry.ByteOffset}; " +
                $"source time {Entry.SourceTimestamp?.ToString("O") ?? "N/A"}; observed {Entry.ObservedAt:O}.";
            if (IsToolActivity)
            {
                baseValue += $" Tool name {Entry.ToolName ?? "N/A"}; exact state.status " +
                             $"{Entry.ToolStatus ?? "N/A"}; outcome {Entry.ToolOutcome}.";
            }

            if (IsStepActivity && _step is { } step)
            {
                baseValue += $" Step state {step.State}; start part {Entry.PartId} at byte " +
                             $"{Entry.ByteOffset} with digest {Entry.RecordDigest}.";
                if (step.Finish is { } finish)
                {
                    baseValue += $" Finish part {finish.PartId} at byte {finish.ByteOffset} with digest " +
                                 $"{finish.RecordDigest}; finish source time " +
                                 $"{finish.SourceTimestamp?.ToString("O") ?? "N/A"}; reason " +
                                 $"{finish.StepFinishReason ?? "N/A"}.";
                }

                if (Entry.StepIdentityConflict)
                {
                    baseValue += " Duplicate or conflicting step identity observed; pairing fails closed.";
                }
            }

            return baseValue;
        }
    }

    private string StepReason
    {
        get
        {
            var reason = _step?.Finish?.StepFinishReason ?? Entry.StepFinishReason;
            return string.IsNullOrWhiteSpace(reason) ? "finished" : reason;
        }
    }

    private long? ToolStartUnixMilliseconds() => Entry.ToolStartUnixMilliseconds;

    private TimeSpan? ResolveDuration()
    {
        if (IsStepActivity)
        {
            return StepDuration();
        }

        return Entry.ObservedDuration ?? _messageDuration?.Duration;
    }

    /// <summary>
    /// A paired step uses only its two exact source timestamps when both are
    /// known and ordered. An unpaired start is live only with defensible
    /// evidence (running job, active monitoring, newest lifecycle record) and
    /// stops as soon as that evidence disappears. Everything else is N/A.
    /// </summary>
    private TimeSpan? StepDuration()
    {
        if (_step is not { } step)
        {
            return null;
        }

        if (step.State == LiveActivityStepState.Paired &&
            step.Finish is { } finish)
        {
            var start = Entry.SourceTimestamp;
            var end = finish.SourceTimestamp;
            if (start is null || end is null || end < start)
            {
                return null;
            }

            return end.Value - start.Value;
        }

        if (step.State == LiveActivityStepState.UnmatchedStart &&
            _isActiveStep &&
            Entry.SourceTimestamp is { } started &&
            _observedNow is { } now &&
            now >= started)
        {
            return now - started;
        }

        return null;
    }

    private string StepDurationTooltip()
    {
        if (_step is not { } step)
        {
            return "N/A — no pairing evidence for this step record; no duration is inferred.";
        }

        switch (step.State)
        {
            case LiveActivityStepState.Paired when step.Finish is { } finish:
                if (Entry.SourceTimestamp is { } start && finish.SourceTimestamp is { } end && end >= start)
                {
                    return $"Step source span {FormatDurationDetail(end - start)} from start record " +
                           $"{Entry.PartId} at byte {Entry.ByteOffset} ({start:O}) to finish record " +
                           $"{finish.PartId} at byte {finish.ByteOffset} ({end:O}); reason " +
                           $"{finish.StepFinishReason ?? "N/A"}. Two exact source timestamps only; not a job " +
                           "duration, cost, execution-liveness proof, or completion evidence.";
                }

                return "N/A — the paired step timestamps are missing, unordered, or invalid; " +
                       "no duration is inferred.";
            case LiveActivityStepState.UnmatchedStart:
                return _isActiveStep
                    ? "Live source span from this step start record's timestamp while the job is reported " +
                      "running, monitoring is active, and this is the newest observed lifecycle record. " +
                      "It stops when that evidence disappears; it is not a job duration or completion."
                    : "N/A — this step start has no visible finish in the bounded window and no defensible " +
                      "active evidence; no duration is inferred.";
            case LiveActivityStepState.UnmatchedFinish:
                return "N/A — this step finish has no visible start in the bounded window; " +
                       "no duration is inferred.";
            case LiveActivityStepState.Ambiguous:
                return "N/A — repeated, duplicated, or conflicting step records prevent an unambiguous " +
                       "start/finish pairing; no duration is inferred.";
            default:
                return "N/A — no reliable step pairing evidence; no duration is inferred.";
        }
    }

    private static string FormatStarted(long unixMilliseconds) =>
        FormatUnixMilliseconds(unixMilliseconds).ToLocalTime()
            .ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTimeOffset FormatUnixMilliseconds(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);

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
