using LabDashboard.Catalog;
using LabDashboard.Execution;
using LabDashboard.History;
using LabDashboard.Journey;
using LabDashboard.Reporting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabDashboard.Tests;

/// <summary>
/// Validation by hand of a scenario not passed yet (failed for any reason, or without a result): it counts as passed everywhere (progress,
/// journey, snapshot sent to the admin dashboard), the result of its checks is kept, and removing the validation gives that result back.
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

    /// <summary>The 3 scenarios of the Program.cs of the exercise.</summary>
    private static readonly IReadOnlyList<CommonUtilities.LabScenario> Program =
        [.. Enumerable.Range(1, 3).Select(n => new CommonUtilities.LabScenario(n, $"S{n}", []))];

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

        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.ValidateAsync(history, "lab", Program, 3, T0.AddHours(1)));

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
        await ManualValidation.ValidateAsync(history, "lab", Program, 3, T0);

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
        await ManualValidation.ValidateAsync(history, "lab", Program, 3, T0);
        await history.AddAsync(Run(2, RunStatus.Passed, null, P(3)));

        await ManualValidation.RemoveAsync(history, "lab", 3);

        IReadOnlyList<RunRecord> runs = history.ForLab("lab");
        Assert.Equal(ScenarioStatus.Passed, ExerciseProgress.LastScenarioResults(runs, RunTarget.Start)[3].Status);
        Assert.Equal("r2", ExerciseProgress.CompletingRun(runs)?.RunId);
    }

    [Fact]
    public async Task Any_failure_of_the_exercise_can_be_validated_but_not_a_scenario_already_passed()
    {
        RunHistoryStore history = History();

        // A crash (failure stage "run", with its error) and a timeout: the learner may consider the scenario correct anyway.
        await history.AddAsync(Run(1, RunStatus.Failed, "run", P(1), F(2)) with { Scenarios = [
            new ScenarioResult(1, "S1", ScenarioStatus.Passed),
            new ScenarioResult(2, "S2", ScenarioStatus.Failed) { Errors = ["boom"] },
            new ScenarioResult(3, "S3", ScenarioStatus.Skipped)] });
        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.ValidateAsync(history, "lab", Program, 2, T0));
        await history.AddAsync(Run(2, RunStatus.TimedOut, null, F(3)));
        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.ValidateAsync(history, "lab", Program, 3, T0));
        Assert.Equal(RunStatus.Passed, ExerciseProgress.EffectiveStatus(history.ForLab("lab")[0]));
        Assert.Equal("r2", ExerciseProgress.CompletingRun(history.ForLab("lab"))?.RunId);

        // A scenario that passed (or is validated already) needs nothing; a scenario not in the exercise cannot be validated.
        Assert.Equal(ManualValidationOutcome.Unchanged, await ManualValidation.ValidateAsync(history, "lab", Program, 1, T0));
        Assert.Equal(ManualValidationOutcome.Unchanged, await ManualValidation.ValidateAsync(history, "lab", Program, 2, T0));
        Assert.Equal(ManualValidationOutcome.UnknownScenario, await ManualValidation.ValidateAsync(history, "lab", Program, 9, T0));
        Assert.Equal(2, history.ForLab("lab").Count);

        // A solution run is never validated, and its results are not those of the exercise.
        RunRecord solution = Run(3, RunStatus.Failed, "checks", F(1)) with { Target = RunTarget.Solution };
        Assert.False(ExerciseProgress.CanValidateManually(solution, solution.Scenarios![0]));
    }

    [Fact]
    public async Task A_scenario_never_run_is_validated_with_a_manual_record_hidden_from_the_runs()
    {
        RunHistoryStore history = History();
        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.ValidateAsync(history, "lab", Program, 1, T0));

        RunRecord manual = Assert.Single(history.ForLab("lab"));
        Assert.True(manual.Manual);
        Assert.Equal(T0, manual.Scenarios![0].ValidatedAt);
        Assert.All(manual.Scenarios.Skip(1), s => Assert.Equal(ScenarioStatus.Skipped, s.Status));
        Assert.Equal(ScenarioStatus.Passed, ExerciseProgress.LastScenarioResults([manual], RunTarget.Start)[1].Status);
        // One scenario of three: the exercise is started, not completed, and the record is no run.
        Assert.Null(ExerciseProgress.CompletingRun([manual]));
        LabProgressSnapshot snapshot = ProgressSnapshot.ForLab("lab", [manual]);
        Assert.Equal("inProgress", snapshot.State);
        Assert.Equal(0, snapshot.RunCount);
        Assert.Null(snapshot.LastRunAt);
        LabJourney journey = LearningJourney.ForLab(Lab(), [manual], running: false);
        Assert.Equal(JourneyState.InProgress, journey.State);
        Assert.Equal(0, journey.Attempts);

        // A scenario stopped before it ran (not run) has no result either: a manual record too.
        await history.AddAsync(Run(1, RunStatus.Failed, "build", (2, ScenarioStatus.NotRun), (3, ScenarioStatus.NotRun)));
        await ManualValidation.ValidateAsync(history, "lab", Program, 2, T0.AddMinutes(2));
        await ManualValidation.ValidateAsync(history, "lab", Program, 3, T0.AddMinutes(3));
        IReadOnlyList<RunRecord> runs = History().ForLab("lab");
        Assert.Equal(3, runs.Count(r => r.Manual));
        RunRecord? completing = ExerciseProgress.CompletingRun(runs);
        Assert.True(completing?.Manual);
        Assert.Equal(JourneyState.Completed, LearningJourney.ForLab(Lab(), runs, running: false).State);
        Assert.Equal(1, ProgressSnapshot.ForLab("lab", runs).RunCount);

        // Cancelling deletes the manual record: the scenario has no result again.
        Assert.Equal(ManualValidationOutcome.Changed, await ManualValidation.RemoveAsync(history, "lab", 3));
        Assert.False(ExerciseProgress.LastScenarioResults(history.ForLab("lab"), RunTarget.Start).ContainsKey(3));
        Assert.Equal(2, history.ForLab("lab").Count(r => r.Manual));
        Assert.Null(ExerciseProgress.CompletingRun(history.ForLab("lab")));
    }

    [Fact]
    public async Task A_bonus_scenario_validated_by_hand_completes_the_bonus()
    {
        RunHistoryStore history = History();
        await history.AddAsync(Run(1, RunStatus.Failed, "checks", P(1), P(2), F(3)));
        Assert.Equal("r1", ExerciseProgress.CompletingRun(history.ForLab("lab"), [3])?.RunId);
        Assert.Null(ExerciseProgress.BonusCompletingRun(history.ForLab("lab"), [3]));

        await ManualValidation.ValidateAsync(history, "lab", Program, 3, T0);

        Assert.Equal("r1", ExerciseProgress.BonusCompletingRun(history.ForLab("lab"), [3])?.RunId);
        Assert.True(LearningJourney.ForLab(Lab(3), history.ForLab("lab"), running: false).BonusCompleted);
    }
}
