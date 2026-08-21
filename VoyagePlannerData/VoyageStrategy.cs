using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepwaterEngagementSuite.VoyagePlannerData;

/// <summary>
/// One positional shaping rule of a strategy. Cells are in PLUGIN order
/// (row 0 = bottom, cell = row * 3 + col), or resolved dynamically from the
/// rolled borders via <see cref="NearBorderId"/>.
///
/// <see cref="Bonus"/> applies to charts matching <see cref="ModPrefixes"/> /
/// <see cref="RoomNames"/>; <see cref="RewardStatSubstrings"/> additionally
/// credits any chart on the cell with value/100 × <see cref="RewardStatPer"/>
/// summed over its mods whose raw name contains one of the substrings.
/// </summary>
public sealed record StrategyRule(
    int[] Cells = null,
    string NearBorderId = null,
    bool AdjacentToBorder = false,
    string[] ModPrefixes = null,
    string[] RoomNames = null,
    string[] RewardStatSubstrings = null,
    double RewardStatPer = 0,
    double Bonus = 0,
    string Label = null);

public sealed record StrategyLayoutVariant(string Id, string Label, Direction[] Arms);

/// <summary>Charts held back from a strategy's solve pool (another strategy's fuel).</summary>
public sealed record ReservationGroup(string Label, string[] ModPrefixes = null, string[] RoomNames = null);

/// <summary>Pieces a strategy needs before it's worth running.</summary>
public sealed record StrategyRequirement(string Label, int Count, string[] ModPrefixes = null, string[] RoomNames = null);

/// <summary>How well a strategy fits the current borders and chart inventory.</summary>
public sealed record StrategySuggestion(
    VoyageStrategy Strategy,
    bool BorderSatisfied,
    int RequirementsMet,
    int RequirementsTotal,
    List<string> Missing,
    double Score)
{
    public bool Ready => BorderSatisfied && RequirementsMet == RequirementsTotal;
}

/// <summary>
/// A curated voyage strategy: reward-weight override (unlisted mods count 0),
/// position rules and an optional exact connector layout. Ported from
/// one-more-map's solver strategies (Milkybk_'s "Curse of the Allflame Buffs
/// and My Strategy" et al.), with mod ids mapped to this game's raw names.
/// </summary>
public sealed record VoyageStrategy(
    string Id,
    string Name,
    string Tagline,
    string[] Guide,
    Dictionary<string, double> Weights,
    StrategyRule[] Rules,
    StrategyLayoutVariant[] Layouts = null,
    double LayoutPenalty = 300,
    ReservationGroup[] Reservations = null,
    // Rare-implicit charts are Divine-strategy fuel and Fracture charts are
    // Meatfish fuel: held back from every strategy that doesn't opt in.
    bool AllowRareImplicits = false,
    bool AllowFractureCharts = false,
    StrategyRequirement[] Requirements = null,
    // A border roll the strategy hinges on; without it the strategy is not
    // suggested at all.
    string RequiredBorderId = null,
    // Relative payoff used to rank suggestions when a strategy is ready.
    int SuggestionWeight = 50);

public static class VoyageStrategies
{
    private const string OpBox = "MapDeepwaterChartAdjacentOperativeBox";
    private const string DivBox = "MapDeepwaterChartAdjacentDivinerBox";
    private const string ArcBox = "MapDeepwaterChartAdjacentArcanistBox";
    private const string Msg = "MapDeepwaterChartAdjacentLostMessage";
    private const string Box = "MapDeepwaterChartAdjacentStrongboxes";
    private const string Star = "MapDeepwaterChartAdjacentStarfish";
    private const string Lantern = "MapDeepwaterChartAdjacentGoldenLanterns";
    private const string Pantheon = "MapDeepwaterChartAdjacentPantheon";
    private const string Wisps = "MapDeepwaterChartAdjacentWisps";
    private const string AdjRare = "MapDeepwaterChartAdjacentIncreasedRareMonsters";
    private const string AdjMagic = "MapDeepwaterChartAdjacentIncreasedMagicMonsters";
    private const string VoyRare = "MapDeepwaterChartVoyageIncreasedRareMonsters";
    private const string VoyMagic = "MapDeepwaterChartVoyageIncreasedMagicMonsters";
    private const string VoyMinMagic = "MapDeepwaterChartVoyageMinimumMagicMonsters";
    private const string Possess = "MapDeepwaterChartVoyageMonstersPossessed";
    private const string Fracture = "MapDeepwaterChartVoyageRareFracture";
    private const string NoEquip = "MapDeepwaterChartVoyageNoEquipmentDrops";
    private const string VoyQuant = "MapDeepwaterChartVoyageQuantity";
    private const string VoySulph = "MapDeepwaterChartVoyageResourceFound";

