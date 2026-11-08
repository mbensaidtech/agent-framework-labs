using LabDashboard.Catalog;
using LabDashboard.History;

namespace LabDashboard.Execution;

public enum ManualValidationOutcome
{
    /// <summary>The history changed: the scenario counts as passed now, or its real result is back.</summary>
    Changed,

    /// <summary>Nothing to change: already passed or validated, or no validation to remove.</summary>
    Unchanged,

    /// <summary>The exercise has no such scenario.</summary>
    UnknownScenario,
}

/// <summary>
/// Validation by hand of a scenario of the exercise: the learner considers it correct, whatever its last result (failed, or no result
/// yet). On a failed result, it is written on the scenario result in the history (<see cref="ScenarioResult.ValidatedAt"/>), never on the
/// result of the checks, so removing it gives the real result back. A scenario without a result gets a <see cref="RunRecord.Manual"/>
/// record, removed with the validation. The runs and their checks are left as they are.
/// </summary>
public static class ManualValidation
{
    /// <summary>
    /// Validates a scenario of the exercise (Start project), <paramref name="scenarios"/> being the scenarios of its program now: its last
    /// result when it failed, a manual record when it has no result.
    /// </summary>
    public static async Task<ManualValidationOutcome> ValidateAsync(
        RunHistoryStore history, string labId, IReadOnlyList<CommonUtilities.LabScenario> scenarios, int number, DateTimeOffset now)
    {
        if (ExerciseProgress.LastScenarioResults(history.ForLab(labId), RunTarget.Start).TryGetValue(number, out var last))
        {
            if (last.Status == ScenarioStatus.Passed)
            {
                return ManualValidationOutcome.Unchanged;
            }

            int changed = await history.UpdateLabAsync(labId, run => run.RunId != last.Run.RunId ? run : WithScenario(run, number, now));
            return changed > 0 ? ManualValidationOutcome.Changed : ManualValidationOutcome.Unchanged;
        }

        if (scenarios.FirstOrDefault(s => s.Number == number) is not { } scenario)
        {
            return ManualValidationOutcome.UnknownScenario;
        }

        await history.AddAsync(new RunRecord
        {
            RunId = $"manual-{Guid.NewGuid():N}",
            LabId = labId,
            Target = RunTarget.Start,
            Status = RunStatus.Passed,
            Manual = true,
            Summary = $"Scenario {number} validated by hand",
            StartedAt = now,
            DurationMs = 0,
            Scenarios = scenarios
                .Select(s => s.Number == number
                    ? new ScenarioResult(s.Number, s.Title, ScenarioStatus.Passed) { ValidatedAt = now }
                    : new ScenarioResult(s.Number, s.Title, ScenarioStatus.Skipped))
                .ToList(),
        });
        return ManualValidationOutcome.Changed;
    }

    /// <summary>
    /// Removes every validation by hand of a scenario on the exercise: each run of the history counts again with the result of its checks,
    /// and the manual records of that scenario are deleted.
    /// </summary>
    public static async Task<ManualValidationOutcome> RemoveAsync(RunHistoryStore history, string labId, int number)
    {
        int changed = await history.UpdateLabAsync(labId, run =>
            run.Target != RunTarget.Start || run.Scenarios?.Any(s => s.Number == number && s.ValidatedAt is not null) != true ? run
            : run.Manual ? null
            : WithScenario(run, number, null));
        return changed > 0 ? ManualValidationOutcome.Changed : ManualValidationOutcome.Unchanged;
    }

    private static RunRecord WithScenario(RunRecord run, int number, DateTimeOffset? validatedAt) => run with
    {
        Scenarios = run.Scenarios?
            .Select(s => s.Number == number && (validatedAt is null || ExerciseProgress.CanValidateManually(run, s)) ? s with { ValidatedAt = validatedAt } : s)
            .ToList(),
    };
}
