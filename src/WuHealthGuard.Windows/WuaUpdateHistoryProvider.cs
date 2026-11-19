using WuHealthGuard.Core.Abstractions;

namespace WuHealthGuard.Windows;

/// <summary>Reads update history from the Windows Update Agent COM API (<c>Microsoft.Update.Session</c>).</summary>
public sealed class WuaUpdateHistoryProvider : IUpdateHistoryProvider
{
    public Task<IReadOnlyList<UpdateHistoryEntry>> GetHistoryAsync(int maxEntries, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<UpdateHistoryEntry>>(() => Read(maxEntries), cancellationToken);

    private static List<UpdateHistoryEntry> Read(int maxEntries)
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new InvalidOperationException("Windows Update Agent is not available.");
        dynamic session = Activator.CreateInstance(type)!;
        session.ClientApplicationID = "WU Health Guard";
        dynamic searcher = session.CreateUpdateSearcher();

        int total = searcher.GetTotalHistoryCount();
        var count = Math.Min(total, maxEntries);
        var entries = new List<UpdateHistoryEntry>(count);
        if (count == 0)
        {
            return entries;
        }

        dynamic history = searcher.QueryHistory(0, count);
        foreach (dynamic entry in history)
        {
            int operation = entry.Operation;
            if (operation is not (1 or 2))
            {
                continue;
            }

            string updateId = "";
            try
            {
                updateId = entry.UpdateIdentity.UpdateID;
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // Some entries (e.g. from other clients) have no identity.
            }

            DateTime date = entry.Date; // WUA reports UTC.
            entries.Add(new UpdateHistoryEntry(
                updateId,
                (string)entry.Title ?? "",
                new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
                (UpdateOperation)operation,
                (UpdateResultCode)(int)entry.ResultCode,
                (int)entry.HResult));
        }

        return entries;
    }
}
