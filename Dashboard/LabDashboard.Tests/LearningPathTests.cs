using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LabDashboard.Catalog;
using LabDashboard.Reporting;

namespace LabDashboard.Tests;

/// <summary>
/// labs.json is the single source of truth of the learning path (its stages, and the order of its labs).
/// The Lab Bench, the admin dashboard (catalog.synced, published catalog), the README Learning Path and the training roadmap must agree with it.
/// </summary>
public partial class LearningPathTests
{
    private static readonly string RepositoryRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The path of the training, stage by stage (README Learning Path), as the exercise ids of the catalog.</summary>
    private static readonly (string Stage, string[] Labs)[] ExpectedPath =
    [
        ("build", ["azureopenai-lab01", "azureopenai-lab02", "azureopenai-lab03", "azureopenai-lab04"]),
        ("conversations", ["azureopenai-lab05", "azureopenai-lab09"]),
        ("knowledge", ["azureopenai-lab07", "azureopenai-lab12", "azureopenai-lab15"]),
        ("multi-agent", ["multiagent-lab01", "multiagent-lab02", "multiagent-lab03", "azureopenai-lab06-server", "azureopenai-lab06-client", "azureopenai-lab16-server", "azureopenai-lab16-client"]),
        ("production", ["azureopenai-lab11", "azureopenai-lab14", "azureopenai-lab13"]),
        ("further", ["azureopenai-lab10", "azureopenai-lab08"]),
    ];

    [Fact]
    public void The_real_catalog_lists_the_labs_stage_by_stage_in_the_order_of_the_training()
    {
        LabCatalog catalog = LabCatalogTests.LoadRealCatalog();

        Assert.Equal(ExpectedPath.Select(stage => stage.Stage), catalog.Stages.Select(stage => stage.Id));
        Assert.Equal(ExpectedPath.SelectMany(stage => stage.Labs), catalog.Labs.Select(lab => lab.Id));
        Assert.All(ExpectedPath, stage => Assert.Equal(stage.Labs, catalog.Labs.Where(lab => lab.Stage == stage.Stage).Select(lab => lab.Id)));
        // Nothing re-sorts the path: the lab list and the journey get the labs in the order of labs.json.
        Assert.Equal(catalog.Labs, catalog.LearningPath);

        // Five essential stages numbered 1 to 5, then the optional labs (no number), every stage translated in French.
        Assert.Equal([1, 2, 3, 4, 5, null], catalog.Stages.Select(stage => catalog.StageOf(catalog.Labs.First(lab => lab.Stage == stage.Id))!.Number));
        Assert.Equal(["further"], catalog.Stages.Where(stage => stage.Optional).Select(stage => stage.Id));
        Assert.All(catalog.Stages, stage => Assert.False(string.IsNullOrWhiteSpace(stage.Translations["fr"].Title)));
    }

    [Fact]
    public void The_catalog_sent_to_the_admin_dashboard_carries_the_stage_of_every_lab_in_the_order_of_the_path()
    {
        LabCatalog catalog = LabCatalogTests.LoadRealCatalog();

        JsonArray labs = ProgressSnapshot.Catalog(catalog)["labs"]!.AsArray();

        Assert.Equal(catalog.Labs.Select(lab => lab.Id), labs.Select(lab => (string)lab!["id"]!));
        Assert.Equal(Enumerable.Range(0, labs.Count), labs.Select(lab => (int)lab!["sortOrder"]!));
        JsonNode first = labs[0]!["stage"]!;
        Assert.Equal(("build", "Build an agent", "Construire un agent"), ((string)first["id"]!, (string)first["title"]!, (string)first["translations"]!["fr"]!["title"]!));
        Assert.Null(first["optional"]);
        Assert.True((bool)labs[^1]!["stage"]!["optional"]!);
    }

    [Fact]
    public void The_readme_learning_path_lists_the_labs_of_each_stage_in_the_order_of_labs_json()
    {
        LabCatalog catalog = LabCatalogTests.LoadRealCatalog();
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        string diagram = Regex.Match(readme, @"## Learning Path.*?```\n(?<diagram>.*?)```", RegexOptions.Singleline).Groups["diagram"].Value;
        Assert.NotEmpty(diagram);

        // The diagram: "│ 1. Build an agent │" opens a stage, "Optional:" opens the optional labs; the labs are named
        // "Lab06" or "MultiAgentSystem Lab01", a lab of two exercises (server, client) once per exercise.
        List<(string Title, List<string> Labs)> stages = [];
        foreach (string line in diagram.Split('\n'))
        {
            Match heading = ReadmeStage().Match(line);
            if (heading.Success)
            {
                stages.Add((heading.Groups["title"].Value.Trim(), []));
            }
            else if (line.TrimStart().StartsWith("Optional:", StringComparison.Ordinal))
            {
                stages.Add(("Optional", []));
            }

            if (stages.Count > 0)
            {
                stages[^1].Labs.AddRange(ReadmeLab().Matches(line).Select(match => match.Value));
            }
        }

        // The catalog in the README's words: a lab of two exercises is named once.
        static string Name(LabDefinition lab) => lab.Track == "Multi-Agent System" ? $"MultiAgentSystem Lab{lab.Number}" : $"Lab{lab.Number}";
        static List<string> Distinct(IEnumerable<string> names) => names.Aggregate(new List<string>(), (list, name) =>
        {
            if (list.Count == 0 || list[^1] != name)
            {
                list.Add(name);
            }

            return list;
        });

        Assert.Equal(catalog.Stages.Select(stage => stage.Optional ? "Optional" : stage.Title), stages.Select(stage => stage.Title));
        foreach ((LabStage stage, (string Title, List<string> Labs) readmeStage) in catalog.Stages.Zip(stages))
        {
            Assert.Equal(Distinct(catalog.Labs.Where(lab => lab.Stage == stage.Id).Select(Name)), Distinct(readmeStage.Labs));
        }
    }

