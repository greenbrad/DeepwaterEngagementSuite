using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepwaterEngagementSuite.VoyagePlannerData;

namespace DeepwaterEngagementSuite;

public class VoyagePlannerFast
{
    private const int GridSize = 3;
    private const int Cells = GridSize * GridSize;
    private const int States = 1 << Cells;
    private const double LockBonus = 1e9;

    private static readonly (Direction Dir, int Dr, int Dc)[] Dirs =
    [
        (Direction.Up, 1, 0),
        (Direction.Down, -1, 0),
        (Direction.Left, 0, -1),
        (Direction.Right, 0, 1),
    ];

    private static readonly int[] InGrid = BuildInGrid();
    private static readonly int[][] Topologies = BuildTopologies();

    private static int[] BuildInGrid()
    {
        var mask = new int[Cells];
        for (var r = 0; r < GridSize; r++)
        for (var c = 0; c < GridSize; c++)
        foreach (var (dir, dr, dc) in Dirs)
        {
            var nr = r + dr;
            var nc = c + dc;
            if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
            mask[r * GridSize + c] |= (int)dir;
        }

        return mask;
    }

    // Every subset of the 12 internal edges whose open edges connect all nine cells.
    private static int[][] BuildTopologies()
    {
        var edges = new List<(int A, int B, Direction Dir)>();
        for (var r = 0; r < GridSize; r++)
        for (var c = 0; c < GridSize; c++)
        {
            var i = r * GridSize + c;
            if (c < GridSize - 1) edges.Add((i, i + 1, Direction.Right));
            if (r < GridSize - 1) edges.Add((i, i + GridSize, Direction.Up));
        }

        var found = new List<int[]>();
        var neighbours = new List<int>[Cells];

        for (var subset = 0; subset < 1 << 12; subset++)
        {
            var cellMask = new int[Cells];
            for (var i = 0; i < Cells; i++) neighbours[i] = [];

            for (var e = 0; e < edges.Count; e++)
            {
                if ((subset >> e & 1) == 0) continue;
                var (a, b, dir) = edges[e];
                cellMask[a] |= (int)dir;
                cellMask[b] |= (int)dir.Opposite();
                neighbours[a].Add(b);
                neighbours[b].Add(a);
            }

            if (Reaches(neighbours)) found.Add(cellMask);
        }

        return found.ToArray();
    }

    private static bool Reaches(List<int>[] neighbours)
    {
        var seen = 1;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.TryPop(out var cell))
        {
            foreach (var next in neighbours[cell])
            {
                if ((seen >> next & 1) != 0) continue;
                seen |= 1 << next;
                stack.Push(next);
            }
        }