    private const string DivineBorder = VoyagePlacementRules.RareDivine;
    private const string FilthscrabbleBorder = "DeepwaterBorderGiantOctopus";

    private const string SeaPillarsRoom = "Sea Pillars";
    private const string PelagicRoom = VoyagePlacementRules.PelagicRoomName;
    private const string AnchorfieldRoom = VoyagePlacementRules.AnchorfieldRoomName;

    private static readonly string[] SpeedrunCenterMods = [OpBox, DivBox, Msg];

    // Plugin cell order: row 0 = bottom. Center = 4; sides = {1,3,5,7};
    // corners = {0,2,6,8}; 7 = top-middle, 1 = bottom-middle, 5 = right-middle.
    private static readonly int[] AllCells = [0, 1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly int[] Center = [4];
    private static readonly int[] NotCenter = [0, 1, 2, 3, 5, 6, 7, 8];
    private static readonly int[] Sides = [1, 3, 5, 7];
    private static readonly int[] Corners = [0, 2, 6, 8];

    private static readonly ReservationGroup MeatfishKeepers = new("Meatfish keepers",
        [Star, Pantheon, Lantern, Possess, Fracture, NoEquip, Wisps], [SeaPillarsRoom]);

    private static readonly ReservationGroup EtherealKeepers = new("Ethereal keepers",
        [Lantern, NoEquip, Wisps, AdjMagic, VoyMinMagic]);

    private static ReservationGroup DivineKeepers(bool includeSpeedrunCentres) => new("Divine keepers",
        includeSpeedrunCentres
            ? [AdjRare, VoyRare, Star, Box, OpBox, DivBox, Msg]
            : [AdjRare, VoyRare, Star, Box],
        [SeaPillarsRoom, PelagicRoom]);

    /// <summary>
    /// Rank all strategies against the rolled borders and the chart inventory.
    /// Border-gated strategies without their border are excluded. Ready
    /// strategies rank by payoff; unready ones are deeply discounted so a
    /// runnable interim strategy always beats an aspirational one.
    /// </summary>
    public static List<StrategySuggestion> RankStrategies(
        IReadOnlyList<(IReadOnlyList<string> ModNames, string Room)> charts,
        IReadOnlyList<BorderEffect>[,] tileBorders)
    {
        var rolledBorders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tileBorders != null)
        {
            for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            foreach (var b in tileBorders[r, c] ?? [])
                rolledBorders.Add(b.Name);
        }

        var suggestions = new List<StrategySuggestion>();
        foreach (var strategy in All)
        {
            var borderOk = strategy.RequiredBorderId == null ||
                           rolledBorders.Contains(strategy.RequiredBorderId);
            if (!borderOk)
                continue;

            var requirements = strategy.Requirements ?? [];
            var met = 0;
            var missing = new List<string>();
            var consumed = new bool[charts.Count];
            foreach (var req in requirements)
            {
                var have = 0;
                for (var i = 0; i < charts.Count && have < req.Count; i++)
                {
                    if (consumed[i])
                        continue;

                    var matches =
                        (req.ModPrefixes is { Length: > 0 } && charts[i].ModNames.Any(m =>
                            req.ModPrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase)))) ||
                        (req.RoomNames is { Length: > 0 } && !string.IsNullOrEmpty(charts[i].Room) &&
                         req.RoomNames.Any(rm => charts[i].Room.Contains(rm, StringComparison.OrdinalIgnoreCase)));
                    if (!matches)
                        continue;

                    consumed[i] = true;
                    have++;
                }

                if (have >= req.Count)
                    met++;
                else
                    missing.Add($"{req.Count - have}x {req.Label}");
            }