    [Fact]
    public void The_training_roadmap_is_generated_from_labs_json_and_up_to_date()
    {
        LabCatalog catalog = LabCatalogTests.LoadRealCatalog();
        string html = File.ReadAllText(Path.Combine(RepositoryRoot, "Roadmap-Formation-Session2.html"));
        string json = Regex.Match(html, """<!-- path-data:start -->\s*<script type="application/json" id="path-data">(?<json>.*?)</script>\s*<!-- path-data:end -->""", RegexOptions.Singleline)
            .Groups["json"].Value;
        Assert.False(string.IsNullOrWhiteSpace(json), "The roadmap has no path-data block: run python3 Dashboard/tools/generate-roadmap.py");
        JsonNode data = JsonNode.Parse(json.Replace("<\\/", "</"))!;

        const string Hint = "The roadmap is out of date: run python3 Dashboard/tools/generate-roadmap.py";
        Assert.True(catalog.Stages.Select(stage => (stage.Id, stage.Optional, stage.Title, stage.Translations["fr"].Title))
            .SequenceEqual(data["stages"]!.AsArray().Select(stage =>
                ((string)stage!["id"]!, (bool)stage["optional"]!, (string)stage["title"]!["en"]!, (string?)stage["title"]!["fr"]))), Hint);
        Assert.True(catalog.Labs.Select(lab => (lab.Id, lab.Stage, lab.Number, lab.Title, lab.Path, lab.BonusScenarios.Count))
            .SequenceEqual(data["labs"]!.AsArray().Select(lab => ((string)lab!["id"]!, (string?)lab["stage"], (string)lab["number"]!,
                (string)lab["title"]!["en"]!, (string)lab["path"]!, lab["bonusScenarios"]!.AsArray().Count))), Hint);
    }

    [Theory]
    [InlineData("""[ { "id": "a", "title": "A" } ]""", "\"stage\": \"b\"", "\"stage\": \"a\"", "is not one of the stages")]
    [InlineData("""[ { "id": "a", "title": "A" }, { "id": "b", "title": "B" } ]""", "\"stage\": \"b\"", "\"stage\": \"a\"", "stage by stage")]
    [InlineData("""[ { "id": "a", "title": "A" }, { "id": "b", "title": "B" }, { "id": "c", "title": "C" } ]""", "\"stage\": \"a\"", "\"stage\": \"b\"", "Stage 'c' has no lab")]
    [InlineData("""[ { "id": "a", "title": "A" }, { "id": "a", "title": "A again" } ]""", "\"stage\": \"a\"", "\"stage\": \"a\"", "declared twice")]
    [InlineData("""[ { "id": "A b", "title": "A" } ]""", "\"stage\": \"A b\"", "\"stage\": \"A b\"", "the id must match")]
    [InlineData("null", "\"stage\": \"a\"", "\"readme\": \"README.md\"", "the catalog has no stages")]
    public void Refuses_a_catalog_whose_stages_do_not_describe_one_path(string stages, string firstLab, string secondLab, string error)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "Lab"));
        File.WriteAllText(Path.Combine(root, "Lab", "a.csproj"), "<Project />");
        string catalogFile = Path.Combine(root, "labs.json");
        string Lab(string id, string stage) => $$"""
            { "id": "{{id}}", "number": "01", "track": "t", {{stage}}, "title": "t", "summary": "s", "level": "l",
              "path": "Lab", "startProject": "a.csproj", "solutionProject": "a.csproj" }
            """;
        File.WriteAllText(catalogFile, $$"""{ "stages": {{stages}}, "labs": [ {{Lab("x", firstLab)}}, {{Lab("y", secondLab)}} ] }""");

        try
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => LabCatalog.Load(catalogFile, root));
            Assert.Contains(error, exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [GeneratedRegex(@"│\s*\d+\.\s+(?<title>[^│]+?)\s*│")]
    private static partial Regex ReadmeStage();

    [GeneratedRegex(@"(?:MultiAgentSystem )?Lab\d\d")]
    private static partial Regex ReadmeLab();
}
