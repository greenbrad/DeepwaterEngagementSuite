using System.Collections.Generic;

namespace DeepwaterEngagementSuite.VoyagePlannerData;

public record VoyageSolution(
    MapPiecePlacement[,] Grid,
    double TotalScore,
    bool IsValid,
    // Ranking objective when a strategy is active: reward + rule bonuses -
    // layout penalties. Null when no strategy shaped the solve.
    double? StrategyObjective = null);

public record VoyageSolutionResult(
    List<VoyageSolution> Solutions,
    long NodesExplored,
    long NodesPruned);
