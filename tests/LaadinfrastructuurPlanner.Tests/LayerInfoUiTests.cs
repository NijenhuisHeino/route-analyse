namespace LaadinfrastructuurPlanner.Tests;

public sealed class LayerInfoUiTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void HomePageDefinesInlineInfoForEveryMapLayer()
    {
        var home = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "Components",
            "Pages",
            "Home.razor"));

        Assert.Contains("OpenLayerInfoId", home);
        Assert.Contains("LayerInfoExpanded", home);
        Assert.Contains("ToggleLayerInfo(layer.Id)", home);
        Assert.Contains("layer-info-{layer.Id}", home);
        Assert.DoesNotContain("Wegvlakken zijn klikbare lijnsegmenten voor detailanalyse. Wegdrukte is een vloeiende heatmap", home);

        AssertLayerInfo(home, "stopdrukte", "Stopdrukte", "operationele stops, niet automatisch standplaatsen");
        AssertLayerInfo(home, "toplocaties", "Toplocaties", "gegroepeerd op afgeronde coordinaten");
        AssertLayerInfo(home, "wegvlakken", "Wegvlakken", "openen een detailanalyse per aangeklikt wegvlak");
        AssertLayerInfo(home, "wegdrukte", "Wegdrukte", "veel meer concentratiepunten");
        AssertLayerInfo(home, "vaste-stilstandlocaties", "Vaste stilstandlocaties", "gevraagde publieke laadvraag");
        AssertLayerInfo(home, "standplaatsen", "Standplaatsen (PostNL-wagenpark)", "thuisbases zijn");
        AssertLayerInfo(home, "charter-standplaatsen", "Charterstandplaatsen", "Fysieke standplaatsen van charters");
        AssertLayerInfo(home, "laadlocaties", "Laadlocaties", "vermogen, stekkers, toegang");
    }

    [Fact]
    public void StylesDefineAccessibleLayerInfoControls()
    {
        var css = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "wwwroot",
            "app.css"));

        Assert.Contains(".layer-toggle-row", css);
        Assert.Contains(".layer-toggle-label", css);
        Assert.Contains(".layer-info-button", css);
        Assert.Contains(".layer-info-button.is-open", css);
        Assert.Contains(".layer-info-button:focus-visible", css);
        Assert.Contains(".layer-info-panel", css);
    }

    [Fact]
    public void HomePageKeepsMapSelectionAsLeadingContext()
    {
        var home = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "Components",
            "Pages",
            "Home.razor"));
        var selectionPanel = ReadPanel("SelectionDetailPanel.razor");

        Assert.Contains("HasActiveMapSelection", home);
        Assert.Contains("@if (!HasActiveMapSelection)", home);
        Assert.Contains("SelectionDetailPanel", home);
        Assert.Contains("Kaartselectie actief", selectionPanel);
        Assert.Contains("Pauzelaadvraag per uur", selectionPanel);
    }

    [Fact]
    public void HomePageShowsFilterableSortableRoadTableBelowMap()
    {
        var home = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "Components",
            "Pages",
            "Home.razor"));
        var roadTablePanel = ReadPanel("RoadTablePanel.razor");

        Assert.Contains("RoadTablePanel", home);
        Assert.Contains("Wegvlakken onder de kaart", roadTablePanel);
        Assert.Contains("RoadTableFilter", roadTablePanel);
        Assert.Contains("SetRoadSort", roadTablePanel);
        Assert.Contains("RoadDisplayName", roadTablePanel);
        Assert.Contains("Passages</button>", roadTablePanel);
    }

    private static string ReadPanel(string fileName)
    {
        return File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "Components",
            "Panels",
            fileName));
    }

    private static void AssertLayerInfo(string home, string id, string label, string expectedExplanation)
    {
        Assert.Contains($"new(\"{id}\", \"{label}\"", home);
        Assert.Contains(expectedExplanation, home);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LaadinfrastructuurPlanner.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
