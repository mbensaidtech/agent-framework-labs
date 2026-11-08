using LabDashboard.Catalog;

namespace LabDashboard.Execution;

/// <summary>
/// When an exercise is completed, from its Start runs: the rule of the lab list, the journey and the progress sent to the trainer.
/// A run with a scenario selection validates the scenarios that passed in it, and the exercise is completed once every scenario
/// passed (in one run or across several): a learner never has to run again the scenarios already validated.
/// A run without scenario results (made before the selection existed, or of a lab without scenarios) completes it when it passed.
/// <para>
/// <b>Bonus scenarios</b> (<see cref="LabDefinition.BonusScenarios"/>) are optional: the exercise is completed once every <i>other</i>
/// scenario passed, and completed with its bonus ("Done+") once every scenario, bonus included, passed. A bonus scenario that fails does
/// not undo the exercise: an attempt whose only failures are bonus scenarios passed the exercise (<see cref="PassedExercise"/>).
/// A run without scenario results never completes a bonus (nothing tells which scenarios it ran).
/// </para>
/// <para>
/// <b>Manual validation</b>: a scenario that the learner validated by hand (<see cref="ScenarioResult.ValidatedAt"/>) counts as passed
/// everywhere (<see cref="IsValidated"/>, <see cref="EffectiveStatus"/>), exactly as if its checks had passed: the result of the checks
/// (<see cref="ScenarioResult.Status"/>, <see cref="RunRecord.Status"/>) is kept as it was. A scenario validated before it had a result
/// is a <see cref="RunRecord.Manual"/> record: an attempt for the progress, never counted as a run (<see cref="IsRun"/>).
/// </para>
/// </summary>
public static class ExerciseProgress
{
    /// <summary>Whether a scenario counts as passed: its checks passed, or it failed and the learner validated it by hand.</summary>
    public static bool IsValidated(ScenarioResult scenario) =>
        scenario.Status == ScenarioStatus.Passed || (scenario.Status == ScenarioStatus.Failed && scenario.ValidatedAt is not null);

    /// <summary>A real run (not the <see cref="RunRecord.Manual"/> record of a validation by hand): what the history and the counts show.</summary>
    public static bool IsRun(RunRecord run) => !run.Manual;

    /// <summary>The status a scenario counts with: <see cref="ScenarioStatus.Passed"/> when validated by hand, its own status otherwise.</summary>
    public static ScenarioStatus EffectiveStatus(ScenarioResult scenario) => IsValidated(scenario) ? ScenarioStatus.Passed : scenario.Status;

    /// <summary>
    /// The status a run counts with: <see cref="RunStatus.Passed"/> when it failed (or timed out) only on scenarios the learner validated
    /// by hand (every selected scenario passed or was validated), its own status otherwise.
    /// </summary>
    public static RunStatus EffectiveStatus(RunRecord run) =>
        run.Status is RunStatus.Failed or RunStatus.TimedOut && run.Scenarios is { Count: > 0 } scenarios && scenarios.Any(s => s.ValidatedAt is not null)
            && scenarios.Where(s => s.Status != ScenarioStatus.Skipped).All(IsValidated)
            ? RunStatus.Passed
            : run.Status;

    /// <summary>
    /// Whether the learner may validate a scenario result of a run by hand: a failed scenario of an exercise (Start) run, whatever made
    /// it fail (output checks, crash, timeout). A scenario without a result (never run, or stopped before it ran) is validated with a
    /// <see cref="RunRecord.Manual"/> record instead (<see cref="ManualValidation"/>).
    /// </summary>
    public static bool CanValidateManually(RunRecord run, ScenarioResult scenario) =>
        run.Target == RunTarget.Start && scenario.Status == ScenarioStatus.Failed;

    /// <summary>The attempts, oldest first: the Start runs that went to the end (a cancelled run proves nothing either way).</summary>
    public static RunRecord[] Attempts(IEnumerable<RunRecord> runsNewestFirst) =>
        runsNewestFirst.Where(r => r.Target == RunTarget.Start && r.Status is RunStatus.Passed or RunStatus.Failed or RunStatus.TimedOut)
            .Reverse()
            .ToArray();

    /// <summary>The index of the attempt that completed the exercise (every scenario but the bonus ones passed), or -1.</summary>
    public static int CompletingAttempt(IReadOnlyList<RunRecord> attemptsOldestFirst, IReadOnlyCollection<int>? bonus = null) =>
        FirstAttemptValidating(attemptsOldestFirst, number => bonus?.Contains(number) != true, legacyRunCounts: true);

