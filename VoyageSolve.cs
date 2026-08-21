using System.Collections.Generic;
using System.Linq;
using DeepwaterEngagementSuite.VoyagePlannerData;

namespace DeepwaterEngagementSuite;

public sealed class VoyageSolve
{
    public VoyageScorer Scorer { get; private set; }
    public VoyagePlacementRules.Result Placement { get; private set; }
    public VoyagePuzzle Puzzle { get; private set; }
    public int ReservedCount { get; private set; }
    public bool NotEnoughFreeCharts { get; private set; }

    public IEnumerable<VoyageSolutionResult> Run(
        List<MapPiece> pieces,
        IReadOnlyList<BorderEffect>[,] tileBorders,
        VoyagePlannerSettings settings = null,
        VoyageStrategy strategy = null,
        string strategyLayoutId = null,
        bool protectKeepers = true)
    {
        settings ??= new VoyagePlannerSettings();
        ReservedCount = 0;
        NotEnoughFreeCharts = false;

        StrategyContext context = null;
        if (strategy != null)
        {
            // An active strategy takes full control of placement (its rules
            // and layout replace the built-in auto-locking and piece saving).
            // Keeper charts - other strategies' fuel - are held back from the
            // pool unless protection is turned off.
            var pool = pieces.ToList();
            if (protectKeepers)
            {
                pool = pieces.Where(p => !VoyageStrategies.IsReservedChart(strategy,
                    p.Modifiers.Select(m => m.Name), p.Name)).ToList();
                ReservedCount = pieces.Count - pool.Count;
                NotEnoughFreeCharts = pool.Count < 9;
            }

            Placement = null;
            Puzzle = new VoyagePuzzle(pool, tileBorders, []);
            context = StrategyContext.Build(strategy, strategyLayoutId, Puzzle.AvailablePieces, tileBorders);
        }
        else
        {
            Placement = VoyagePlacementRules.Apply(pieces, tileBorders);
            Puzzle = new VoyagePuzzle(Placement.Pieces, tileBorders, Placement.Locks);
        }

        Scorer = new VoyageScorer(Puzzle);
        return new VoyagePlannerFast().Solve(Puzzle, settings, context);
    }
}
