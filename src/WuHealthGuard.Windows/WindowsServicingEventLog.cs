using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>
/// Reads the few event log entries <see cref="Core.Checks.ServicingRestart"/> needs: boots, "a reboot is
/// necessary" and failures from servicing, and unexpected shutdowns. Queries by provider and event id, so it
/// works on every Windows language.
/// </summary>
public sealed class WindowsServicingEventLog : IServicingEventLog
{
    private const string Servicing = "Microsoft-Windows-Servicing";
    private const string KernelGeneral = "Microsoft-Windows-Kernel-General";
    private const string KernelPower = "Microsoft-Windows-Kernel-Power";
    private const string EventLogProvider = "EventLog";

    public ServicingEvents? Read(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var from = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var time = $"TimeCreated[@SystemTime>='{from}']";
        try
        {
            var setup = Query("Setup", $"*[System[Provider[@Name='{Servicing}'] and (EventID=3 or EventID=4) and {time}]]", cancellationToken);
            var system = Query("System",
                $"*[System[((Provider[@Name='{KernelGeneral}'] and EventID=12) or (Provider[@Name='{KernelPower}'] and EventID=41) or " +
                $"(Provider[@Name='{EventLogProvider}'] and EventID=6008)) and {time}]]",
                cancellationToken);

            return new ServicingEvents(
                Boots: Times(system, KernelGeneral, 12),
                RestartRequired: Times(setup, Servicing, 4),
                ServicingFailures: Times(setup, Servicing, 3),
                UnexpectedShutdowns: [.. Times(system, KernelPower, 41), .. Times(system, EventLogProvider, 6008)]);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // Can't tell: the checks keep the real last boot.
        }
    }

    private static List<(string Provider, int Id, DateTimeOffset Time)> Query(string log, string xpath, CancellationToken cancellationToken)
    {
        var results = new List<(string, int, DateTimeOffset)>();
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.TimeCreated is { } created)
                {
                    results.Add((record.ProviderName, record.Id, new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero)));
                }
            }
        }

        return results;
    }

    private static List<DateTimeOffset> Times(List<(string Provider, int Id, DateTimeOffset Time)> events, string provider, int id) =>
        events.Where(e => e.Id == id && string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase)).Select(e => e.Time).ToList();
}
