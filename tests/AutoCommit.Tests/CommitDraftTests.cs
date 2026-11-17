using AutoCommit.Core;
using Xunit;

public class CommitDraftTests
{
    [Fact]
    public void ReplacementCanRestoreEditedTextAndOriginalUsage()
    {
        var draft = new CommitDraft(); var snapshot = new ChangeSnapshot("repo", "first");
        var original = new GenerationResult { Text = "first", InputTokens = 12 };
        draft.Accept(original, snapshot); draft.Edit("manual edits");
        draft.Accept(new GenerationResult { Text = "second", InputTokens = 30 }, snapshot);
        Assert.True(draft.CanUndo); Assert.False(draft.Edited);
        draft.Undo(); Assert.Equal("manual edits", draft.Text); Assert.True(draft.Edited);
        Assert.Same(original, draft.Response); Assert.True(draft.Matches(snapshot)); Assert.False(draft.CanUndo);
    }
    [Fact]
    public void ChangedContextKeepsDraftButDisablesCommitEligibility()
    {
        var draft = new CommitDraft(); var snapshot = new ChangeSnapshot("repo", "first");
        draft.Accept(new GenerationResult { Text = "keep me" }, snapshot);
        Assert.False(draft.Matches(new ChangeSnapshot("other", "first")));
        Assert.False(draft.Matches(new ChangeSnapshot("repo", "second")));
        Assert.False(draft.Matches(null)); Assert.Equal("keep me", draft.Text);
    }
    [Fact]
    public void CommittedDraftCannotBeSubmittedAgainEvenAfterEditing()
    {
        var draft = new CommitDraft(); var snapshot = new ChangeSnapshot("repo", "first");
        draft.Accept(new GenerationResult { Text = "first" }, snapshot);
        draft.Accept(new GenerationResult { Text = "second" }, snapshot);
        draft.MarkCommitted(); draft.Edit("edited after commit");
        Assert.False(draft.Matches(snapshot)); Assert.False(draft.CanUndo);
        Assert.Equal("edited after commit", draft.Text);
    }
}
