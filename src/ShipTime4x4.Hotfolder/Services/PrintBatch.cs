using ShipTime4x4.Hotfolder.Models;

namespace ShipTime4x4.Hotfolder.Services;

internal enum BatchPrintOutcome { Sent, Skipped, Stopped, SentAndStopped }

internal sealed record BatchPrintResult(int Total, int Sent, int Skipped, bool Stopped)
{
    public int Remaining => Total - Sent - Skipped;
}

internal static class PrintBatch
{
    public static long LabelCount(IEnumerable<LabelRecord> snapshot) =>
        snapshot.Sum(record => (long)Math.Max(1, record.PageCount));

    public static bool IsEligible(LabelRecord record) =>
        !record.PrintedAt.HasValue && record.Status is LabelStatus.Ready or LabelStatus.Warning;

    public static LabelRecord[] Snapshot(IEnumerable<LabelRecord> labels, bool oldestFirst)
    {
        var eligible = labels.Where(IsEligible).DistinctBy(record => record.Id);
        return (oldestFirst ? eligible.OrderBy(record => record.ImportedAt)
            : eligible.OrderByDescending(record => record.ImportedAt)).ToArray();
    }

    // A fixed snapshot excludes later imports. Await each complete print/verification
    // before advancing; stopping never interrupts a PDF already sent to the spooler.
    public static async Task<BatchPrintResult> RunAsync(IReadOnlyList<LabelRecord> snapshot,
        Func<LabelRecord, Task<BatchPrintOutcome>> print,
        Func<bool> stopRequested, Action<int, int> progress)
    {
        var sent = 0;
        var skipped = 0;
        for (var index = 0; index < snapshot.Count; index++)
        {
            if (stopRequested()) return new(snapshot.Count, sent, skipped, true);
            progress(index + 1, snapshot.Count);
            var outcome = await print(snapshot[index]);
            switch (outcome)
            {
                case BatchPrintOutcome.Sent: sent++; break;
                case BatchPrintOutcome.Skipped: skipped++; break;
                case BatchPrintOutcome.SentAndStopped:
                    return new(snapshot.Count, sent + 1, skipped, true);
                case BatchPrintOutcome.Stopped:
                    return new(snapshot.Count, sent, skipped, true);
                default: throw new InvalidOperationException("Unexpected batch print result.");
            }
        }
        return new(snapshot.Count, sent, skipped, false);
    }
}
