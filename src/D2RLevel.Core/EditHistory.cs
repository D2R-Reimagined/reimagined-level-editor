namespace D2RLevel.Core;

public sealed class EditHistory
{
    private sealed record Edit(Action Undo, Action Redo, object? Subject, long Sequence);
    private static long sequence;
    private readonly Stack<Edit> undo = new(), redo = new();
    private List<Edit>? transaction;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool Shared { get; internal set; }
    public event Action<object?>? Changed;
    public void Record(Action reverse, Action forward, object? subject = null)
    {
        var edit = new Edit(reverse, forward, subject, Interlocked.Increment(ref sequence));
        if (transaction is not null) { transaction.Add(edit); return; }
        undo.Push(edit); redo.Clear(); Changed?.Invoke(subject);
    }
    public void Transaction(Action action, object? subject = null)
    {
        if (transaction is not null) { action(); return; }
        var edits = transaction = [];
        try { action(); }
        catch
        {
            for (int i = edits.Count - 1; i >= 0; i--) edits[i].Undo();
            throw;
        }
        finally { transaction = null; }
        if (edits.Count > 0) Record(() => { for (int i = edits.Count - 1; i >= 0; i--) edits[i].Undo(); },
            () => { foreach (var edit in edits) edit.Redo(); }, subject);
    }
    public object? Undo()
    {
        if (!undo.TryPeek(out var edit)) return null;
        edit.Undo(); undo.Pop(); redo.Push(edit); Changed?.Invoke(edit.Subject); return edit.Subject;
    }
    public object? Redo()
    {
        if (!redo.TryPeek(out var edit)) return null;
        edit.Redo(); redo.Pop(); undo.Push(edit); Changed?.Invoke(edit.Subject); return edit.Subject;
    }
    public void Clear() { undo.Clear(); redo.Clear(); }
    internal void Merge(EditHistory other)
    {
        if (ReferenceEquals(this, other)) return;
        var edits = undo.Concat(other.undo).OrderBy(e => e.Sequence).ToArray();
        Clear(); other.Clear(); foreach (var edit in edits) undo.Push(edit);
    }
}
