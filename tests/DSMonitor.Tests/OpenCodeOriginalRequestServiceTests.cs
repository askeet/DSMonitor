using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DSMonitor.Parsing;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class OpenCodeOriginalRequestServiceTests
{
    [TestMethod]
    public async Task Singleton_UnicodeText_IsExactAndLabelledBestEffort()
    {
        var text = "Résumé 🚀\n第二行\tend";
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_1", text)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.BestEffortSingleton, result.Status);
        Assert.AreEqual(text, result.Text);
        StringAssert.Contains(result.Label, "Best-effort singleton");
        Assert.AreEqual(1, executor.CallCount);

        var request = executor.Requests.Single();
        Assert.AreEqual("opencode", request.FileName);
        Assert.IsFalse(request.UseShellExecute);
        CollectionAssert.AreEqual(
            new[]
            {
                "api",
                "get",
                "/api/session/ses_1/message?type=user&limit=50&order=asc"
            },
            request.ArgumentList.ToArray());
    }

    [TestMethod]
    public async Task Singleton_LongText_IsNotTruncated()
    {
        var text = new string('x', 20_000) + "\n" + new string('é', 1_000);
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_long", text)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_long", "ses_long", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.BestEffortSingleton, result.Status);
        Assert.AreEqual(text.Length, result.Text.Length);
        Assert.AreEqual(text, result.Text);
    }

    [TestMethod]
    public async Task MultipleMessages_AreAmbiguousAndNotCached()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_1", "first"), ("msg_2", "second")))));
        using var service = CreateService(executor);

        var first = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);
        var second = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, first.Status);
        Assert.AreEqual(string.Empty, first.Text);
        StringAssert.Contains(first.Label, "Ambiguous");
        Assert.AreEqual(2, executor.CallCount);
        Assert.AreEqual(OriginalRequestStatus.Ambiguous, second.Status);
    }

    [TestMethod]
    public async Task NoMessages_ReturnsEmptyNone()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.None, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual(string.Empty, result.Label);
    }

    [TestMethod]
    public async Task Pagination_OneMessageThenEmptyPage_ProvesSingleton()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                requests.Count == 1
                    ? Execution(Page("cursor_one", ("msg_1", "only")))
                    : Execution("""{"data":[],"cursor":{}}"""));
        });
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.BestEffortSingleton, result.Status);
        Assert.AreEqual("only", result.Text);
        Assert.AreEqual(2, requests.Count);
        var second = requests[1].ArgumentList[2];
        StringAssert.Contains(second, "cursor=cursor_one");
        Assert.IsFalse(second.Contains("&order=", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Pagination_SecondPageHasAnotherMessage_IsAmbiguous()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                requests.Count == 1
                    ? Execution(Page("cursor_one", ("msg_1", "first")))
                    : Execution(Page(null, ("msg_2", "second"))));
        });
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task Pagination_RepeatedCursor_FailsClosed()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                requests.Count == 1
                    ? Execution(Page("cursor_one", ("msg_1", "first")))
                    : Execution(Page("cursor_one", ("msg_2", "second"))));
        });
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OriginalRequestUnavailableReason.PaginationInconsistent,
            result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task Pagination_PageLimit_FailsClosed()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            var index = requests.Count;
            return Task.FromResult(Execution(Page("cursor_" + index)));
        });
        using var service = CreateService(executor, maxPages: 3);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.PaginationLimit, result.Reason);
        Assert.AreEqual(3, requests.Count);
    }

    [DataTestMethod]
    [DataRow("not json")]
    [DataRow("""{"data":[],"cursor":{"previous":42}}""")]
    [DataRow("""{"data":[],"cursor":{"next":42}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","type":"assistant"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"bad id","time":{"created":1},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","files":{},"type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1794084044805.00005},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1e400},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    public async Task MalformedOrIncompleteResponse_IsUnavailable(string json)
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(json)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.MalformedResponse, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual(string.Empty, result.Label);
    }

    [TestMethod]
    public async Task NonZeroExit_IsUnavailableWithoutText()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(new OpenCodeCliExecution(1, "secret", false, false, true, false)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.ProcessFailure, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task TransientFailure_IsNotCached()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(new OpenCodeCliExecution(-1, string.Empty, false, true, false, false)));
        using var service = CreateService(executor);

        var first = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);
        var second = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, first.Status);
        Assert.AreEqual(OriginalRequestStatus.Unavailable, second.Status);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task TimedOutExecutor_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(new OpenCodeCliExecution(-1, string.Empty, false, true, false, false)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.TimedOut, result.Reason);
    }

    [TestMethod]
    public async Task ReadFailure_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(new OpenCodeCliExecution(
                0,
                "{\"data\":[],\"cursor\":{\"previous\":null,\"next\":null}}",
                false,
                false,
                false,
                false,
                true)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.ProcessFailure, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task OperationTimeout_IsUnavailable()
    {
        var executor = new FakeExecutor(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return Execution(Page(null, ("msg_1", "late")));
        });
        using var service = CreateService(
            executor,
            operationTimeout: TimeSpan.FromMilliseconds(50));

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.TimedOut, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task OutputLimit_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(new OpenCodeCliExecution(0, "{}", true, false, false, false)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OriginalRequestUnavailableReason.ResponseLimitExceeded,
            result.Reason);
    }

    [TestMethod]
    public async Task CallerCancellation_Propagates()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeExecutor(async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return Execution(Page(null, ("msg_1", "value")));
        });
        using var service = CreateService(executor);
        using var cancellation = new CancellationTokenSource();

        var pending = service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            async () => await pending);
        release.SetResult(true);
    }

    [TestMethod]
    public async Task Cache_IsKeyedByJobAndSession_AndRepeatUsesCache()
    {
        var executor = new FakeExecutor((request, _) =>
        {
            var query = request.ArgumentList[2];
            return Task.FromResult(Execution(Page(null, ("msg_1", query))));
        });
        using var service = CreateService(executor);

        var first = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);
        var repeat = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(first.Text, repeat.Text);

        await service.GetOriginalRequestAsync(
            "job_2", "ses_1", null, CancellationToken.None);
        Assert.AreEqual(2, executor.CallCount);

        await service.GetOriginalRequestAsync(
            "job_1", "ses_2", null, CancellationToken.None);
        Assert.AreEqual(3, executor.CallCount);
    }

    [TestMethod]
    public async Task InvalidateJob_RemovesCachedSuccess()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_1", "value")))));
        using var service = CreateService(executor);

        await service.GetOriginalRequestAsync("job_1", "ses_1", null, CancellationToken.None);
        service.InvalidateJob("job_1");
        await service.GetOriginalRequestAsync("job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task ConcurrentSameIdentity_IsDeduplicated()
    {
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var executor = new FakeExecutor(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            await release.Task.WaitAsync(cancellationToken);
            return Execution(Page(null, ("msg_1", "value")));
        });
        using var service = CreateService(executor);

        var first = service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);
        var second = service.GetOriginalRequestAsync(
            "job_1", "ses_1", null, CancellationToken.None);
        release.SetResult(true);

        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(OriginalRequestStatus.BestEffortSingleton, results[0].Status);
        Assert.AreEqual(results[0].Text, results[1].Text);
    }

    [TestMethod]
    public async Task InvalidSessionOrJob_DoesNotStartCli()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_1", "value")))));
        using var service = CreateService(executor);

        var badSession = await service.GetOriginalRequestAsync(
            "job_1", "ses_1&limit=999", null, CancellationToken.None);
        var badJob = await service.GetOriginalRequestAsync(
            "job_1\napi", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, badSession.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.InvalidIdentifier, badSession.Reason);
        Assert.AreEqual(OriginalRequestStatus.Unavailable, badJob.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.InvalidIdentifier, badJob.Reason);
        Assert.AreEqual(0, executor.CallCount);
    }

    [TestMethod]
    public async Task CursorInjection_IsEncodedInOneArgument()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                requests.Count == 1
                    ? Execution(Page("a&limit=999", ("msg_1", "value")))
                    : Execution(Page(null)));
        });
        using var service = CreateService(executor);

        await service.GetOriginalRequestAsync("job_1", "ses_1", null, CancellationToken.None);

        Assert.AreEqual(2, requests.Count);
        Assert.AreEqual(3, requests[1].ArgumentList.Count);
        Assert.AreEqual("api", requests[1].ArgumentList[0]);
        Assert.AreEqual("get", requests[1].ArgumentList[1]);
        StringAssert.Contains(requests[1].ArgumentList[2], "cursor=a%26limit%3D999");
        Assert.IsFalse(requests[1].ArgumentList[2].Contains("&limit=999", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task VerifiedCorrelation_SelectsUniqueMatchingMessage()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_1", "first"), ("msg_2", "second")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", "msg_2", CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.VerifiedJobCorrelation, result.Status);
        Assert.AreEqual("second", result.Text);
        StringAssert.Contains(result.Label, "Verified job-specific correlation");
    }

    [TestMethod]
    public async Task VerifiedCorrelation_WithoutMatch_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(Page(null, ("msg_1", "first"), ("msg_2", "second")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_1", "ses_1", "msg_missing", CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_SharedSession_DistinctStartSelectsCorrespondingMessage()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_first", "first request 🚀", 1_000),
                ("msg_second", "second request\nsecond line", 2_000)))));
        using var service = CreateService(executor);

        var first = await service.GetOriginalRequestAsync(
            "job_a",
            "ses_shared",
            verifiedUserMessageId: null,
            CancellationToken.None,
            Start(1_000));
        var second = await service.GetOriginalRequestAsync(
            "job_b",
            "ses_shared",
            verifiedUserMessageId: null,
            CancellationToken.None,
            Start(2_000));

        Assert.AreEqual(
            OriginalRequestStatus.BestEffortTimeCorrelation,
            first.Status);
        Assert.AreEqual("first request 🚀", first.Text);
        Assert.AreEqual(
            OriginalRequestStatus.BestEffortTimeCorrelation,
            second.Status);
        Assert.AreEqual("second request\nsecond line", second.Text);
        StringAssert.Contains(first.Label, "not verified job ownership");
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task TimeCorrelation_SelectsNearestOnEitherSideOfStart()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_before", "before", 900),
                ("msg_after", "after", 2_000)))));
        using var service = CreateService(executor);

        var before = await service.GetOriginalRequestAsync(
            "job_before",
            "ses_time",
            null,
            CancellationToken.None,
            Start(1_000));
        var after = await service.GetOriginalRequestAsync(
            "job_after",
            "ses_time",
            null,
            CancellationToken.None,
            Start(1_950));

        Assert.AreEqual(OriginalRequestStatus.BestEffortTimeCorrelation, before.Status);
        Assert.AreEqual("before", before.Text);
        Assert.AreEqual(OriginalRequestStatus.BestEffortTimeCorrelation, after.Status);
        Assert.AreEqual("after", after.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_HasNoMaximumGap()
    {
        const long start = 1_000_000_000;
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_near", "near", start - (10L * 24 * 60 * 60 * 1000)),
                ("msg_far", "far", start + (20L * 24 * 60 * 60 * 1000))))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_gap",
            "ses_gap",
            null,
            CancellationToken.None,
            Start(start));

        Assert.AreEqual(
            OriginalRequestStatus.BestEffortTimeCorrelation,
            result.Status);
        Assert.AreEqual("near", result.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_EqualDistance_IsAmbiguousAndEmpty()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_early", "early", 0),
                ("msg_late", "late", 2_000)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_tie",
            "ses_tie",
            null,
            CancellationToken.None,
            Start(1_000));

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
        StringAssert.Contains(result.Label, "Ambiguous");
    }

    [TestMethod]
    public async Task TimeCorrelation_MissingStart_IsAmbiguousAndEmpty()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_1", "first", 1_000),
                ("msg_2", "second", 2_000)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_missing",
            "ses_missing",
            null,
            CancellationToken.None,
            jobStartedAt: null);

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_UnsupportedStart_IsUnavailableAndEmpty()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_1", "first", 1_000),
                ("msg_2", "second", 2_000)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_invalid_start",
            "ses_invalid_start",
            null,
            CancellationToken.None,
            DateTimeOffset.MinValue);

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OriginalRequestUnavailableReason.InvalidJobTimestamp,
            result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_LaterPageCandidate_IsSelected()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                requests.Count == 1
                    ? Execution(PageWithTimes(
                        "cursor_one",
                        ("msg_far", "far", 100_000)))
                    : Execution(PageWithTimes(
                        null,
                        ("msg_near", "near", 1_000),
                        ("msg_mid", "mid", 50_000))));
        });
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_later",
            "ses_later",
            null,
            CancellationToken.None,
            Start(1_000));

        Assert.AreEqual(OriginalRequestStatus.BestEffortTimeCorrelation, result.Status);
        Assert.AreEqual("near", result.Text);
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task TimeCorrelation_IncompletePaging_FailsClosed()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(Execution(PageWithTimes(
                "cursor_" + requests.Count,
                ($"msg_{requests.Count}", "value", 1_000 + requests.Count))));
        });
        using var service = CreateService(executor, maxPages: 2);

        var result = await service.GetOriginalRequestAsync(
            "job_incomplete",
            "ses_incomplete",
            null,
            CancellationToken.None,
            Start(1_000));

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.PaginationLimit, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task TimeCorrelation_CacheIncludesExactStart()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_1", "first", 1_000),
                ("msg_2", "second", 5_000)))));
        using var service = CreateService(executor);

        var first = await service.GetOriginalRequestAsync(
            "job_cache",
            "ses_cache",
            null,
            CancellationToken.None,
            Start(1_000));
        var second = await service.GetOriginalRequestAsync(
            "job_cache",
            "ses_cache",
            null,
            CancellationToken.None,
            Start(5_000));
        var repeat = await service.GetOriginalRequestAsync(
            "job_cache",
            "ses_cache",
            null,
            CancellationToken.None,
            Start(1_000));

        Assert.AreEqual("first", first.Text);
        Assert.AreEqual("second", second.Text);
        Assert.AreEqual("first", repeat.Text);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task TimeCorrelation_ExactTicks_EqualDistanceIsTie()
    {
        const long startMilliseconds = 1_794_084_044_805;
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_early", "early", "1794084044805.0001"),
                ("msg_late", "late", "1794084044805.0003")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_exact_tie",
            "ses_exact_tie",
            null,
            CancellationToken.None,
            StartWithTicks(startMilliseconds, 2));

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_ExactTicks_SelectsNearest()
    {
        const long startMilliseconds = 1_794_084_044_805;
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_early", "early", "1794084044805.0001"),
                ("msg_late", "late", "1794084044805.0004")))));
        using var service = CreateService(executor);

        var nearestBefore = await service.GetOriginalRequestAsync(
            "job_exact_near_before",
            "ses_exact_near",
            null,
            CancellationToken.None,
            StartWithTicks(startMilliseconds, 2));
        var nearestAfter = await service.GetOriginalRequestAsync(
            "job_exact_near_after",
            "ses_exact_near",
            null,
            CancellationToken.None,
            StartWithTicks(startMilliseconds, 3));

        Assert.AreEqual(OriginalRequestStatus.BestEffortTimeCorrelation, nearestBefore.Status);
        Assert.AreEqual("early", nearestBefore.Text);
        Assert.AreEqual(OriginalRequestStatus.BestEffortTimeCorrelation, nearestAfter.Status);
        Assert.AreEqual("late", nearestAfter.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_MetadataFractionalStart_FeedsExactTicks()
    {
        const long startMilliseconds = 1_794_084_044_805;
        using var workspace = new TestWorkspace();
        var path = workspace.WriteRawMetadata(
            "job_precise.json",
            """{"id":"job_precise","pid":1,"started":1794084044805.0002,"deadline":1794084044905}""");
        var metadata = ForgeMetadataParser.ParseFile(path, workspace.RunsRoot);
        var expected = StartWithTicks(startMilliseconds, 2);

        Assert.AreEqual(expected, metadata.Started);
        Assert.AreEqual(
            expected.UtcTicks,
            metadata.Started!.Value.UtcTicks);

        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_early", "early", "1794084044805.0001"),
                ("msg_late", "late", "1794084044805.0004")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_precise",
            "ses_precise",
            null,
            CancellationToken.None,
            metadata.Started);

        Assert.AreEqual(OriginalRequestStatus.BestEffortTimeCorrelation, result.Status);
        Assert.AreEqual("early", result.Text);
    }

    [TestMethod]
    public async Task TimeCorrelation_SubTickPrecision_FailsClosed()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_too_precise", "unsupported", "1794084044805.00005"),
                ("msg_other", "other", "1794084044805.0003")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_sub_tick",
            "ses_sub_tick",
            null,
            CancellationToken.None,
            StartWithTicks(1_794_084_044_805, 2));

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OriginalRequestUnavailableReason.MalformedResponse,
            result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task VerifiedCorrelation_TakesPriorityOverTimeSelection()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithTimes(
                null,
                ("msg_verified", "verified", 100_000),
                ("msg_nearest", "nearest", 1_000)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_priority",
            "ses_priority",
            "msg_verified",
            CancellationToken.None,
            Start(1_000));

        Assert.AreEqual(OriginalRequestStatus.VerifiedJobCorrelation, result.Status);
        Assert.AreEqual("verified", result.Text);
    }

    [TestMethod]
    public async Task BoundedReader_StopsAtLimitAndSignalsOverflowOnce()
    {
        var content = new string('x', 10_000);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var overflowCalls = 0;

        var result = await OpenCodeCliProcessExecutor.ReadBoundedAsync(
            reader,
            100,
            () => overflowCalls++,
            CancellationToken.None);

        Assert.AreEqual(100, result.Text.Length);
        Assert.IsTrue(result.Overflow);
        Assert.IsTrue(result.Present);
        Assert.AreEqual(1, overflowCalls);
    }

    [TestMethod]
    public async Task BoundedReader_AtExactLimitDoesNotSignalOverflow()
    {
        var content = new string('y', 100);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var overflowCalls = 0;

        var result = await OpenCodeCliProcessExecutor.ReadBoundedAsync(
            reader,
            100,
            () => overflowCalls++,
            CancellationToken.None);

        Assert.AreEqual(content, result.Text);
        Assert.IsFalse(result.Overflow);
        Assert.AreEqual(0, overflowCalls);
    }

    private static OpenCodeOriginalRequestService CreateService(
        FakeExecutor executor,
        int maxPages = 5,
        TimeSpan? operationTimeout = null) =>
        new(
            executor,
            new OpenCodeOriginalRequestOptions
            {
                PageSize = 50,
                MaxPages = maxPages,
                MaxResponseCharacters = 200_000,
                MaxTextCharacters = 100_000,
                MaxCachedCharacters = 200_000,
                MaxCacheEntries = 8,
                OperationTimeout = operationTimeout ?? TimeSpan.FromSeconds(5),
                CommandTimeout = TimeSpan.FromSeconds(2)
            });

    private static OpenCodeCliExecution Execution(string output) =>
        new(0, output, false, false, false, false);

    private static DateTimeOffset Start(long unixMilliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);

    private static DateTimeOffset StartWithTicks(
        long unixMilliseconds,
        long extraTicks) =>
        DateTimeOffset.UnixEpoch.AddTicks(
            (unixMilliseconds * TimeSpan.TicksPerMillisecond) + extraTicks);

    private static string Page(string? next, params (string Id, string Text)[] messages) =>
        PageWithTimes(
            next,
            messages
                .Select(message => (Id: message.Id, Text: message.Text, Created: 1d))
                .ToArray());

    private static string PageWithTimes(
        string? next,
        params (string Id, string Text, double Created)[] messages)
    {
        var payload = new
        {
            data = messages.Select(message => new
            {
                id = message.Id,
                time = new { created = message.Created },
                text = message.Text,
                files = Array.Empty<object>(),
                type = "user"
            }).ToArray(),
            cursor = new { previous = (string?)null, next }
        };
        return JsonSerializer.Serialize(payload);
    }

    private static string PageWithRawTimes(
        string? next,
        params (string Id, string Text, string Created)[] messages)
    {
        var data = string.Join(
            ",",
            messages.Select(message =>
                "{\"id\":" + JsonSerializer.Serialize(message.Id) +
                ",\"time\":{\"created\":" + message.Created +
                "},\"text\":" + JsonSerializer.Serialize(message.Text) +
                ",\"files\":[],\"type\":\"user\"}"));
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
