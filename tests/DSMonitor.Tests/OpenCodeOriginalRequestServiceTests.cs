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
    [DataRow("""{"data":[],"data":[],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[],"cursor":{},"cursor":{}}""")]
    [DataRow("""{"data":[],"cursor":{"previous":null,"previous":null,"next":null}}""")]
    [DataRow("""{"data":[],"cursor":{"previous":null,"next":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","type":"assistant"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"bad id","time":{"created":1},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":"1"},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","files":{},"type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1e400},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","id":"msg_1","time":{"created":1},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","type":"user","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"time":{"created":1},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1,"created":1},"text":"a","type":"user"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_1","time":{"created":1},"text":"a","files":[],"files":[],"type":"user"}],"cursor":{"previous":null,"next":null}}""")]
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
    public async Task ValidUnknownPayloadProperties_AreIgnoredAndSingletonPreserved()
    {
        const string json =
            "{\"data\":[{\"id\":\"msg_1\",\"type\":\"user\",\"text\":\"request\"," +
            "\"time\":{\"created\":1,\"extra\":\"ok\"},\"files\":[]," +
            "\"extra\":{\"nested\":true}}]," +
            "\"cursor\":{\"previous\":null,\"next\":null,\"extra\":42}," +
            "\"extra\":\"root\"}";
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(json)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_5", "ses_5", null, CancellationToken.None);

        Assert.AreEqual(OriginalRequestStatus.BestEffortSingleton, result.Status);
        Assert.AreEqual("request", result.Text);
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
    public async Task ObservedCorrelation_SharedSession_DistinctAnchorsSelectCorrespondingRequest()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user_a", "user", "first request 🚀"),
                ("msg_assistant_a", "assistant", null),
                ("msg_user_b", "user", "second request\nsecond line"),
                ("msg_assistant_b", "assistant", null)))));
        using var service = CreateService(executor);

        var first = await service.GetOriginalRequestAsync(
            "job_a",
            "ses_shared",
            verifiedUserMessageId: null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_a" });
        var second = await service.GetOriginalRequestAsync(
            "job_b",
            "ses_shared",
            verifiedUserMessageId: null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_b" });

        Assert.AreEqual(
            OriginalRequestStatus.ObservedJobCorrelation,
            first.Status);
        Assert.AreEqual("first request 🚀", first.Text);
        Assert.AreEqual(
            OriginalRequestStatus.ObservedJobCorrelation,
            second.Status);
        Assert.AreEqual("second request\nsecond line", second.Text);
        StringAssert.Contains(first.Label, "inference");
        StringAssert.Contains(first.Label, "not authoritative");
        Assert.IsFalse(first.Label.Contains("Verified", StringComparison.Ordinal));
        Assert.AreEqual(2, executor.CallCount);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(6)]
    public async Task ObservedCorrelation_ManyJobsAcrossPages_SelectsEachRequest(int jobCount)
    {
        var pages = new List<string>();
        for (var i = 0; i < jobCount; i++)
        {
            pages.Add(StreamPage(
                i + 1 < jobCount ? $"cursor_{i + 1}" : null,
                ($"msg_user_{i}", "user", $"request {i}\nтест"),
                ($"msg_assistant_{i}_a", "assistant", null),
                ($"msg_assistant_{i}_b", "assistant", null)));
        }

        var executor = new FakeExecutor((request, _) =>
        {
            var query = request.ArgumentList[2];
            var index = 0;
            for (var i = 1; i < jobCount; i++)
            {
                if (query.Contains($"cursor=cursor_{i}", StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }
            return Task.FromResult(Execution(pages[index]));
        });
        using var service = CreateService(executor, maxPages: jobCount + 1);

        for (var i = 0; i < jobCount; i++)
        {
            var result = await service.GetOriginalRequestAsync(
                $"job_{i}", "ses_many", null, CancellationToken.None,
                observedAssistantMessageIds: new[]
                {
                    $"msg_assistant_{i}_a", $"msg_assistant_{i}_b"
                });
            Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status, $"Job {i}");
            Assert.AreEqual($"request {i}\nтест", result.Text, $"Job {i}");
        }
    }

    [DataTestMethod]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(6)]
    public async Task ObservedCorrelation_InterleavedJobAcrossRequests_IsAmbiguous(int requestCount)
    {
        var messages = new List<(string Id, string Type, string? Text)>();
        for (var i = 0; i < requestCount; i++)
        {
            messages.Add(($"msg_user_{i}", "user", $"request {i}"));
            messages.Add(($"msg_assistant_{i}", "assistant", null));
        }
        messages.Add(("msg_assistant_late", "assistant", null));
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(StreamPage(null, messages.ToArray()))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_overlap", "ses_overlap", null, CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_0", "msg_assistant_late" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    [Ignore("Known V1 limitation: ownership claims are evaluated per job; the service does not yet coordinate assistant message IDs claimed by different jobs in the same session.")]
    public async Task ObservedCorrelation_SameAssistantClaimedByTwoJobs_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(null,
                ("msg_user_shared", "user", "request"),
                ("msg_assistant_shared", "assistant", null)))));
        using var service = CreateService(executor);

        await service.GetOriginalRequestAsync(
            "job_first", "ses_shared", null, CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_shared" });
        var second = await service.GetOriginalRequestAsync(
            "job_second", "ses_shared", null, CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_shared" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, second.Status);
        Assert.AreEqual(string.Empty, second.Text);
    }

    [TestMethod]
    public async Task MultipleUserMessages_AreAmbiguousRegardlessOfJobStart()
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

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, before.Status);
        Assert.AreEqual(string.Empty, before.Text);
        Assert.AreEqual(OriginalRequestStatus.Ambiguous, after.Status);
        Assert.AreEqual(string.Empty, after.Text);
    }

    [TestMethod]
    public async Task MultipleUserMessages_LargeStartGap_StillAmbiguous()
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

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task MultipleUserMessages_EqualDistance_IsAmbiguousAndEmpty()
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
    public async Task MultipleUserMessages_MissingStart_IsAmbiguousAndEmpty()
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
    public async Task MultipleUserMessages_UnsupportedStart_IsAmbiguousAndEmpty()
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

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_AnchorOnLaterPage_IsSelected()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(
                requests.Count == 1
                    ? Execution(StreamPage(
                        "cursor_one",
                        ("msg_far_user", "user", "far"),
                        ("msg_far_assistant", "assistant", null)))
                    : Execution(StreamPage(
                        null,
                        ("msg_near_user", "user", "near"),
                        ("msg_near_assistant", "assistant", null))));
        });
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_later",
            "ses_later",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_near_assistant" });

        Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status);
        Assert.AreEqual("near", result.Text);
        Assert.AreEqual(2, requests.Count);
        var firstQuery = requests[0].ArgumentList[2];
        StringAssert.Contains(firstQuery, "limit=50&order=asc");
        Assert.IsFalse(firstQuery.Contains("type=user", StringComparison.Ordinal));
        var secondQuery = requests[1].ArgumentList[2];
        StringAssert.Contains(secondQuery, "cursor=cursor_one");
        Assert.IsFalse(secondQuery.Contains("&order=", StringComparison.Ordinal));
        Assert.IsFalse(secondQuery.Contains("type=user", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ObservedCorrelation_IncompletePaging_FailsClosed()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(Execution(StreamPage(
                "cursor_" + requests.Count,
                ($"msg_user_{requests.Count}", "user", "value"),
                ($"msg_assistant_{requests.Count}", "assistant", null))));
        });
        using var service = CreateService(executor, maxPages: 2);

        var result = await service.GetOriginalRequestAsync(
            "job_incomplete",
            "ses_incomplete",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_1" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.PaginationLimit, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task ObservedCorrelation_CacheIncludesExactEvidenceSet()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user_a", "user", "first"),
                ("msg_assistant_a", "assistant", null),
                ("msg_user_b", "user", "second"),
                ("msg_assistant_b", "assistant", null)))));
        using var service = CreateService(executor);
        var anchorsA = new[] { "msg_assistant_a" };
        var anchorsB = new[] { "msg_assistant_b" };

        var first = await service.GetOriginalRequestAsync(
            "job_cache",
            "ses_cache",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: anchorsA);
        var second = await service.GetOriginalRequestAsync(
            "job_cache",
            "ses_cache",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: anchorsB);
        var repeat = await service.GetOriginalRequestAsync(
            "job_cache",
            "ses_cache",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: anchorsA);

        Assert.AreEqual("first", first.Text);
        Assert.AreEqual("second", second.Text);
        Assert.AreEqual("first", repeat.Text);
        Assert.AreEqual(2, executor.CallCount);
    }

    [TestMethod]
    public async Task MultipleUserMessages_ExactTicks_EqualDistanceIsAmbiguous()
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
    public async Task ObservedCorrelation_MultipleAnchorsSameUser_AreCorrelated()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "the request"),
                ("msg_assistant_a", "assistant", null),
                ("msg_assistant_b", "assistant", null),
                ("msg_user_later", "user", "later request"),
                ("msg_assistant_later", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_same_user",
            "ses_same_user",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_a", "msg_assistant_b" });

        Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status);
        Assert.AreEqual("the request", result.Text);
    }

    [TestMethod]
    public async Task MetadataFractionalStart_DoesNotSelectAmongMultipleUsers()
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

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task MultipleUserMessages_SubTickPrecision_IsAmbiguous()
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

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task Singleton_SubTickTimestamp_StillReturnsSingleton()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_only", "only request", "1794084044805.00005")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_singleton_sub_tick",
            "ses_singleton_sub_tick",
            null,
            CancellationToken.None,
            StartWithTicks(1_794_084_044_805, 2));

        Assert.AreEqual(OriginalRequestStatus.BestEffortSingleton, result.Status);
        Assert.AreEqual("only request", result.Text);
    }

    [TestMethod]
    public async Task VerifiedCorrelation_UnusableTimestampPrecision_StillSelectsTarget()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_target", "target request", "1794084044805.0003"),
                ("msg_imprecise", "imprecise target", "1794084044805.00005"),
                ("msg_other", "other", "1794084044805.00007")))));
        using var service = CreateService(executor);

        var validTarget = await service.GetOriginalRequestAsync(
            "job_verified_valid",
            "ses_verified",
            "msg_target",
            CancellationToken.None);
        var impreciseTarget = await service.GetOriginalRequestAsync(
            "job_verified_imprecise",
            "ses_verified",
            "msg_imprecise",
            CancellationToken.None);

        Assert.AreEqual(
            OriginalRequestStatus.VerifiedJobCorrelation,
            validTarget.Status);
        Assert.AreEqual("target request", validTarget.Text);
        Assert.AreEqual(
            OriginalRequestStatus.VerifiedJobCorrelation,
            impreciseTarget.Status);
        Assert.AreEqual("imprecise target", impreciseTarget.Text);
    }

    [TestMethod]
    public async Task MultipleUserMessages_UnavailableTimestamp_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            PageWithRawTimes(
                null,
                ("msg_valid", "valid", "1794084044805.0003"),
                ("msg_imprecise", "imprecise", "1794084044805.00005")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_candidate_unavailable",
            "ses_candidate_unavailable",
            null,
            CancellationToken.None,
            StartWithTicks(1_794_084_044_805, 2));

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task VerifiedCorrelation_TakesPriorityOverObservedAnchors()
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
            Start(1_000),
            observedAssistantMessageIds: new[] { "msg_anchor" });

        Assert.AreEqual(OriginalRequestStatus.VerifiedJobCorrelation, result.Status);
        Assert.AreEqual("verified", result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_RolelessLiveActivityEvidence_FeedsApiCorrelation()
    {
        const string session = "ses_roleless_flow";
        var snapshot = new DSMonitor.Models.ForgeJobSnapshot
        {
            Metadata = new DSMonitor.Models.ForgeJobMetadata
            {
                JobId = "job_roleless_flow",
                MetadataPath = @"C:\synthetic\job_roleless_flow.json",
                LogPath = @"C:\synthetic\job_roleless_flow.log"
            },
            SessionId = session,
            ObservedAt = DateTimeOffset.UtcNow,
            LiveActivity = new DSMonitor.Models.LiveActivitySnapshot
            {
                IsHealthy = true,
                History = new[]
                {
                    new DSMonitor.Models.LiveActivityHistoryEntry(
                        "job_roleless_flow",
                        session,
                        "msg_roleless_flow",
                        "part_roleless_flow",
                        "Observed roleless text — unverified",
                        "request",
                        @"C:\synthetic\job_roleless_flow.log",
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        "digest",
                        null)
                }
            }
        };
        var ids = OriginalRequestEvidence.Collect(snapshot, session);
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user_flow", "user", "request"),
                ("msg_roleless_flow", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_roleless_flow",
            session,
            null,
            CancellationToken.None,
            observedAssistantMessageIds: ids);

        Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status);
        Assert.AreEqual("request", result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_RolelessObservedId_ApiAssistantCorrelates()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "request"),
                ("msg_roleless", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_roleless_assistant",
            "ses_roleless_assistant",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_roleless" });

        Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status);
        Assert.AreEqual("request", result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_RolelessObservedId_ApiUserRejectsCorrelation()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_roleless", "user", "request"),
                ("msg_assistant", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_roleless_user",
            "ses_roleless_user",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_roleless" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_ConflictingApiRoleForSameId_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_conflict", "user", "request"),
                ("msg_conflict", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_conflicting_role",
            "ses_conflicting_role",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_conflict" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.DuplicateMessage, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_MissingAnchor_IsAmbiguousWithoutSingletonFallback()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "only request"),
                ("msg_assistant_other", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_missing_anchor",
            "ses_missing_anchor",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_missing" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_AnchorTypeMismatch_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "only request"),
                ("msg_assistant", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_type_mismatch",
            "ses_type_mismatch",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_user" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_ConflictingPredecessors_AreAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user_first", "user", "first request"),
                ("msg_assistant_first", "assistant", null),
                ("msg_user_second", "user", "second request"),
                ("msg_assistant_second", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_conflict",
            "ses_conflict",
            null,
            CancellationToken.None,
            observedAssistantMessageIds:
                new[] { "msg_assistant_first", "msg_assistant_second" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_ContinuationSecondRequest_SelectsSecondUser()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user_first", "user", "first request"),
                ("msg_assistant_first", "assistant", null),
                ("msg_user_second", "user", "second request"),
                ("msg_assistant_second", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_continuation",
            "ses_continuation",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant_second" });

        Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status);
        Assert.AreEqual("second request", result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_AnchorIdOnlyInUserText_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "please inspect msg_fake"),
                ("msg_assistant", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_fake_text",
            "ses_fake_text",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_fake" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_UnknownVariants_ContentsIgnored()
    {
        const string json =
            "{\"data\":[" +
            "{\"id\":\"msg_user\",\"type\":\"user\",\"text\":\"request\"," +
            "\"time\":{\"created\":1},\"files\":[]}," +
            "{\"id\":\"msg_assistant\",\"type\":\"assistant\"," +
            "\"text\":{\"unexpected\":true},\"time\":\"bad\",\"files\":{},\"extra\":[1]}," +
            "{\"id\":\"msg_other\",\"type\":\"system\",\"text\":false," +
            "\"time\":[],\"files\":{}}" +
            "],\"cursor\":{\"previous\":null,\"next\":null,\"extra\":\"ok\"}," +
            "\"extra\":true}";
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(json)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_variants",
            "ses_variants",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.ObservedJobCorrelation, result.Status);
        Assert.AreEqual("request", result.Text);
    }

    [DataTestMethod]
    [DataRow("""{"data":[{"id":"msg_assistant","type":"assistant"}],"data":[],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_assistant","type":"assistant"}],"cursor":{},"cursor":{}}""")]
    [DataRow("""{"data":[{"id":"msg_assistant","type":"assistant"}],"cursor":{"previous":null,"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_assistant","type":"assistant"}],"cursor":{"previous":null,"next":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_assistant","id":"msg_assistant","type":"assistant"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_assistant","type":"assistant","type":"assistant"}],"cursor":{"previous":null,"next":null}}""")]
    [DataRow("""{"data":[{"id":"msg_assistant","type":"assistant","time":{"created":1,"created":1}}],"cursor":{"previous":null,"next":null}}""")]
    public async Task ObservedCorrelation_DuplicateConsumedProperties_IsUnavailable(string json)
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(Execution(json)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_duplicate_properties",
            "ses_duplicate_properties",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OriginalRequestUnavailableReason.MalformedResponse,
            result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_DuplicateApiMessageId_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "first"),
                ("msg_assistant", "assistant", null),
                ("msg_user", "user", "duplicate")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_duplicate_api",
            "ses_duplicate_api",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.DuplicateMessage, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_DuplicateEvidenceIds_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_user", "user", "request"),
                ("msg_assistant", "assistant", null)))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_duplicate_evidence",
            "ses_duplicate_evidence",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant", "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
        Assert.AreEqual(0, executor.CallCount);
    }

    [TestMethod]
    public async Task ObservedCorrelation_InvalidEvidenceId_IsUnavailable()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(null, ("msg_user", "user", "request")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_invalid_evidence",
            "ses_invalid_evidence",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "bad id" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.InvalidIdentifier, result.Reason);
        Assert.AreEqual(0, executor.CallCount);
    }

    [TestMethod]
    public async Task ObservedCorrelation_ApiFailure_IsUnavailableWithoutFallback()
    {
        var executor = new FakeExecutor((_, _) =>
            Task.FromResult(new OpenCodeCliExecution(1, "secret", false, false, true, false)));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_api_failure",
            "ses_api_failure",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(OriginalRequestUnavailableReason.ProcessFailure, result.Reason);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public async Task ObservedCorrelation_RepeatedCursor_FailsClosed()
    {
        var requests = new List<ProcessStartInfo>();
        var executor = new FakeExecutor((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(Execution(
                requests.Count == 1
                    ? StreamPage(
                        "cursor_one",
                        ("msg_user", "user", "request"),
                        ("msg_assistant", "assistant", null))
                    : StreamPage("cursor_one", ("msg_other", "assistant", null))));
        });
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_repeated_cursor",
            "ses_repeated_cursor",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.Unavailable, result.Status);
        Assert.AreEqual(
            OriginalRequestUnavailableReason.PaginationInconsistent,
            result.Reason);
        Assert.AreEqual(2, requests.Count);
    }

    [TestMethod]
    public async Task ObservedCorrelation_AssistantBeforeAnyUser_IsAmbiguous()
    {
        var executor = new FakeExecutor((_, _) => Task.FromResult(Execution(
            StreamPage(
                null,
                ("msg_assistant", "assistant", null),
                ("msg_user", "user", "later request")))));
        using var service = CreateService(executor);

        var result = await service.GetOriginalRequestAsync(
            "job_no_predecessor",
            "ses_no_predecessor",
            null,
            CancellationToken.None,
            observedAssistantMessageIds: new[] { "msg_assistant" });

        Assert.AreEqual(OriginalRequestStatus.Ambiguous, result.Status);
        Assert.AreEqual(string.Empty, result.Text);
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

    private static string StreamPage(
        string? next,
        params (string Id, string Type, string? Text)[] messages)
    {
        var data = string.Join(
            ",",
            messages.Select(message =>
                string.Equals(message.Type, "user", StringComparison.Ordinal)
                    ? "{\"id\":" + JsonSerializer.Serialize(message.Id) +
                      ",\"type\":\"user\",\"text\":" +
                      JsonSerializer.Serialize(message.Text ?? string.Empty) +
                      ",\"time\":{\"created\":1},\"files\":[]}"
                    : "{\"id\":" + JsonSerializer.Serialize(message.Id) +
                      ",\"type\":" + JsonSerializer.Serialize(message.Type) + "}"));
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
