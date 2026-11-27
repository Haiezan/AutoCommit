namespace AutoCommit.Core;

// Draft replacement happens only after a complete, validated response arrives.
public sealed class CommitDraft
{
    public string Text { get; private set; } = "";
    public ChangeSnapshot? Snapshot { get; private set; }
    public GenerationResult? Response { get; private set; }
    public bool Edited { get; private set; }
    public bool Committed { get; private set; }
    private CommitDraft? previous;
    public bool CanUndo => previous != null;
    public void Edit(string text) { if (Text != text) { Text = text; Edited = true; Committed = false; } }
    public bool Matches(ChangeSnapshot? current) => !Committed && current != null && Snapshot != null &&
        string.Equals(current.Root, Snapshot.Root, System.StringComparison.OrdinalIgnoreCase) && current.Fingerprint == Snapshot.Fingerprint;
    public void Accept(GenerationResult response, ChangeSnapshot snapshot)
    {
        previous = string.IsNullOrWhiteSpace(Text) ? null : new CommitDraft {
            Text = Text, Snapshot = Snapshot, Response = Response, Edited = Edited, Committed = Committed
        };
        Text = response.Text; Snapshot = snapshot; Response = response; Edited = false; Committed = false;
    }
    public void Undo()
    {
        if (previous == null) return;
        Text = previous.Text; Snapshot = previous.Snapshot; Response = previous.Response;
        Edited = previous.Edited; Committed = previous.Committed; previous = null;
    }
    public void MarkCommitted()
    {
        Text = "";
        Snapshot = null;
        Response = null;
        Edited = false;
        previous = null;
        Committed = true;
    }
}
