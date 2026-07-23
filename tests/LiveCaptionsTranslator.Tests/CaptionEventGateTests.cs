using LiveCaptionsTranslator.captioning;
using Xunit;

namespace LiveCaptionsTranslator.Tests;

public sealed class CaptionEventGateTests
{
    [Fact]
    public void TextBeforeResetIsRejected()
    {
        var gate = new CaptionEventGate();

        AssertRejected(gate, CaptionEventFactory.Text());
    }

    [Fact]
    public void NewSessionResetIsAccepted()
    {
        var gate = new CaptionEventGate();

        AssertAccepted(gate, CaptionEventFactory.Reset());
        Assert.Equal(CaptionEventFactory.SessionA, gate.ActiveSessionId);
        Assert.Equal(1, gate.LastAcceptedSequence);
        Assert.Equal(1, gate.HighestObservedSequence);
        Assert.Null(gate.CurrentSegmentId);
    }

    [Fact]
    public void FirstNewSessionResetMustUseSequenceOne()
    {
        var gate = new CaptionEventGate();

        AssertRejected(gate, CaptionEventFactory.Reset(sequence: 2));
    }

    [Fact]
    public void DelayedPreviousSessionEventIsRejected()
    {
        var gate = new CaptionEventGate();
        AssertAccepted(gate, CaptionEventFactory.Reset(CaptionEventFactory.SessionA));
        AssertAccepted(gate, CaptionEventFactory.Reset(CaptionEventFactory.SessionB));

        AssertRejected(gate, CaptionEventFactory.Text(sessionId: CaptionEventFactory.SessionA));
    }

    [Fact]
    public void DelayedPreviousSessionResetIsRejected()
    {
        var gate = new CaptionEventGate();
        AssertAccepted(gate, CaptionEventFactory.Reset(CaptionEventFactory.SessionA));
        AssertAccepted(gate, CaptionEventFactory.Reset(CaptionEventFactory.SessionB));

        AssertRejected(gate, CaptionEventFactory.Reset(CaptionEventFactory.SessionA));
    }

