using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DSMonitor.Models;

namespace DSMonitor.Parsing;

/// <summary>
/// Parses OpenCode JSON-lines events captured by forge-delegate. The parser
/// only keeps short, safe summaries; raw model text and tool output never
/// enter DSMonitor's persistent monitoring logs.
/// </summary>
public static class ForgeEventParser
{
    private const int MaxSummaryLength = 180;

    public static bool TryParse(
        string line,
        string jobId,
        DateTimeOffset observedAt,
        out ParsedLogEvent? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var payload = line.TrimStart('\uFEFF');
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = document.RootElement;
            var type = GetString(root, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                return false;
            }

            var sessionId = GetString(root, "sessionID") ?? GetString(root, "sessionId");
            var timestamp = ParseTimestamp(root, "timestamp");
            var part = root.TryGetProperty("part", out var partElement) &&
                       partElement.ValueKind == JsonValueKind.Object
                ? partElement
                : (JsonElement?)null;

            var partId = part is { } p
                ? GetString(p, "partID") ?? GetString(p, "id")
                : null;
            var messageId = part is { } m ? GetString(m, "messageID") : null;
            var toolName = part is { } t ? GetString(t, "tool") : null;
            var toolStatus = part is { } s &&
                             s.TryGetProperty("state", out var state) &&
                             state.ValueKind == JsonValueKind.Object
                ? GetString(state, "status")
                : null;

            var category = MonitoringEventCategory.Other;
            var severity = MonitoringEventSeverity.Info;
            var summary = "Observed event";
            double? cost = null;

            var normalizedType = NormalizeToken(type);
            var normalizedPartType = part is { } pt ? NormalizeToken(GetString(pt, "type")) : string.Empty;
            var normalizedTool = NormalizeToken(toolName);
            var normalizedToolStatus = NormalizeToken(toolStatus);

            if (normalizedType == "step-start" || normalizedPartType == "step-start")
            {
                category = MonitoringEventCategory.Step;
                summary = "Session step started";
            }
            else if (normalizedType == "step-finish" || normalizedPartType == "step-finish")
            {
                category = MonitoringEventCategory.Step;
                if (part is { } finishPart)
                {
                    cost = GetDouble(finishPart, "cost");
                    var reason = part is { } rp && rp.TryGetProperty("reason", out var reasonElement)
                        ? GetString(rp, "reason")
                        : null;
                    var safeReason = NormalizeToken(reason);
                    var occurrence = cost.HasValue ? " (cost reported)" : string.Empty;
                    summary = string.IsNullOrWhiteSpace(safeReason) || safeReason == "unknown"
                        ? "Step finished" + occurrence
                        : $"Step finished: {TrimToken(safeReason, 24)}{occurrence}";
                }
                else
                {
                    summary = "Step finished";
                }
            }
            else if (normalizedType == "text" || normalizedPartType == "text")
            {
                category = MonitoringEventCategory.Output;
                var textLength = part is { } textPart && textPart.TryGetProperty("text", out var textElement) &&
                                 textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()?.Length ?? 0
                    : 0;
                summary = textLength > 0
                    ? $"Model output observed ({textLength} chars)"
                    : "Model output observed";
            }
            else if (normalizedType == "tool-use" || normalizedType == "tool_use" ||
                     normalizedPartType == "tool" || !string.IsNullOrWhiteSpace(toolName))
            {
                category = normalizedTool is "edit" or "write" or "patch"
                    ? MonitoringEventCategory.File
                    : MonitoringEventCategory.Tool;
                var toolLabel = string.IsNullOrWhiteSpace(normalizedTool) || normalizedTool == "unknown"
                    ? "Tool"
                    : $"Tool {TrimToken(normalizedTool, 32)}";

                if (normalizedToolStatus is "error" or "failed" or "failure")
                {
                    severity = MonitoringEventSeverity.Error;
                    category = MonitoringEventCategory.Error;
                    summary = $"{toolLabel} failed";
                }
                else if (normalizedToolStatus is "running" or "pending" or "in-progress" or "inprogress")
                {
                    severity = MonitoringEventSeverity.Warning;
                    summary = $"{toolLabel} {normalizedToolStatus}";
                }
                else if (normalizedToolStatus is "completed" or "complete" or "done")
                {
                    severity = MonitoringEventSeverity.Success;
                    var duration = GetToolDuration(part);
                    summary = duration is null
                        ? $"{toolLabel} completed"
                        : $"{toolLabel} completed in {FormatDuration(duration.Value)}";
                }
                else
                {
                    summary = toolLabel;
                }
            }
            else if (normalizedType.Contains("permission", StringComparison.Ordinal) ||
                     normalizedPartType.Contains("permission", StringComparison.Ordinal))
            {
                category = MonitoringEventCategory.Permission;
                severity = MonitoringEventSeverity.Warning;
                summary = "Permission interaction observed (not acted on)";
            }
            else if (normalizedType.Contains("error", StringComparison.Ordinal) ||
                     normalizedType.Contains("failed", StringComparison.Ordinal))
            {
                category = MonitoringEventCategory.Error;
                severity = MonitoringEventSeverity.Error;
                summary = $"Error event: {TrimToken(normalizedType, 32)}";
            }
            else
            {
                summary = $"Event: {TrimToken(normalizedType, 40)}";
            }

            var eventKey = BuildEventKey(
                sessionId,
                messageId,
                partId,
                type,
                timestamp,
                normalizedToolStatus,
                payload);

            var monitoringEvent = new MonitoringEvent
            {
                EventKey = eventKey,
                JobId = jobId,
                SessionId = sessionId,
                Timestamp = timestamp,
                ObservedAt = observedAt,
                Category = category,
                Severity = severity,
                Summary = TrimSummary(summary)
            };

            parsed = new ParsedLogEvent(eventKey, monitoringEvent, cost);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    private static string BuildEventKey(
        string? sessionId,
        string? messageId,
        string? partId,
        string type,
        DateTimeOffset? timestamp,
        string status,
        string rawLine)
    {
        // The raw-line hash prevents a growing text/tool part from being
        // mistaken for a duplicate when it is emitted more than once.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawLine));
        return string.Join(
            '|',
            sessionId ?? string.Empty,
            messageId ?? string.Empty,
            partId ?? string.Empty,
            type,
            timestamp?.ToUnixTimeMilliseconds().ToString() ?? string.Empty,
            status,
            Convert.ToHexString(hash));
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        long milliseconds;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetInt64(out milliseconds))
            {
                return null;
            }
        }
        else if (value.ValueKind == JsonValueKind.String &&
                 long.TryParse(value.GetString(), out milliseconds))
        {
            // Parsed below.
        }
        else
        {
            return null;
        }