            var ready = met == requirements.Length;
            var fraction = requirements.Length == 0 ? 1.0 : (double)met / requirements.Length;
            var score = ready
                ? strategy.SuggestionWeight
                : strategy.SuggestionWeight * fraction * 0.3;
            suggestions.Add(new StrategySuggestion(strategy, borderOk, met, requirements.Length, missing, score));
        }

        return suggestions.OrderByDescending(s => s.Score).ToList();
    }

    /// <summary>
    /// True when a chart is another strategy's fuel and should be excluded
    /// from this strategy's solve pool: matches one of the strategy's
    /// reservation groups, or the global rare-implicit / fracture holdbacks.
    /// </summary>
    public static bool IsReservedChart(VoyageStrategy strategy, IEnumerable<string> modNames, string roomName)
    {
        var names = modNames as IReadOnlyList<string> ?? modNames.ToList();

        bool MatchesMods(string[] prefixes) => prefixes is { Length: > 0 } && names.Any(m =>
            prefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        if (!strategy.AllowRareImplicits && MatchesMods([AdjRare, VoyRare]))
            return true;
        if (!strategy.AllowFractureCharts && MatchesMods([Fracture]))
            return true;

        foreach (var group in strategy.Reservations ?? [])
        {
            if (MatchesMods(group.ModPrefixes))
                return true;
            if (group.RoomNames is { Length: > 0 } && !string.IsNullOrEmpty(roomName) &&
                group.RoomNames.Any(r => roomName.Contains(r, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Build a layout from 9 arm strings in one-more-map's order (top-left,
    /// row-major, letters NESW = screen directions), converting to plugin cell
    /// order (row 0 = bottom) and Direction flags.
    /// </summary>
    private static Direction[] Layout(params string[] cells)
    {
        var result = new Direction[9];
        for (var i = 0; i < 9; i++)
        {
            Direction arms = 0;
            foreach (var ch in cells[i])
            {
                arms |= ch switch
                {
                    'N' => Direction.Up,
                    'E' => Direction.Right,
                    'S' => Direction.Down,
                    'W' => Direction.Left,
                    _ => 0,
                };
            }

            result[(2 - i / 3) * 3 + i % 3] = arms;
        }

        return result;
    }

    public static readonly IReadOnlyList<VoyageStrategy> All =
    [
        new VoyageStrategy(
            "alc-and-go",
            "Alc & Go",
            "Burn the charts nothing else wants - one-lane highways, hope for random encounters.",
            [
                "Forms single-lane highways (or the S-snake variant) from whatever shapes are spare.",
                "Don't care what's on the tiles: scattered loot, sulphur and random encounters.",
                "Alc, go, place every lantern, click everything, leave.",
            ],
            new Dictionary<string, double> { [VoyQuant] = 2, [VoySulph] = 2 },
            [
                new StrategyRule(Cells: AllCells, RewardStatSubstrings: ["Quantity", "Resource", "Sulphur"], RewardStatPer: 2),
            ],
            Layouts:
            [
                new StrategyLayoutVariant("highway", "Three-lane highway",
                    Layout("S", "S", "S", "NS", "NS", "NS", "NE", "NEW", "NW")),
                new StrategyLayoutVariant("snake", "S-snake (one continuous path)",
                    Layout("ES", "EW", "W", "NE", "EW", "SW", "E", "EW", "NW")),
            ],
            LayoutPenalty: 15,
            Reservations: [DivineKeepers(true), MeatfishKeepers, EtherealKeepers],
            SuggestionWeight: 10),

        new VoyageStrategy(
            "anchorfield-fishing",
            "Anchorfield Fishing",
            "Fish for the chaos-to-divine blessing, then crack the Anchorfield open - jackpot or go next.",
            [
                "Put ONE Anchorfield chart in - reroll it to decent Quantity. Fill the rest with high-Quantity charts.",
                "Run normally hunting ONE blessing: Chaos Orbs become Divine Orbs. Blast the Anchorfield but open NO Sunken Loot there yet.",
                "Found the blessing? Sprint back and open every Sunken Loot - the 20% chaos-to-divine conversion turns them into Divines.",
                "No blessing and ~6 lanterns left? Take the scraps or just leave - you're fishing for the jackpot.",
            ],
            new Dictionary<string, double> { [VoyQuant] = 8, [VoySulph] = 1 },
            [
                new StrategyRule(Cells: AllCells, RoomNames: [AnchorfieldRoom], Bonus: 40),
                new StrategyRule(Cells: AllCells, RewardStatSubstrings: ["Quantity"], RewardStatPer: 6),
            ],
            Requirements: [new StrategyRequirement("Anchorfield chart", 1, RoomNames: [AnchorfieldRoom])],
            SuggestionWeight: 60),

        new VoyageStrategy(
            "milky-speedrun",
            "Speedrun Strongboxes",
            "Milky's interim farm - burn spare charts, crack boxes, get in, get out.",
            [
                "Exactly ONE Operative's chart in the CENTRE (best); Diviner's or Message-in-a-Bottle are the fallbacks.",
                "Roll charts to 110%+ Item Quantity BEFORE running - quantity scales the boxes. Highest quantity on the four sides.",
                "Corners are junk that makes the connectors line up. Take Alch/Scour/Exalt in to juice every box.",
                "If a Filthscrabble border appears, the solver parks your highest-sulphur chart on its tile.",
            ],
            new Dictionary<string, double>
            {
                [OpBox] = 10, [DivBox] = 7, [Msg] = 7, [VoyQuant] = 5, [VoySulph] = 3,
            },
            [
                new StrategyRule(Cells: Center, ModPrefixes: [OpBox], Bonus: 55, Label: "Operative's Box"),
                new StrategyRule(Cells: Center, ModPrefixes: [DivBox, Msg], Bonus: 40, Label: "Diviner's / Message"),
                new StrategyRule(Cells: NotCenter, ModPrefixes: SpeedrunCenterMods, Bonus: -40),
                new StrategyRule(Cells: Sides, RewardStatSubstrings: ["Quantity"], RewardStatPer: 6, Label: "High Quantity"),
                new StrategyRule(NearBorderId: FilthscrabbleBorder, RewardStatSubstrings: ["Resource", "Sulphur"], RewardStatPer: 8, Label: "High Sulphur"),
            ],
            Reservations: [DivineKeepers(false), MeatfishKeepers, EtherealKeepers],
            Requirements: [new StrategyRequirement("Diviner's / Operative's / Message chart (centre)", 1, SpeedrunCenterMods)]),

        new VoyageStrategy(
            "milky-meatfish",
            "Meatfish",
            "Milky's big one - possessed, Pantheon-touched giga-starfish rares that rain uniques.",
            [
                "Composition: 2x Starfish, 1x Pantheon, 2x Sea-Pillars (corners), 2x Golden Lanterns, 1x Possessed Rares, 1x No-Equipment.",
                "Starfish always top- and bottom-middle; Pantheon only ever right-middle; Golden Lantern preferably centre.",
                "\"Monsters cannot drop Equipment\" is the jackpot piece - Rares Fracture is the fallback.",
                "Collect every lantern (~280% Quantity, 840 Rarity), kill all the giga-rares. Very risky, all-or-nothing.",
            ],
            new Dictionary<string, double>
            {
                [Star] = 10, [Pantheon] = 10, [Lantern] = 10, [Possess] = 10,
                [Fracture] = 8, [NoEquip] = 8, [Wisps] = 6,
            },
            [
                new StrategyRule(Cells: [7, 1], ModPrefixes: [Star], Bonus: 80, Label: "Starfish"),
                new StrategyRule(Cells: [0, 2, 3, 4, 5, 6, 8], ModPrefixes: [Star], Bonus: -80),
                new StrategyRule(Cells: [5], ModPrefixes: [Pantheon], Bonus: 80, Label: "Pantheon"),
                new StrategyRule(Cells: [0, 1, 2, 3, 4, 6, 7, 8], ModPrefixes: [Pantheon], Bonus: -80),
                new StrategyRule(Cells: Center, ModPrefixes: [Lantern], Bonus: 40, Label: "Golden Lantern"),
                new StrategyRule(Cells: Corners, RoomNames: [SeaPillarsRoom], Bonus: 40, Label: "Sea Pillars"),
                new StrategyRule(Cells: [1, 3, 4, 5, 7], RoomNames: [SeaPillarsRoom], Bonus: -40),
            ],
            Layouts:
            [
                new StrategyLayoutVariant("meatfish", "Milky's board (10 connections)",
                    Layout("ES", "ESW", "SW", "NS", "NES", "NSW", "NS", "NE", "NW")),
            ],
            LayoutPenalty: 6,
            AllowFractureCharts: true,
            Requirements:
            [
                new StrategyRequirement("Giant Starfish chart", 2, [Star]),
                new StrategyRequirement("Pantheon (or 4k Wisp) chart", 1, [Pantheon, Wisps]),
                new StrategyRequirement("Sea-Pillar chart (corners)", 2, RoomNames: [SeaPillarsRoom]),
                new StrategyRequirement("Golden Lantern chart", 2, [Lantern]),
                new StrategyRequirement("Possessed Rares chart", 1, [Possess]),
                new StrategyRequirement("No-Equipment (or Fracture) chart", 1, [NoEquip, Fracture]),
            ],
            SuggestionWeight: 80),

        new VoyageStrategy(
            "milky-ethereal",
            "Magic Ethereal",
            "Milky's magic-monster variant - wisps, lanterns and everything at least Magic.",
            [
                "Field reports are underwhelming (~5 div runs); kept for reference.",
                "Wisp charts on the four sides, Golden Lanterns on the corners, the Crossing chart dead centre.",
                "Go wide on magic monsters: All Monsters at least Magic + increased Magic Monsters.",
            ],
            new Dictionary<string, double>
            {
                [Wisps] = 10, [VoyMinMagic] = 10, [AdjMagic] = 9, [VoyMagic] = 9,
                [Lantern] = 8, [NoEquip] = 8,
            },
            [
                new StrategyRule(Cells: Sides, ModPrefixes: [Wisps], Bonus: 6, Label: "Wisps"),
                new StrategyRule(Cells: [6, 8, 2], ModPrefixes: [Lantern], Bonus: 5, Label: "Lantern"),
                new StrategyRule(Cells: Center, ModPrefixes: [AdjMagic, Wisps], Bonus: 5, Label: "Magic / Wisps"),
            ],
            Layouts:
            [
                new StrategyLayoutVariant("ethereal", "Milky's board (11 connections)",
                    Layout("ES", "ESW", "SW", "NES", "NESW", "NSW", "NS", "NES", "NW")),
            ],
            Requirements:
            [
                new StrategyRequirement("Wildwood Wisp chart", 4, [Wisps]),
                new StrategyRequirement("Golden Lantern chart", 3, [Lantern]),
            ],
            SuggestionWeight: 30),

        new VoyageStrategy(
            "divine-border-rares",
            "Divine Border Rares",
            "Roll a Divine border, park a Sea-Pillar chart on it, and drown that tile in rares.",
            [
                "Needs the \"+1 Divine Orb per Rare Monster\" border roll (reroll with Dead Man's Sulphur).",
                "The Sea-Pillar chart sits ON the Divine tile - its pillars rain extra rares into that exact area.",
                "\"+5 Strongboxes\" adjacent charts feed it: roll the boxes for Stream of Monsters / of Rarity - 7 rares per box, a Divine each.",
                "Starfish also feed it; increased Rare Monsters charts fill the rest.",
            ],
            new Dictionary<string, double>
            {
                [AdjRare] = 10, [VoyRare] = 10, [Star] = 8, [Box] = 8, [Possess] = 6,
            },
            [
                new StrategyRule(NearBorderId: DivineBorder, RoomNames: [SeaPillarsRoom], Bonus: 100, Label: "Sea Pillars"),
                new StrategyRule(NearBorderId: DivineBorder, AdjacentToBorder: true, ModPrefixes: [Box + "3"], Bonus: 35, Label: "+5 Strongboxes"),
                new StrategyRule(NearBorderId: DivineBorder, AdjacentToBorder: true, ModPrefixes: [Box + "1", Box + "2"], Bonus: 22),
                new StrategyRule(NearBorderId: DivineBorder, AdjacentToBorder: true, ModPrefixes: [Star], Bonus: 15),
            ],
            AllowRareImplicits: true,
            RequiredBorderId: DivineBorder,
            Requirements:
            [
                new StrategyRequirement("Sea-Pillar chart", 1, RoomNames: [SeaPillarsRoom]),
                new StrategyRequirement("Starfish or Strongbox chart", 3, [Star, Box]),
                new StrategyRequirement("Increased Rares chart", 5, [AdjRare, VoyRare]),
            ],
            SuggestionWeight: 90),

        new VoyageStrategy(
            "cutedog-divine-boxes",
            "Divine Strongboxes",
            "cutedog_'s Divine-border variant - Pelagic Abyss on the Divine tile, any strongboxes feeding it.",
            [
                "Needs the \"+1 Divine Orb per Rare\" border. Pelagic Abyss chart with high Pack Size on that exact tile.",
                "3x strongbox adjacent charts (ANY type) beside the Divine tile - each rolled box is up to 7 guaranteed divines.",
                "Every other tile: voyage-wide increased Rare Monsters.",
            ],
            new Dictionary<string, double>
            {
                [VoyRare] = 10, [AdjRare] = 8, [Box] = 9, [DivBox] = 8, [ArcBox] = 8, [OpBox] = 8,
            },
            [
                new StrategyRule(NearBorderId: DivineBorder, RoomNames: [PelagicRoom], Bonus: 80, Label: "Pelagic Abyss"),
                new StrategyRule(NearBorderId: DivineBorder, RewardStatSubstrings: ["PackSize"], RewardStatPer: 8),
                new StrategyRule(NearBorderId: DivineBorder, AdjacentToBorder: true,
                    ModPrefixes: [Box, DivBox, ArcBox, OpBox], Bonus: 25),
            ],
            AllowRareImplicits: true,
            RequiredBorderId: DivineBorder,
            Requirements:
            [
                new StrategyRequirement("Pelagic Abyss chart", 1, RoomNames: [PelagicRoom]),
                new StrategyRequirement("Strongbox adjacent chart (any type)", 3, [Box, DivBox, ArcBox, OpBox]),
                new StrategyRequirement("Increased Rares (voyage) chart", 5, [VoyRare]),
            ],
            SuggestionWeight: 85),
    ];

    public static VoyageStrategy ById(string id) => All.FirstOrDefault(s => s.Id == id);

    public static double WeightFor(this VoyageStrategy strategy, string rawModName)
    {
        double best = 0;
        foreach (var (prefix, weight) in strategy.Weights)
        {
            if (rawModName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                best = Math.Max(best, weight);
        }

        return best;
    }
}

/// <summary>
/// A strategy resolved against a concrete puzzle: per-piece-per-cell objective
/// bonuses (rules and reward stats applied against the rolled borders) plus
/// the layout targets. Consumed by both solvers as an extra ranking term;
/// displayed solution scores stay pure reward.
/// </summary>
public sealed class StrategyContext
{
    private const int GridSize = 3;
    private const int Cells = GridSize * GridSize;

    /// <summary>[pieceIndex][cell] objective bonus.</summary>
    public double[][] Bonus { get; private init; }

    /// <summary>Exact target arms per cell (null cell = unconstrained).</summary>
    public Direction?[] LayoutArms { get; private init; }

    public double LayoutPenalty { get; private init; }

    public static StrategyContext Build(
        VoyageStrategy strategy,
        string layoutId,
        IReadOnlyList<MapPiece> pieces,
        IReadOnlyList<BorderEffect>[,] tileBorders)
    {
        var bonus = new double[pieces.Count][];
        for (var i = 0; i < pieces.Count; i++)
            bonus[i] = new double[Cells];

        foreach (var rule in strategy.Rules)
        {
            var cells = ResolveCells(rule, tileBorders);
            if (cells.Count == 0)
                continue;

            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];
                var value = 0.0;

                if (rule.Bonus != 0 && Matches(piece, rule))
                    value += rule.Bonus;

                if (rule.RewardStatSubstrings is { Length: > 0 })
                {
                    var statSum = piece.Modifiers
                        .Where(m => rule.RewardStatSubstrings.Any(s =>
                            m.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                        .Sum(m => m.Value1);
                    value += statSum / 100.0 * rule.RewardStatPer;
                }

                if (value == 0)
                    continue;

                foreach (var cell in cells)
                    bonus[i][cell] += value;
            }
        }

        var arms = new Direction?[Cells];
        var variant = strategy.Layouts is { Length: > 0 }
            ? strategy.Layouts.FirstOrDefault(l => l.Id == layoutId) ?? strategy.Layouts[0]
            : null;
        if (variant != null)
        {
            for (var cell = 0; cell < Cells; cell++)
                arms[cell] = variant.Arms[cell];
        }

        return new StrategyContext
        {
            Bonus = bonus,
            LayoutArms = arms,
            LayoutPenalty = variant != null ? strategy.LayoutPenalty : 0,
        };
    }

    private static bool Matches(MapPiece piece, StrategyRule rule)
    {
        if (rule.ModPrefixes is { Length: > 0 } &&
            piece.Modifiers.Any(m => rule.ModPrefixes.Any(p =>
                m.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            return true;

        if (rule.RoomNames is { Length: > 0 } && !string.IsNullOrEmpty(piece.Name) &&
            rule.RoomNames.Any(r => piece.Name.Contains(r, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static List<int> ResolveCells(StrategyRule rule, IReadOnlyList<BorderEffect>[,] tileBorders)
    {
        if (rule.Cells != null)
            return rule.Cells.ToList();

        if (rule.NearBorderId == null || tileBorders == null)
            return [];

        var borderCells = new List<int>();
        for (var cell = 0; cell < Cells; cell++)
        {
            var borders = tileBorders[cell / GridSize, cell % GridSize] ?? [];
            if (borders.Any(b => b.Name.Equals(rule.NearBorderId, StringComparison.OrdinalIgnoreCase)))
                borderCells.Add(cell);
        }

        if (!rule.AdjacentToBorder)
            return borderCells;

        var neighbours = new HashSet<int>();
        foreach (var cell in borderCells)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;
            if (r > 0) neighbours.Add(cell - GridSize);
            if (r < GridSize - 1) neighbours.Add(cell + GridSize);
            if (c > 0) neighbours.Add(cell - 1);
            if (c < GridSize - 1) neighbours.Add(cell + 1);
        }

        return neighbours.ToList();
    }

    /// <summary>
    /// The tiles a strategy wants specific charts on, with display labels —
    /// for drawing placement guidance over the board. Border-dependent rules
    /// resolve against the currently rolled borders; rules covering the whole
    /// board are skipped as noise.
    /// </summary>
    public static List<(int Cell, StrategyRule Rule)> WantedPlacements(
        VoyageStrategy strategy, IReadOnlyList<BorderEffect>[,] tileBorders)
    {
        var result = new List<(int, StrategyRule)>();
        foreach (var rule in strategy.Rules)
        {
            if (string.IsNullOrEmpty(rule.Label))
                continue;

            var cells = ResolveCells(rule, tileBorders);
            if (cells.Count is 0 or >= Cells)
                continue;

            foreach (var cell in cells)
                result.Add((cell, rule));
        }

        return result;
    }

    /// <summary>Does a placed chart (its implicit mod names + room name) satisfy this rule's matcher?</summary>
    public static bool ChartSatisfiesRule(StrategyRule rule, IEnumerable<string> modNames, string roomName)
    {
        if (rule.ModPrefixes is { Length: > 0 } && modNames.Any(m =>
                rule.ModPrefixes.Any(p => m.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            return true;

        if (rule.RoomNames is { Length: > 0 } && !string.IsNullOrEmpty(roomName) &&
            rule.RoomNames.Any(r => roomName.Contains(r, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    /// <summary>Bonus minus layout penalty for one concrete placement.</summary>
    public double PlacementDelta(int pieceIndex, int cell, Direction connections)
    {
        var delta = Bonus[pieceIndex][cell];
        if (LayoutArms[cell] is { } target && connections != target)
            delta -= LayoutPenalty;
        return delta;
    }

    /// <summary>Admissible best-case bonus of placing any unused piece on a cell.</summary>
    public double MaxBonusAt(int cell, bool[] pieceUsed)
    {
        var best = double.NegativeInfinity;
        for (var i = 0; i < Bonus.Length; i++)
        {
            if (pieceUsed != null && pieceUsed[i])
                continue;
            if (Bonus[i][cell] > best)
                best = Bonus[i][cell];
        }

        return double.IsNegativeInfinity(best) ? 0 : best;
    }
}
