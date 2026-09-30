using FluentAssertions;

namespace DotnetDiagnostics.Core.Tests.DurableCounterSpike;

public sealed class ApparentByteReservationLedgerTests
{
    [Theory]
    [InlineData(99, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public void ExactCapacityBoundaryIsEnforced(long bytes, bool accepted)
    {
        var ledger = Create(capacity: 100);

        Action register = () => ledger.Register(Id("file"), bytes);

        if (accepted)
        {
            register.Should().NotThrow();
            ledger.GetSnapshot().ChargedBytes.Should().Be(bytes);
        }
        else
        {
            register.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("CapacityExceeded");
            ledger.GetSnapshot().ChargedBytes.Should().Be(0);
        }
    }

    [Fact]
    public void HugeValuesAndOverflowFailWithoutChangingState()
    {
        var ledger = Create(long.MaxValue);
        var file = ledger.Register(Id("huge"), long.MaxValue - 1);

        Action overflow = () => ledger.Reserve(file, 2);
        overflow.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ArithmeticOverflow");
        ledger.GetSnapshot().Should().BeEquivalentTo(
            new ApparentByteLedgerSnapshot(long.MaxValue, long.MaxValue - 1, 1, 0, false, null));

        Action negative = () => ledger.Reserve(file, -1);
        negative.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidLength");
        ledger.GetSnapshot().ChargedBytes.Should().Be(long.MaxValue - 1);
    }

    [Fact]
    public async Task CompetingReservationsCannotBothClaimTheFinalByte()
    {
        var ledger = Create(capacity: 1, maximumTrackedFiles: 2, maximumOutstandingPermits: 2);
        var first = ledger.Register(Id("first"), 0);
        var second = ledger.Register(Id("second"), 0);
        using var start = new Barrier(3);

        var attempts = new[] { first, second }.Select(file => Task.Run(() =>
        {
            start.SignalAndWait();
            try
            {
                ledger.Reserve(file, 1);
                return true;
            }
            catch (DurableStorageExperimentException exception) when (exception.Code == "CapacityExceeded")
            {
                return false;
            }
        })).ToArray();

        start.SignalAndWait();
        var results = await Task.WhenAll(attempts);

        results.Should().ContainSingle(result => result);
        ledger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.ChargedBytes == 1 && snapshot.OutstandingPermits == 1);
    }

    [Fact]
    public void AggregateIncludesSourceAndOutputFiles()
    {
        var ledger = Create(capacity: 20, maximumTrackedFiles: 3);
        ledger.Register(Id("source"), 8);
        var output = ledger.Register(Id("output"), 7);
        ledger.Reserve(output, 5);

        ledger.GetSnapshot().ChargedBytes.Should().Be(20);
        Action extra = () => ledger.Register(Id("manifest"), 1);
        extra.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("CapacityExceeded");
    }

    [Fact]
    public void InvalidOperationsDoNotStealCharge()
    {
        var ledger = Create(capacity: 20);
        var file = ledger.Register(Id("file"), 5);
        var permit = ledger.Reserve(file, 5);
        var before = ledger.GetSnapshot();

        Action badLength = () => ledger.CompleteConfirmed(permit, -1);
        badLength.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidLength");
        Action secondPermit = () => ledger.Reserve(file, 1);
        secondPermit.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("FilePermitAlreadyOutstanding");
        Action duplicateIdentity = () => ledger.Register(Id("file"), 0);
        duplicateIdentity.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("DuplicateFileIdentity");

        ledger.GetSnapshot().Should().BeEquivalentTo(before);
    }

    [Fact]
    public void ConfirmedPartialWriteReleasesRemainderButUnknownRetainsCeiling()
    {
        var confirmedLedger = Create(capacity: 20);
        var confirmedFile = confirmedLedger.Register(Id("confirmed"), 4);
        var confirmedPermit = confirmedLedger.Reserve(confirmedFile, 10);
        confirmedLedger.CompleteConfirmed(confirmedPermit, 7);
        confirmedLedger.GetSnapshot().ChargedBytes.Should().Be(7);

        var unknownLedger = Create(capacity: 20);
        var unknownFile = unknownLedger.Register(Id("unknown"), 4);
        var unknownPermit = unknownLedger.Reserve(unknownFile, 10);
        unknownLedger.CompleteUnknown(unknownPermit);
        unknownLedger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.ChargedBytes == 14 && snapshot.OutstandingPermits == 0);
    }

