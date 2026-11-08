using LabDashboard.Catalog;
using LabDashboard.Execution;
using LabDashboard.History;
using LabDashboard.Journey;
using LabDashboard.Reporting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabDashboard.Tests;

/// <summary>
/// Validation by hand of a failed scenario: it counts as passed everywhere (progress, journey, snapshot sent to the admin dashboard),
/// the result of its checks is kept, and removing the validation gives that result back.
/// </summary>
public sealed class ManualValidationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("labbench-validation-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static LabDefinition Lab(params int[] bonus) => new()
    {
        Id = "lab",
        Number = "01",
        Track = "Azure OpenAI",
        Title = "Lab",
        Summary = "",
        Level = "Beginner",
        Path = "Labs/lab",
        StartProject = "Start/Start.csproj",
        SolutionProject = "Solution/Solution.csproj",
        BonusScenarios = bonus,
    };

    /// <summary>A run of the 3 scenarios of the lab; a scenario not listed was not selected (skipped).</summary>
    private static RunRecord Run(int minute, RunStatus status, string? stage, params (int Number, ScenarioStatus Status)[] results) => new()
    {
        RunId = $"r{minute}",
        LabId = "lab",
        Target = RunTarget.Start,
        Status = status,
        FailureStage = stage,
        Summary = "",
        StartedAt = T0.AddMinutes(minute),
        DurationMs = 1,
        Scenarios = Enumerable.Range(1, 3)
            .Select(n => new ScenarioResult(n, $"S{n}", results.FirstOrDefault(r => r.Number == n) is { Number: > 0 } r ? r.Status : ScenarioStatus.Skipped))
            .ToList(),
    };

    private static (int, ScenarioStatus) P(int number) => (number, ScenarioStatus.Passed);
    private static (int, ScenarioStatus) F(int number) => (number, ScenarioStatus.Failed);

    private RunHistoryStore History() =>
        new(new DashboardOptions { DataDirectory = "data" }, new HostingEnvironment { ContentRootPath = _root }, NullLogger<RunHistoryStore>.Instance);

    [Fact]
    public async Task A_failed_scenario_validated_by_hand_counts_as_passed_and_keeps_the_result_of_its_checks()
    {
        RunHistoryStore history = History();
        await history.AddAsync(Run(1, RunStatus.Failed, "checks", P(1), P(2), F(3)));
        Assert.Null(ExerciseProgress.CompletingRun(history.ForLab("lab")));

        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.ValidateAsync(history, "lab", 3, T0.AddHours(1)));

        RunRecord run = Assert.Single(history.ForLab("lab"));
        ScenarioResult scenario = run.Scenarios![2];
        // The result of the checks is kept: only the status the scenario and the run count with changes.
        Assert.Equal(ScenarioStatus.Failed, scenario.Status);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(T0.AddHours(1), scenario.ValidatedAt);
        Assert.Equal(ScenarioStatus.Passed, ExerciseProgress.EffectiveStatus(scenario));
        Assert.Equal(RunStatus.Passed, ExerciseProgress.EffectiveStatus(run));
        Assert.Equal(ScenarioStatus.Passed, ExerciseProgress.LastScenarioResults(history.ForLab("lab"), RunTarget.Start)[3].Status);

        // The lab is completed exactly as if the checks had passed: lab list, journey (first try) and the snapshot for the admin dashboard.
        Assert.Equal("r1", ExerciseProgress.CompletingRun(history.ForLab("lab"))?.RunId);
        LabJourney journey = LearningJourney.ForLab(Lab(), history.ForLab("lab"), running: false);
        Assert.Equal(JourneyState.Completed, journey.State);
        Assert.True(journey.FirstTry);
        LabProgressSnapshot snapshot = ProgressSnapshot.ForLab("lab", history.ForLab("lab"));
        Assert.Equal("completed", snapshot.State);
        Assert.Equal(RunStatus.Passed, snapshot.LastRunStatus);
        Assert.Equal(T0.AddMinutes(1), snapshot.CompletedAt);

        // Persistent: a new store reads it from the file.
        Assert.Equal(T0.AddHours(1), History().ForLab("lab")[0].Scenarios![2].ValidatedAt);
    }

    [Fact]
    public async Task Removing_the_validation_gives_the_real_result_back()
    {
        RunHistoryStore history = History();
        await history.AddAsync(Run(1, RunStatus.Failed, "checks", P(1), P(2), F(3)));
        await ManualValidation.ValidateAsync(history, "lab", 3, T0);

        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.RemoveAsync(history, "lab", 3));
        Assert.Equal(ManualValidationOutcome.Unchanged, await ManualValidation.RemoveAsync(history, "lab", 3));

        RunRecord run = Assert.Single(History().ForLab("lab"));
        Assert.Null(run.Scenarios![2].ValidatedAt);
        Assert.Equal(RunStatus.Failed, ExerciseProgress.EffectiveStatus(run));
        Assert.Equal(ScenarioStatus.Failed, ExerciseProgress.LastScenarioResults([run], RunTarget.Start)[3].Status);
        Assert.Null(ExerciseProgress.CompletingRun([run]));
        Assert.Equal("inProgress", ProgressSnapshot.ForLab("lab", [run]).State);
    }

    [Fact]
    public async Task Removing_the_validation_of_a_scenario_that_passed_since_keeps_it_passed()
    {
        RunHistoryStore history = History();
        await history.AddAsync(Run(1, RunStatus.Failed, "checks", P(1), P(2), F(3)));
        await ManualValidation.ValidateAsync(history, "lab", 3, T0);
        await history.AddAsync(Run(2, RunStatus.Passed, null, P(3)));

        await ManualValidation.RemoveAsync(history, "lab", 3);

        IReadOnlyList<RunRecord> runs = history.ForLab("lab");
        Assert.Equal(ScenarioStatus.Passed, ExerciseProgress.LastScenarioResults(runs, RunTarget.Start)[3].Status);
        Assert.Equal("r2", ExerciseProgress.CompletingRun(runs)?.RunId);
    }

    [Fact]
    public async Task Only_a_failure_of_the_output_checks_of_the_exercise_can_be_validated()
    {
        RunHistoryStore history = History();
        Assert.Equal(ManualValidationOutcome.NoResult, await ManualValidation.ValidateAsync(history, "lab", 1, T0));

        // A crash (failure stage "run"): not a difference of output.
        await history.AddAsync(Run(1, RunStatus.Failed, "run", P(1), F(2)));
        Assert.Equal(ManualValidationOutcome.NotEligible, await ManualValidation.ValidateAsync(history, "lab", 2, T0));

        // A scenario that passed needs nothing; one that never ran has no result.
        await history.AddAsync(Run(2, RunStatus.Failed, "checks", P(1), F(2)) with { Scenarios = [
            new ScenarioResult(1, "S1", ScenarioStatus.Passed),
            new ScenarioResult(2, "S2", ScenarioStatus.Failed),
            new ScenarioResult(3, "S3", ScenarioStatus.NotRun)] });
        Assert.Equal(ManualValidationOutcome.Unchanged, await ManualValidation.ValidateAsync(history, "lab", 1, T0));
        Assert.Equal(ManualValidationOutcome.NoResult, await ManualValidation.ValidateAsync(history, "lab", 3, T0));

        // A solution run is never validated, and its results are not those of the exercise.
        RunRecord solution = Run(3, RunStatus.Failed, "checks", F(1)) with { Target = RunTarget.Solution };
        Assert.False(ExerciseProgress.CanValidateManually(solution, solution.Scenarios![0]));

        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.ValidateAsync(history, "lab", 2, T0));
        Assert.Equal(ManualValidationOutcome.Unchanged, await ManualValidation.ValidateAsync(history, "lab", 2, T0));
        // The run of scenarios 1 and 2 still lists scenario 3 as not run: the run does not count as passed.
        Assert.Equal(RunStatus.Failed, ExerciseProgress.EffectiveStatus(history.ForLab("lab")[0]));
    }

    [Fact]
    public async Task A_bonus_scenario_validated_by_hand_completes_the_bonus()
    {
        RunHistoryStore history = History();
        await history.AddAsync(Run(1, RunStatus.Failed, "checks", P(1), P(2), F(3)));
        Assert.Equal("r1", ExerciseProgress.CompletingRun(history.ForLab("lab"), [3])?.RunId);
        Assert.Null(ExerciseProgress.BonusCompletingRun(history.ForLab("lab"), [3]));

        await ManualValidation.ValidateAsync(history, "lab", 3, T0);

        Assert.Equal("r1", ExerciseProgress.BonusCompletingRun(history.ForLab("lab"), [3])?.RunId);
        Assert.True(LearningJourney.ForLab(Lab(3), history.ForLab("lab"), running: false).BonusCompleted);
    }
}
