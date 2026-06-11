namespace LaadinfrastructuurPlanner.Tests;

public sealed class MapInteractionUiTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void HomePageOffersCloseButtonForEveryMapSelection()
    {
        var home = ReadHome();

        Assert.Contains("Sluit selectie", home);
        Assert.Contains("ClearMapSelectionAsync", home);
        Assert.Contains("SelectedStandplaats", home);
        Assert.Contains("SelectStandplaatsAsync", home);
        Assert.Contains("Standplaatsselectie", home);
        Assert.Contains("SelectedDetail is not null || SelectedStandplaats is not null", home);
    }

    [Fact]
    public void HomePageShowsClickableHotspotOverviewWithoutSelection()
    {
        var home = ReadHome();

        Assert.Contains("Drukste pauzevraag-wegvlakken", home);
        Assert.Contains("Drukste stilstandlocaties", home);
        Assert.Contains("Drukste standplaatsen", home);
        Assert.Contains("FocusBreakSegmentAsync", home);
        Assert.Contains("FocusDepotAsync", home);
        Assert.Contains("FocusStandplaatsAsync", home);
        Assert.Contains("FocusStopAsync", home);
        Assert.Contains("FocusRoadRowAsync", home);
        Assert.Contains("clickable-row", home);
        Assert.Contains("EnableBreakDemandLayerAsync", home);
        Assert.Contains("Laag inschakelen", home);
    }

    [Fact]
    public void HomePageRendersFriendlyEmptyAndLoadingStates()
    {
        var home = ReadHome();

        Assert.Contains("Geen wegvlakken gevonden voor dit filter", home);
        Assert.Contains("Selectiedetails worden geladen", home);
        Assert.Contains("Geen pauzemomenten gevonden", home);
        Assert.Contains("Geen stilstandvensters gevonden", home);
        Assert.Contains("Geen voertuigen gevonden voor deze selectie", home);

        // Regressie op gelekte Razor-expressies zoals "100 van 100.ToString(\"N0\") wegvlakken":
        // een expliciete expressie moet de hele methodeketen omsluiten.
        Assert.Contains(@"@((Dashboard?.TopRoadSegments.Length ?? 0).ToString(""N0""))", home);
        Assert.DoesNotContain(@"?? 0).ToString(""N0"") wegvlakken", home);
    }

    [Fact]
    public void HomePageRendersBreakDemandLegendWithAbsoluteCounts()
    {
        var home = ReadHome();

        Assert.Contains("BreakDemandLegend", home);
        Assert.Contains("BreakLegendGradientStyle", home);
        Assert.Contains("BreakLegendBinLabel", home);
        Assert.Contains("legend-bins", home);
        Assert.Contains("wegvlakken</span>", home);
    }

    [Fact]
    public void PlannerMapAppliesDynamicPaintAndSelectionCallbacks()
    {
        var js = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "wwwroot",
            "plannerMap.js"));

        Assert.Contains("applyRoadBreakDemandPaint", js);
        Assert.Contains("setPaintProperty", js);
        Assert.Contains("UpdateMapOverviewAsync", js);
        Assert.Contains("SelectStandplaatsAsync", js);
        Assert.Contains("focusBreakSegment", js);
        Assert.Contains("focusRoadSegment", js);
        Assert.Contains("focusPoint", js);
        Assert.DoesNotContain("showStandplaatsPopup", js);
    }

    [Fact]
    public void StylesDefineInteractiveMapAffordances()
    {
        var css = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "wwwroot",
            "app.css"));

        Assert.Contains(".clickable-row", css);
        Assert.Contains(".hotspot-grid", css);
        Assert.Contains(".hotspot-empty", css);
        Assert.Contains(".ghost-button", css);
        Assert.Contains(".legend-bins", css);
        Assert.Contains(".legend-bin-dot", css);
        Assert.Contains(".standplaats-chip", css);
    }

    private static string ReadHome()
    {
        return File.ReadAllText(Path.Combine(
            RepoRoot,
            "src",
            "LaadinfrastructuurPlanner",
            "Components",
            "Pages",
            "Home.razor"));
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
