using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeepwaterEngagementSuite.VoyagePlannerData;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Direction = DeepwaterEngagementSuite.VoyagePlannerData.Direction;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    private VoyageSolutionResult _result;
    private Task _run;
    private SyncTask<bool> _voyagePlaceTask;
    private VoyageSolve _voyageSolve;
    private VoyageScorer _uiScorer;
    private int _selectedSolutionIndex = 0;
    private bool _voyageSolving;
    private long _voyageNodesExplored;
    private long _voyageNodesPruned;
    private List<StrategySuggestion> _strategySuggestions;
    private long _nextSuggestionRefreshTicks;

    public List<NormalInventoryItem> GetAvailableCharts()
    {
        if (GameController.IngameState.IngameUi.VoyageWindow is { IsValid: true, IsVisible: true } voyageWindow)
        {

            var charts = voyageWindow.AvailableCharts;
            if (!charts.Any())
            {
                return [];
            }
            var filters = Settings.VoyageSettings.IgnoredCharts.Content.Where(x => x.Enabled).Select(x => x.Query).ToList();
            if (!filters.Any())
            {
                return charts;
            }

            var chartSize = charts[0].GetClientRectCache.Size;
            var containerRect = voyageWindow.ChartContainer.GetClientRectCache;
            var containerSize = containerRect.Size;
            var inventorySize = new Vector2i(
                (int)Math.Round(containerSize.Width/chartSize.Width),
                (int)Math.Round(containerSize.Height / chartSize.Height)); //TODO: is this gettable somewhere?
            var filtered = charts.Select(x =>
                {
                    var coord = ((x.GetClientRectCache.TopLeft - containerRect.TopLeft).ToVector2Num()
                                 / new Vector2(containerSize.Width, containerSize.Height)
                                 * inventorySize)
                        .RoundToVector2I();
                    return (x, new ChartData(x.Item, GameController, coord));
                })
                .Where(x => !filters.Any(f => f.Matches(x.Item2)))
                .Select(x => x.x)
                .ToList();
            return filtered;
        }

        return [];
    }

    private static bool TileHasChart(VoyageTileElement tile) =>
        tile?.ItemContainer?.Entity?.GetComponent<DeepwaterChart>() != null;

    private static Direction? GetTileOrientation(VoyageTileElement tile)
    {
        var chart = tile?.ItemContainer?.Entity?.GetComponent<DeepwaterChart>();
        if (chart?.Room == null)
            return null;

        return ((Direction)chart.Room.Path).RotateCcw(chart.Rotation);
    }

    private static bool BoardIsClear(VoyageWindow tree) =>
        tree.Tiles.All(t => !TileHasChart(t));

    private static async SyncTask<bool> WiggleCursorToFocus(Vector2 screenPos)
    {
        const float delta = 4f;
        Input.SetCursorPos(screenPos + new Vector2(delta, 0));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos + new Vector2(-delta, 0));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos + new Vector2(0, delta));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos);
        await TaskUtils.NextFrame();
        return true;
    }

    private async SyncTask<bool> PlacePieces(VoyageSolution solution)
    {
        try
        {
            var tree = GameController.IngameState.IngameUi.VoyageWindow;
            var winOrigin = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();
            var needsFocusWiggle = true;

            if (!BoardIsClear(tree))
            {
                var clearPos = winOrigin + tree.ClearButton.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(clearPos);
                if (needsFocusWiggle)
                {
                    await WiggleCursorToFocus(clearPos);
                    needsFocusWiggle = false;
                }

                await TaskUtils.CheckEveryFrameWithThrow(
                    () => tree.ClearButton.HasShinyHighlight,
                    () => "Clear button never highlighted (board may already be empty?)",
                    TimeSpan.FromSeconds(2));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => BoardIsClear(tree),
                    () => "Board still has charts after Clear",
                    TimeSpan.FromSeconds(3));
            }

            var availableCharts = GetAvailableCharts();

            // Place charts from the currently visible inventory tab first so
            // a mixed-tab solution switches tabs once instead of potentially
            // once per piece.
            var placements = new List<(VoyageTileElement Tile, MapPiecePlacement P)>();
            for (var i = 0; i < 9; i++)
            {
                var p = solution.Grid[i / 3, i % 3];
                if (p?.Piece != null)
                    placements.Add((tree.Tiles[i], p));
            }

            placements = placements
                .OrderByDescending(x => x.P.Piece.Id >= 0 &&
                                        x.P.Piece.Id < availableCharts.Count &&
                                        availableCharts[x.P.Piece.Id].IsVisible)
                .ToList();

            foreach (var (tile, p) in placements)
            {
                if (p.Piece.Id < 0 || p.Piece.Id >= availableCharts.Count)
                {
                    DebugWindow.LogError($"Voyage Place: piece id {p.Piece.Id} out of range ({availableCharts.Count} charts)");
                    continue;
                }

                var pieceElem = availableCharts[p.Piece.Id];
                if (!pieceElem.IsVisible && !await TrySwitchChartTab(tree, pieceElem))
                {
                    // A chart on the inactive inventory tab can never be
                    // hovered, so the pickup click below would stall until its
                    // timeout.
                    DebugWindow.LogError(
                        $"Voyage Place: chart #{p.Piece.Id} is on the other chart tab and " +
                        "automatic tab switching failed - see voyage_ui_dump.txt in the plugin folder.");
                    return false;
                }

                var click1Pos = winOrigin + pieceElem.GetClientRectCache.Center.ToVector2Num();
                var click2Pos = winOrigin + tile.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(click1Pos);
                if (needsFocusWiggle)
                {
                    await WiggleCursorToFocus(click1Pos);
                    needsFocusWiggle = false;
                }

                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.UIHover?.Address.Equals(pieceElem.Address) ?? false,
                    () => $"Hover address was {GameController.IngameState.UIHover?.Address:X} not {pieceElem.Address:X}",
                    TimeSpan.FromSeconds(1));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.HoldItemForSell,
                    TimeSpan.FromSeconds(1));
                Input.SetCursorPos(click2Pos);
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.UIHoverElement?.Address.Equals(tile.Address) ?? false,
                    () => $"Hover address was {GameController.IngameState.UIHoverElement?.Address:X} not {tile.Address:X}",
                    TimeSpan.FromSeconds(1));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.Free &&
                          TileHasChart(tile),
                    TimeSpan.FromSeconds(1));

                // The chart component can lag a frame or two behind the
                // placement click; without this wait the rotation loop below
                // used to read null once and silently skip rotating.
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GetTileOrientation(tile) != null,
                    () => "Placed chart component never appeared",
                    TimeSpan.FromSeconds(1));

                // Compare the resulting orientation (Room.Path rotated by the
                // counter - same formula the optimizer's match markers use)
                // instead of the raw counter: symmetric pieces reach the
                // target orientation at several counter values, so insisting
                // on counter equality over-clicks and can never terminate if
                // the game normalizes the counter.
                var rotationClicks = 0;
                while (GetTileOrientation(tile) is { } orientation &&
                       orientation != p.Connections)
                {
                    if (++rotationClicks > 4)
                    {
                        DebugWindow.LogError(
                            $"Voyage Place: piece #{p.Piece.Id} stuck at orientation {orientation}, wanted {p.Connections}");
                        return false;
                    }

                    var counterBefore = tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>()?.Rotation;
                    var click3Pos = winOrigin + tile.GetClientRectCache.Center.ToVector2Num();
                    Input.SetCursorPos(click3Pos);
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => GameController.IngameState.UIHover?.Address.Equals(tile.ItemContainer.Address) ?? false,
                        TimeSpan.FromSeconds(1));
                    Input.RightDown();
                    await TaskUtils.NextFrame();
                    Input.RightUp();
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>()?.Rotation is { } counter &&
                              counter != counterBefore,
                        TimeSpan.FromSeconds(1));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"Voyage Place failed: {ex.Message}");
            return false;
        }
    }

    private void DrawVoyageHighlights()
    {
        var settings = Settings.VoyageSettings;
        if (!settings.EnableVoyageHandling)
            return;

        if (Input.IsKeyDown(Keys.Escape) && _voyagePlaceTask != null)
        {
            _voyagePlaceTask = null;
        }

        VoyageWindow tree;
        try
        {
            tree = GameController?.IngameState?.IngameUi?.VoyageWindow;
        }
        catch (Exception ex)
        {
            _voyagePlaceTask = null;
            DebugWindow.LogError(ex.ToString());
            return;
        }

        if (tree is not { IsValid: true, IsVisible: true })
        {
            _voyagePlaceTask = null;
            return;
        }

        TaskUtils.RunOrRestart(ref _voyagePlaceTask, () => null);

        var modsPerTileIndex = GetTileMods(tree);

        var tiles = tree.Tiles;
        if (settings.DrawComboLabels.Value)
        {
            for (var index = 0; index < tiles.Count; index++)
            {
                var tile = tiles[index];
                var mods = modsPerTileIndex.GetValueOrDefault(index) ?? [];
                var tileCenter = tile.GetClientRectCache.Center.ToVector2Num();
                var chart = tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>();
                if (chart != null)
                {
                    var chartModOffset = -10f;

                    if (VoyagePlacementRules.TrySpecialtyRoomLabel(chart.Room.Name, out var roomLabel))
                    {
                        var roomText = roomLabel;
                        var roomSize = Graphics.MeasureText(roomText);
                        chartModOffset -= roomSize.Y;
                        Graphics.DrawTextWithBackground(roomText, tileCenter + new Vector2(0, chartModOffset),
                            Color.Violet, FontAlign.Center, Color.Black);
                    }

                    var chartMods = tile.ItemContainer.Entity.GetComponent<Mods>()?.ImplicitMods ?? [];
                    foreach (var im in chartMods)
                    {
                        if (!Settings.VoyageSettings.ShowAllChartModifiers &&
                            !VoyagePlacementRules.IsSpecialtyComboModifier(im.RawName))
                            continue;

                        var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                            .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                        if (chartMod?.HighlightColor.Value is not { A: > 0 } color)
                            continue;

                        var displayName = chartMod.Label.Value is { Length: > 0 } label
                            ? label
                            : TrimChartPrefix(im.RawName);
                        var prefix = chartMod.IsGlobal.Value ? "[G] " : "";
                        var weight = chartMod.Weight.Value;
                        var chartName = $"{prefix}{displayName}\n({weight:F1})";
                        var textSize = Graphics.MeasureText(chartName);
                        chartModOffset -= textSize.Y;
                        Graphics.DrawTextWithBackground(chartName, tileCenter + new Vector2(0, chartModOffset),
                            color, FontAlign.Center, Color.Black);
                    }
                }

                var strongTreasureAnchors = VoyagePlacementRules.IsStrongTreasureAnchors(
                    mods.Select(m => m.RawName));
                tileCenter = tileCenter + new Vector2(0, 10);
                foreach (var itemMod in mods)
                {
                    var isStrategy = VoyagePlacementRules.IsStrategyBorder(itemMod.RawName);
                    var isTreasureHighlight = strongTreasureAnchors &&
                                              VoyagePlacementRules.IsTreasureAnchorsBorder(itemMod.RawName);
                    if (!Settings.VoyageSettings.ShowAllBorderModifiers && !isStrategy && !isTreasureHighlight)
                        continue;

                    var matchingSetting = Settings.VoyageSettings.BorderModifiers.Content
                        .FirstOrDefault(c => c.Id.Value.Equals(itemMod.RawName, StringComparison.OrdinalIgnoreCase));
                    var text = matchingSetting?.Abbreviation.Value is { Length: > 0 } abbv
                        ? abbv
                        : itemMod.RawName.StartsWith("DeepwaterBorder", StringComparison.Ordinal)
                            ? itemMod.RawName["DeepwaterBorder".Length..]
                            : itemMod.RawName;
                    var color = matchingSetting?.HighlightColor.Value is { A: > 0 } c ? c : Color.Cyan;
                    var size = Graphics.DrawTextWithBackground(text, tileCenter, color, FontAlign.Center, Color.Black);
                    tileCenter.Y += size.Y;
                }
            }

        }

        // With a strategy active, label each target tile with the chart the
        // strategy wants there (border-dependent tiles resolve live). The tile
        // frame reflects fulfillment: green when the placed chart satisfies
        // the want, red when the tile is empty or holds the wrong chart; the
        // label text itself stays light blue.
        var activeStrategy = VoyageStrategies.ById(settings.SelectedStrategyId.Value);
        if (activeStrategy != null)
        {
            var wanted = StrategyContext.WantedPlacements(activeStrategy, BuildTileBorders(tree));
            var labelOffsets = new float[tiles.Count];
            var satisfiedByCell = new Dictionary<int, bool>();

            foreach (var (cell, rule) in wanted)
            {
                if (cell >= tiles.Count)
                    continue;

                var tileRect = tiles[cell].GetClientRectCache;
                var pos = new Vector2(tileRect.Center.X, tileRect.Top + 4 + labelOffsets[cell]);
                var size = Graphics.DrawTextWithBackground($"want: {rule.Label}", pos,
                    Color.Cyan, FontAlign.Center, Color.Black);
                labelOffsets[cell] += size.Y;

                // Reward-stat wants ("High Quantity") have no binary yes/no;
                // only chart/room-matcher rules drive the frame color.
                if (rule.ModPrefixes is not { Length: > 0 } && rule.RoomNames is not { Length: > 0 })
                    continue;

                var placedEntity = tiles[cell].ItemContainer?.Entity;
                var placedChart = placedEntity?.GetComponent<DeepwaterChart>();
                var satisfied = false;
                if (placedChart != null)
                {
                    var placedMods = placedEntity.GetComponent<Mods>()?.ImplicitMods ?? [];
                    satisfied = StrategyContext.ChartSatisfiesRule(rule,
                        placedMods.Select(m => m.RawName), placedChart.Room.Name ?? "");
                }

                satisfiedByCell[cell] = satisfiedByCell.GetValueOrDefault(cell) || satisfied;
            }

            foreach (var (cell, satisfied) in satisfiedByCell)
            {
                var frameRect = tiles[cell].GetClientRectCache;
                frameRect.Inflate(-2f, -2f);
                Graphics.DrawFrame(frameRect,
                    satisfied ? settings.GoodChartColor : settings.JunkChartColor, 2);
            }
        }

        if (settings.DrawComboLabels.Value || settings.HighlightChartQuality.Value)
        {
            var charts = GetAvailableCharts();
            var specialtyIndices = GetInventorySpecialtyIndices(charts);

            for (int i = 0; i < charts.Count; i++)
            {
                // Hidden-tab charts share screen rects with the visible tab;
                // drawing for them would stack wrong labels over other charts.
                if (!charts[i].IsVisible)
                    continue;

                var rect = charts[i].GetClientRectCache;

                if (settings.HighlightChartQuality.Value)
                {
                    // With a strategy active, keepers (other strategies' fuel,
                    // held out of this strategy's pool) show violet so it's
                    // clear why the solver won't burn them.
                    var reserved = activeStrategy != null &&
                                   settings.ProtectKeeperCharts.Value &&
                                   VoyageStrategies.IsReservedChart(activeStrategy,
                                       (charts[i].Item?.GetComponent<Mods>()?.ImplicitMods ?? [])
                                       .Select(m => m.RawName),
                                       charts[i].Item?.GetComponent<DeepwaterChart>()?.Room.Name ?? "");

                    Color frameColor;
                    if (reserved || (activeStrategy == null && specialtyIndices.Contains(i)))
                    {
                        frameColor = settings.SpecialtyChartColor;
                    }
                    else
                    {
                        var score = ChartHighlightScore(charts[i], activeStrategy);
                        frameColor = score >= settings.GoodChartThreshold.Value
                            ? settings.GoodChartColor
                            : score > 0
                                ? settings.UsefulChartColor
                                : settings.JunkChartColor;
                    }

                    var frameRect = rect;
                    frameRect.Inflate(-1.5f, -1.5f);
                    Graphics.DrawFrame(frameRect, frameColor, 2);
                }

                if (!settings.DrawComboLabels.Value)
                    continue;

                var pos = rect.TopLeft.ToVector2Num();
                if (specialtyIndices.Contains(i))
                {
                    var exclSize = Graphics.DrawTextWithBackground("!", pos, Color.Violet, Color.Black);
                    pos.Y += exclSize.Y;
                }

                if (Settings.VoyageSettings.ShowChartInventoryInformation)
                {
                    var size = Graphics.DrawTextWithBackground($"#{i}", pos, Color.Black);
                    var chartMods = charts[i].Entity.GetComponent<Mods>()?.ImplicitMods ?? [];

                    foreach (var chartMod in chartMods)
                    {
                        var chartSettings = Settings.VoyageSettings.ChartModifiers.Content
                            .FirstOrDefault(cm => cm.Id.Value.Equals(chartMod.RawName, StringComparison.OrdinalIgnoreCase));
                        if (chartSettings != null && !string.IsNullOrEmpty(chartSettings.Label.Value))
                        {
                            pos.Y += size.Y;
                            Graphics.DrawTextWithBackground(chartSettings.Label.Value, pos, chartSettings.HighlightColor, Color.Black);
                        }
                    }
                }
            }
        }

        if (settings.ShowOptimizerWindow.Value)
        {
            ShowVoyageOptimizerWindow(tree,tiles);
        }
    }

    // The chart inventory has multiple tabs; ExileCore's VoyageWindow doesn't
    // expose the tab buttons, so they are located by shape instead of by a
    // hardcoded child path: a horizontal run of 2-4 equal-size button-height
    // elements sitting just above the chart container (measured via DevTree:
    // 96x42 at 0.9 UI scale, exactly adjacent, shiny-highlight on hover).
    private List<ExileCore.PoEMemory.Element> FindChartTabButtons(VoyageWindow tree)
    {
        // Fast path: the tab strip sits at a fixed child path under
        // VoyageWindow (verified against a live UI dump: strip=[3.11.0],
        // children are [tab1, full-width background, tab2] with the page
        // number as nested text). The buttons are the narrow children.
        var strip = tree.GetChildFromIndices(3, 11, 0);
        if (strip is { IsVisibleLocal: true })
        {
            var stripWidth = strip.GetClientRectCache.Width;
            var directButtons = (strip.Children ?? Enumerable.Empty<ExileCore.PoEMemory.Element>())
                .Where(c => c is { IsVisibleLocal: true })
                .Where(c =>
                {
                    var r = c.GetClientRectCache;
                    return r.Height > 10 && r.Width > 10 && r.Width < stripWidth * 0.5f;
                })
                .OrderBy(c => c.GetClientRectCache.Left)
                .ToList();
            if (directButtons.Count >= 2)
                return directButtons;
        }

        // Fallback if a game patch moves the strip: locate by shape.
        var container = tree.ChartContainer?.GetClientRectCache ?? default;
        if (container.Width <= 0 || container.Height <= 0)
            return [];

        var clearRect = tree.ClearButton?.GetClientRectCache ?? default;
        var startRect = tree.StartButton?.GetClientRectCache ?? default;
        var chartRects = (tree.AvailableCharts ?? [])
            .Where(c => c.IsVisible)
            .Select(c => c.GetClientRectCache)
            .ToList();

        var all = new List<ExileCore.PoEMemory.Element>();
        CollectVisibleElements(tree, all, 0);

        // Nested wrappers share the same rect (the buttons have a single
        // child); keep the deepest element per rect since that's what the
        // game reports as hovered.
        var byRect = new Dictionary<(int, int, int, int), ExileCore.PoEMemory.Element>();
        foreach (var element in all)
        {
            var r = element.GetClientRectCache;
            if (r.Height is <= 25 or >= 75 || r.Width is <= 55 or >= 180)
                continue;
            if (r.Bottom > container.Top + 40 || r.Bottom < container.Top - 160)
                continue;
            if (r.Right < container.Left - 30 || r.Left > container.Right + 30)
                continue;
            if (r.Intersects(clearRect) || r.Intersects(startRect))
                continue;
            if (chartRects.Any(c => c.Intersects(r)))
                continue;
            byRect[((int)Math.Round(r.Left / 2), (int)Math.Round(r.Top / 2),
                (int)Math.Round(r.Width / 2), (int)Math.Round(r.Height / 2))] = element;
        }

        // Group the survivors into rows, then find an adjacent equal-size run.
        var rows = byRect.Values
            .GroupBy(e => (int)Math.Round(e.GetClientRectCache.Top / 8))
            .OrderByDescending(g => g.Key);
        foreach (var row in rows)
        {
            var ordered = row.OrderBy(e => e.GetClientRectCache.Left).ToList();
            for (var start = 0; start < ordered.Count; start++)
            {
                var run = new List<ExileCore.PoEMemory.Element> { ordered[start] };
                for (var next = start + 1; next < ordered.Count; next++)
                {
                    var prev = run[^1].GetClientRectCache;
                    var cand = ordered[next].GetClientRectCache;
                    if (Math.Abs(cand.Left - prev.Right) > 10 ||
                        Math.Abs(cand.Width - prev.Width) > 12 ||
                        Math.Abs(cand.Height - prev.Height) > 6)
                        break;
                    run.Add(ordered[next]);
                }

                if (run.Count is >= 2 and <= 4)
                    return run;
            }
        }

        return [];
    }

    private static void CollectVisibleElements(ExileCore.PoEMemory.Element element,
        List<ExileCore.PoEMemory.Element> sink, int depth)
    {
        if (element == null || depth > 8 || !element.IsVisibleLocal)
            return;

        sink.Add(element);
        var children = element.Children;
        if (children == null)
            return;

        foreach (var child in children)
            CollectVisibleElements(child, sink, depth + 1);
    }

    private async SyncTask<bool> TrySwitchChartTab(VoyageWindow tree,
        NormalInventoryItem targetChart)
    {
        var buttons = FindChartTabButtons(tree);
        if (buttons.Count == 0)
        {
            DumpVoyageUi(tree);
            return false;
        }

        var winOrigin = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();

        // One of the buttons is the already-active tab; clicking it is
        // harmless, so just try them in order until the chart shows up.
        // requireHighlight=false is a second pass for the case where the
        // shiny-highlight read misbehaves - the click is still aimed at a
        // shape-verified tab button.
        foreach (var requireHighlight in new[] { true, false })
        {
            foreach (var button in buttons)
            {
                if (targetChart.IsVisible)
                    return true;

                var clickPos = winOrigin + button.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(clickPos);
                if (requireHighlight)
                {
                    try
                    {
                        await TaskUtils.CheckEveryFrameWithThrow(
                            () => button.HasShinyHighlight ||
                                  (button.Children?.Any(c => c.HasShinyHighlight) ?? false),
                            TimeSpan.FromMilliseconds(700));
                    }
                    catch
                    {
                        continue;
                    }
                }
                else
                {
                    await TaskUtils.NextFrame();
                }

                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();

                try
                {
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => targetChart.IsVisible,
                        TimeSpan.FromMilliseconds(700));
                    return true;
                }
                catch
                {
                    // Chart still hidden; try the next button.
                }
            }
        }

        if (targetChart.IsVisible)
            return true;

        DumpVoyageUi(tree);
        return false;
    }

    private void DumpVoyageUi(VoyageWindow tree)
    {
        try
        {
            var path = Path.Combine(DirectoryFullName, "voyage_ui_dump.txt");
            using (var writer = new StreamWriter(path, false))
            {
                writer.WriteLine($"=== VoyageWindow dump {DateTime.Now:O} ===");

                writer.WriteLine("--- AvailableCharts (unfiltered) ---");
                var charts = tree.AvailableCharts ?? [];
                for (var i = 0; i < charts.Count; i++)
                {
                    var chart = charts[i];
                    var room = chart?.Item?.GetComponent<DeepwaterChart>()?.Room.Name;
                    writer.WriteLine(
                        $"#{i} addr={chart?.Address:X} visible={chart?.IsVisible} " +
                        $"rect={chart?.GetClientRectCache} room={room}");
                }

                writer.WriteLine("--- ChartContainer addr ---");
                writer.WriteLine($"{tree.ChartContainer?.Address:X}");

                writer.WriteLine("--- Element tree ---");
                DumpElementRecursive(writer, tree, "", 0);
            }

            DebugWindow.LogMsg($"Voyage UI dumped to {path}", 10);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"Voyage UI dump failed: {ex.Message}");
        }
    }

    private static void DumpElementRecursive(StreamWriter writer, ExileCore.PoEMemory.Element element, string path, int depth)
    {
        if (element == null || depth > 8)
            return;

        var text = element.Text;
        text = string.IsNullOrWhiteSpace(text) ? "" : $" text=\"{text.Replace("\n", "\\n")}\"";
        writer.WriteLine(
            $"{new string(' ', depth * 2)}[{path}] addr={element.Address:X} vis={element.IsVisibleLocal} " +
            $"rect={element.GetClientRectCache} children={element.ChildCount}{text}");

        var children = element.Children;
        if (children == null)
            return;

        for (var i = 0; i < children.Count; i++)
            DumpElementRecursive(writer, children[i], path.Length == 0 ? i.ToString() : $"{path}.{i}", depth + 1);
    }

    // How valuable a chart looks for the highlight frames: the active
    // strategy's weights plus its positive rule matches (scaled into weight
    // range), or the user's configured chart modifier weights otherwise.
    private double ChartHighlightScore(NormalInventoryItem chart, VoyageStrategy strategy)
    {
        var chartMods = chart.Item?.GetComponent<Mods>()?.ImplicitMods ?? [];

        if (strategy == null)
        {
            double configured = 0;
            foreach (var im in chartMods)
            {
                var cm = Settings.VoyageSettings.ChartModifiers.Content
                    .FirstOrDefault(c => c.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                configured += cm?.Weight.Value ?? 0;
            }

            return configured;
        }

        var room = chart.Item?.GetComponent<DeepwaterChart>()?.Room.Name ?? "";
        var score = chartMods.Sum(im => strategy.WeightFor(im.RawName));
        foreach (var rule in strategy.Rules)
        {
            if (rule.Bonus <= 0)
                continue;

            var matchesMod = rule.ModPrefixes is { Length: > 0 } && chartMods.Any(im =>
                rule.ModPrefixes.Any(p => im.RawName.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            var matchesRoom = rule.RoomNames is { Length: > 0 } &&
                              rule.RoomNames.Any(r => room.Contains(r, StringComparison.OrdinalIgnoreCase));
            if (matchesMod || matchesRoom)
                score += rule.Bonus / 10.0;
        }

        return score;
    }

    private static Dictionary<int, List<ItemMod>> GetTileMods(VoyageWindow tree)
    {
        var borderMods = tree.Data.BorderMods;
        Dictionary<int, List<ItemMod>> modsPerTileIndex = [];
        if (borderMods.Count >= 12)
        {
            modsPerTileIndex = new Dictionary<int, List<int>>
            {
                [0] = [0, 11],
                [1] = [1],
                [2] = [2, 3],
                [3] = [10],
                [4] = [],
                [5] = [4],
                [6] = [8, 9],
                [7] = [7],
                [8] = [5, 6],
            }.ToDictionary(
                x => x.Key,
                x => x.Value.Select(v => borderMods[v])
                    .ToList());
        }

        return modsPerTileIndex;
    }

    private void ShowVoyageOptimizerWindow(VoyageWindow tree, List<VoyageTileElement> tiles)
    {
        if (!ImGui.Begin("Voyage Optimizer"))
        {
            ImGui.End();
            return;
        }

        _voyageSolving = _run is { IsCompleted: false };

        // Suggest the best-fitting strategy for the rolled borders and the
        // current chart inventory (refreshed twice a second, not per frame).
        var nowTicks = DateTime.UtcNow.Ticks;
        if (_strategySuggestions == null || nowTicks >= _nextSuggestionRefreshTicks)
        {
            var chartInfos = GetAvailableCharts()
                .Select(ch => (
                    (IReadOnlyList<string>)(ch.Item?.GetComponent<Mods>()?.ImplicitMods ?? [])
                    .Select(m => m.RawName).ToList(),
                    ch.Item?.GetComponent<DeepwaterChart>()?.Room.Name ?? ""))
                .ToList();
            _strategySuggestions = VoyageStrategies.RankStrategies(chartInfos, BuildTileBorders(tree));
            _nextSuggestionRefreshTicks = nowTicks + TimeSpan.TicksPerMillisecond * 500;
        }

        if (_strategySuggestions is { Count: > 0 })
        {
            var best = _strategySuggestions[0];
            ImGui.TextColored((best.Ready ? Color.LimeGreen : Color.Orange).ToImguiVec4(),
                $"Suggested: {best.Strategy.Name}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Use"))
            {
                Settings.VoyageSettings.SelectedStrategyId.Value = best.Strategy.Id;
                Settings.VoyageSettings.SelectedStrategyLayoutId.Value = "";
            }

            var reasonBits = new List<string>();
            if (best.Strategy.RequiredBorderId != null)
                reasonBits.Add("required border rolled");
            reasonBits.Add(best.Ready
                ? best.RequirementsTotal > 0 ? "all pieces ready" : "always runnable"
                : $"missing: {string.Join(", ", best.Missing)}");
            ImGui.TextDisabled(string.Join("; ", reasonBits));

            // What the player is still collecting toward: the highest-payoff
            // strategy that isn't ready yet.
            var banking = _strategySuggestions
                .Where(s => !s.Ready && s.Strategy.SuggestionWeight > best.Strategy.SuggestionWeight)
                .OrderByDescending(s => s.Strategy.SuggestionWeight)
                .FirstOrDefault();
            if (banking != null)
            {
                ImGui.TextDisabled(
                    $"Banking toward {banking.Strategy.Name} - missing: {string.Join(", ", banking.Missing.Take(3))}");
            }
        }

        ImGui.Spacing();

        // Strategy picker: curated community strategies (ported from
        // one-more-map) that override reward weights and shape placement.
        var strategies = VoyageStrategies.All;
        var strategyNames = new string[strategies.Count + 1];
        strategyNames[0] = "None (built-in rules)";
        var strategyIndex = 0;
        for (var i = 0; i < strategies.Count; i++)
        {
            strategyNames[i + 1] = strategies[i].Name;
            if (strategies[i].Id == Settings.VoyageSettings.SelectedStrategyId.Value)
                strategyIndex = i + 1;
        }

        ImGui.SetNextItemWidth(230);
        if (ImGui.Combo("Strategy", ref strategyIndex, strategyNames, strategyNames.Length))
        {
            Settings.VoyageSettings.SelectedStrategyId.Value =
                strategyIndex == 0 ? "" : strategies[strategyIndex - 1].Id;
            Settings.VoyageSettings.SelectedStrategyLayoutId.Value = "";
        }

        var selectedStrategy = strategyIndex > 0 ? strategies[strategyIndex - 1] : null;
        if (selectedStrategy != null)
        {
            ImGui.TextDisabled(selectedStrategy.Tagline);

            if (selectedStrategy.Layouts is { Length: > 1 })
            {
                var layoutNames = selectedStrategy.Layouts.Select(l => l.Label).ToArray();
                var layoutIndex = Math.Max(0, Array.FindIndex(selectedStrategy.Layouts,
                    l => l.Id == Settings.VoyageSettings.SelectedStrategyLayoutId.Value));
                ImGui.SetNextItemWidth(230);
                if (ImGui.Combo("Layout", ref layoutIndex, layoutNames, layoutNames.Length))
                {
                    Settings.VoyageSettings.SelectedStrategyLayoutId.Value =
                        selectedStrategy.Layouts[layoutIndex].Id;
                }
            }

            var protect = Settings.VoyageSettings.ProtectKeeperCharts.Value;
            if (ImGui.Checkbox("Protect keeper charts", ref protect))
                Settings.VoyageSettings.ProtectKeeperCharts.Value = protect;

            if (_voyageSolve != null && _voyageSolve.ReservedCount > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"({_voyageSolve.ReservedCount} held back)");
            }

            if (_voyageSolve is { NotEnoughFreeCharts: true })
            {
                ImGui.TextColored(Color.Orange.ToImguiVec4(),
                    "Not enough free charts to fill the board - untick protection or bank more junk charts.");
            }

            if (ImGui.TreeNode("Strategy guide"))
            {
                foreach (var line in selectedStrategy.Guide)
                    ImGui.TextWrapped($"- {line}");
                ImGui.TreePop();
            }
        }

        var selectedLayoutId = Settings.VoyageSettings.SelectedStrategyLayoutId.Value;
        var protectKeepers = Settings.VoyageSettings.ProtectKeeperCharts.Value;

        if (ImGui.Button("Solve"))
        {
            _result = null;
            _selectedSolutionIndex = 0;
            _voyageNodesExplored = 0;
            _voyageNodesPruned = 0;
            _run = Task.Run(() =>
            {
                var pieces = BuildMapPiecesFromAvailableCharts(selectedStrategy);
                var tileBorders = BuildTileBorders(tree);

                var session = new VoyageSolve();
                _voyageSolve = session;

                foreach (var r in session.Run(
                             pieces,
                             tileBorders,
                             settings: new VoyagePlannerSettings(),
                             strategy: selectedStrategy,
                             strategyLayoutId: selectedLayoutId,
                             protectKeepers: protectKeepers))
                {
                    _result = r;
                    _voyageNodesExplored = r.NodesExplored;
                    _voyageNodesPruned = r.NodesPruned;
                    _uiScorer = session.Scorer;
                }

                _uiScorer = session.Scorer;
                LogPlacement(session.Placement);
                _voyageSolving = false;
            });
        }

        ImGui.SameLine();
        if (ImGui.Button("Dump UI"))
        {
            DumpVoyageUi(tree);
        }

        if (_result != null && _result.Solutions.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Place"))
            {
                if (_selectedSolutionIndex >= _result.Solutions.Count)
                    _selectedSolutionIndex = 0;
                var sol = _result.Solutions[_selectedSolutionIndex];
                _voyagePlaceTask = PlacePieces(sol);
            }
        }

        ImGui.Spacing();

        if (_voyageSolving || _result != null)
        {
            ImGui.Text($"Nodes: {_voyageNodesExplored:N0} explored, {_voyageNodesPruned:N0} pruned");
        }

        if (_result == null || _result.Solutions.Count == 0)
        {
            if (_voyageSolving)
            {
                ImGui.TextColored(Color.Yellow.ToImguiVec4(), "Searching...");
            }
            else
            {
                ImGui.TextColored(Color.Gray.ToImguiVec4(), "No solutions yet. Press Solve.");
            }

            ImGui.End();
            return;
        }

        _selectedSolutionIndex = Math.Clamp(_selectedSolutionIndex, 0, _result.Solutions.Count - 1);
        var currentSolution = _result.Solutions[_selectedSolutionIndex];

        var asciiArt = BuildAsciiGrid(currentSolution.Grid, tiles);

        using (ImGuiHelpers.UseStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0)))
            foreach (var line in asciiArt)
            {
                ImGui.TextUnformatted(line);
            }

        ImGui.Spacing();

        ImGui.Text($"Score: {currentSolution.TotalScore:F2}");
        if (currentSolution.StrategyObjective is { } currentObjective)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"(strategy objective: {currentObjective:F2})");
        }

        ImGui.Text($"Valid: {(currentSolution.IsValid ? "Yes" : "No")}");

        if (_result.Solutions.Count > 0)
        {
            // With a strategy active the list ranks by the strategy objective
            // (reward + rule bonuses - layout penalties), not raw reward, so
            // show the objective column to make the ordering legible.
            var showObjective = _result.Solutions.Any(s => s.StrategyObjective.HasValue);

            ImGui.Spacing();
            if (ImGui.BeginTable("SolutionsList", showObjective ? 5 : 4,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#");
                ImGui.TableSetupColumn("Score");
                if (showObjective)
                    ImGui.TableSetupColumn("Objective");
                ImGui.TableSetupColumn("Valid");
                ImGui.TableSetupColumn("Select");
                ImGui.TableHeadersRow();

                for (int i = 0; i < _result.Solutions.Count; i++)
                {
                    var sol = _result.Solutions[i];
                    ImGui.TableNextRow();
                    ImGui.PushID(i);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{i + 1}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{sol.TotalScore:F2}");
                    if (showObjective)
                    {
                        ImGui.TableNextColumn();
                        ImGui.Text(sol.StrategyObjective is { } obj ? $"{obj:F2}" : "-");
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text($"{sol.IsValid}");
                    ImGui.TableNextColumn();
                    var isSelected = i == _selectedSolutionIndex;
                    if (isSelected)
                        ImGui.PushStyleColor(ImGuiCol.Button, Color.Green.ToImguiVec4());
                    if (ImGui.Button(isSelected ? "Selected" : "Select"))
                    {
                        _selectedSolutionIndex = i;
                    }

                    if (isSelected)
                        ImGui.PopStyleColor();
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        if (Settings.VoyageSettings.ShowScoreDebugDetails.Value)
        {
            var cellScores = _uiScorer?.CellScores(currentSolution.Grid);

            if (ImGui.BeginTable("ScoreBreakdown", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableSetupColumn("Tile", ImGuiTableColumnFlags.WidthFixed, 25);
                ImGui.TableSetupColumn("Piece", ImGuiTableColumnFlags.WidthFixed, 20);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("Score", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Mods");
                ImGui.TableHeadersRow();

                for (int i = 0; i < 9; i++)
                {
                    var r = i / 3;
                    var c = i % 3;
                    var placement = currentSolution.Grid[r, c];

                    ImGui.TableNextRow();
                    ImGui.PushID($"tile{i}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{r},{c}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"#{placement.Piece.Id}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{placement.Piece.Type}");
                    ImGui.TableNextColumn();
                    ImGui.Text(cellScores != null ? $"{cellScores[r, c]:F1}" : "-");
                    ImGui.TableNextColumn();
                    var modText = string.Join(", ", placement.Piece.Modifiers.Where(m => m.Name != "Default").Select(m =>
                    {
                        var displayName = TrimChartPrefix(m.Name);
                        var prefix = m.IsGlobal ? "[Global] " : "";
                        return $"{prefix}{displayName}({m.Weight:F1})";
                    }));
                    ImGui.Text(string.IsNullOrEmpty(modText) ? "-" : modText);
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }

            DrawScoreDetails(currentSolution);
        }

        ImGui.End();
    }

    private void DrawScoreDetails(VoyageSolution solution)
    {
        if (_uiScorer == null)
            return;

        ImGui.Spacing();
        if (!ImGui.TreeNode("Score details"))
            return;

        var explanation = _uiScorer.Explain(solution.Grid);
        for (int i = 0; i < 9; i++)
        {
            var r = i / 3;
            var c = i % 3;
            var placement = solution.Grid[r, c];
            var rows = explanation[r, c];
            var total = rows.Sum(x => x.Value);

            ImGui.PushID($"detail{i}");
            var open = ImGui.TreeNode("node", $"({r},{c}) #{placement.Piece.Id} {placement.Piece.Type} — {total:F1}");
            if (open)
            {
                var borders = _uiScorer.BordersAt(r, c);
                ImGui.TextDisabled(borders.Count > 0
                    ? "Borders: " + string.Join(",  ", borders.Select(FormatBorderEffect))
                    : "No borders touch this tile");

                if (rows.Count == 0)
                {
                    ImGui.TextDisabled("No score contributions");
                }
                else if (ImGui.BeginTable("details", 6,
                             ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Mod");
                    ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthFixed, 75);
                    ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 60);
                    ImGui.TableSetupColumn("Mult", ImGuiTableColumnFlags.WidthFixed, 130);
                    ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 65);
                    ImGui.TableSetupColumn("Applied borders");
                    ImGui.TableHeadersRow();

                    foreach (var row in rows)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text($"{(row.IsGlobal ? "[G] " : "")}{TrimChartPrefix(row.ModName)}");
                        ImGui.TableNextColumn();
                        ImGui.Text(row.SourcePieceId < 0
                            ? "-"
                            : row.IsGlobal
                                ? "self"
                                : $"#{row.SourcePieceId} ({row.SourceRow},{row.SourceCol})");
                        ImGui.TableNextColumn();
                        ImGui.Text($"{row.Weight:F1}");
                        ImGui.TableNextColumn();
                        ImGui.Text(row.SourcePieceId < 0
                            ? $"x{row.TileFactor:F2}"
                            : row.IsGlobal
                                ? $"x{row.ChartMultiplier:F2} sum{row.TileFactor:F2}"
                                : $"x{row.ChartMultiplier:F2} x{row.TileFactor:F2}");
                        ImGui.TableNextColumn();
                        ImGui.Text($"{row.Value:F1}");
                        ImGui.TableNextColumn();
                        var applied = row.TileBorders
                            .Select(b => $"{TrimBorderPrefix(b.Name)} x{b.Multiplier:0.##}")
                            .Concat(row.ChartBorders
                                .Select(b => $"{TrimBorderPrefix(b.Name)} x{b.Multiplier:0.##} (boosts chart at ({row.SourceRow},{row.SourceCol}))"))
                            .ToList();
                        ImGui.Text(applied.Count > 0 ? string.Join(", ", applied) : "-");
                    }

                    ImGui.EndTable();
                }

                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        ImGui.TreePop();
    }

    private string FormatBorderEffect(BorderEffect border)
    {
        return $"{TrimBorderPrefix(border.Name)} x{border.Multiplier:0.##}{(border.PerConnection ? "/conn" : "")}" +
               $"{(border.AffectsPlacedChart ? " (boosts this tile's chart, value lands where its mods point)" : "")} [{border.Tags}]";
    }

    private static string TrimBorderPrefix(string name)
    {
        return name.StartsWith("DeepwaterBorder", StringComparison.Ordinal)
            ? name["DeepwaterBorder".Length..]
            : name;
    }

    private static string[] BuildAsciiGrid(MapPiecePlacement[,] grid, List<VoyageTileElement> tiles)
    {
        const int H = 5;
        const int W = 7;
        const int GH = H * 3 + 2;
        const int GW = W * 3 + 2;

        var buf = new char[GH, GW];
        for (int y = 0; y < GH; y++)
        for (int x = 0; x < GW; x++)
            buf[y, x] = ' ';

        FillBox(buf, '+', '+', '+', '+', '-', '|', 0, 0, GH - 1, GW - 1);

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                var left = c * W + 1;
                var right = left + W - 1;
                var top = r * H + 1;
                var bot = top + H - 1;
                var cx = left + W / 2;
                var cy = top + H / 2;

                var p = grid[2 - r, c];
                var conn = p.Connections;

                for (int y = top; y <= bot; y++)
                for (int x = left; x <= right; x++)
                    buf[y, x] = ' ';

                if (conn.HasFlag(Direction.Up))
                    for (int y = top; y < cy; y++)
                        buf[y, cx] = '|';
                if (conn.HasFlag(Direction.Down))
                    for (int y = cy + 1; y <= bot; y++)
                        buf[y, cx] = '|';
                if (conn.HasFlag(Direction.Left))
                    for (int x = left; x < cx; x++)
                        buf[cy, x] = '-';
                if (conn.HasFlag(Direction.Right))
                    for (int x = cx + 1; x <= right; x++)
                        buf[cy, x] = '-';

                buf[cy, cx] = conn switch
                {
                    Direction.Up | Direction.Down => '|',
                    Direction.Left | Direction.Right => '-',
                    Direction.All => '+',
                    _ => '.',
                };

                var tileIdx = (2 - r) * 3 + c;
                bool matches = false;
                if (tileIdx < tiles.Count)
                {
                    var t = tiles[tileIdx];
                    if (t.ItemContainer?.Address != null)
                    {
                        var placed = t.ItemContainer.Entity.GetComponent<DeepwaterChart>();
                        if (placed != null)
                        {
                            var actualRot = ((Direction)placed.Room.Path).RotateCcw(placed.Rotation);
                            var expectedRot = p.Connections;
                            matches = actualRot == expectedRot;
                        }
                    }
                }

                buf[cy + 1, cx + 2] = matches ? 'O' : 'X';
            }
        }

        var lines = new string[GH];
        for (int y = 0; y < GH; y++)
        {
            var row = new char[GW];
            for (int x = 0; x < GW; x++)
                row[x] = buf[y, x];
            lines[y] = new string(row);
        }

        return lines;
    }

    private static void FillBox(char[,] buf, char tl, char tr, char bl, char br, char h, char v, int y1, int x1, int y2, int x2)
    {
        buf[y1, x1] = tl;
        buf[y1, x2] = tr;
        buf[y2, x1] = bl;
        buf[y2, x2] = br;
        for (int x = x1 + 1; x < x2; x++)
        {
            buf[y1, x] = h;
            buf[y2, x] = h;
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buf[y, x1] = v;
            buf[y, x2] = v;
        }
    }

    private List<MapPiece> BuildMapPiecesFromAvailableCharts(VoyageStrategy strategy = null)
    {
        var pieces = new List<MapPiece>();
        var i = 0;
        foreach (var chart in GetAvailableCharts())
        {
            if (chart.Item.TryGetComponent(out DeepwaterChart c))
            {
                var rotation = (Direction)c.Room.Path;
                var chartName = c.Room.Name ?? "";
                var itemMods = chart.Item.GetComponent<Mods>();

                var modifiers = new List<Modifier> { new("Default", 1) };
                foreach (var im in itemMods?.ImplicitMods ?? [])
                {
                    var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                        .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));

                    // An active strategy replaces the configured weights with
                    // its own (unlisted mods count 0), per the source strategy
                    // definitions; tags stay user-configured.
                    var weight = strategy?.WeightFor(im.RawName) ?? chartMod?.Weight.Value ?? 0;
                    var isGlobal = strategy != null
                        ? im.RawName.StartsWith("MapDeepwaterChartVoyage", StringComparison.Ordinal)
                        : chartMod?.IsGlobal.Value ?? false;
                    modifiers.Add(new Modifier(im.RawName, weight, isGlobal,
                        ModifierTagParser.Parse(chartMod?.Tags.Value, ModifierTag.None), im.Value1));
                }

                if (strategy != null)
                {
                    // Weight-0 explicit (map) mods so strategy reward-stat rules
                    // can see quantity/sulphur/pack rolls; they never score.
                    foreach (var em in itemMods?.ExplicitMods ?? [])
                        modifiers.Add(new Modifier(em.RawName, 0, false, ModifierTag.None, em.Value1));
                }

                pieces.Add(new MapPiece(i,
                    int.PopCount((int)rotation) switch
                    {
                        4 => PieceType.Cross,
                        3 => PieceType.Tee,
                        1 => PieceType.Single,
                        2 => rotation.HasFlag(Direction.Left) == rotation.HasFlag(Direction.Right)
                            ? PieceType.Straight
                            : PieceType.Corner,
                        _ => PieceType.Single
                    }, rotation, modifiers, chartName));
            }

            i++;
        }

        return pieces;
    }

    private IReadOnlyList<BorderEffect>[,] BuildTileBorders(VoyageWindow tree)
    {
        var modsPerTileIndex = GetTileMods(tree);
        var tileBorders = new IReadOnlyList<BorderEffect>[3, 3];
        for (var tileIndex = 0; tileIndex < 9; tileIndex++)
        {
            var borderMods = modsPerTileIndex.GetValueOrDefault(tileIndex) ?? [];
            tileBorders[tileIndex / 3, tileIndex % 3] = borderMods.Select(m =>
            {
                var setting = Settings.VoyageSettings.BorderModifiers.Content
                    .FirstOrDefault(c => c.Id.Value.Equals(m.RawName, StringComparison.OrdinalIgnoreCase));
                return new BorderEffect(
                    m.RawName,
                    ModifierTagParser.Parse(setting?.Tags.Value, ModifierTag.All),
                    setting?.ValueMultiplier.Value ?? 1,
                    setting?.PerConnection.Value ?? false,
                    setting?.AffectsPlacedChart.Value ?? false);
            }).ToList();
        }

        return tileBorders;
    }

    private static void LogPlacement(VoyagePlacementRules.Result placement)
    {
        if (placement == null)
            return;

        var savedBits = new List<string>();
        if (placement.SavedKisharaCount > 0)
            savedBits.Add($"{placement.SavedKisharaCount} Kishara (place boss yourself)");
        if (placement.SavedPelagicCount > 0)
            savedBits.Add($"{placement.SavedPelagicCount} Pelagic");
        if (placement.SavedFarmCount > 0)
            savedBits.Add($"{placement.SavedFarmCount} Anchorfield (no-consume)");
        if (placement.SavedClamCount > 0)
            savedBits.Add($"{placement.SavedClamCount} Clam (for Unique Amulet2)");
        if (placement.SavedUniqueAmuletCount > 0)
            savedBits.Add($"{placement.SavedUniqueAmuletCount} Unique Amulet2");
        if (placement.SavedStrongboxCount > 0)
            savedBits.Add($"{placement.SavedStrongboxCount} boxes (Strongboxes/Diviner/Arcanist)");
        if (placement.SavedOperativeBoxCount > 0)
            savedBits.Add($"{placement.SavedOperativeBoxCount} Operative boxes");
        if (placement.SavedStarfishCount > 0)
            savedBits.Add($"{placement.SavedStarfishCount} Starfish");
        if (placement.SavedAdjacentRareCount > 0)
            savedBits.Add($"{placement.SavedAdjacentRareCount} adj. rare T2");
        if (placement.SavedRareVoyageCount > 0)
            savedBits.Add($"{placement.SavedRareVoyageCount} voyage rares");
        if (placement.SavedLostMessageCount > 0)
            savedBits.Add($"{placement.SavedLostMessageCount} Lost Message");
        if (savedBits.Count > 0)
            DebugWindow.LogMsg($"Voyage: saved {string.Join(", ", savedBits)} for better borders", 5);
        if (placement.Locks.Count > 0)
            DebugWindow.LogMsg($"Voyage: {placement.Locks.Count} strategy lock(s), solver fills the rest", 5);
    }

    private static HashSet<int> GetInventorySpecialtyIndices(List<NormalInventoryItem> charts)
    {
        var roomNames = new List<string>(charts.Count);
        var modsPerChart = new List<IReadOnlyList<(string RawName, int Value1)>>(charts.Count);

        foreach (var chart in charts)
        {
            var room = "";
            if (chart?.Entity != null && chart.Entity.TryGetComponent(out DeepwaterChart c))
                room = c.Room.Name ?? "";
            roomNames.Add(room);

            var mods = chart?.Entity?.GetComponent<Mods>()?.ImplicitMods;
            if (mods == null || mods.Count == 0)
            {
                modsPerChart.Add([]);
                continue;
            }

            modsPerChart.Add(mods.Select(m => (m.RawName, m.Value1)).ToList());
        }

        return VoyagePlacementRules.SelectInventorySpecialtyIndices(roomNames, modsPerChart);
    }

    private static string TrimChartPrefix(string name)
    {
        if (name.StartsWith("MapDeepwaterChartVoyage", StringComparison.Ordinal))
            return name["MapDeepwaterChartVoyage".Length..];
        if (name.StartsWith("MapDeepwaterChartAdjacent", StringComparison.Ordinal))
            return name["MapDeepwaterChartAdjacent".Length..];
        return name;
    }
}
