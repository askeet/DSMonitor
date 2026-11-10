using System.Collections;
using System.IO;
using DSMonitor.Models;
using DSMonitor.Persistence;

namespace DSMonitor.Services;

/// <summary>
/// Complete immutable monitoring configuration. Runs, logs, database path and
/// the permitted database root are supplied together so an isolated startup
/// cannot silently mix synthetic and production locations.
/// </summary>
public sealed record MonitoringCompositionOptions(
    string RunsRoot,
    string LogsRoot,
    string DatabasePath,
    string DatabaseRoot,
    TimeSpan RefreshInterval);

/// <summary>
/// Single production composition point for V3 monitoring. The runtime ledger
/// factory always opens the V3 projection; there is no V1/V2 fallback. A
/// failed V3 open remains the service's existing metadata-only fail-closed path.
/// </summary>
public sealed class MonitoringComposition
{
    public const string IsolatedRunsRootVariable = "DSMONITOR_ISOLATED_RUNS_ROOT";
    public const string IsolatedLogsRootVariable = "DSMONITOR_ISOLATED_LOGS_ROOT";
    public const string IsolatedDatabasePathVariable = "DSMONITOR_ISOLATED_DATABASE_PATH";
    public const string IsolatedDatabaseRootVariable = "DSMONITOR_ISOLATED_DATABASE_ROOT";
    public const string IsolatedRefreshSecondsVariable = "DSMONITOR_ISOLATED_REFRESH_SECONDS";

    private static readonly string[] IsolatedRequiredVariables =
    {
        IsolatedRunsRootVariable,
        IsolatedLogsRootVariable,
        IsolatedDatabasePathVariable,
        IsolatedDatabaseRootVariable
    };

    public MonitoringComposition(MonitoringCompositionOptions options)
    {
        Options = Normalize(options);
    }

    public MonitoringCompositionOptions Options { get; }

    public ForgeMonitoringService CreateService(EventLedgerOptions? ledgerOptions = null) =>
        new(
            Options.RunsRoot,
            new MonitoringRunLoggerFactory(Options.LogsRoot),
            new MonitoringOptions(Options.RefreshInterval),
            ledgerFactory: () => EventLedger.OpenV3Projection(
                Options.DatabasePath,
                Options.DatabaseRoot,
                ledgerOptions));

    public static MonitoringComposition CreateProduction() =>
        new(new MonitoringCompositionOptions(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".forge-delegate",
                "runs"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DSMonitor",
                "Logs"),
            LedgerStoragePaths.DefaultV3DatabasePath,
            LedgerStoragePaths.DefaultDataDirectory,
            TimeSpan.FromSeconds(5)));

    /// <summary>
    /// Reads the explicit isolated configuration. When any isolated setting is
    /// present, every required settings must be present and valid; production
    /// defaults are never used as a partial fallback.
    /// </summary>
    public static MonitoringComposition CreateForStartup(
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var values = environment ?? ReadEnvironment();
        if (!HasAnyIsolatedSetting(values))
        {
            return CreateProduction();
        }

        if (!TryCreateIsolated(values, out var composition, out var error))
        {
            throw new InvalidOperationException(error);
        }

        return composition!;
    }

    public static bool TryCreateIsolated(
        IReadOnlyDictionary<string, string?> environment,
        out MonitoringComposition? composition,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(environment);
        composition = null;

        var missing = IsolatedRequiredVariables
            .Where(name => !environment.TryGetValue(name, out var value) ||
                           string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (missing.Length > 0)
        {
            error = "Isolated monitoring configuration is incomplete; missing: " +
                    string.Join(", ", missing) +
                    ". Production defaults were not used.";
            return false;
        }

        try
        {
            var refresh = TimeSpan.FromSeconds(5);
            if (environment.TryGetValue(IsolatedRefreshSecondsVariable, out var refreshValue) &&
                !string.IsNullOrWhiteSpace(refreshValue))
            {
                if (!int.TryParse(refreshValue, out var seconds) || seconds <= 0 || seconds > 3600)
                {
                    error = $"Isolated setting {IsolatedRefreshSecondsVariable} must be an integer from 1 to 3600.";
                    return false;
                }

                refresh = TimeSpan.FromSeconds(seconds);
            }

            composition = new MonitoringComposition(new MonitoringCompositionOptions(
                environment[IsolatedRunsRootVariable]!,
                environment[IsolatedLogsRootVariable]!,
                environment[IsolatedDatabasePathVariable]!,
                environment[IsolatedDatabaseRootVariable]!,
                refresh));
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            error = "Isolated monitoring configuration is invalid: " + ex.Message;
            return false;
        }
    }

    private static bool HasAnyIsolatedSetting(IReadOnlyDictionary<string, string?> environment) =>
        environment.Keys.Any(key => key.StartsWith(
            "DSMONITOR_ISOLATED_",
            StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, string?> ReadEnvironment()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
            {
                values[key] = entry.Value as string;
            }
        }

        return values;
    }

    private static MonitoringCompositionOptions Normalize(MonitoringCompositionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var runsRoot = NormalizeRoot(options.RunsRoot, nameof(options.RunsRoot));
        var logsRoot = NormalizeRoot(options.LogsRoot, nameof(options.LogsRoot));
        var databaseRoot = NormalizeRoot(options.DatabaseRoot, nameof(options.DatabaseRoot));
        if (string.IsNullOrWhiteSpace(options.DatabasePath))
        {
            throw new ArgumentException(
                "A database path is required.",
                nameof(options.DatabasePath));
        }

        var databasePath = Path.GetFullPath(options.DatabasePath);
        if (string.Equals(databasePath, databaseRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The database path must be a file inside the permitted database root.",
                nameof(options.DatabasePath));
        }

        if (!PathSafety.IsWithinRoot(databasePath, databaseRoot))
        {
            throw new ArgumentException(
                "The database path must be inside the permitted database root.",
                nameof(options.DatabasePath));
        }

        if (options.RefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.RefreshInterval),
                "The refresh interval must be positive.");
        }

        return options with
        {
            RunsRoot = runsRoot,
            LogsRoot = logsRoot,
            DatabasePath = databasePath,
            DatabaseRoot = databaseRoot
        };
    }

    private static string NormalizeRoot(string? path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A monitoring root is required.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
