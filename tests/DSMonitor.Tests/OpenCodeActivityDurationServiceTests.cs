using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using DSMonitor.Models;
using DSMonitor.Services;
using DSMonitor.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class OpenCodeActivityDurationServiceTests
{
    [TestMethod]
    public async Task Available_AssistantDuration_IsExact()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(null, Assistant("msg_a", 1_000, 15_781)))));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_a", "ses_a", new[] { "msg_a" }, CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Available, result.Status);
        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(14_781),
            result.Durations["msg_a"].Duration);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(1_000),
            result.Durations["msg_a"].Created);
        Assert.AreEqual(
            DateTimeOffset.FromUnixTimeMilliseconds(15_781),
            result.Durations["msg_a"].Completed);
        Assert.AreEqual(1, executor.CallCount);
    }

    [TestMethod]
    public async Task Available_ZeroDuration_IsPreserved()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(null, Assistant("msg_zero", 5_000, 5_000)))));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_zero", "ses_zero", new[] { "msg_zero" }, CancellationToken.None);

        Assert.AreEqual(TimeSpan.Zero, result.Durations["msg_zero"].Duration);
        Assert.IsTrue(result.IsComplete);
    }

    [TestMethod]
    public async Task MoreThanOneHundredMessagesAcrossPages_FindsLaterAnchor()
    {
        var pages = new List<string>();
        var messages = Enumerable.Range(0, 120)
            .Select(index => Assistant("msg_" + index, 1_000, 2_000 + index))
            .ToList();
        for (var offset = 0; offset < messages.Count; offset += 7)
        {
            var pageMessages = messages.Skip(offset).Take(7).ToArray();
            var next = offset + 7 < messages.Count
                ? "cursor_" + (offset + 7).ToString(CultureInfo.InvariantCulture)
                : null;
            pages.Add(Page(next, pageMessages));
        }

        var calls = 0;
        var executor = new FakeExecutor((_, _) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return Task.FromResult(Execution(pages[index]));
        });
        using var service = CreateService(executor, pageSize: 7, maxPages: 25);

        var result = await service.GetDurationsAsync(
            "job_many", "ses_many", new[] { "msg_119" }, CancellationToken.None);

        Assert.IsTrue(result.IsComplete);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(2_119 - 1_000),
            result.Durations["msg_119"].Duration);
        Assert.AreEqual(18, executor.CallCount);
    }

    [TestMethod]
    public async Task UserRoleAnchor_HasNoDuration()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(null, User("msg_user", 1_000, 2_000)))));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_user", "ses_user", new[] { "msg_user" }, CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Available, result.Status);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, result.Durations.Count);
    }

    [TestMethod]
    public async Task MissingOrNegativeTiming_HasNoDuration()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(
                null,
                Assistant("msg_missing", null, null),
                Assistant("msg_negative", 2_000, 1_000),
                Assistant("msg_negative_epoch", -1, 1_000)))));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_bad_time",
            "ses_bad_time",
            new[] { "msg_missing", "msg_negative", "msg_negative_epoch" },
            CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Available, result.Status);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, result.Durations.Count);
    }

    [TestMethod]
    public async Task MalformedAnchorTiming_IsSuppressed()
    {
        const string json =
            "{\"data\":[{\"id\":\"msg_bad_time\",\"type\":\"assistant\",\"time\":{\"created\":1,\"created\":2,\"completed\":3}}]," +
            "\"cursor\":{\"previous\":null,\"next\":null}}";
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(json)));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_malformed_time",
            "ses_malformed_time",
            new[] { "msg_bad_time" },
            CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Available, result.Status);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(0, result.Durations.Count);
    }

    [TestMethod]
    public async Task DuplicateMessageId_IsUnavailable()
    {
        var pages = new List<string>
        {
            Page("cursor_1", Assistant("msg_dup", 1_000, 2_000)),
            Page(null, Assistant("msg_dup", 1_000, 2_000))
        };
        var index = 0;
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(pages[index++])));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_dup", "ses_dup", new[] { "msg_dup" }, CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OpenCodeActivityDurationReason.DuplicateMessage,
            result.Reason);
    }

    [TestMethod]
    public async Task RepeatedCursor_IsPaginationInconsistent()
    {
        var pages = new List<string>
        {
            Page("cursor_1", Assistant("msg_a", 1_000, 2_000)),
            Page("cursor_1", Assistant("msg_b", 1_000, 2_000))
        };
        var index = 0;
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(pages[index++])));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_cursor", "ses_cursor", new[] { "msg_a" }, CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OpenCodeActivityDurationReason.PaginationInconsistent,
            result.Reason);
    }

    [TestMethod]
    public async Task PageLimit_IsPaginationLimit()
    {
        var calls = 0;
        var executor = new FakeExecutor((_, _) =>
        {
            var index = Interlocked.Increment(ref calls);
            return Task.FromResult(Execution(
                Page("cursor_" + index.ToString(CultureInfo.InvariantCulture),
                    Assistant("msg_" + index.ToString(CultureInfo.InvariantCulture), 1_000, 2_000))));
        });
        using var service = CreateService(executor, maxPages: 2);

        var result = await service.GetDurationsAsync(
            "job_pages", "ses_pages", new[] { "msg_1" }, CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Unavailable, result.Status);
        Assert.AreEqual(OpenCodeActivityDurationReason.PaginationLimit, result.Reason);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task Cache_CompleteResultIsReused_Invalidated_AndCleared()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(null, Assistant("msg_a", 1_000, 2_000)))));
        using var service = CreateService(executor);

        var first = await service.GetDurationsAsync(
            "job_cache", "ses_cache", new[] { "msg_a" }, CancellationToken.None);
        var repeat = await service.GetDurationsAsync(
            "job_cache", "ses_cache", new[] { "msg_a" }, CancellationToken.None);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(first.Durations["msg_a"], repeat.Durations["msg_a"]);

        var otherJob = await service.GetDurationsAsync(
            "job_other", "ses_cache", new[] { "msg_a" }, CancellationToken.None);
        Assert.AreEqual(2, executor.CallCount);
        Assert.IsTrue(otherJob.IsSuccess);

        service.InvalidateJob("job_cache");
        await service.GetDurationsAsync(
            "job_cache", "ses_cache", new[] { "msg_a" }, CancellationToken.None);
        Assert.AreEqual(3, executor.CallCount);

        service.ClearCache();
        await service.GetDurationsAsync(
            "job_other", "ses_cache", new[] { "msg_a" }, CancellationToken.None);
        Assert.AreEqual(4, executor.CallCount);
    }

    [TestMethod]
    public async Task IncompleteResult_IsNotPermanentlyCached()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(null, Assistant("msg_pending", null, null)))));
        using var service = CreateService(executor);

        var first = await service.GetDurationsAsync(
            "job_pending", "ses_pending", new[] { "msg_pending" }, CancellationToken.None);
        var second = await service.GetDurationsAsync(
            "job_pending", "ses_pending", new[] { "msg_pending" }, CancellationToken.None);

        Assert.IsFalse(first.IsComplete);
        Assert.IsFalse(second.IsComplete);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task SharedSession_DistinctAnchors_GetDistinctDurations()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            Page(
                null,
                Assistant("msg_a", 1_000, 2_000),
                Assistant("msg_b", 1_000, 16_781)))));
        using var service = CreateService(executor);

        var first = await service.GetDurationsAsync(
            "job_a", "ses_shared", new[] { "msg_a" }, CancellationToken.None);
        var second = await service.GetDurationsAsync(
            "job_b", "ses_shared", new[] { "msg_b" }, CancellationToken.None);

        Assert.AreEqual(TimeSpan.FromSeconds(1), first.Durations["msg_a"].Duration);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(15_781),
            second.Durations["msg_b"].Duration);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task InvalidIdentifiersOrNoAnchors_DoNotStartCli()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null))));
        using var service = CreateService(executor);

        var badJob = await service.GetDurationsAsync(
            "job\napi", "ses_a", new[] { "msg_a" }, CancellationToken.None);
        var badSession = await service.GetDurationsAsync(
            "job_a", "ses_bad&limit=1", new[] { "msg_a" }, CancellationToken.None);
        var badMessage = await service.GetDurationsAsync(
            "job_a", "ses_a", new[] { "bad id" }, CancellationToken.None);
        var none = await service.GetDurationsAsync(
            "job_a", "ses_a", Array.Empty<string>(), CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationReason.InvalidIdentifier, badJob.Reason);
        Assert.AreEqual(OpenCodeActivityDurationReason.InvalidIdentifier, badSession.Reason);
        Assert.AreEqual(OpenCodeActivityDurationReason.InvalidIdentifier, badMessage.Reason);
        Assert.AreEqual(OpenCodeActivityDurationStatus.None, none.Status);
        Assert.AreEqual(0, executor.CallCount);
    }

    [TestMethod]
    public async Task MalformedResponse_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution("not json")));
        using var service = CreateService(executor);

        var result = await service.GetDurationsAsync(
            "job_bad", "ses_bad", new[] { "msg_a" }, CancellationToken.None);

        Assert.AreEqual(OpenCodeActivityDurationStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OpenCodeActivityDurationReason.MalformedResponse,
            result.Reason);
    }

    [TestMethod]
    public async Task Cancellation_Propagates()
    {
        var executor = new FakeExecutor(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Execution(Page(null));
        });
        using var service = CreateService(executor);
        using var cancellation = new CancellationTokenSource();

        var pending = service.GetDurationsAsync(
            "job_cancel", "ses_cancel", new[] { "msg_a" }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await pending);
    }

    [TestMethod]
    public void Presentation_ZeroTwoDecimalsCompactAndToolPrecedence()
    {
        var zero = Row(Message("msg_zero", TimeSpan.Zero));
        Assert.AreEqual("0s", zero.DurationText);

        var shortDuration = Row(Message("msg_short", TimeSpan.FromMilliseconds(14_781)));
        Assert.AreEqual("14s", shortDuration.DurationText);
        StringAssert.Contains(
            shortDuration.DurationTooltip,
            "OpenCode assistant message duration");

        var longer = Row(Message("msg_long", TimeSpan.FromSeconds(65)));
        Assert.AreEqual("1m 5s", longer.DurationText);

        var tool = Row(
            Message("msg_tool", TimeSpan.FromMilliseconds(14_781)),
            toolDuration: TimeSpan.FromSeconds(2));
        Assert.AreEqual("2s", tool.DurationText);
        StringAssert.Contains(tool.DurationTooltip, "same-part");

        var toolRow = Row(
            Message("msg_tool_row", TimeSpan.FromMilliseconds(14_781)),
            kind: "Observed tool activity");
        Assert.AreEqual("N/A", toolRow.DurationText);
    }

    [TestMethod]
    public void Presentation_NumericSortAndExactMessageIdentity()
    {
        var shortDuration = Row(Message("msg_short", TimeSpan.FromMilliseconds(14_781)));
        var zero = Row(Message("msg_zero", TimeSpan.Zero));
        var missing = Row(null);
        var otherMessage = Row(
            Message("msg_other", TimeSpan.FromSeconds(2)),
            entryMessage: "msg_short",
            sourceTimestamp: DateTimeOffset.FromUnixTimeMilliseconds(2_000));

        Assert.AreEqual(14.781d, shortDuration.DurationSortSeconds, 0.0001);
        Assert.AreEqual(0d, zero.DurationSortSeconds);
        Assert.AreEqual(double.PositiveInfinity, missing.DurationSortSeconds);
        Assert.AreEqual("N/A", otherMessage.DurationText);
        Assert.AreEqual(FormatStarted(otherMessage.Entry.SourceTimestamp!.Value), otherMessage.StartedText);
    }

    [TestMethod]
    public void Presentation_StartedUsesExactMessageCreatedAndPreservesFallbacks()
    {
        var created = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var sourceTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(2_000);
        var message = new OpenCodeMessageDuration(
            "msg_started",
            created,
            created.AddSeconds(14),
            TimeSpan.FromSeconds(14));
        var text = Row(
            message,
            entryMessage: "msg_started",
            sourceTimestamp: sourceTimestamp);

        Assert.AreEqual(FormatStarted(created), text.StartedText);
        StringAssert.Contains(text.StartedTooltip, $"time.created {created:O}");
        StringAssert.Contains(text.StartedTooltip, $"Forge source timestamp {sourceTimestamp:O}");
        Assert.AreEqual("14s", text.DurationText);

        var noSourceEntry = text.Entry with { SourceTimestamp = null };
        var createdWithoutSource = new LiveActivityHistoryRowViewModel(
            noSourceEntry,
            null,
            false,
            null,
            message);
        Assert.AreEqual(FormatStarted(created), createdWithoutSource.StartedText);

        var noMessageEvidence = Row(
            null,
            entryMessage: "msg_started",
            sourceTimestamp: sourceTimestamp);
        Assert.AreEqual(FormatStarted(sourceTimestamp), noMessageEvidence.StartedText);

        var tool = Row(
            message,
            kind: "Observed tool activity",
            entryMessage: "msg_started",
            sourceTimestamp: sourceTimestamp);
        Assert.AreEqual(FormatStarted(sourceTimestamp), tool.StartedText);
        Assert.AreEqual("N/A", tool.DurationText);

        var result = Row(
            message,
            kind: "Agent-reported result — unverified",
            entryMessage: "msg_started",
            sourceTimestamp: sourceTimestamp);
        Assert.AreEqual(FormatStarted(sourceTimestamp), result.StartedText);

        var rolelessText = Row(
            message,
            kind: "Observed roleless text — unverified",
            entryMessage: "msg_started",
            sourceTimestamp: sourceTimestamp);
        Assert.AreEqual(FormatStarted(created), rolelessText.StartedText);
    }

    private static LiveActivityHistoryRowViewModel Row(
        OpenCodeMessageDuration? duration,
        TimeSpan? toolDuration = null,
        string kind = "Assistant-reported text",
        string? entryMessage = null,
        DateTimeOffset? sourceTimestamp = null)
    {
        entryMessage ??= duration?.MessageId ?? "msg";
        var entry = new LiveActivityHistoryEntry(
            "job",
            "ses",
            entryMessage,
            "part",
            kind,
            "synthetic",
            @"C:\synthetic\job.log",
            0,
            sourceTimestamp ?? DateTimeOffset.FromUnixTimeMilliseconds(1_000),
            DateTimeOffset.FromUnixTimeMilliseconds(2_000),
            "digest",
            toolDuration);
        if (toolDuration is { } observed)
        {
            entry = entry with
            {
                DurationStartUnixMilliseconds = 1_000,
                DurationEndUnixMilliseconds =
                    1_000 + (long)observed.TotalMilliseconds
            };
        }

        return new LiveActivityHistoryRowViewModel(entry, null, false, null, duration);
    }

    private static OpenCodeMessageDuration Message(string id, TimeSpan duration) =>
        new(
            id,
            DateTimeOffset.FromUnixTimeMilliseconds(1_000),
            DateTimeOffset.FromUnixTimeMilliseconds(1_000) + duration,
            duration);

    private static string FormatStarted(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("MMM dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static OpenCodeActivityDurationService CreateService(
        FakeExecutor executor,
        int pageSize = 50,
        int maxPages = 5) =>
        new(
            executor,
            new OpenCodeOriginalRequestOptions
            {
                PageSize = pageSize,
                MaxPages = maxPages,
                MaxResponseCharacters = 200_000,
                MaxCachedCharacters = 200_000,
                MaxCacheEntries = 8,
                OperationTimeout = TimeSpan.FromSeconds(5),
                CommandTimeout = TimeSpan.FromSeconds(2)
            });

    private static OpenCodeCliExecution Execution(string output) =>
        new(0, output, false, false, false, false);

    private static (string Id, string Type, long? Created, long? Completed) Assistant(
        string id,
        long? created,
        long? completed) => (id, "assistant", created, completed);

    private static (string Id, string Type, long? Created, long? Completed) User(
        string id,
        long? created,
        long? completed) => (id, "user", created, completed);

    private static string Page(
        string? next,
        params (string Id, string Type, long? Created, long? Completed)[] messages)
    {
        var data = string.Join(
            ",",
            messages.Select(message =>
            {
                var json = "{\"id\":" + JsonSerializer.Serialize(message.Id) +
                           ",\"type\":" + JsonSerializer.Serialize(message.Type);
                if (message.Created is not null && message.Completed is not null)
                {
                    json += ",\"time\":{\"created\":" +
                            message.Created.Value.ToString(CultureInfo.InvariantCulture) +
                            ",\"completed\":" +
                            message.Completed.Value.ToString(CultureInfo.InvariantCulture) +
                            "}";
                }

                if (string.Equals(message.Type, "user", StringComparison.Ordinal))
                {
                    json += ",\"text\":\"synthetic\",\"files\":[]";
                }

                return json + "}";
            }));
        var nextJson = next is null ? "null" : JsonSerializer.Serialize(next);
        return "{\"data\":[" + data + "],\"cursor\":{\"previous\":null,\"next\":" +
               nextJson + "}}";
    }

    private sealed class FakeExecutor : IOpenCodeCliExecutor
    {
        private readonly Func<ProcessStartInfo, CancellationToken, Task<OpenCodeCliExecution>> _handler;

        public FakeExecutor(
            Func<ProcessStartInfo, CancellationToken, Task<OpenCodeCliExecution>> handler)
        {
            _handler = handler;
        }

        public List<ProcessStartInfo> Requests { get; } = new();

        public int CallCount
        {
            get
            {
                lock (Requests)
                {
                    return Requests.Count;
                }
            }
        }

        public Task<OpenCodeCliExecution> ExecuteAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(startInfo);
            }

            return _handler(startInfo, cancellationToken);
        }
    }
}
