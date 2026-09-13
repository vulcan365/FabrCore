using FabrCore.Services.Memory.EvalConsole;

namespace FabrCore.Services.Memory.Tests.Unit;

[TestClass]
public sealed class EvalConsoleTests
{
    [TestMethod]
    public void CompareDetectsRegression()
    {
        Assert.HasCount(1, ReportComparison.Regressions(Report(true), Report(false)));
        Assert.IsEmpty(ReportComparison.Regressions(Report(false), Report(true)));
    }

    [TestMethod]
    public void CompareRejectsIncompleteOrDifferentScoring()
    {
        var baseline = Report(true);
        var candidate = Report(true); candidate.Status = "running";
        Assert.Throws<ArgumentException>(() => ReportComparison.Regressions(baseline, candidate));
        candidate.Status = "complete"; candidate.Mode = "harness";
        Assert.Throws<ArgumentException>(() => ReportComparison.Regressions(baseline, candidate));
        candidate.Mode = "code"; candidate.CorpusHash = "different";
        Assert.Throws<ArgumentException>(() => ReportComparison.Regressions(baseline, candidate));
    }

    [TestMethod]
    public void CompareRejectsMissingOrDuplicateChecks()
    {
        var baseline = Report(true);
        var candidate = Report(true); candidate.Checks.Clear();
        Assert.Throws<ArgumentException>(() => ReportComparison.Regressions(baseline, candidate));
        candidate.Checks.Add(new("other", "fact", true, 0, ""));
        Assert.Throws<ArgumentException>(() => ReportComparison.Regressions(baseline, candidate));
        candidate = Report(true); candidate.Checks.Add(candidate.Checks[0]);
        Assert.Throws<ArgumentException>(() => ReportComparison.Regressions(baseline, candidate));
    }

    private static RunReport Report(bool passed) => new() {
        Status = "complete", Mode = "code", CorpusHash = "fixed", Iterations = 1,
        Checks = [new("1:fact", "fact", passed, 1, "evidence")]
    };
}
