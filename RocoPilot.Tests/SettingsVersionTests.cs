using Microsoft.VisualStudio.TestTools.UnitTesting;

using RocoPilot.ViewModels;

namespace RocoPilot.Tests;

[TestClass]
public sealed class SettingsVersionTests
{
    [TestMethod]
    public void FormatsFourPartAppVersion()
    {
        Assert.AreEqual(
            "v0.3.3.1",
            SettingsViewModel.FormatAppVersion(new Version(0, 3, 3, 1)));
    }

    [TestMethod]
    public void FillsMissingRevisionWithZero()
    {
        Assert.AreEqual(
            "v0.3.3.0",
            SettingsViewModel.FormatAppVersion(new Version(0, 3, 3)));
    }
}