    [Fact]
    public void NewerSameSessionResetClearsSegmentState()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text());

        AssertAccepted(gate, CaptionEventFactory.Reset(sequence: 3));

        Assert.Equal(3, gate.LastAcceptedSequence);
        Assert.Null(gate.CurrentSegmentId);
        Assert.Null(gate.CurrentRevision);
        Assert.False(gate.IsCurrentSegmentFinal);
    }

    [Fact]
    public void DuplicateResetIsRejected()
    {
        var gate = StartedGate();

        AssertRejected(gate, CaptionEventFactory.Reset(sequence: 1));
    }

    [Fact]
    public void OlderResetIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Reset(sequence: 3));

        AssertRejected(gate, CaptionEventFactory.Reset(sequence: 2));
    }

    [Fact]
    public void StrictlyIncreasingSequenceIsAccepted()
    {
        var gate = StartedGate();

        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2));
        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3));
    }

    [Fact]
    public void DuplicateSequenceIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 2));
    }

    [Fact]
    public void DecreasingSequenceIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 3));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 2));
    }

    [Fact]
    public void RejectedNewerEventMakesLaterLowerSequenceOlder()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2));
        AssertRejected(gate, CaptionEventFactory.Text(sequence: 4, segmentId: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3));

        Assert.Equal(2, gate.LastAcceptedSequence);
        Assert.Equal(4, gate.HighestObservedSequence);
    }

    [Fact]
    public void RejectedEventSequenceCannotBeReused()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2, text: "accepted"));
        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3, text: "rejected"));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3, text: "accepted"));

        Assert.Equal(2, gate.LastAcceptedSequence);
        Assert.Equal(3, gate.HighestObservedSequence);
    }

    [Fact]
    public void EventNewerThanRejectedEventCanStillBeAccepted()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2, text: "accepted"));
        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3, text: "rejected"));

        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 4, text: "accepted"));

        Assert.Equal(4, gate.LastAcceptedSequence);
        Assert.Equal(4, gate.HighestObservedSequence);
    }

    [Fact]
    public void RejectedForeignSessionDoesNotPoisonActiveObservedSequence()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            sessionId: CaptionEventFactory.SessionB, sequence: 100));
        Assert.Equal(2, gate.HighestObservedSequence);

        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3));
    }

    [Fact]
    public void NewSessionResetResetsObservedSequenceState()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 5));

        AssertAccepted(gate, CaptionEventFactory.Reset(CaptionEventFactory.SessionB));

        Assert.Equal(CaptionEventFactory.SessionB, gate.ActiveSessionId);
        Assert.Equal(1, gate.LastAcceptedSequence);
        Assert.Equal(1, gate.HighestObservedSequence);
        Assert.Null(gate.CurrentSegmentId);
        Assert.Null(gate.CurrentRevision);
    }

    [Fact]
    public void SameSessionResetMustBeNewerThanHighestObservedSequence()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2));
        AssertRejected(gate, CaptionEventFactory.Text(sequence: 4, segmentId: 2));

        AssertRejected(gate, CaptionEventFactory.Reset(sequence: 3));

        Assert.Equal(2, gate.LastAcceptedSequence);
        Assert.Equal(4, gate.HighestObservedSequence);
        Assert.Equal(1, gate.CurrentSegmentId);
        Assert.Equal(1, gate.CurrentRevision);
    }

    [Fact]
    public void RejectedEventDoesNotMutateAcceptedState()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Partial, sequence: 2, text: "accepted"));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3, text: "rejected"));

        Assert.Equal(2, gate.LastAcceptedSequence);
        Assert.Equal(3, gate.HighestObservedSequence);
        Assert.Equal(1, gate.CurrentSegmentId);
        Assert.Equal(1, gate.CurrentRevision);
        Assert.False(gate.IsCurrentSegmentFinal);

        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 4, text: "accepted"));
    }

    [Fact]
    public void PartialCommittedFinalProgressionIsAccepted()
    {
        var gate = StartedGate();

        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Partial, sequence: 2));
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Committed, sequence: 3));
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 4));
        Assert.True(gate.IsCurrentSegmentFinal);
    }

    [Fact]
    public void PartialToFinalIsAccepted()
    {
        var gate = StartedGate();

        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Partial, sequence: 2));
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 3));
    }

    [Fact]
    public void CommittedToFinalIsAccepted()
    {
        var gate = StartedGate();

        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Committed, sequence: 2));
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 3));
    }

    [Fact]
    public void DirectFinalIsAccepted()
    {
        var gate = StartedGate();

        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final));
    }

    [Fact]
    public void LifecycleRegressionIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Committed, sequence: 2));

        AssertRejected(gate, CaptionEventFactory.Text(CaptionEventKind.Partial, sequence: 3));
    }

    [Fact]
    public void UpdateAfterFinalIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Partial, sequence: 3, revision: 2, text: "changed"));
    }

    [Fact]
    public void NewerRevisionIsAccepted()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2, text: "first"));

        AssertAccepted(gate, CaptionEventFactory.Text(
            sequence: 3, revision: 2, text: "changed"));
        Assert.Equal(2, gate.CurrentRevision);
    }

    [Fact]
    public void OlderRevisionIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2, text: "first"));
        AssertAccepted(gate, CaptionEventFactory.Text(
            sequence: 3, revision: 3, text: "third"));

        AssertRejected(gate, CaptionEventFactory.Text(
            sequence: 4, revision: 2, text: "second"));
    }

    [Fact]
    public void SameRevisionWithDifferentTextIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2, text: "first"));

        AssertRejected(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 3, text: "changed"));
    }

    [Fact]
    public void CommittedFollowedByPartialOfNewerRevisionIsAccepted()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Committed, sequence: 2, text: "first"));

        AssertAccepted(gate, CaptionEventFactory.Text(
            CaptionEventKind.Partial, sequence: 3, revision: 2, text: "changed"));
    }

    [Fact]
    public void FirstSegmentMustBeOne()
    {
        var gate = StartedGate();

        AssertRejected(gate, CaptionEventFactory.Text(segmentId: 2));
    }

    [Fact]
    public void NewSegmentBeforePreviousFinalIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(sequence: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            sequence: 3, segmentId: 2));
    }

    [Fact]
    public void SegmentIdentitySkippingAValueIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            sequence: 3, segmentId: 3));
    }

    [Fact]
    public void NextSegmentAfterFinalIsAccepted()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 2));

        AssertAccepted(gate, CaptionEventFactory.Text(
            sequence: 3, segmentId: 2));
        Assert.Equal(2, gate.CurrentSegmentId);
    }

    [Fact]
    public void DecreasingSegmentIdentityIsRejected()
    {
        var gate = StartedGate();
        AssertAccepted(gate, CaptionEventFactory.Text(CaptionEventKind.Final, sequence: 2));
        AssertAccepted(gate, CaptionEventFactory.Text(
            sequence: 3, segmentId: 2));

        AssertRejected(gate, CaptionEventFactory.Text(
            sequence: 4, segmentId: 1, revision: 2, text: "old"));
    }

    private static CaptionEventGate StartedGate()
    {
        var gate = new CaptionEventGate();
        AssertAccepted(gate, CaptionEventFactory.Reset());
        return gate;
    }

    private static void AssertAccepted(CaptionEventGate gate, CaptionEvent captionEvent)
    {
        Assert.True(gate.TryAccept(captionEvent, out var rejectionReason));
        Assert.Null(rejectionReason);
    }

    private static void AssertRejected(CaptionEventGate gate, CaptionEvent captionEvent)
    {
        Assert.False(gate.TryAccept(captionEvent, out var rejectionReason));
        Assert.False(string.IsNullOrWhiteSpace(rejectionReason));
    }
}