        return seen == States - 1;
    }

    // Pieces with the same rotation set and modifier signature are interchangeable for both
    // connectivity and scoring, so the DP only needs one entry per distinct group (with
    // multiplicity), not one per chart. Locked pieces stay in singleton groups.
    private sealed class Group
    {
        public List<int> Members = [];
        public MapPiece Piece;
        public int ArmCount;
        public int LockedCell = -1;
        public int[] Eligible = new int[Cells];
        public byte[] Rotation = new byte[Cells * 16];

        // Per cell: strategy-layout penalty if this group cannot realize the
        // exact target arms there (applied only when the topology matches the
        // layout's in-grid arms; otherwise the whole cell already pays).
        public double[] LayoutPen = new double[Cells];
    }

    public IEnumerable<VoyageSolutionResult> Solve(
        VoyagePuzzle puzzle, VoyagePlannerSettings settings = null, StrategyContext strategy = null)
    {
        settings ??= new VoyagePlannerSettings();
        var pieces = puzzle.AvailablePieces;
        var n = pieces.Count;
        var topN = Math.Max(1, settings.TopN);

        if (n < Cells)
        {
            yield return new VoyageSolutionResult([], 0, 0);
            yield break;
        }

        // Reported solution scores come from the real scorer so they always agree with the
        // score-details UI and never contain lock bonuses; the solver-internal score (which
        // does contain lock bonuses so locks dominate) is only used for ranking and pruning.
        var scorer = new VoyageScorer(puzzle);

        var borders = new IReadOnlyList<BorderEffect>[Cells];
        for (var cell = 0; cell < Cells; cell++)
            borders[cell] = puzzle.TileBorders?[cell / GridSize, cell % GridSize] ?? [];

        // Cells carrying at least one per-connection border. Their multipliers depend on the
        // connection count of the piece placed there — but connection count is the piece's
        // total arm count, which is rotation-invariant. So exactness only needs a small
        // enumeration of arm counts over these cells; everything else stays separable.
        var perConnCells = new List<int>();
        for (var cell = 0; cell < Cells; cell++)
            if (borders[cell].Any(b => b.PerConnection))
                perConnCells.Add(cell);

        double TileFactor(int cell, ModifierTag tags, int conn)
        {
            double m = 1;
            foreach (var b in borders[cell])
            {
                if (b.AffectsPlacedChart || !ModifierTagParser.Matches(b.Tags, tags)) continue;
                m *= PerConnValue(b, conn);
            }

            return m;
        }

        double ChartFactor(int cell, ModifierTag tags, int conn)
        {
            double m = 1;
            foreach (var b in borders[cell])
            {
                if (!b.AffectsPlacedChart || !ModifierTagParser.Matches(b.Tags, tags)) continue;
                m *= PerConnValue(b, conn);
            }

            return m;
        }

        // conn < 0 means "unknown": use the most optimistic value so bounds stay admissible.
        static double PerConnValue(BorderEffect b, int conn)
        {
            if (!b.PerConnection) return b.Multiplier;
            if (conn >= 0) return Math.Max(0, 1 + (b.Multiplier - 1) * conn);
            double best = 0;
            for (var k = 1; k <= 4; k++)
                best = Math.Max(best, Math.Max(0, 1 + (b.Multiplier - 1) * k));
            return best;
        }

        // ---- group interchangeable pieces ----
        var lockByPiece = new Dictionary<int, LockedPlacement>();
        foreach (var lp in puzzle.LockedPlacements ?? [])
        {
            var idx = pieces.FindIndex(p => p.Id == lp.PieceId);
            if (idx >= 0) lockByPiece[idx] = lp;
        }

        var groups = new List<Group>();
        var groupIndex = new Dictionary<string, int>();
        for (var i = 0; i < n; i++)
        {
            var piece = pieces[i];
            if (lockByPiece.ContainsKey(i))
            {
                groups.Add(new Group { Piece = piece, Members = { i } });
                continue;
            }

            var canon = int.MaxValue;
            for (var rot = 0; rot < piece.DistinctRotations; rot++)
                canon = Math.Min(canon, (int)piece.GetConnections(rot));

            // Strategy bonuses differentiate otherwise-identical pieces (by
            // room name / matched rules), so the bonus row is part of the key.
            var bonusKey = strategy == null
                ? ""
                : string.Join(",", strategy.Bonus[i].Select(b => b.ToString("R")));
            var key = $"{canon}|{GetModifierSignature(piece)}|{bonusKey}";
            if (!groupIndex.TryGetValue(key, out var g))
            {
                g = groups.Count;
                groupIndex[key] = g;
                groups.Add(new Group { Piece = piece });
            }

            groups[g].Members.Add(i);
        }

        // Eligibility per group: which inward arm-sets it can present per cell, plus one
        // rotation per set. Locks pin the piece to its cell and honor a fixed rotation.
        foreach (var group in groups)
        {
            group.ArmCount = BitOperations.PopCount((uint)group.Piece.BaseConnections);
            group.Rotation.AsSpan().Fill(byte.MaxValue);

            var pieceIdx = group.Members[0];
            LockedPlacement lockInfo = null;
            if (group.Members.Count == 1 && lockByPiece.TryGetValue(pieceIdx, out var lp))
            {
                lockInfo = lp;
                group.LockedCell = lp.Row * GridSize + lp.Col;
            }

            for (var rot = 0; rot < group.Piece.DistinctRotations; rot++)
            {
                if (lockInfo?.Rotation is { } lockedRot && rot != lockedRot) continue;
                var conn = (int)group.Piece.GetConnections(rot);
                for (var cell = 0; cell < Cells; cell++)
                {
                    if (group.LockedCell >= 0 && cell != group.LockedCell) continue;
                    var inGrid = conn & InGrid[cell];
                    var slot = cell * 16 + inGrid;
                    if (group.Rotation[slot] != byte.MaxValue) continue;
                    group.Eligible[cell] |= 1 << inGrid;
                    group.Rotation[slot] = (byte)rot;
                }
            }

            // Strategy layout: where possible prefer the rotation realizing the
            // exact target arms (off-grid arms included); where the shape can't,
            // record the per-cell deviation penalty.
            if (strategy != null && strategy.LayoutPenalty > 0)
            {
                for (var cell = 0; cell < Cells; cell++)
                {
                    if (strategy.LayoutArms[cell] is not { } target) continue;
                    if (group.LockedCell >= 0 && cell != group.LockedCell) continue;

                    var realized = false;
                    for (var rot = 0; rot < group.Piece.DistinctRotations; rot++)
                    {
                        if (lockInfo?.Rotation is { } lockedRot && rot != lockedRot) continue;
                        var conn = (int)group.Piece.GetConnections(rot);
                        if (conn != (int)target) continue;
                        group.Rotation[cell * 16 + (conn & InGrid[cell])] = (byte)rot;
                        realized = true;
                        break;
                    }

                    if (!realized)
                        group.LayoutPen[cell] = strategy.LayoutPenalty;
                }
            }
        }

        var groupCount = groups.Count;

        // weight[g][cell] with a given arm-count profile for the per-connection cells
        // (profile == null → optimistic upper-bound weights, used for topology ordering;
        // with no per-connection borders those weights are simply exact).
        double[][] ComputeWeights(int[] connAtCell)
        {
            var sByTag = new Dictionary<ModifierTag, double>();

            double GlobalSum(ModifierTag tags)
            {
                if (sByTag.TryGetValue(tags, out var cached)) return cached;
                double sum = 0;
                for (var cell = 0; cell < Cells; cell++)
                    sum += TileFactor(cell, tags, connAtCell?[cell] ?? -1);
                return sByTag[tags] = sum;
            }

            var weight = new double[groupCount][];
            for (var g = 0; g < groupCount; g++)
            {
                weight[g] = new double[Cells];
                var group = groups[g];
                for (var cell = 0; cell < Cells; cell++)
                {
                    double w = 0;
                    foreach (var mod in group.Piece.Modifiers)
                    {
                        if (mod.Weight == 0) continue;
                        var cf = ChartFactor(cell, mod.Tags, connAtCell?[cell] ?? -1);
                        if (mod.IsGlobal)
                        {
                            w += mod.Weight * cf * GlobalSum(mod.Tags);
                        }
                        else
                        {
                            var r = cell / GridSize;
                            var c = cell % GridSize;
                            double sum = 0;
                            foreach (var (_, dr, dc) in Dirs)
                            {
                                var nr = r + dr;
                                var nc = c + dc;
                                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                                var v = nr * GridSize + nc;
                                sum += TileFactor(v, mod.Tags, connAtCell?[v] ?? -1);
                            }

                            w += mod.Weight * cf * sum;
                        }
                    }

                    weight[g][cell] = w + (strategy?.Bonus[group.Members[0]][cell] ?? 0);
                }
            }

            ApplyLockBonuses(groups, weight);
            return weight;
        }

        var upperWeight = ComputeWeights(null);
        var exactWhenNoPerConn = perConnCells.Count == 0 ? upperWeight : null;

        // Strategy layout targets, split into in-grid arms (constrains the
        // topology) and full arms (constrains piece rotation at the cell).
        var layoutInGrid = new int[Cells];
        Array.Fill(layoutInGrid, -1);
        var hasLayout = false;
        if (strategy is { LayoutPenalty: > 0 })
        {
            for (var cell = 0; cell < Cells; cell++)
            {
                if (strategy.LayoutArms[cell] is not { } target) continue;
                layoutInGrid[cell] = (int)target & InGrid[cell];
                hasLayout = true;
            }
        }

        // Admissible per-topology bound: per-cell best eligible group, per-conn at optimum.
        var bound = new double[Topologies.Length];
        var reachable = new int[Topologies.Length][];
        var topoPen = new double[Topologies.Length];
        for (var t = 0; t < Topologies.Length; t++)
        {
            var topo = Topologies[t];
            var allow = new int[groupCount];
            var total = 0.0;
            var feasible = true;

            for (var cell = 0; cell < Cells && feasible; cell++)
            {
                var best = double.NegativeInfinity;
                for (var g = 0; g < groupCount; g++)
                {
                    if ((groups[g].Eligible[cell] >> topo[cell] & 1) == 0) continue;
                    allow[g] |= 1 << cell;
                    if (upperWeight[g][cell] > best) best = upperWeight[g][cell];
                }

                if (double.IsNegativeInfinity(best)) feasible = false;
                else total += best;
            }

            if (hasLayout)
            {
                for (var cell = 0; cell < Cells; cell++)
                {
                    if (layoutInGrid[cell] >= 0 && topo[cell] != layoutInGrid[cell])
                        topoPen[t] += strategy.LayoutPenalty;
                }
            }

            reachable[t] = allow;
            bound[t] = feasible ? total - topoPen[t] : double.NegativeInfinity;
        }

        var order = Enumerable.Range(0, Topologies.Length).OrderByDescending(t => bound[t]).ToArray();

        // DP entries: one per usable copy of each group (at most 9 copies matter).
        var entryGroup = new List<int>();
        for (var g = 0; g < groupCount; g++)
        {
            var copies = Math.Min(groups[g].Members.Count, Cells);
            for (var k = 0; k < copies; k++) entryGroup.Add(g);
        }

        var entries = entryGroup.ToArray();
        var dpPrev = new double[States];
        var dpNext = new double[States];
        var choice = new byte[entries.Length][];
        for (var i = 0; i < entries.Length; i++) choice[i] = new byte[States];

        var top = new List<(double Internal, VoyageSolution Solution)>(topN);
        var explored = 0L;
        var pruned = 0L;
        var assignment = new int[Cells];
        var profileConn = new int[Cells];
        var groupUse = new int[groupCount];

        for (var o = 0; o < order.Length; o++)
        {
            var t = order[o];

            if (double.IsNegativeInfinity(bound[t]) || (top.Count >= topN && bound[t] <= top[^1].Internal))
            {
                pruned += order.Length - o;
                break;
            }

            explored++;
            var topo = Topologies[t];

            // With per-connection borders, enumerate the arm counts a piece on each such cell
            // could have under this topology; each profile is separable and scored exactly.
            foreach (var profile in EnumerateProfiles(perConnCells, groups, reachable[t], topo))
            {
                double[][] weight;
                if (profile == null)
                {
                    weight = exactWhenNoPerConn;
                }
                else
                {
                    Array.Fill(profileConn, -1);
                    for (var pi = 0; pi < perConnCells.Count; pi++)
                        profileConn[perConnCells[pi]] = profile[pi];
                    weight = ComputeWeights(profileConn);
                }

                var allow = reachable[t];
                if (profile != null)
                {
                    // Restrict per-conn cells to groups whose arm count matches the profile.
                    allow = (int[])allow.Clone();
                    for (var pi = 0; pi < perConnCells.Count; pi++)
                    {
                        var cell = perConnCells[pi];
                        for (var g = 0; g < groupCount; g++)
                            if (groups[g].ArmCount != profile[pi])
                                allow[g] &= ~(1 << cell);
                    }

                    // Every cell still needs at least one candidate.
                    var covered = 0;
                    for (var g = 0; g < groupCount; g++) covered |= allow[g];
                    if (covered != States - 1) continue;
                }

                // Group-level layout penalties apply only where the topology
                // already realizes the layout's in-grid arms (other cells pay
                // the flat per-topology penalty instead).
                if (hasLayout)
                {
                    var adjusted = new double[groupCount][];
                    for (var g = 0; g < groupCount; g++)
                    {
                        adjusted[g] = (double[])weight[g].Clone();
                        for (var cell = 0; cell < Cells; cell++)
                        {
                            if (layoutInGrid[cell] >= 0 && topo[cell] == layoutInGrid[cell])
                                adjusted[g][cell] -= groups[g].LayoutPen[cell];
                        }
                    }

                    weight = adjusted;
                }

                var score = BestAssignment(entries, weight, allow, dpPrev, dpNext, choice, assignment);
                if (double.IsNegativeInfinity(score)) continue;
                score -= topoPen[t];
                if (top.Count >= topN && score <= top[^1].Internal) continue;

                // Map group assignments back to distinct concrete charts. A
                // group can hold pieces with different BASE orientations (same
                // canonical rotation set), so the representative's rotation
                // index must be translated into each member's own rotation that
                // realizes the same target arms.
                Array.Clear(groupUse, 0, groupCount);
                var grid = new MapPiecePlacement[GridSize, GridSize];
                for (var cell = 0; cell < Cells; cell++)
                {
                    var g = assignment[cell];
                    var group = groups[g];
                    var piece = pieces[group.Members[groupUse[g]++]];
                    var targetConn = group.Piece.GetConnections(group.Rotation[cell * 16 + topo[cell]]);
                    var rot = 0;
                    for (var r2 = 0; r2 < piece.DistinctRotations; r2++)
                    {
                        if (piece.GetConnections(r2) != targetConn) continue;
                        rot = r2;
                        break;
                    }

                    grid[cell / GridSize, cell % GridSize] = new MapPiecePlacement(piece, rot, piece.GetConnections(rot));
                }

                Insert(top, topN, score,
                    new VoyageSolution(grid, scorer.Score(grid), true,
                        strategy != null ? score : null));
            }
        }

        yield return new VoyageSolutionResult(top.Select(x => x.Solution).ToList(), explored, pruned);
    }

    private static IEnumerable<int[]> EnumerateProfiles(
        List<int> perConnCells, List<Group> groups, int[] allow, int[] topo)
    {
        if (perConnCells.Count == 0)
        {
            yield return null;
            yield break;
        }

        // Candidate arm counts per per-conn cell: those of the groups actually eligible there.
        var options = new List<int>[perConnCells.Count];
        for (var pi = 0; pi < perConnCells.Count; pi++)
        {
            var cell = perConnCells[pi];
            var counts = new SortedSet<int>();
            for (var g = 0; g < groups.Count; g++)
            {
                if ((allow[g] >> cell & 1) == 0) continue;
                if ((groups[g].Eligible[cell] >> topo[cell] & 1) == 0) continue;
                counts.Add(groups[g].ArmCount);
            }

            if (counts.Count == 0) yield break;
            options[pi] = counts.ToList();
        }

        var profile = new int[perConnCells.Count];
        foreach (var p in Cartesian(options, profile, 0))
            yield return p;
    }

    private static IEnumerable<int[]> Cartesian(List<int>[] options, int[] profile, int depth)
    {
        if (depth == options.Length)
        {
            yield return profile;
            yield break;
        }

        foreach (var v in options[depth])
        {
            profile[depth] = v;
            foreach (var p in Cartesian(options, profile, depth + 1))
                yield return p;
        }
    }

    private static double BestAssignment(
        int[] entries, double[][] weight, int[] allow, double[] dpPrev, double[] dpNext,
        byte[][] choice, int[] assignment)
    {
        dpPrev.AsSpan().Fill(double.NegativeInfinity);
        dpPrev[0] = 0;

        for (var i = 0; i < entries.Length; i++)
        {
            var mine = choice[i];
            var g = entries[i];
            var open = allow[g];
            var w = weight[g];

            mine.AsSpan().Fill(byte.MaxValue);
            if (open == 0)
                continue; // dpPrev already holds "entry skipped" for every state.

            Array.Copy(dpPrev, dpNext, States);

            for (var mask = 0; mask < States; mask++)
            {
                var from = dpPrev[mask];
                if (double.IsNegativeInfinity(from)) continue;

                var free = open & ~mask;
                while (free != 0)
                {
                    var bit = free & -free;
                    free ^= bit;
                    var cell = BitOperations.TrailingZeroCount(bit);
                    var value = from + w[cell];
                    if (value <= dpNext[mask | bit]) continue;
                    dpNext[mask | bit] = value;
                    mine[mask | bit] = (byte)cell;
                }
            }

            (dpPrev, dpNext) = (dpNext, dpPrev);
        }

        var full = States - 1;
        if (double.IsNegativeInfinity(dpPrev[full])) return double.NegativeInfinity;

        var state = full;
        for (var i = entries.Length - 1; i >= 0; i--)
        {
            var cell = choice[i][state];
            if (cell == byte.MaxValue) continue;
            assignment[cell] = entries[i];
            state ^= 1 << cell;
        }

        return dpPrev[full];
    }

    private static void Insert(
        List<(double Internal, VoyageSolution Solution)> top, int topN, double internalScore, VoyageSolution solution)
    {
        var at = top.Count;
        for (var i = 0; i < top.Count; i++)
        {
            if (internalScore <= top[i].Internal) continue;
            at = i;
            break;
        }

        top.Insert(at, (internalScore, solution));
        if (top.Count > topN) top.RemoveAt(top.Count - 1);
    }

    private static void ApplyLockBonuses(List<Group> groups, double[][] weight)
    {
        for (var g = 0; g < groups.Count; g++)
        {
            var cell = groups[g].LockedCell;
            if (cell < 0) continue;
            if (groups[g].Eligible[cell] == 0) continue;

            weight[g][cell] += LockBonus;
            for (var other = 0; other < groups.Count; other++)
            {
                if (other == g) continue;
                weight[other][cell] -= LockBonus;
            }
        }
    }

    private static string GetModifierSignature(MapPiece piece)
    {
        return string.Join("|", piece.Modifiers
            .OrderBy(m => m.Tags)
            .ThenBy(m => m.IsGlobal)
            .ThenBy(m => m.Weight)
            .Select(m => $"{(int)m.Tags}:{(m.IsGlobal ? 1 : 0)}:{m.Weight:R}"));
    }
}