    [Fact]
    public void ConfirmedShrinkAndActualRetirementReleaseCharge()
    {
        var ledger = Create(capacity: 20);
        var file = ledger.Register(Id("persistent-name"), 12);

        ledger.ConfirmObservation(file, 8);
        ledger.GetSnapshot().ChargedBytes.Should().Be(8);
        ledger.RetireConfirmed(file);
        ledger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.ChargedBytes == 0 && snapshot.TrackedFiles == 0);
    }

    [Fact]
    public void SameIdentityRequiresRetirementAndOldGenerationCannotMutateReplacement()
    {
        var ledger = Create(capacity: 20);
        var oldFile = ledger.Register(Id("same-id"), 5);
        var oldPermit = ledger.Reserve(oldFile, 2);
        ledger.CompleteUnknown(oldPermit);
        ledger.RetireConfirmed(oldFile);
        var replacement = ledger.Register(Id("same-id"), 6);

        Action staleFile = () => ledger.ConfirmObservation(oldFile, 0);
        staleFile.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("StaleFileToken");
        Action stalePermit = () => ledger.CompleteUnknown(oldPermit);
        stalePermit.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("StalePermit");
        ledger.GetSnapshot().ChargedBytes.Should().Be(6);

        ledger.ConfirmObservation(replacement, 6);
    }

    [Fact]
    public void OutstandingPermitBlocksRetirementWithoutReleasingCharge()
    {
        var ledger = Create(capacity: 7);
        var file = ledger.Register(Id("unresolved"), 5);
        var permit = ledger.Reserve(file, 2);
        var before = ledger.GetSnapshot();

        Action retire = () => ledger.RetireConfirmed(file);
        retire.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PermitOutstanding");
        ledger.GetSnapshot().Should().BeEquivalentTo(before);

        Action consumeHeadroom = () => ledger.Register(Id("other"), 1);
        consumeHeadroom.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("CapacityExceeded");
        ledger.CompleteUnknown(permit);
        ledger.GetSnapshot().ChargedBytes.Should().Be(7);
        ledger.RetireConfirmed(file);
        ledger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.ChargedBytes == 0 && snapshot.OutstandingPermits == 0 && snapshot.TrackedFiles == 0);
    }

    [Fact]
    public void DuplicateAndForeignPermitsCannotReleaseReservation()
    {
        var ledger = Create(capacity: 20);
        var file = ledger.Register(Id("file"), 5);
        var permit = ledger.Reserve(file, 5);
        ledger.CompleteConfirmed(permit, 8);

        Action duplicate = () => ledger.CompleteUnknown(permit);
        duplicate.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("DuplicatePermitCompletion");

        var other = Create(capacity: 20);
        var otherFile = other.Register(Id("file"), 2);
        var foreign = other.Reserve(otherFile, 1);
        Action foreignUse = () => ledger.CompleteUnknown(foreign);
        foreignUse.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ForeignPermit");
        ledger.GetSnapshot().ChargedBytes.Should().Be(8);
    }

    [Fact]
    public void MetadataAndPermitCapsHaveExactBoundaries()
    {
        var ledger = Create(capacity: 20, maximumTrackedFiles: 2, maximumOutstandingPermits: 1);
        var first = ledger.Register(Id("first"), 1);
        var second = ledger.Register(Id("second"), 1);

        Action third = () => ledger.Register(Id("third"), 0);
        third.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("TrackedFileLimitExceeded");

        ledger.Reserve(first, 1);
        Action secondPermit = () => ledger.Reserve(second, 1);
        secondPermit.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("PermitLimitExceeded");
        ledger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.TrackedFiles == 2 && snapshot.OutstandingPermits == 1 && snapshot.ChargedBytes == 3);
    }

    [Fact]
    public void ObservationAboveReservedCeilingFaultsWithoutManufacturingHeadroom()
    {
        var ledger = Create(capacity: 20);
        var file = ledger.Register(Id("file"), 5);
        var permit = ledger.Reserve(file, 5);

        Action violation = () => ledger.CompleteConfirmed(permit, 11);
        violation.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("ObservedLengthExceedsReservation");
        ledger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.IsFaulted &&
                snapshot.FaultCode == "ObservedLengthExceedsReservation" &&
                snapshot.ChargedBytes == 10 &&
                snapshot.OutstandingPermits == 1);

        Action furtherMutation = () => ledger.RetireConfirmed(file);
        furtherMutation.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("LedgerFaulted");
    }

    [Fact]
    public void CallerTokensAreNotPathsAndInvalidIdentityFails()
    {
        var ledger = Create(capacity: 20);

        Action empty = () => ledger.Register(Id(" "), 0);
        empty.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("InvalidFileIdentity");

        var identity = Id("inventory-device-and-file-id-token");
        ledger.Register(identity, 3);
        ledger.GetSnapshot().ChargedBytes.Should().Be(3);
    }

    [Fact]
    public void IdentityLengthIsBoundedOnRegistrationAndTokenUse()
    {
        var ledger = Create(capacity: 20);
        var maximum = Id(new string('x', ApparentByteReservationLedger.MaximumIdentityCharacters));
        var excessive = Id(new string('x', ApparentByteReservationLedger.MaximumIdentityCharacters + 1));
        var file = ledger.Register(maximum, 3);
        var permit = ledger.Reserve(file, 2);
        var before = ledger.GetSnapshot();
        Action[] invalidOperations =
        [
            () => ledger.Register(excessive, 0),
            () => ledger.Register(default, 0),
            () => ledger.Reserve(file with { Identity = excessive }, 1),
            () => ledger.CompleteUnknown(permit with { Identity = excessive }),
        ];

        foreach (var operation in invalidOperations)
        {
            operation.Should().Throw<DurableStorageExperimentException>()
                .Which.Code.Should().Be("InvalidFileIdentity");
            ledger.GetSnapshot().Should().BeEquivalentTo(before);
        }

        ledger.CompleteUnknown(permit);
        ledger.GetSnapshot().ChargedBytes.Should().Be(5);
    }

    [Fact]
    public void UnreservedObservationFaultsWithoutReleasingTheExistingCharge()
    {
        var ledger = Create(capacity: 20);
        var file = ledger.Register(Id("unreserved"), 5);

        Action observe = () => ledger.ConfirmObservation(file, 6);
        observe.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("UnreservedObservedGrowth");
        ledger.GetSnapshot().Should().Match<ApparentByteLedgerSnapshot>(
            snapshot => snapshot.IsFaulted && snapshot.FaultCode == "UnreservedObservedGrowth"
                && snapshot.ChargedBytes == 5 && snapshot.OutstandingPermits == 0);

        Action release = () => ledger.RetireConfirmed(file);
        release.Should().Throw<DurableStorageExperimentException>()
            .Which.Code.Should().Be("LedgerFaulted");
    }

    private static ApparentByteReservationLedger Create(
        long capacity,
        int maximumTrackedFiles = 4,
        int maximumOutstandingPermits = 4) =>
        new(capacity, maximumTrackedFiles, maximumOutstandingPermits);

    private static ApparentFileIdentity Id(string value) => new(value);
}
