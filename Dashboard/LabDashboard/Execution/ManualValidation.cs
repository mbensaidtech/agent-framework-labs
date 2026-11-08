using LabDashboard.Catalog;
using LabDashboard.History;

namespace LabDashboard.Execution;

public enum ManualValidationOutcome
{
    /// <summary>The history changed: the scenario counts as passed now, or its real result is back.</summary>
    Changed,

    /// <summary>Nothing to change: already validated, or no validation to remove.</summary>
    Unchanged,

    /// <summary>The scenario has no result on the exercise yet: nothing to validate.</summary>
    NoResult,

    /// <summary>Its last result is not a failure of the output checks (<see cref="ExerciseProgress.CanValidateManually"/>).</summary>
    NotEligible,
}

/// <summary>
/// Validation by hand of a scenario of the exercise: the learner accepts a failed result whose output differs from the reference in a
/// minor way. It is written on the scenario result in the history (<see cref="ScenarioResult.ValidatedAt"/>), never on the result of
/// the checks, so removing it gives the real result back. The runs and the checks are left as they are.
/// </summary>
public static class ManualValidation
{
    /// <summary>Validates the last result of a scenario on the exercise (Start project), when it is a failure of the output checks.</summary>
    public static async Task<ManualValidationOutcome> ValidateAsync(RunHistoryStore history, string labId, int number, DateTimeOffset now)
    {
        if (!ExerciseProgress.LastScenarioResults(history.ForLab(labId), RunTarget.Start).TryGetValue(number, out var last))
        {
            return ManualValidationOutcome.NoResult;
        }

        if (last.Scenario.ValidatedAt is not null || last.Scenario.Status == ScenarioStatus.Passed)
        {
            return ManualValidationOutcome.Unchanged;
        }

        if (!ExerciseProgress.CanValidateManually(last.Run, last.Scenario))
        {
            return ManualValidationOutcome.NotEligible;
        }

        int changed = await history.UpdateLabAsync(labId, run => run.RunId != last.Run.RunId ? run : WithScenario(run, number, now));
        return changed > 0 ? ManualValidationOutcome.Changed : ManualValidationOutcome.NoResult;
    }

    /// <summary>
    /// Removes every validation by hand of a scenario on the exercise: each run of the history counts again with the result of its checks.
    /// </summary>
    public static async Task<ManualValidationOutcome> RemoveAsync(RunHistoryStore history, string labId, int number)
    {
        int changed = await history.UpdateLabAsync(labId, run =>
            run.Target == RunTarget.Start && run.Scenarios?.Any(s => s.Number == number && s.ValidatedAt is not null) == true
                ? WithScenario(run, number, null)
                : run);
        return changed > 0 ? ManualValidationOutcome.Changed : ManualValidationOutcome.Unchanged;
    }

    private static RunRecord WithScenario(RunRecord run, int number, DateTimeOffset? validatedAt) => run with
    {
        Scenarios = run.Scenarios?
            .Select(s => s.Number == number && (validatedAt is null || ExerciseProgress.CanValidateManually(run, s)) ? s with { ValidatedAt = validatedAt } : s)
            .ToList(),
    };
}