    /// <summary>
    /// The index of the attempt that completed the exercise with its bonus (every scenario passed, bonus included), or -1;
    /// always -1 for a lab without bonus scenarios.
    /// </summary>
    public static int BonusCompletingAttempt(IReadOnlyList<RunRecord> attemptsOldestFirst, IReadOnlyCollection<int>? bonus)
    {
        if (bonus is not { Count: > 0 })
        {
            return -1;
        }

        int completing = CompletingAttempt(attemptsOldestFirst, bonus);
        int allScenarios = FirstAttemptValidating(attemptsOldestFirst, _ => true, legacyRunCounts: false);
        return completing < 0 || allScenarios < 0 ? -1 : Math.Max(completing, allScenarios);
    }

    /// <summary>The run that completed the exercise, or null.</summary>
    public static RunRecord? CompletingRun(IEnumerable<RunRecord> runsNewestFirst, IReadOnlyCollection<int>? bonus = null)
    {
        RunRecord[] attempts = Attempts(runsNewestFirst);
        int index = CompletingAttempt(attempts, bonus);
        return index < 0 ? null : attempts[index];
    }

    /// <summary>The run that completed the exercise with its bonus scenarios ("Done+"), or null.</summary>
    public static RunRecord? BonusCompletingRun(IEnumerable<RunRecord> runsNewestFirst, IReadOnlyCollection<int>? bonus)
    {
        RunRecord[] attempts = Attempts(runsNewestFirst);
        int index = BonusCompletingAttempt(attempts, bonus);
        return index < 0 ? null : attempts[index];
    }

    /// <summary>
    /// Whether an attempt passed the exercise: it passed, or its only failures are bonus scenarios (a failed or not run bonus scenario,
    /// every other selected scenario passed). A failure outside the scenarios (build, configuration check) fails every scenario.
    /// </summary>
    public static bool PassedExercise(RunRecord run, IReadOnlyCollection<int>? bonus = null)
    {
        if (EffectiveStatus(run) == RunStatus.Passed)
        {
            return true;
        }

        if (bonus is not { Count: > 0 } || run.Scenarios is not { Count: > 0 } scenarios)
        {
            return false;
        }

        List<ScenarioResult> selected = scenarios.Where(s => s.Status != ScenarioStatus.Skipped).ToList();
        return selected.Any(s => IsValidated(s) && !bonus.Contains(s.Number))
            && selected.All(s => IsValidated(s) || bonus.Contains(s.Number));
    }

    /// <summary>
    /// The first attempt after which every scenario that <paramref name="counts"/> passed (in that attempt or in earlier ones).
    /// A run without scenario results validates everything when it passed, if <paramref name="legacyRunCounts"/>.
    /// </summary>
    private static int FirstAttemptValidating(IReadOnlyList<RunRecord> attemptsOldestFirst, Func<int, bool> counts, bool legacyRunCounts)
    {
        HashSet<int> validated = [];
        for (int i = 0; i < attemptsOldestFirst.Count; i++)
        {
            RunRecord run = attemptsOldestFirst[i];
            if (run.Scenarios is not { Count: > 0 } scenarios)
            {
                if (legacyRunCounts && run.Status == RunStatus.Passed)
                {
                    return i;
                }

                continue;
            }

            validated.UnionWith(scenarios.Where(IsValidated).Select(s => s.Number));
            if (scenarios.Where(s => counts(s.Number)).All(s => validated.Contains(s.Number)))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The last result of each scenario on a target (passed or failed), from the newest run where it ran: what the learner already
    /// tested. Cancelled runs and the scenarios that did not run are not results. <c>Status</c> is the status the scenario counts with
    /// (passed when validated by hand, see <see cref="EffectiveStatus(ScenarioResult)"/>); <c>Scenario</c> keeps the result of its checks.
    /// </summary>
    public static IReadOnlyDictionary<int, (ScenarioStatus Status, RunRecord Run, ScenarioResult Scenario)> LastScenarioResults(
        IEnumerable<RunRecord> runsNewestFirst, RunTarget target)
    {
        Dictionary<int, (ScenarioStatus, RunRecord, ScenarioResult)> last = [];
        foreach (RunRecord run in runsNewestFirst.Where(r => r.Target == target && r.Status != RunStatus.Cancelled))
        {
            foreach (ScenarioResult scenario in run.Scenarios ?? [])
            {
                if (scenario.Status is ScenarioStatus.Passed or ScenarioStatus.Failed)
                {
                    last.TryAdd(scenario.Number, (EffectiveStatus(scenario), run, scenario));
                }
            }
        }

        return last;
    }
}
