using DSMonitor.Models;
using DSMonitor.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class ForgeEventParserTests
{
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

    [TestMethod]
    public void Parse_TextEvent_DoesNotLeakRawContent()
    {
        var line = TestWorkspace.Text(
            1_800_000_000_000,
            "TOP SECRET raw model output with source code");

        var parsed = Parse(line);

        Assert.AreEqual(MonitoringEventCategory.Output, parsed.Event.Category);
        Assert.IsFalse(parsed.Event.Summary.Contains("TOP SECRET", StringComparison.Ordinal));
        StringAssert.Contains(parsed.Event.Summary, "chars");
        Assert.AreEqual("ses_test0001", parsed.Event.SessionId);
    }

    [TestMethod]
    public void Parse_ToolCompleted_ProducesSafeSummaryAndDuration()
    {
        var line = TestWorkspace.ToolUse(
            1_800_000_000_000,
            "read",
            "completed",
            1_800_000_000_000,
            1_800_000_001_250);

        var parsed = Parse(line);

        Assert.AreEqual(MonitoringEventCategory.Tool, parsed.Event.Category);
        Assert.AreEqual(MonitoringEventSeverity.Success, parsed.Event.Severity);
        StringAssert.Contains(parsed.Event.Summary, "Tool read completed");
        StringAssert.Contains(parsed.Event.Summary, "1.3 s");
        Assert.IsFalse(parsed.Event.Summary.Contains("dotnet test", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_ToolError_IsErrorSeverity()
    {
        var parsed = Parse(TestWorkspace.ToolUse(1_800_000_000_000, "shell", "error"));

        Assert.AreEqual(MonitoringEventCategory.Error, parsed.Event.Category);
        Assert.AreEqual(MonitoringEventSeverity.Error, parsed.Event.Severity);
        StringAssert.Contains(parsed.Event.Summary, "failed");
    }

    [TestMethod]
    public void Parse_ToolRunning_IsWarningSeverity()
    {
        var parsed = Parse(TestWorkspace.ToolUse(1_800_000_000_000, "shell", "running"));

        Assert.AreEqual(MonitoringEventSeverity.Warning, parsed.Event.Severity);
        StringAssert.Contains(parsed.Event.Summary, "running");
    }

    [TestMethod]
    public void Parse_FileTool_UsesFileCategory()
    {
        var parsed = Parse(TestWorkspace.ToolUse(1_800_000_000_000, "edit"));

        Assert.AreEqual(MonitoringEventCategory.File, parsed.Event.Category);
    }

    [TestMethod]
    public void Parse_StepFinish_ExtractsCostAndSafeReason()
    {
        var parsed = Parse(TestWorkspace.StepFinish(1_800_000_000_000, 0.00125, "tool-calls"));

        Assert.AreEqual(MonitoringEventCategory.Step, parsed.Event.Category);
        Assert.IsNotNull(parsed.Cost);
        Assert.AreEqual(0.00125, parsed.Cost.Value, 1e-12);
        StringAssert.Contains(parsed.Event.Summary, "tool-calls");
        StringAssert.Contains(parsed.Event.Summary, "cost");
    }

    [TestMethod]
    public void Parse_MalformedLine_ReturnsFalse()
    {
        Assert.IsFalse(ForgeEventParser.TryParse("{ nope", "job_x", Observed, out _));
        Assert.IsFalse(ForgeEventParser.TryParse(string.Empty, "job_x", Observed, out _));
        Assert.IsFalse(ForgeEventParser.TryParse("""{"timestamp":123}""", "job_x", Observed, out _));
    }

    [TestMethod]
    public void Parse_UnknownType_IsSafeGenericEvent()
    {
        var parsed = Parse("""{"type":"mystery_event","timestamp":1800000000000,"sessionID":"ses_x","part":{"id":"p1","secret":"do not show"}}""");

        Assert.AreEqual(MonitoringEventCategory.Other, parsed.Event.Category);
        StringAssert.Contains(parsed.Event.Summary, "mystery");
        Assert.IsFalse(parsed.Event.Summary.Contains("do not show", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parse_EventKey_IsStableForIdenticalLine()
    {
        var line = TestWorkspace.StepStart(1_800_000_000_000);
        var first = Parse(line);
        var second = Parse(line);

        Assert.AreEqual(first.EventKey, second.EventKey);
    }

    [TestMethod]
    public void Parse_EventKey_ChangesWhenTextPartGrows()
    {
        var first = Parse(TestWorkspace.Text(1_800_000_000_000, "hello", part: "prt_same", message: "msg_same"));
        var second = Parse(TestWorkspace.Text(1_800_000_000_000, "hello world", part: "prt_same", message: "msg_same"));

        Assert.AreNotEqual(first.EventKey, second.EventKey);
    }

    [TestMethod]
    public void Parse_Identity_UsesStructuredSourceIdsAndCanonicalEventType()
    {
        var parsed = Parse(TestWorkspace.StepStart(
            1_800_000_000_000,
            session: "ses_identity",
            part: "part_identity",
            message: "message_identity"));

        Assert.IsNotNull(parsed.Identity);
        Assert.IsTrue(parsed.Identity!.HasMessageAndPart);
        Assert.AreEqual(ForgeEventParser.SourceNamespace, parsed.Identity.Namespace);
        Assert.AreEqual("ses_identity", parsed.Identity.SessionId);
        Assert.AreEqual("message_identity", parsed.Identity.MessageId);
        Assert.AreEqual("part_identity", parsed.Identity.PartId);
        Assert.AreEqual("step-start", parsed.Identity.CanonicalEventType);
    }

    [TestMethod]
    public void Parse_Identity_IsStableWhenTextContentGrows()
    {
        var first = Parse(TestWorkspace.Text(
            1_800_000_000_000,
            "hello",
            part: "prt_same",
            message: "msg_same"));
        var second = Parse(TestWorkspace.Text(
            1_800_000_000_000,
            "hello world",
            part: "prt_same",
            message: "msg_same"));

        Assert.AreEqual(first.Identity, second.Identity);
        Assert.AreNotEqual(first.EventKey, second.EventKey);
    }

    [TestMethod]
    public void Parse_Identity_WithoutMessageOrPart_IsExplicitlyIncomplete()
    {
        var parsed = Parse("""{"type":"server_connected","timestamp":1800000000000}""");

        Assert.IsNotNull(parsed.Identity);
        Assert.IsFalse(parsed.Identity!.HasMessageAndPart);
    }

    [TestMethod]
    public void Parse_StepFinish_PreservesDecimalCostAndCostExpectedFlag()
    {
        var parsed = Parse(TestWorkspace.StepFinish(
            1_800_000_000_000,
            0.00125m,
            part: "cost-part",
            message: "cost-message"));

        Assert.IsTrue(parsed.CostExpected);
        Assert.AreEqual(0.00125m, parsed.CostAmount);
        Assert.AreEqual(0.00125, parsed.Cost!.Value, 1e-12);
    }

    [TestMethod]
    public void Parse_StepFinishWithoutCost_IsExpectedButMissing()
    {
        var parsed = Parse(
            """{"type":"step_finish","timestamp":1800000000000,"sessionID":"ses_x","part":{"id":"p1","messageID":"m1","type":"step-finish"}}""");

        Assert.IsTrue(parsed.CostExpected);
        Assert.IsNull(parsed.CostAmount);
        Assert.IsNull(parsed.Cost);
    }

    [TestMethod]
    public void Parse_MissingTimestamp_UsesObservationTimeAndKeepsHint()
    {
        var parsed = Parse("""{"type":"text","sessionID":"ses_x","part":{"id":"p1","type":"text","text":"hi"}}""");

        Assert.IsNull(parsed.Event.Timestamp);
        Assert.AreEqual(Observed, parsed.Event.ObservedAt);
        StringAssert.Contains(parsed.Event.TimestampTooltip, "unavailable");
    }

    private static ParsedLogEvent Parse(string line)
    {
        Assert.IsTrue(ForgeEventParser.TryParse(line, "job_test", Observed, out var parsed));
        Assert.IsNotNull(parsed);
        return parsed;
    }
}
