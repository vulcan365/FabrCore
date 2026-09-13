namespace FabrCore.Services.Memory.EvalConsole;

internal static class ReportComparison
{
    public static IReadOnlyList<string> Regressions(RunReport baseline, RunReport candidate)
    {
        if (baseline.CorpusHash != candidate.CorpusHash || baseline.BenchmarkVersion != candidate.BenchmarkVersion || baseline.Iterations != candidate.Iterations)
            throw new ArgumentException("Incompatible corpus, benchmark version, or iteration count.");
        if (baseline.Status != "complete" || candidate.Status != "complete") throw new ArgumentException("Cannot compare incomplete runs.");
        if (baseline.Checks.Count == 0 || candidate.Checks.Count == 0
            || baseline.Checks.Select(c => c.Id).Distinct().Count() != baseline.Checks.Count
            || candidate.Checks.Select(c => c.Id).Distinct().Count() != candidate.Checks.Count)
            throw new ArgumentException("Empty or duplicate check inventory.");
        if ((baseline.Mode == "harness") != (candidate.Mode == "harness")) throw new ArgumentException("Harness answer scores cannot be compared with retrieval scores.");
        if (!baseline.Checks.Select(c => c.Id).Order().SequenceEqual(candidate.Checks.Select(c => c.Id).Order())) throw new ArgumentException("Check inventories differ.");
        return candidate.Checks.Where(c => !c.Passed && baseline.Checks.Any(old => old.Id == c.Id && old.Passed)).Select(c => c.Id).ToList();
    }
}