        if (milliseconds <= 0 ||
            milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    private static long? GetToolDuration(JsonElement? part)
    {
        if (part is not { } p ||
            !p.TryGetProperty("state", out var state) ||
            state.ValueKind != JsonValueKind.Object ||
            !state.TryGetProperty("time", out var time) ||
            time.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var start = GetLong(time, "start");
        var end = GetLong(time, "end");
        if (start is null || end is null || end < start)
        {
            return null;
        }

        return end - start;
    }

    private static string FormatDuration(long milliseconds) =>
        milliseconds >= 1000
            ? (milliseconds / 1000d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s"
            : milliseconds + " ms";

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static long? GetLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt64(out var number) ? number : null;
    }

    private static double? GetDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        if (!value.TryGetDouble(out var number) ||
            double.IsNaN(number) ||
            double.IsInfinity(number) ||
            number < 0)
        {
            return null;
        }

        return number;
    }

    private static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (char.IsWhiteSpace(character))
            {
                builder.Append('-');
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string TrimToken(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string TrimSummary(string summary)
    {
        var sanitized = new string(summary
            .Where(character => !char.IsControl(character) || character == '\t')
            .ToArray());
        return sanitized.Length <= MaxSummaryLength
            ? sanitized
            : sanitized[..MaxSummaryLength];
    }
}
