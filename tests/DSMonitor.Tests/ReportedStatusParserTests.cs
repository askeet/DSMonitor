using DSMonitor.Models;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

[TestClass]
public sealed class ReportedStatusParserTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_010_000);

    [TestMethod]
    public void Extract_ParsesUserExamplesAndValidCounterparts()
    {
        var cases = new (string Text, string Expected)[]
        {
            ("Status: DONE", "DONE"),
            ("status: DONE", "DONE"),
            ("## Status: DONE", "DONE"),
            ("###Status: DONE", "DONE"),
            ("## Status: DONE ##", "DONE"),
            ("**Status:** DONE", "DONE"),
            ("**Status**: DONE", "DONE"),
            ("*Status:* DONE", "DONE"),
            ("*Status*: DONE", "DONE"),
            ("**Status: DONE**", "DONE"),
            ("*Status: DONE*", "DONE"),
            ("Status: **DONE**", "DONE"),
            ("Status: *DONE*", "DONE"),
            ("Status: \"DONE\"", "DONE"),
            ("Status: “DONE”", "DONE"),
            ("Status:: DONE", "DONE"),
            ("  ## **Status:** *DONE*", "DONE")
        };

        foreach (var (text, expected) in cases)
        {
            var extraction = Extract(text);
            Assert.AreEqual(ReportedStatusState.Reported, extraction.State, text);
            Assert.AreEqual(expected, extraction.Value, text);
            Assert.AreEqual(1, extraction.Declarations.Count, text);
            Assert.IsFalse(extraction.Declarations[0].IsMalformed, text);
        }
    }

    [TestMethod]
    public void Extract_PairedValuesPreservePunctuationAndFullLength()
    {
        var exact = Extract("Status: **v1.2.3-complete**");
        Assert.AreEqual(ReportedStatusState.Reported, exact.State, exact.Reason);
        Assert.AreEqual("v1.2.3-complete", exact.Value);
        Assert.AreEqual(15, exact.Value!.Length);
        Assert.IsTrue(exact.Declarations[0].IsPaired);

        Assert.AreEqual("C# package", Extract("**Status: C# package**").Value);
        Assert.AreEqual("DONE.", Extract("Status: **DONE.**").Value);
        Assert.AreEqual("a*b", Extract("Status: **a*b**").Value);
        Assert.AreEqual("v1.2.3", Extract("Status: \"v1.2.3\"").Value);
        Assert.AreEqual("v1.2", Extract("Status: : **v1.2**").Value);
        Assert.AreEqual("v1.2", Extract("Status: : 'v1.2'").Value);

        var longValue = new string('x', 601);
        var untruncated = Extract("Status: **" + longValue + "**");
        Assert.AreEqual(ReportedStatusState.Reported, untruncated.State, untruncated.Reason);
        Assert.AreEqual(601, untruncated.Value!.Length);
        Assert.AreEqual(longValue, untruncated.Value);
    }

    [TestMethod]
    public void Extract_UnpairedValuesStopAtPunctuationOrLineEnd()
    {
        var cases = new (string Text, string Expected)[]
        {
            ("Status: DONE. Next", "DONE"),
            ("Status: DONE # note", "DONE"),
            ("Status: DONE* trailing", "DONE"),
            ("Status: DONE!", "DONE!"),
            ("Status: v1.2.3", "v1"),
            ("Status: C#", "C"),
            ("Status: **DONE", "DONE"),
            ("Status: DONE**", "DONE")
        };

        foreach (var (text, expected) in cases)
        {
            var extraction = Extract(text);
            Assert.AreEqual(ReportedStatusState.Reported, extraction.State, text);
            Assert.AreEqual(expected, extraction.Value, text);
            Assert.IsFalse(extraction.Declarations[0].IsPaired, text);
        }
    }

    [TestMethod]
    public void Extract_IgnoresUnanchoredAndUnrelatedDeclarations()
    {
        // Blockquote and list candidates are covered separately: V1 reports
        // those unsupported contexts as Unavailable rather than NotReported.
        var cases = new[]
        {
            "The Status: DONE is discussed in prose.",
            "\"status\": \"done\"",
            "Status DONE",
            "Statuses: DONE",
            "intro\nno declarations here"
        };

        foreach (var text in cases)
        {
            var extraction = Extract(text);
            Assert.AreEqual(ReportedStatusState.NotReported, extraction.State, text);
            Assert.IsNull(extraction.Value, text);
        }
    }

    [TestMethod]
    public void Extract_ExactStatusLabelDoesNotMatchEmbeddedWords()
    {
        var paired = Extract("Status: **BuildStatus : passing**");
        Assert.AreEqual(ReportedStatusState.Reported, paired.State, paired.Reason);
        Assert.AreEqual("BuildStatus : passing", paired.Value);

        var plain = Extract("Status: BuildStatus : passing");
        Assert.AreEqual(ReportedStatusState.Reported, plain.State, plain.Reason);
        Assert.AreEqual("BuildStatus : passing", plain.Value);

        var embeddedLine = Extract("MyStatus: DONE");
        Assert.AreEqual(ReportedStatusState.NotReported, embeddedLine.State, embeddedLine.Reason);

        var listSubstring = Extract("- **BuildStatus : passing**\n\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, listSubstring.State, listSubstring.Reason);
        Assert.AreEqual("DONE", listSubstring.Value);

        var quoteSubstring = Extract("> **BuildStatus : passing**\n\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, quoteSubstring.State, quoteSubstring.Reason);
        Assert.AreEqual("DONE", quoteSubstring.Value);
    }

    [TestMethod]
    public void Extract_BlockQuoteStatusIsUnavailable()
    {
        var explicitQuote = Extract("> Status: DONE");
        Assert.AreEqual(ReportedStatusState.Unavailable, explicitQuote.State, explicitQuote.Reason);
        Assert.IsNull(explicitQuote.Value);
        StringAssert.Contains(explicitQuote.Reason, "N/A");

        var lazyContinuation = Extract("> quoted\nStatus: DONE");
        Assert.AreEqual(
            ReportedStatusState.Unavailable,
            lazyContinuation.State,
            lazyContinuation.Reason);
        Assert.IsNull(lazyContinuation.Value);

        var continuedAfterHeading = Extract("> quoted\n# Heading\nStatus: DONE");
        Assert.AreEqual(
            ReportedStatusState.Unavailable,
            continuedAfterHeading.State,
            continuedAfterHeading.Reason);
        Assert.IsNull(continuedAfterHeading.Value);
    }

    [TestMethod]
    public void Extract_ListStatusIsUnavailable()
    {
        var cases = new[]
        {
            "- Status: DONE",
            "* Status: DONE",
            "+ Status: DONE",
            "1. Status: DONE",
            "1) Status: DONE",
            "- item\nStatus: DONE",
            "- item\n  Status: DONE",
            "- item\n\n  Status: DONE",
            "- item\n  - Status: DONE"
        };

        foreach (var text in cases)
        {
            var extraction = Extract(text);
            Assert.AreEqual(ReportedStatusState.Unavailable, extraction.State, text);
            Assert.IsNull(extraction.Value, text);
            StringAssert.Contains(extraction.Reason, "N/A", text);
        }
    }

    [TestMethod]
    public void Extract_ContextTransitionsPreserveTopLevelDeclarations()
    {
        Assert.AreEqual("DONE", Extract("> quote\n\nStatus: DONE").Value);
        Assert.AreEqual("DONE", Extract("- item\n\nStatus: DONE").Value);
        Assert.AreEqual("DONE", Extract("> quote\n\n**Status:** DONE").Value);
        Assert.AreEqual("DONE", Extract("- item\n\n## Status: DONE").Value);
        Assert.AreEqual(
            "DONE",
            Extract("- item\n\n```\nStatus: FAILED\n```\nStatus: DONE").Value);
        Assert.AreEqual(ReportedStatusState.NotReported, Extract("> quote only").State);
        Assert.AreEqual(ReportedStatusState.NotReported, Extract("- item only").State);
        Assert.AreEqual(ReportedStatusState.NotReported, Extract("\"Status: DONE\"").State);
        Assert.AreEqual(ReportedStatusState.NotReported, Extract("'Status: DONE'").State);
        Assert.AreEqual(ReportedStatusState.NotReported, Extract("`Status: DONE`").State);
    }

    [TestMethod]
    public void Extract_FenceInsideListOrQuoteIsUnavailable()
    {
        var listFence = Extract(
            "- item\n  ```\n  Status: FAILED\n  ```\n\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Unavailable, listFence.State, listFence.Reason);
        Assert.IsNull(listFence.Value);

        var quoteFence = Extract("> ```\n> Status: FAILED\n> ```\n\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Unavailable, quoteFence.State, quoteFence.Reason);
        Assert.IsNull(quoteFence.Value);
    }

    [TestMethod]
    public void Extract_ExcludesFencedCodeExamples()
    {
        Assert.AreEqual(
            "DONE",
            Extract("```\nStatus: FAILED\n```\nStatus: DONE").Value);
        Assert.AreEqual(
            ReportedStatusState.NotReported,
            Extract("~~~text\nStatus: FAILED\n~~~").State);
        Assert.AreEqual(
            ReportedStatusState.NotReported,
            Extract("```\nStatus: FAILED").State);
        Assert.AreEqual(
            "DONE",
            Extract("Status: DONE\n```\nStatus: FAILED\n```").Value);
    }

    [TestMethod]
    public void Extract_DistinguishesMissingEmptyAndReported()
    {
        Assert.AreEqual(ReportedStatusState.NotReported, Extract("no status").State);

        foreach (var text in new[] { "Status:", "Status:   ", "Status: \"\"", "**Status:**" })
        {
            var empty = Extract(text);
            Assert.AreEqual(ReportedStatusState.Empty, empty.State, text);
            Assert.IsNull(empty.Value, text);
        }

        var onlyEmpty = Extract("Status:\nStatus:");
        Assert.AreEqual(ReportedStatusState.Empty, onlyEmpty.State, onlyEmpty.Reason);
        Assert.AreEqual(2, onlyEmpty.Declarations.Count);

        var withValue = Extract("Status:\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, withValue.State, withValue.Reason);
        Assert.AreEqual("DONE", withValue.Value);
        Assert.AreEqual(2, withValue.Declarations.Count);
    }

    [TestMethod]
    public void Extract_MultipleIdenticalValuesAgreeAndDifferentValuesAreAmbiguous()
    {
        var identical = Extract("Status: DONE\n**Status:** DONE");
        Assert.AreEqual(ReportedStatusState.Reported, identical.State, identical.Reason);
        Assert.AreEqual("DONE", identical.Value);
        Assert.AreEqual(2, identical.Declarations.Count);

        var conflicting = Extract("Status: DONE\nStatus: FAILED");
        Assert.AreEqual(ReportedStatusState.Ambiguous, conflicting.State, conflicting.Reason);
        Assert.IsNull(conflicting.Value);
        Assert.AreEqual(2, conflicting.Declarations.Count);
        CollectionAssert.AreEquivalent(
            new[] { "DONE", "FAILED" },
            conflicting.Declarations.Select(declaration => declaration.Value).ToArray());

        // Case-only differences remain conflicting because the full reported
        // value is evidence, not a normalized status enum.
        Assert.AreEqual(
            ReportedStatusState.Ambiguous,
            Extract("Status: DONE\nStatus: done").State);
    }

    [TestMethod]
    public void Extract_MalformedUnsafeDuplicateOrInlineDeclarationsAreAmbiguous()
    {
        var cases = new[]
        {
            "***Status: DONE***",
            "Status: \u202eDONE",
            "Status: DONE\u0000",
            "Status: DONE\uD800",
            "Status: DONE Status: FAILED",
            "Status: **Status: DONE**",
            "Status: \"Status: DONE\"",
            "Status: `Status: DONE`",
            "**Status: DONE**\n***Status: FAILED***"
        };

        foreach (var text in cases)
        {
            var extraction = Extract(text);
            Assert.AreEqual(ReportedStatusState.Ambiguous, extraction.State, text);
            Assert.IsNull(extraction.Value, text);
        }
    }

    [TestMethod]
    public void Extract_PairedTrailingTextIsIgnoredUnlessItDeclaresAnotherStatus()
    {
        Assert.AreEqual("DONE", Extract("**Status:** **DONE**").Value);
        Assert.AreEqual("DONE", Extract("**Status: DONE** extra").Value);
        Assert.AreEqual("DONE", Extract("Status: **DONE** extra").Value);

        Assert.AreEqual(
            ReportedStatusState.Ambiguous,
            Extract("**Status: DONE** Status: FAILED").State);
        Assert.AreEqual(
            ReportedStatusState.Ambiguous,
            Extract("Status: **DONE** Status: FAILED").State);
    }

    [TestMethod]
    public void Extract_IneligibleRetrievalStatesAreNeverPartiallyParsed()
    {
        var statuses = new[]
        {
            FullReportStatus.Incomplete,
            FullReportStatus.Unavailable,
            FullReportStatus.Unsafe
        };

        foreach (var status in statuses)
        {
            var extraction = ReportedStatusParser.Extract(
                Retrieval(status, "Status: DONE"));
            Assert.AreEqual(ReportedStatusState.Ineligible, extraction.State, status.ToString());
            Assert.IsNull(extraction.Value, status.ToString());
            Assert.AreEqual(0, extraction.Declarations.Count, status.ToString());
            Assert.AreEqual(status, extraction.Source.Status, status.ToString());
        }

        foreach (var text in new[] { null, string.Empty, "   " })
        {
            var extraction = ReportedStatusParser.Extract(Retrieval(FullReportStatus.Complete, text));
            Assert.AreEqual(ReportedStatusState.Ineligible, extraction.State);
            Assert.IsNull(extraction.Value);
        }
    }

    [TestMethod]
    public void Extract_RolelessSourceRemainsUnverifiedAndProvenanceIsPreserved()
    {
        var source = Complete("Status: DONE");
        var assistant = ReportedStatusParser.Extract(source);
        Assert.IsTrue(assistant.IsRoleVerified);
        Assert.IsFalse(assistant.IsUnverified);
        Assert.AreSame(source, assistant.Source);
        Assert.AreEqual("job", assistant.Source.JobId);
        Assert.AreEqual("shared-session", assistant.Source.SessionId);
        Assert.AreEqual("message-m", assistant.Source.MessageId);
        Assert.AreEqual("part-m", assistant.Source.PartId);
        Assert.AreEqual(@"C:\synthetic\runs\job.log", assistant.Source.SourcePath);
        Assert.AreEqual(128L, assistant.Source.ByteOffset);
        Assert.AreEqual(512L, assistant.Source.RecordLengthBytes);
        Assert.AreEqual(Now.AddMinutes(-1), assistant.Source.SourceTimestamp);
        Assert.AreEqual(Now, assistant.Source.ObservedAt);
        StringAssert.Contains(assistant.Provenance, "part part-m");
        StringAssert.Contains(assistant.Provenance, "Declaration lines: 1");
        StringAssert.Contains(assistant.Provenance, "not authoritative job status");

        var roleless = Extract(
            "Status: DONE",
            "Observed roleless text — unverified");
        Assert.AreEqual(ReportedStatusState.Reported, roleless.State, roleless.Reason);
        Assert.IsFalse(roleless.IsRoleVerified);
        Assert.IsTrue(roleless.IsUnverified);
        StringAssert.Contains(roleless.Provenance, "unverified");

        var unknown = Extract("Status: DONE", "Unexpected kind");
        Assert.IsFalse(unknown.IsRoleVerified);
        Assert.IsTrue(unknown.IsUnverified);
    }

    [TestMethod]
    public void Extract_InvalidClosingFenceWithTrailingTextDoesNotClose()
    {
        var extraction = Extract(
            "```\nStatus: FAILED\n``` not a close\nStatus: ALSO FAILED\n```\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, extraction.State, extraction.Reason);
        Assert.AreEqual("DONE", extraction.Value);
        Assert.AreEqual(1, extraction.Declarations.Count);

        var trailingWhitespace = Extract("```\nStatus: FAILED\n```   \nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, trailingWhitespace.State, trailingWhitespace.Reason);
        Assert.AreEqual("DONE", trailingWhitespace.Value);

        var trailingTab = Extract("```\nStatus: FAILED\n```\t\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, trailingTab.State, trailingTab.Reason);
        Assert.AreEqual("DONE", trailingTab.Value);

        var indentedClose = Extract("```\nStatus: FAILED\n   ```\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, indentedClose.State, indentedClose.Reason);
        Assert.AreEqual("DONE", indentedClose.Value);

        // A closing fence indented four columns is code content, so the
        // following Status line remains inside the fence until the valid close.
        var overIndentedClose = Extract("```\nStatus: FAILED\n    ```\nStatus: DONE\n```");
        Assert.AreEqual(ReportedStatusState.NotReported, overIndentedClose.State, overIndentedClose.Reason);
        Assert.IsNull(overIndentedClose.Value);
    }

    [TestMethod]
    public void Extract_EmphasizedSecondStatusLabelIsConflicting()
    {
        var cases = new[]
        {
            "Status: DONE **Status**: FAILED",
            "Status: DONE *Status*: FAILED",
            "Status: DONE **Status:** FAILED",
            "Status: DONE **Status** : FAILED"
        };

        foreach (var text in cases)
        {
            var extraction = Extract(text);
            Assert.AreEqual(ReportedStatusState.Ambiguous, extraction.State, text);
            Assert.IsNull(extraction.Value, text);
        }

        var pairedValue = Extract("Status: **DONE**");
        Assert.AreEqual(ReportedStatusState.Reported, pairedValue.State, pairedValue.Reason);
        Assert.AreEqual("DONE", pairedValue.Value);

        var noColon = Extract("Status: DONE **Status** accepted");
        Assert.AreEqual(ReportedStatusState.Reported, noColon.State, noColon.Reason);
        Assert.AreEqual("DONE", noColon.Value);

        var italicNoColon = Extract("Status: DONE *Status* accepted");
        Assert.AreEqual(ReportedStatusState.Reported, italicNoColon.State, italicNoColon.Reason);
        Assert.AreEqual("DONE", italicNoColon.Value);

        var pairedThenConflict = Extract("Status: **DONE** **Status**: FAILED");
        Assert.AreEqual(ReportedStatusState.Ambiguous, pairedThenConflict.State, pairedThenConflict.Reason);
        Assert.IsNull(pairedThenConflict.Value);
    }

    [TestMethod]
    public void Extract_IndentedMarkdownCodeIsNotADeclaration()
    {
        var fourSpaces = Extract("    Status: FAILED\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, fourSpaces.State, fourSpaces.Reason);
        Assert.AreEqual("DONE", fourSpaces.Value);

        var tab = Extract("\tStatus: FAILED\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, tab.State, tab.Reason);
        Assert.AreEqual("DONE", tab.Value);

        var mixed = Extract("   \tStatus: FAILED\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, mixed.State, mixed.Reason);
        Assert.AreEqual("DONE", mixed.Value);

        var threeSpaces = Extract("   Status: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, threeSpaces.State, threeSpaces.Reason);
        Assert.AreEqual("DONE", threeSpaces.Value);

        var threeSpaceHeading = Extract("   ## Status: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, threeSpaceHeading.State, threeSpaceHeading.Reason);
        Assert.AreEqual("DONE", threeSpaceHeading.Value);

        var fourSpaceHeading = Extract("    ## Status: FAILED\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, fourSpaceHeading.State, fourSpaceHeading.Reason);
        Assert.AreEqual("DONE", fourSpaceHeading.Value);

        var indentedFence = Extract("    ```\nStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, indentedFence.State, indentedFence.Reason);
        Assert.AreEqual("DONE", indentedFence.Value);
    }

    [TestMethod]
    public void Extract_HandlesCrlfLoneCrAndBomWithoutChangingValue()
    {
        var crlf = Extract("intro\r\nStatus: DONE\r\n");
        Assert.AreEqual(ReportedStatusState.Reported, crlf.State, crlf.Reason);
        Assert.AreEqual("DONE", crlf.Value);
        Assert.AreEqual(2, crlf.Declarations[0].LineNumber);

        var loneCr = Extract("intro\rStatus: DONE");
        Assert.AreEqual("DONE", loneCr.Value);
        Assert.AreEqual(2, loneCr.Declarations[0].LineNumber);

        var bom = Extract("\uFEFFStatus: DONE");
        Assert.AreEqual(ReportedStatusState.Reported, bom.State, bom.Reason);
        Assert.AreEqual("DONE", bom.Value);
    }

    private static ReportedStatusExtraction Extract(
        string text,
        string kind = "Assistant-reported text") =>
        ReportedStatusParser.Extract(Complete(text, kind));

    private static FullReportResult Complete(
        string text,
        string kind = "Assistant-reported text") =>
        new(
            FullReportStatus.Complete,
            "Complete guarded capture of one text part.",
            text,
            "job",
            "shared-session",
            "message-m",
            "part-m",
            @"C:\synthetic\runs\job.log",
            128,
            512,
            Now.AddMinutes(-1),
            Now,
            kind);

    private static FullReportResult Retrieval(FullReportStatus status, string? text) =>
        new(
            status,
            "Synthetic retrieval outcome.",
            text,
            "job",
            "shared-session",
            "message-m",
            "part-m",
            @"C:\synthetic\runs\job.log",
            128,
            512,
            Now.AddMinutes(-1),
            Now,
            "Assistant-reported text");
}
