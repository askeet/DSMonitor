using System.Globalization;
using DSMonitor.Models;
using DSMonitor.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DSMonitor.Tests;

/// <summary>
/// Contract corpus for the conservative Reported Status V1 parser. Expectations
/// come from the approved V1 contract, not from current parser behavior. Cases
/// with an explicitly unspecified contract are recorded as ambiguities and are
/// observed but not asserted.
/// </summary>
[TestClass]
public sealed class ReportedStatusContractCorpusTests
{
    private const string AssistantKind = "Assistant-reported text";
    private const string RolelessKind = "Observed roleless text — unverified";
    private const uint GenerationSeed = 20261111;

    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_010_000);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Corpus_HandwrittenPreviousBlockers()
    {
        var cases = new[]
        {
            Case("blocker-nbsp-before-label", "\u00A0Status: DONE", ReportedStatusState.NotReported),
            Case("blocker-em-space-before-label", "\u2003Status: DONE", ReportedStatusState.NotReported),
            Case("blocker-nbsp-before-quote", "\u00A0> Status: DONE", ReportedStatusState.NotReported),
            Case("blocker-nbsp-after-quote", ">\u00A0Status: DONE", ReportedStatusState.Unavailable),
            Case("blocker-nbsp-before-emphasis", "\u00A0**Status:** DONE", ReportedStatusState.NotReported),
            Case("blocker-nbsp-after-colon", "Status:\u00A0DONE", ReportedStatusState.Reported, "DONE"),
            Case("blocker-nbsp-at-end", "Status: DONE\u00A0", ReportedStatusState.Reported, "DONE"),
            Case(
                "blocker-invalid-fence-trailing-text",
                "```\nStatus: FAILED\n``` trailing\nStatus: DONE\n```\nStatus: OK",
                ReportedStatusState.Reported,
                "OK"),
            Case(
                "blocker-fence-short-closer",
                "```\nStatus: FAILED\n``\nStatus: DONE",
                ReportedStatusState.NotReported),
            Case(
                "blocker-fence-mismatched-closer",
                "~~~\nStatus: FAILED\n```\nStatus: DONE\n~~~",
                ReportedStatusState.NotReported),
            Case(
                "blocker-fence-longer-opener",
                "````\nStatus: FAILED\n```\nStatus: DONE\n````\nStatus: OK",
                ReportedStatusState.Reported,
                "OK"),
            Case(
                "blocker-fence-trailing-tab",
                "```\nStatus: FAILED\n```\t\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "blocker-fence-over-indented-closer",
                "```\nStatus: FAILED\n    ```\nStatus: DONE\n```",
                ReportedStatusState.NotReported),
            Case(
                "blocker-indented-fence-opener",
                "    ```\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE")
        };

        RunGroup("handwritten-blockers", cases);
    }

    [TestMethod]
    public void Corpus_SafeOutcomeBlockers()
    {
        var cases = new[]
        {
            new SafeOutcomeCase(
                "safe-invalid-backtick-opener-info",
                "```text `sample`\n\n```\nStatus: FAILED\n```\nStatus: DONE",
                new[] { ReportedStatusState.Reported, ReportedStatusState.Unavailable },
                "DONE",
                "FAILED"),
            new SafeOutcomeCase(
                "safe-valid-fence-info-counterpart",
                "```text sample\nStatus: FAILED\n```\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "safe-quote-nbsp-lazy-continuation",
                "> Example response:\n\u00A0\nStatus: FAILED",
                new[] { ReportedStatusState.Unavailable, ReportedStatusState.NotReported },
                null,
                "FAILED"),
            new SafeOutcomeCase(
                "safe-quote-ascii-blank-counterpart",
                "> Example response:\n\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "safe-quote-ascii-space-blank-counterpart",
                "> Example response:\n   \nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "safe-nbsp-before-fence-closer",
                "```\nStatus: FAILED\n\u00A0```\nStatus: DONE",
                new[] { ReportedStatusState.NotReported, ReportedStatusState.Unavailable },
                null,
                "DONE"),
            new SafeOutcomeCase(
                "safe-ascii-space-before-fence-closer-counterpart",
                "```\nStatus: FAILED\n   ```\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "safe-nbsp-fence-closing-suffix",
                "```\nStatus: FAILED\n```\u00A0\nStatus: DONE",
                new[] { ReportedStatusState.NotReported, ReportedStatusState.Unavailable },
                null,
                "DONE"),
            new SafeOutcomeCase(
                "safe-ascii-space-fence-closing-suffix-counterpart",
                "```\nStatus: FAILED\n```   \nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "safe-tab-fence-closing-suffix-counterpart",
                "```\nStatus: FAILED\n```\t\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null)
        };

        RunSafeOutcomeGroup("safe-outcome-blockers", cases);
    }

    [TestMethod]
    public void Corpus_SyntaxAndContextMatrix()
    {
        var cases = new[]
        {
            Case("plain", "Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("lowercase-label", "status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("heading-1", "# Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("heading-6", "###### Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("heading-7", "####### Status: DONE", ReportedStatusState.NotReported),
            Case("heading-three-space", "   ## Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("bold-label", "**Status:** DONE", ReportedStatusState.Reported, "DONE"),
            Case("bold-label-colon-outside", "**Status**: DONE", ReportedStatusState.Reported, "DONE"),
            Case("italic-label", "*Status:* DONE", ReportedStatusState.Reported, "DONE"),
            Case("italic-label-colon-outside", "*Status*: DONE", ReportedStatusState.Reported, "DONE"),
            Case("bold-whole", "**Status: DONE**", ReportedStatusState.Reported, "DONE"),
            Case("italic-whole", "*Status: DONE*", ReportedStatusState.Reported, "DONE"),
            Case("bold-value", "Status: **DONE**", ReportedStatusState.Reported, "DONE"),
            Case("italic-value", "Status: *DONE*", ReportedStatusState.Reported, "DONE"),
            Case("double-quoted-value", "Status: \"DONE\"", ReportedStatusState.Reported, "DONE"),
            Case("smart-quoted-value", "Status: “DONE”", ReportedStatusState.Reported, "DONE"),
            Case("unpaired-period", "Status: DONE. Next", ReportedStatusState.Reported, "DONE"),
            Case("unpaired-hash", "Status: DONE # note", ReportedStatusState.Reported, "DONE"),
            Case("unpaired-star", "Status: DONE* note", ReportedStatusState.Reported, "DONE"),
            Case("unpaired-version", "Status: v1.2.3", ReportedStatusState.Reported, "v1"),
            Case("unpaired-csharp", "Status: C#", ReportedStatusState.Reported, "C"),
            Case("unpaired-bang", "Status: DONE!", ReportedStatusState.Reported, "DONE!"),
            Case(
                "paired-version",
                "Status: **v1.2.3-complete**",
                ReportedStatusState.Reported,
                "v1.2.3-complete"),
            Case("paired-csharp", "**Status: C# package**", ReportedStatusState.Reported, "C# package"),
            Case("paired-period", "Status: **DONE.**", ReportedStatusState.Reported, "DONE."),
            Case(
                "substring-buildstatus-paired",
                "Status: **BuildStatus : passing**",
                ReportedStatusState.Reported,
                "BuildStatus : passing"),
            Case(
                "substring-buildstatus-plain",
                "Status: BuildStatus : passing",
                ReportedStatusState.Reported,
                "BuildStatus : passing"),
            Case(
                "substring-mystatus-paired",
                "Status: **MyStatus : passing**",
                ReportedStatusState.Reported,
                "MyStatus : passing"),
            Case("substring-mystatus-line", "MyStatus: DONE", ReportedStatusState.NotReported),
            Case("substring-statuses-line", "Statuses: DONE", ReportedStatusState.NotReported),
            Case("substring-statusbar-line", "StatusBar: DONE", ReportedStatusState.NotReported),
            Case("no-colon", "Status DONE", ReportedStatusState.NotReported),
            Case("empty-plain", "Status:", ReportedStatusState.Empty),
            Case("empty-emphasized", "**Status:**", ReportedStatusState.Empty),
            Case("no-status", "intro only", ReportedStatusState.NotReported),
            Case(
                "conflict-two-lines",
                "Status: DONE\nStatus: FAILED",
                ReportedStatusState.Ambiguous),
            Case(
                "agreement-two-lines",
                "Status: DONE\n**Status:** DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "conflict-case-only",
                "Status: DONE\nStatus: done",
                ReportedStatusState.Ambiguous),
            Case(
                "conflict-inline",
                "Status: DONE Status: FAILED",
                ReportedStatusState.Ambiguous),
            Case(
                "conflict-emphasized-inline",
                "Status: DONE **Status**: FAILED",
                ReportedStatusState.Ambiguous),
            Case(
                "nonconflict-emphasized-word",
                "Status: DONE **Status** accepted",
                ReportedStatusState.Reported,
                "DONE"),
            Case("fenced-only", "```\nStatus: FAILED\n```", ReportedStatusState.NotReported),
            Case(
                "fenced-then-top-level",
                "```\nStatus: FAILED\n```\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case("indented-only", "    Status: FAILED", ReportedStatusState.NotReported),
            Case(
                "indented-then-top-level",
                "    Status: FAILED\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case("quote-explicit", "> Status: DONE", ReportedStatusState.Unavailable),
            Case("quote-lazy", "> quoted\nStatus: DONE", ReportedStatusState.Unavailable),
            Case("quote-blank-transition", "> quoted\n\nStatus: DONE", ReportedStatusState.Reported, "DONE"),
            Case("list-dash", "- Status: DONE", ReportedStatusState.Unavailable),
            Case("list-star", "* Status: DONE", ReportedStatusState.Unavailable),
            Case("list-plus", "+ Status: DONE", ReportedStatusState.Unavailable),
            Case("list-ordered", "1. Status: DONE", ReportedStatusState.Unavailable),
            Case("list-blank-transition", "- item\n\nStatus: DONE", ReportedStatusState.Reported, "DONE"),
            Case("nested-list", "- item\n  - Status: DONE", ReportedStatusState.Unavailable),
            Case(
                "nested-fence-list",
                "- item\n  ```\n  Status: FAILED\n  ```\n\nStatus: DONE",
                ReportedStatusState.Unavailable),
            Case(
                "nested-fence-quote",
                "> ```\n> Status: FAILED\n> ```\n\nStatus: DONE",
                ReportedStatusState.Unavailable),
            Case("standalone-double-quote", "\"Status: DONE\"", ReportedStatusState.NotReported),
            Case("standalone-single-quote", "'Status: DONE'", ReportedStatusState.NotReported),
            Case("standalone-backtick", "`Status: DONE`", ReportedStatusState.NotReported)
        };

        RunGroup("syntax-context", cases);
    }

    [TestMethod]
    public void Corpus_BoundaryAndPairwise()
    {
        var longPaired = new string('x', 601);
        var cases = new List<CorpusCase>
        {
            Case("fence-3", "```\nStatus: FAILED\n```", ReportedStatusState.NotReported),
            Case("fence-4", "````\nStatus: FAILED\n````", ReportedStatusState.NotReported),
            Case("fence-5", "`````\nStatus: FAILED\n`````", ReportedStatusState.NotReported),
            Case(
                "fence-open-4-close-3",
                "````\nStatus: FAILED\n```\nStatus: DONE\n````\nStatus: OK",
                ReportedStatusState.Reported,
                "OK"),
            Case(
                "fence-open-3-close-4",
                "```\nStatus: FAILED\n````\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "fence-invalid-then-valid",
                "```\nStatus: FAILED\n``` trailing\nStatus: DONE\n```\nStatus: OK",
                ReportedStatusState.Reported,
                "OK"),
            Case(
                "fence-mismatched",
                "~~~\nStatus: FAILED\n```\nStatus: DONE\n~~~",
                ReportedStatusState.NotReported),
            Case(
                "fence-over-indented-close",
                "```\nStatus: FAILED\n    ```\nStatus: DONE\n```",
                ReportedStatusState.NotReported),
            Case("indent-three", "   Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("indent-four", "    Status: DONE", ReportedStatusState.NotReported),
            Case("indent-tab", "\tStatus: DONE", ReportedStatusState.NotReported),
            Case("indent-mixed", "   \tStatus: DONE", ReportedStatusState.NotReported),
            Case("heading-0", "Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("heading-1", "# Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("heading-6", "###### Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("heading-7", "####### Status: DONE", ReportedStatusState.NotReported),
            Case("paired-punctuation", "Status: **a.b*c#d**", ReportedStatusState.Reported, "a.b*c#d"),
            Case("paired-version", "Status: **v1.2.3**", ReportedStatusState.Reported, "v1.2.3"),
            Case("unpaired-version", "Status: v1.2.3", ReportedStatusState.Reported, "v1"),
            Case("unpaired-hash", "Status: C#", ReportedStatusState.Reported, "C"),
            Case("paired-hash", "Status: **C#**", ReportedStatusState.Reported, "C#"),
            Case(
                "label-pair-and-value-pair",
                "**Status:** **v1.2.3**",
                ReportedStatusState.Reported,
                "v1.2.3"),
            Case(
                "whole-pair",
                "**Status: v1.2.3**",
                ReportedStatusState.Reported,
                "v1.2.3"),
            Case(
                "plain-label-paired-value",
                "Status: **v1.2.3**",
                ReportedStatusState.Reported,
                "v1.2.3"),
            Case(
                "paired-601",
                "Status: **" + longPaired + "**",
                ReportedStatusState.Reported,
                longPaired)
        };

        RunGroup("boundary-pairwise", cases);
    }

    [TestMethod]
    public void Corpus_DeterministicSeededGeneration()
    {
        var random = new SeededRandom(GenerationSeed);
        var cases = new List<CorpusCase>();
        for (var index = 1; index <= 60; index++)
        {
            var value = "V" + index.ToString(CultureInfo.InvariantCulture);
            var wrapper = random.Next(6);
            var declaration = wrapper switch
            {
                0 => "Status: " + value,
                1 => "## Status: " + value,
                2 => "**Status:** " + value,
                3 => "*Status:* " + value,
                4 => "**Status: " + value + "**",
                5 => "*Status: " + value + "*",
                _ => throw new InvalidOperationException()
            };

            var context = random.Next(6);
            var text = context switch
            {
                0 => declaration,
                1 => "> " + declaration,
                2 => "- " + declaration,
                3 => "```\n" + declaration + "\n```",
                4 => "    " + declaration,
                5 => "> quote\n\n" + declaration,
                _ => throw new InvalidOperationException()
            };
            var expected = context switch
            {
                0 or 5 => ReportedStatusState.Reported,
                1 or 2 => ReportedStatusState.Unavailable,
                3 or 4 => ReportedStatusState.NotReported,
                _ => throw new InvalidOperationException()
            };

            cases.Add(
                new CorpusCase(
                    $"seed-{GenerationSeed}-{index:D3}",
                    text,
                    expected,
                    expected == ReportedStatusState.Reported ? value : null,
                    AssistantKind,
                    FullReportStatus.Complete,
                    null));
        }

        RunGroup("seeded-generation", cases);
    }

    [TestMethod]
    public void Corpus_AdversarialSecondSeed()
    {
        const uint adversarialSeed = 20261112;
        var random = new SeededRandom(adversarialSeed);
        var cases = new List<CorpusCase>();
        for (var index = 1; index <= 48; index++)
        {
            var value = "ADV" + index.ToString(CultureInfo.InvariantCulture);
            var scenario = random.Next(8);
            CorpusCase testCase = scenario switch
            {
                0 => LeadingWhitespaceCase(index, value, random),
                1 => QuoteSeparatorCase(index, value, random),
                2 => FenceCloserPrefixCase(index, value, random),
                3 => FenceCloserSuffixCase(index, value, random),
                4 => ValidFenceInfoCase(index, value, random),
                5 => UnicodeValueWhitespaceCase(index, value, random),
                6 => AliasCase(index, value, random),
                7 => ConflictCase(index, value, random),
                _ => throw new InvalidOperationException()
            };
            cases.Add(testCase);
        }

        RunGroup("adversarial-second-seed", cases);
    }

    [TestMethod]
    public void Corpus_AdversarialCombinations()
    {
        var cases = new[]
        {
            new SafeOutcomeCase(
                "adv-invalid-opener-no-blank",
                "```text `sample`\nStatus: FAILED\n```\nStatus: DONE",
                new[] { ReportedStatusState.Unavailable, ReportedStatusState.Reported },
                "DONE",
                "FAILED"),
            new SafeOutcomeCase(
                "adv-invalid-opener-nbsp-suffix",
                "```text `sample`\n\n```\nStatus: FAILED\n```\u00A0\nStatus: DONE",
                new[] { ReportedStatusState.Unavailable },
                null,
                "FAILED"),
            new SafeOutcomeCase(
                "adv-invalid-opener-nbsp-closer",
                "```text `sample`\n\n```\nStatus: FAILED\n\u00A0```\nStatus: DONE",
                new[] { ReportedStatusState.Unavailable },
                null,
                "DONE"),
            new SafeOutcomeCase(
                "adv-tilde-info-with-backtick",
                "~~~text `sample`\nStatus: FAILED\n~~~\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "adv-nbsp-separator-quote-fence",
                "> quote\n\u00A0\n```\nStatus: FAILED\n```\nStatus: DONE",
                new[] { ReportedStatusState.Unavailable },
                null,
                "FAILED"),
            new SafeOutcomeCase(
                "adv-ascii-separator-quote-fence",
                "> quote\n\n```\nStatus: FAILED\n```\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "adv-nbsp-prefix-label-then-top-level",
                "\u00A0Status: FAILED\n\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "adv-nbsp-prefix-quote-then-top-level",
                "\u00A0> Status: FAILED\n\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "adv-ascii-space-prefix-quote",
                "   > Status: FAILED",
                new[] { ReportedStatusState.Unavailable },
                null,
                "FAILED"),
            new SafeOutcomeCase(
                "adv-nbsp-value-whitespace",
                "Status:\u00A0DONE\u00A0",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "adv-fence-suffix-ascii-tab",
                "```\nStatus: FAILED\n```\t\nStatus: DONE",
                new[] { ReportedStatusState.Reported },
                "DONE",
                null),
            new SafeOutcomeCase(
                "adv-fence-suffix-nbsp",
                "```\nStatus: FAILED\n```\u00A0\nStatus: DONE",
                new[] { ReportedStatusState.NotReported, ReportedStatusState.Unavailable },
                null,
                "DONE")
        };

        RunSafeOutcomeGroup("adversarial-combinations", cases);
    }

    [TestMethod]
    public void Corpus_MetamorphicCases()
    {
        var cases = new[]
        {
            Case("meta-base", "Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-leading-one", " Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-leading-two", "  Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-leading-three", "   Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-heading", "## Status: DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-bold-label", "**Status:** DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-italic-label", "*Status:* DONE", ReportedStatusState.Reported, "DONE"),
            Case("meta-bold-whole", "**Status: DONE**", ReportedStatusState.Reported, "DONE"),
            Case("meta-trailing-spaces", "Status: DONE   ", ReportedStatusState.Reported, "DONE"),
            Case("meta-trailing-tab", "Status: DONE\t", ReportedStatusState.Reported, "DONE"),
            Case(
                "meta-fenced-prefix",
                "```\nStatus: FAILED\n```\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "meta-indented-prefix",
                "    Status: FAILED\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "meta-quote-blank-prefix",
                "> quote\n\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "meta-list-blank-prefix",
                "- item\n\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "meta-identical-duplicate",
                "Status: DONE\nStatus: DONE",
                ReportedStatusState.Reported,
                "DONE"),
            Case(
                "meta-empty-after-value",
                "Status: DONE\nStatus:",
                ReportedStatusState.Reported,
                "DONE"),
            Case("meta-quote-wrapper", "> Status: DONE", ReportedStatusState.Unavailable),
            Case("meta-list-wrapper", "- Status: DONE", ReportedStatusState.Unavailable),
            Case("meta-fence-wrapper", "```\nStatus: DONE\n```", ReportedStatusState.NotReported),
            Case("meta-indent-wrapper", "    Status: DONE", ReportedStatusState.NotReported),
            Case(
                "meta-conflict-wrapper",
                "Status: DONE\nStatus: FAILED",
                ReportedStatusState.Ambiguous),
            Case("meta-nbsp-wrapper", "\u00A0Status: DONE", ReportedStatusState.NotReported)
        };

        RunGroup("metamorphic", cases);
    }

    [TestMethod]
    public void Corpus_SourceProvenanceAndRoleless()
    {
        var source = Report("Status: **v1.2.3-complete**", AssistantKind);
        var extraction = ReportedStatusParser.Extract(source);

        Assert.AreEqual(ReportedStatusState.Reported, extraction.State, extraction.Reason);
        Assert.AreEqual("v1.2.3-complete", extraction.Value);
        Assert.AreEqual(15, extraction.Value!.Length);
        Assert.AreSame(source, extraction.Source);
        Assert.AreEqual("job", extraction.Source.JobId);
        Assert.AreEqual("session", extraction.Source.SessionId);
        Assert.AreEqual("message", extraction.Source.MessageId);
        Assert.AreEqual("part", extraction.Source.PartId);
        Assert.AreEqual(@"C:\synthetic\job.log", extraction.Source.SourcePath);
        Assert.AreEqual(128L, extraction.Source.ByteOffset);
        Assert.AreEqual(512L, extraction.Source.RecordLengthBytes);
        Assert.AreEqual(Now.AddMinutes(-1), extraction.Source.SourceTimestamp);
        Assert.AreEqual(Now, extraction.Source.ObservedAt);
        Assert.AreEqual(AssistantKind, extraction.Source.Kind);
        Assert.IsTrue(extraction.IsRoleVerified);
        Assert.IsFalse(extraction.IsUnverified);
        Assert.AreEqual("Status: **v1.2.3-complete**", extraction.Declarations[0].RawLine);
        StringAssert.Contains(extraction.Provenance, "job job");
        StringAssert.Contains(extraction.Provenance, "part part");
        StringAssert.Contains(extraction.Provenance, "not authoritative job status");

        var roleless = ReportedStatusParser.Extract(Report("Status: DONE", RolelessKind));
        Assert.AreEqual(ReportedStatusState.Reported, roleless.State, roleless.Reason);
        Assert.IsFalse(roleless.IsRoleVerified);
        Assert.IsTrue(roleless.IsUnverified);
        StringAssert.Contains(roleless.Provenance, "unverified");

        var longValue = new string('x', 601);
        var longResult = ReportedStatusParser.Extract(
            Report("Status: **" + longValue + "**", AssistantKind));
        Assert.AreEqual(ReportedStatusState.Reported, longResult.State, longResult.Reason);
        Assert.AreEqual(longValue, longResult.Value);
        Assert.AreEqual(601, longResult.Value!.Length);

        foreach (var status in new[]
                 {
                     FullReportStatus.Incomplete,
                     FullReportStatus.Unavailable,
                     FullReportStatus.Unsafe
                 })
        {
            var ineligible = ReportedStatusParser.Extract(
                Report("Status: DONE", AssistantKind, status));
            Assert.AreEqual(ReportedStatusState.Ineligible, ineligible.State, status.ToString());
            Assert.IsNull(ineligible.Value, status.ToString());
            Assert.AreEqual(status, ineligible.Source.Status, status.ToString());
        }
    }

    [TestMethod]
    public void Corpus_SpecificationAmbiguities()
    {
        // NBSP before a closer and as a closing suffix are now asserted as
        // non-structural whitespace in Corpus_SafeOutcomeBlockers. This group
        // covers the remaining specification ambiguities.
        var cases = new[]
        {
            AmbiguityCase(
                "ambiguity-multiword-label",
                "Status: **Build Status : passing**",
                "Whether a separate Status token after a word plus space is a conflict or value text is not specified."),
            AmbiguityCase(
                "ambiguity-quoted-declaration",
                "Status: \"Status: DONE\"",
                "Quote masking for embedded declarations is deferred."),
            AmbiguityCase(
                "ambiguity-inline-code-declaration",
                "Status: `Status: DONE`",
                "Inline-code masking is deferred.")
        };

        Assert.AreEqual(3, cases.Length);
        Assert.IsTrue(cases.All(item => item.Expected is null && !string.IsNullOrWhiteSpace(item.AmbiguityReason)));
        RunGroup("specification-ambiguities", cases);
    }

    private static CorpusCase LeadingWhitespaceCase(
        int index,
        string value,
        SeededRandom random)
    {
        var choices = new (string Prefix, bool Reported)[]
        {
            ("", true),
            (" ", true),
            ("   ", true),
            ("\t", false),
            ("\u00A0", false),
            ("\u2003", false)
        };
        var choice = choices[random.Next(choices.Length)];
        return new CorpusCase(
            $"adv-leading-{index}",
            choice.Prefix + "Status: " + value,
            choice.Reported ? ReportedStatusState.Reported : ReportedStatusState.NotReported,
            choice.Reported ? value : null,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase QuoteSeparatorCase(
        int index,
        string value,
        SeededRandom random)
    {
        var choices = new (string Separator, bool EndsQuote)[]
        {
            ("", true),
            ("   ", true),
            ("\t", true),
            ("\u00A0", false),
            ("\u2003", false)
        };
        var choice = choices[random.Next(choices.Length)];
        return new CorpusCase(
            $"adv-quote-separator-{index}",
            "> quote\n" + choice.Separator + "\nStatus: " + value,
            choice.EndsQuote ? ReportedStatusState.Reported : ReportedStatusState.Unavailable,
            choice.EndsQuote ? value : null,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase FenceCloserPrefixCase(
        int index,
        string value,
        SeededRandom random)
    {
        var choices = new (string Prefix, bool Closes)[]
        {
            ("", true),
            (" ", true),
            ("   ", true),
            ("\t", false),
            ("\u00A0", false),
            ("\u2003", false)
        };
        var choice = choices[random.Next(choices.Length)];
        return new CorpusCase(
            $"adv-fence-prefix-{index}",
            "```\nStatus: FAILED\n" + choice.Prefix + "```\nStatus: " + value,
            choice.Closes ? ReportedStatusState.Reported : ReportedStatusState.NotReported,
            choice.Closes ? value : null,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase FenceCloserSuffixCase(
        int index,
        string value,
        SeededRandom random)
    {
        var choices = new (string Suffix, bool Closes)[]
        {
            ("", true),
            (" ", true),
            ("\t", true),
            ("\u00A0", false),
            ("\u2003", false),
            (" trailing", false)
        };
        var choice = choices[random.Next(choices.Length)];
        return new CorpusCase(
            $"adv-fence-suffix-{index}",
            "```\nStatus: FAILED\n```" + choice.Suffix + "\nStatus: " + value,
            choice.Closes ? ReportedStatusState.Reported : ReportedStatusState.NotReported,
            choice.Closes ? value : null,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase ValidFenceInfoCase(
        int index,
        string value,
        SeededRandom random)
    {
        var infos = new[] { "text sample", "plain", string.Empty };
        var info = infos[random.Next(infos.Length)];
        return new CorpusCase(
            $"adv-valid-info-{index}",
            "```" + info + "\nStatus: FAILED\n```\nStatus: " + value,
            ReportedStatusState.Reported,
            value,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase UnicodeValueWhitespaceCase(
        int index,
        string value,
        SeededRandom random)
    {
        var text = random.Next(2) == 0
            ? "Status:\u00A0" + value
            : "Status: " + value + "\u00A0";
        return new CorpusCase(
            $"adv-value-unicode-{index}",
            text,
            ReportedStatusState.Reported,
            value,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase AliasCase(
        int index,
        string value,
        SeededRandom random)
    {
        if (random.Next(2) == 0)
        {
            return new CorpusCase(
                $"adv-alias-{index}",
                "Status: **BuildStatus : " + value + "**",
                ReportedStatusState.Reported,
                "BuildStatus : " + value,
                AssistantKind,
                FullReportStatus.Complete,
                null);
        }

        return new CorpusCase(
            $"adv-nonconflict-{index}",
            "Status: " + value + " **Status** accepted",
            ReportedStatusState.Reported,
            value,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private static CorpusCase ConflictCase(
        int index,
        string value,
        SeededRandom random)
    {
        if (random.Next(2) == 0)
        {
            return new CorpusCase(
                $"adv-agree-{index}",
                "Status: " + value + "\nStatus: " + value,
                ReportedStatusState.Reported,
                value,
                AssistantKind,
                FullReportStatus.Complete,
                null);
        }

        return new CorpusCase(
            $"adv-conflict-{index}",
            "Status: " + value + "\nStatus: " + value + "-X",
            ReportedStatusState.Ambiguous,
            null,
            AssistantKind,
            FullReportStatus.Complete,
            null);
    }

    private void RunGroup(string group, IReadOnlyList<CorpusCase> cases)
    {
        var failures = new List<string>();
        var ambiguityRows = new List<string>();
        var asserted = 0;
        var passed = 0;

        foreach (var testCase in cases)
        {
            var result = Extract(testCase);
            if (testCase.Expected is null)
            {
                ambiguityRows.Add(
                    $"{testCase.Id}: observed={result.State}, " +
                    $"value={Escape(result.Value ?? "<null>")}, " +
                    $"reason={testCase.AmbiguityReason}");
                continue;
            }

            asserted++;
            var failure = Check(testCase, result);
            if (failure is null)
            {
                passed++;
            }
            else
            {
                failures.Add(failure);
            }
        }

        var summary =
            $"{group}: asserted={asserted}, passed={passed}, failed={failures.Count}, " +
            $"ambiguous={ambiguityRows.Count}";
        TestContext.WriteLine(summary);
        foreach (var row in ambiguityRows)
        {
            TestContext.WriteLine("AMBIGUITY " + row);
        }

        if (failures.Count > 0)
        {
            Assert.Fail(
                summary + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(failure => "FAIL " + failure)));
        }
    }

    private void RunSafeOutcomeGroup(string group, IReadOnlyList<SafeOutcomeCase> cases)
    {
        var failures = new List<string>();
        var passed = 0;

        foreach (var testCase in cases)
        {
            var result = ReportedStatusParser.Extract(Report(testCase.Text));
            var observation =
                $"{testCase.Id}: observed={result.State}, " +
                $"value={Escape(result.Value ?? "<null>")}";
            TestContext.WriteLine(observation);

            var failure = CheckSafeOutcome(testCase, result);
            if (failure is null)
            {
                passed++;
            }
            else
            {
                failures.Add(failure);
            }
        }

        var summary =
            $"{group}: asserted={cases.Count}, passed={passed}, failed={failures.Count}";
        TestContext.WriteLine(summary);
        if (failures.Count > 0)
        {
            Assert.Fail(
                summary + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(failure => "FAIL " + failure)));
        }
    }

    private static string? CheckSafeOutcome(
        SafeOutcomeCase testCase,
        ReportedStatusExtraction result)
    {
        if (testCase.ForbiddenValue is not null &&
            string.Equals(result.Value, testCase.ForbiddenValue, StringComparison.Ordinal))
        {
            return $"{testCase.Id}: forbiddenValue={Escape(testCase.ForbiddenValue)}, " +
                   $"actualState={result.State}, input={Escape(testCase.Text)}";
        }

        if (!testCase.AllowedStates.Contains(result.State))
        {
            return $"{testCase.Id}: expected states={string.Join("|", testCase.AllowedStates)}, " +
                   $"actual={result.State}, value={Escape(result.Value ?? "<null>")}, " +
                   $"input={Escape(testCase.Text)}, reason={result.Reason}";
        }

        if (result.State == ReportedStatusState.Reported)
        {
            if (!string.Equals(
                    testCase.ExpectedReportedValue,
                    result.Value,
                    StringComparison.Ordinal))
            {
                return $"{testCase.Id}: expectedReportedValue=" +
                       $"{Escape(testCase.ExpectedReportedValue ?? "<null>")}, " +
                       $"actualValue={Escape(result.Value ?? "<null>")}, " +
                       $"input={Escape(testCase.Text)}";
            }
        }
        else if (result.Value is not null)
        {
            return $"{testCase.Id}: expected no value, actualValue={Escape(result.Value)}, " +
                   $"input={Escape(testCase.Text)}";
        }

        return null;
    }

    private static string? Check(CorpusCase testCase, ReportedStatusExtraction result)
    {
        if (result.State != testCase.Expected)
        {
            return $"{testCase.Id}: expected={testCase.Expected}, actual={result.State}, " +
                   $"input={Escape(testCase.Text)}, reason={result.Reason}";
        }

        if (testCase.Expected == ReportedStatusState.Reported)
        {
            if (!string.Equals(testCase.ExpectedValue, result.Value, StringComparison.Ordinal))
            {
                return $"{testCase.Id}: expectedValue={Escape(testCase.ExpectedValue ?? "<null>")}, " +
                       $"actualValue={Escape(result.Value ?? "<null>")}, " +
                       $"input={Escape(testCase.Text)}, reason={result.Reason}";
            }
        }
        else if (result.Value is not null)
        {
            return $"{testCase.Id}: expected no value, actualValue={Escape(result.Value)}, " +
                   $"input={Escape(testCase.Text)}, reason={result.Reason}";
        }

        if (result.Source.Status != testCase.Retrieval)
        {
            return $"{testCase.Id}: expected retrieval={testCase.Retrieval}, " +
                   $"actualRetrieval={result.Source.Status}, input={Escape(testCase.Text)}";
        }

        return null;
    }

    private static ReportedStatusExtraction Extract(CorpusCase testCase) =>
        ReportedStatusParser.Extract(
            Report(testCase.Text, testCase.Kind, testCase.Retrieval));

    private static FullReportResult Report(
        string text,
        string kind = AssistantKind,
        FullReportStatus retrieval = FullReportStatus.Complete) =>
        new(
            retrieval,
            retrieval == FullReportStatus.Complete
                ? "Complete guarded capture of one text part."
                : "Synthetic non-complete retrieval outcome.",
            text,
            "job",
            "session",
            "message",
            "part",
            @"C:\synthetic\job.log",
            128,
            512,
            Now.AddMinutes(-1),
            Now,
            kind);

    private static CorpusCase Case(
        string id,
        string text,
        ReportedStatusState expected,
        string? expectedValue = null,
        FullReportStatus retrieval = FullReportStatus.Complete) =>
        new(id, text, expected, expectedValue, AssistantKind, retrieval, null);

    private static CorpusCase AmbiguityCase(string id, string text, string reason) =>
        new(id, text, null, null, AssistantKind, FullReportStatus.Complete, reason);

    private static string Escape(string value) => value
        .Replace("\u00A0", "<NBSP>")
        .Replace("\u2003", "<EMSP>")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");

    private sealed record CorpusCase(
        string Id,
        string Text,
        ReportedStatusState? Expected,
        string? ExpectedValue,
        string Kind,
        FullReportStatus Retrieval,
        string? AmbiguityReason);

    private sealed record SafeOutcomeCase(
        string Id,
        string Text,
        IReadOnlyList<ReportedStatusState> AllowedStates,
        string? ExpectedReportedValue,
        string? ForbiddenValue);

    private sealed class SeededRandom
    {
        private uint _state;

        public SeededRandom(uint seed)
        {
            _state = seed;
        }

        public int Next(int maximum)
        {
            _state = unchecked(_state * 1664525u + 1013904223u);
            return (int)(_state % (uint)maximum);
        }
    }
}
