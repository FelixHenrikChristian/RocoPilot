using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.Services;

namespace RocoPilot.Tests;

[TestClass]
public sealed class EncounterCaptureButtonStateTrackerTests
{
    [TestMethod]
    public void ClassifiesVisibleButtonOnlyByDisabledMarker()
    {
        Assert.AreEqual(
            EncounterCaptureButtonState.Enabled,
            EncounterCaptureButtonRecognition.Classify(0.96, 0.89, 0.74));
        Assert.AreEqual(
            EncounterCaptureButtonState.Disabled,
            EncounterCaptureButtonRecognition.Classify(0.90, 0.97, 0.96));
    }

    [TestMethod]
    public void ClassifiesRealEnabledButtonWhenDisabledOutlineScoresHigher()
    {
        Assert.AreEqual(
            EncounterCaptureButtonState.Enabled,
            EncounterCaptureButtonRecognition.Classify(0.898, 0.916, 0.737));
    }

    [TestMethod]
    public void FullButtonTemplateWinnerDoesNotDetermineState()
    {
        Assert.AreEqual(
            EncounterCaptureButtonState.Enabled,
            EncounterCaptureButtonRecognition.Classify(0.87, 0.93, 0.70));
        Assert.AreEqual(
            EncounterCaptureButtonState.Disabled,
            EncounterCaptureButtonRecognition.Classify(0.93, 0.87, 0.95));
    }

    [TestMethod]
    [DataRow(0.899160, 0.885388, 0.651184, "Enabled")]
    [DataRow(0.850437, 0.948442, 0.932972, "Disabled")]
    [DataRow(0.867888, 0.966959, 0.943713, "Disabled")]
    [DataRow(0.915796, 0.905370, 0.701815, "Enabled")]
    public void ClassifiesCaptured1600x900Samples(
        double enabledScore,
        double disabledScore,
        double disabledMarkerScore,
        string expected)
    {
        Assert.AreEqual(
            Enum.Parse<EncounterCaptureButtonState>(expected),
            EncounterCaptureButtonRecognition.Classify(
                enabledScore,
                disabledScore,
                disabledMarkerScore));
    }

    [TestMethod]
    public void RejectsInvisibleButtonOrAmbiguousDisabledMarker()
    {
        Assert.AreEqual(
            EncounterCaptureButtonState.Unknown,
            EncounterCaptureButtonRecognition.Classify(0.87, 0.86, 0.97));
        Assert.AreEqual(
            EncounterCaptureButtonState.Unknown,
            EncounterCaptureButtonRecognition.Classify(0.94, 0.93, 0.85));
        Assert.AreEqual(
            EncounterCaptureButtonState.Enabled,
            EncounterCaptureButtonRecognition.Classify(0.879, 0.91, 0.70));
        Assert.AreEqual(
            EncounterCaptureButtonState.Disabled,
            EncounterCaptureButtonRecognition.Classify(0.90, 0.919, 0.95));
    }

    [TestMethod]
    public void RequiresDisabledToEnabledTransition()
    {
        var tracker = new EncounterCaptureButtonStateTracker();

        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled));
        Assert.AreEqual(EncounterCaptureButtonState.Enabled, tracker.CurrentState);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Unknown));
        Assert.AreEqual(EncounterCaptureButtonState.Unknown, tracker.CurrentState);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Disabled));
        Assert.IsTrue(tracker.HasSeenDisabled);
        Assert.AreEqual(EncounterCaptureButtonState.Disabled, tracker.CurrentState);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled));
        Assert.AreEqual(1, tracker.EnabledConfirmationCount);
        Assert.IsTrue(tracker.ShouldHoldAttackForUnconfirmedRelief);
        Assert.IsTrue(tracker.Observe(EncounterCaptureButtonState.Enabled));
        Assert.AreEqual(
            EncounterCaptureButtonStateTracker.RequiredEnabledConfirmationCount,
            tracker.EnabledConfirmationCount);
        Assert.IsTrue(tracker.IsRelieved);
        Assert.AreEqual(EncounterCaptureButtonState.Enabled, tracker.CurrentState);
        Assert.IsFalse(tracker.ShouldHoldAttackForUnconfirmedRelief);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled));
    }

    [TestMethod]
    public void ResetStartsANewBattleTransition()
    {
        var tracker = new EncounterCaptureButtonStateTracker();
        tracker.Observe(EncounterCaptureButtonState.Disabled);
        tracker.Observe(EncounterCaptureButtonState.Enabled);
        tracker.Observe(EncounterCaptureButtonState.Enabled);

        tracker.Reset();

        Assert.IsFalse(tracker.HasSeenDisabled);
        Assert.IsFalse(tracker.IsRelieved);
        Assert.AreEqual(EncounterCaptureButtonState.Unknown, tracker.CurrentState);
        Assert.AreEqual(0, tracker.EnabledConfirmationCount);
        Assert.IsFalse(tracker.Observe(EncounterCaptureButtonState.Enabled));
    }

    [TestMethod]
    public void UnknownAfterDisabledPreservesEncounterButMarksCurrentFrameAsUncertain()
    {
        var tracker = new EncounterCaptureButtonStateTracker();

        tracker.Observe(EncounterCaptureButtonState.Disabled);
        Assert.IsFalse(tracker.ShouldHoldAttackForUnconfirmedRelief);
        tracker.Observe(EncounterCaptureButtonState.Unknown);

        Assert.IsTrue(tracker.HasSeenDisabled);
        Assert.IsFalse(tracker.IsRelieved);
        Assert.AreEqual(EncounterCaptureButtonState.Unknown, tracker.CurrentState);
        Assert.IsTrue(tracker.ShouldHoldAttackForUnconfirmedRelief);

        tracker.Observe(EncounterCaptureButtonState.Enabled);

        Assert.IsTrue(tracker.ShouldHoldAttackForUnconfirmedRelief);
        tracker.Observe(EncounterCaptureButtonState.Enabled);
        Assert.IsFalse(tracker.ShouldHoldAttackForUnconfirmedRelief);
    }
}
