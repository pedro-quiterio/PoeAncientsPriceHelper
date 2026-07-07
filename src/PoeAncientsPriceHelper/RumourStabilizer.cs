namespace PoeAncientsPriceHelper;

// Holds the best resolution seen for each rumour row while a panel stays open, so the overlay stops
// flickering resolved↔"unknown" (or name-jumping) as OCR reads each line slightly differently from one
// pass to the next. The in-game rumours don't change while the panel is open, so once a row resolves to
// a known RumourEntry we LOCK it and never downgrade back to "unknown" — only a DIFFERENT entry read on
// two consecutive passes (the panel genuinely changed / was reopened) can move the lock. Reset() clears
// the accumulator when the panel closes so a reopened panel starts fresh.
//
// Keyed by row INDEX (top-to-bottom order), not Y position like the price overlay's RowSlot: a rumour
// panel is a short, fixed, ordered list, so the index IS the stable identity and there's no need for
// the price loop's Y-tolerance matching. This is the rumour-side analogue of ScanEngine.MergeReads.
internal sealed class RumourStabilizer
{
    private sealed class Slot
    {
        public RumourResultRow Row = null!;   // the row currently shown for this index
        public bool Locked;                   // Row resolved to a known entry and is held stable
        public RumourEntry? Pending;          // a DIFFERENT entry seen while locked, awaiting confirmation
        public int PendingCount;
    }

    // Consecutive passes a different known entry must appear on a locked row before it takes over — so a
    // single OCR misread that happens to resolve elsewhere can't flip an otherwise-stable row.
    private const int SwitchAfterPasses = 2;

    private readonly List<Slot> _slots = new();

    // Clear the accumulator — call when the panel closes so a later panel doesn't inherit stale locks.
    public void Reset() => _slots.Clear();

    // Merge one raw read into the accumulator and return the stabilised rows in panel order.
    public IReadOnlyList<RumourResultRow> Stabilize(IReadOnlyList<RumourResultRow> read)
    {
        // A change in row count means a structurally different panel (reopened / a different Atlas node),
        // so rebuild from scratch rather than mapping mismatched indices onto the old slots.
        if (read.Count != _slots.Count)
        {
            _slots.Clear();
            foreach (var row in read)
                _slots.Add(new Slot { Row = row, Locked = row.Matched });
            return Snapshot();
        }

        for (int i = 0; i < read.Count; i++)
        {
            var slot = _slots[i];
            var incoming = read[i];

            if (!slot.Locked)
            {
                // Not resolved yet: take the latest read (keeps the raw OCR name fresh for the --debug
                // display) and lock as soon as it resolves to a known entry.
                slot.Row = incoming;
                slot.Locked = incoming.Matched;
                slot.Pending = null;
                slot.PendingCount = 0;
                continue;
            }

            // Locked to a known entry. An "unknown" read, or the same entry re-read, never disturbs it —
            // the rumour hasn't changed, OCR just read the line differently this pass.
            if (!incoming.Matched || Equals(incoming.Entry, slot.Row.Entry))
            {
                slot.Pending = null;
                slot.PendingCount = 0;
                continue;
            }

            // A DIFFERENT known entry: accept it only once it persists across SwitchAfterPasses passes.
            if (Equals(incoming.Entry, slot.Pending))
                slot.PendingCount++;
            else { slot.Pending = incoming.Entry; slot.PendingCount = 1; }

            if (slot.PendingCount >= SwitchAfterPasses)
            {
                slot.Row = incoming;
                slot.Locked = true;
                slot.Pending = null;
                slot.PendingCount = 0;
            }
        }

        return Snapshot();
    }

    private IReadOnlyList<RumourResultRow> Snapshot() => _slots.Select(s => s.Row).ToList();
}
