#region License notice

/*
  This file is part of the Ceres project at https://github.com/dje-dev/ceres.
  Copyright (C) 2020- by David Elliott and the Ceres Authors.

  Ceres is free software under the terms of the GNU General Public License v3.0.
  You should have received a copy of the GNU General Public License
  along with Ceres. If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

#region Using directives

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Ceres.Base.Math;
using Ceres.Base.Math.Random;
using Ceres.MCTS.Iteration;
using Ceres.MCTS.LeafExpansion;
using Ceres.MCTS.MTCSNodes;
using Ceres.MCTS.Params;

#endregion

[assembly: InternalsVisibleTo("Ceres.EngineMCTS.Test")] // TODO: move or remove me.

namespace Ceres.MCTS.Managers
{
  /// <summary>
  /// Manager that selects which move at the root of the search is best to play.
  /// </summary>
  public class ManagerChooseRootMove
  {
    public static float MLHMoveModifiedFraction => (float)countBestMovesWithMLHChosenWithModification / (float)countBestMovesWithMLHChosen;

    public static long countBestMovesWithMLHChosen = 0;
    public static long countBestMovesWithMLHChosenWithModification = 0;

    public readonly MCTSNode Node;
    public readonly bool UpdateStatistics;
    public readonly float MBonusMultiplier;

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="node"></param>
    /// <param name="updateStatistics"></param>
    /// <param name="mBonusMultiplier"></param>
    public ManagerChooseRootMove(MCTSNode node, bool updateStatistics, float mBonusMultiplier)
    {
      Node = node;
      UpdateStatistics = updateStatistics;
      MBonusMultiplier = mBonusMultiplier;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="n"></param>
    /// <param name="mAvgOfBestQ"></param>
    /// <returns></returns>
    float MLHBoostForNode(MCTSNode n, float mAvgOfBestQ)
    {
      float delta = n.MAvg - mAvgOfBestQ;
      if (float.IsNaN(delta)) return 0;

      //float scaledDelta = 0.004f * MathF.Sqrt(MathF.Abs(delta)) * MathF.Sign(delta);
      float scaledDelta = 0.0005f * StatUtils.Bounded(delta, -30, 30);
      scaledDelta = StatUtils.Bounded(delta, -30, 30) * StatUtils.Bounded(MathF.Abs((float)Node.Context.Root.Q), -0.5f, 0.5f) * MBonusMultiplier;
      if (Node.Context.Root.Q > 0.03f) // we are winning
        return -scaledDelta;
      else if (Node.Context.Root.Q < -0.03f) // we are losing
        return scaledDelta;
      else
        return 0;
    }

    /// <summary>
    /// Calculates the best move to play from root 
    /// given the current state of teh search t
    /// </summary>
    public BestMoveInfo BestMoveCalc
    {
      get
      {
        if (Node.NumPolicyMoves == 0)
          return default;

        // If only one move, make it!
        if (Node.NumPolicyMoves == 1)
        {
          MCTSNode onlyChild = Node.NumChildrenExpanded == 0 ? Node.CreateChild(0) : Node.ChildAtIndex(0);
          return new BestMoveInfo(onlyChild, (float)onlyChild.Q, onlyChild.N, 0);
        }

        if (Node.NumChildrenExpanded == 0)
        {
          // No visits, create a node for the first child (which will be move with highest prior)
          return new BestMoveInfo(Node.CreateChild(0), float.NaN, 0, 0);
        }
        else if (Node.NumChildrenExpanded == 1)
        {
          MCTSNode onlyChild = Node.ChildAtIndex(0);
          return new BestMoveInfo(onlyChild, (float)onlyChild.Q, onlyChild.N, 0);
        }

        return DoCalcBestMove();
      }
    }

      public BestMoveInfo DoCalcBestMove()
      { 
        bool useMLH = MBonusMultiplier > 0 && !float.IsNaN(Node.MAvg);
        if (useMLH && UpdateStatistics) countBestMovesWithMLHChosen++;

        // Get nodes sorted by N and Q (with most attractive move into beginning of array)
        // Note that the sort on N is augmented with an additional term based on Q so that tied N leads to lower Q preferred
        MCTSNode[] childrenSortedN = Node.ChildrenSorted(node => -node.N + (float)node.Q * 0.1f);
        MCTSNode[] childrenSortedQ = Node.ChildrenSorted(n => (float)n.Q);

        float mAvgOfBestQ = childrenSortedQ[0].MAvg;
        MCTSNode priorBest = childrenSortedQ[0];

        if (useMLH)
        {
          childrenSortedQ = Node.ChildrenSorted(n => (float)n.Q + -MLHBoostForNode(n, mAvgOfBestQ));
        }

        const bool VERBOSE = false;
        if (VERBOSE
          && useMLH
          && Math.Abs(Node.Context.Root.Q) > 0.05f
          && childrenSortedQ[0] != priorBest)
        {
          Console.WriteLine("\r\n" + Node.Context.Root.Q + " " + Node.Context.Root.MPosition + " " + Node.Context.Root.MAvg);
          Console.WriteLine(priorBest + "  ==> " + childrenSortedQ[0]);
          for (int i = 0; i < Node.Context.Root.NumChildrenExpanded; i++)
          {
            MCTSNode nodeInner = Node.Context.Root.ChildAtIndex(i);
            Console.WriteLine($" {nodeInner.Q,6:F3} [MAvg= {nodeInner.MAvg,6:F3}] ==> {MLHBoostForNode(nodeInner, mAvgOfBestQ),6:F3} {nodeInner}");
          }
          Console.ReadKey();
        }


        // First see if any were forced losses for the child (i.e. wins for us)
        if (childrenSortedQ.Length == 1 || ParamsSelect.VIsForcedLoss((float)childrenSortedQ[0].Q))
          return new BestMoveInfo(childrenSortedQ[0], (float)childrenSortedQ[0].Q, childrenSortedN[0].N, 0); // TODO: look for quickest win?

        int thisMoveNum = Node.Context.StartPosAndPriorMoves.Moves.Count / 2; // convert ply to moves

        if (Node.Context.ParamsSearch.SearchNoiseBestMoveSampling != null
         && thisMoveNum < Node.Context.ParamsSearch.SearchNoiseBestMoveSampling.MoveSamplingNumMovesApply
         && Node.Context.NumMovesNoiseOverridden < Node.Context.ParamsSearch.SearchNoiseBestMoveSampling.MoveSamplingMaxMoveModificationsPerGame
         )
        {
          // TODO: currently only supported for sorting by N
          MCTSNode bestMoveWithNoise = BestMoveByNWithNoise(childrenSortedN);
          return new BestMoveInfo(bestMoveWithNoise, (float)childrenSortedN[0].Q, childrenSortedN[0].N, MLHBoostForNode(bestMoveWithNoise, mAvgOfBestQ)); // TODO: look for quickest win?
        }
        else
        {
          if (Node.Context.ParamsSearch.BestMoveMode == ParamsSearch.BestMoveModeEnum.TopN)
          {
            // Just return best N (note that tiebreaks are already decided with sort logic above)
            return new BestMoveInfo(childrenSortedN[0], (float)childrenSortedN[0].Q, childrenSortedN[0].N, 0); // TODO: look for quickest win?
          }
          else if (Node.Context.ParamsSearch.BestMoveMode == ParamsSearch.BestMoveModeEnum.TopQIfSufficientN)
          {
            float qOfBestNMove = (float)childrenSortedN[0].Q;

            // Only consider moves having number of visits which is some minimum fraction of visits to most visisted move
            int nOfChildWithHighestN = childrenSortedN[0].N;


            for (int i = 0; i < childrenSortedQ.Length; i++)
            {
              MCTSNode candidate = childrenSortedQ[i];

              // Return if this has a worse Q (for the opponent) and meets minimum move threshold
              if ((float)candidate.Q > qOfBestNMove) break;

              float differenceFromQOfBestN = MathF.Abs((float)candidate.Q - (float)childrenSortedN[0].Q);

              float minFrac = MinFractionNToUseQ(differenceFromQOfBestN);

              int minNToBeConsideredForBestQ = (int)(nOfChildWithHighestN * minFrac);
              if (candidate.N >= minNToBeConsideredForBestQ)
              {
                if (useMLH && UpdateStatistics)
                {
                  ManagerChooseRootMove bestMoveChooserWithoutMLH = new ManagerChooseRootMove(this.Node, false, 0);
                  if (bestMoveChooserWithoutMLH.BestMoveCalc.BestMoveNode != candidate)
                    countBestMovesWithMLHChosenWithModification++;
                }
                //                Console.WriteLine(childrenSortedQ[0].Q + "/" + childrenSortedQ[1].N + "  " +
                //                  childrenSortedQ[1].Q + "/" + childrenSortedQ[1].N);
                return new BestMoveInfo(candidate, (float)childrenSortedN[0].Q, childrenSortedN[0].N, MLHBoostForNode(candidate, mAvgOfBestQ)); // TODO: look for quickest win?
              }
            }

            // We didn't find any moves qualified by Q, fallback to move with highest N
            return new BestMoveInfo(childrenSortedN[0], (float)childrenSortedN[0].Q, childrenSortedN[0].N, 0);
          }
          else
            throw new Exception("Internal error, unknown BestMoveMode");
        }
      }
    


    static internal float MIN_FRAC_N_REQUIRED_MIN(MCTSIterator context) => 0.30f;

    /// <summary>
    /// Given a specified superiority of Q relative to another move,
    /// returns the minimum fraction of N (relative to the other move)
    /// required before the move will be preferred.
    /// 
    /// Returned values are fairly close to 1.0 
    /// to avoid choising moves which are relatively much less explored.
    ///
    /// The greater the Q superiority of the cadidate, the lower the fraction required.
    /// </summary>
    /// <param name="qDifferenceFromBestQ"></param>
    /// <returns></returns>
    internal float MinFractionNToUseQ(float qDifferenceFromBestQ)
    {
      bool isSmallTree = Node.Context.Root.N < 50_000;

      float minFrac;

      if (isSmallTree)
      {
        // For small trees we are even more reluctant to rely upon Q if few visits
        minFrac = qDifferenceFromBestQ switch
        {
          >= 0.06f => MIN_FRAC_N_REQUIRED_MIN(Node.Context) + 0.10f,
          >= 0.04f => 0.55f,
          >= 0.02f => 0.75f,
          _ => 0.90f
        };
      }
      else
      {
        minFrac = qDifferenceFromBestQ switch
        {
          >= 0.05f => MIN_FRAC_N_REQUIRED_MIN(Node.Context),
          >= 0.02f => 0.55f,
          >= 0.01f => 0.75f,
          _ => 0.90f
        };
      }

      return minFrac;
    }

    private MCTSNode BestMoveByNWithNoise(MCTSNode[] childrenSortedByAttractiveness)
    {
      if (Node.Context.ParamsSearch.BestMoveMode != ParamsSearch.BestMoveModeEnum.TopN)
        throw new NotImplementedException("SearchNoiseBestMoveSampling requires ParamsSearch.BestMoveModeEnum.TopN");

      float threshold = childrenSortedByAttractiveness[0].N * Node.Context.ParamsSearch.SearchNoiseBestMoveSampling.MoveSamplingConsiderMovesWithinFraction;
      float minN = childrenSortedByAttractiveness[0].N - threshold;
      List<MCTSNode> childrenWithinThreshold = new List<MCTSNode>();
      List<float> densities = new List<float>(childrenSortedByAttractiveness.Length);
      for (int i = 0; i < childrenSortedByAttractiveness.Length; i++)
      {
        if (childrenSortedByAttractiveness[i].N >= minN)
        {
          childrenWithinThreshold.Add(childrenSortedByAttractiveness[i]);
          densities.Add(childrenSortedByAttractiveness[i].N);
        }
      }

      if (childrenWithinThreshold.Count == 1)
        return childrenSortedByAttractiveness[0];
      else
      {
        MCTSNode bestMove = childrenWithinThreshold[ThompsonSampling.Draw(densities.ToArray(), Node.Context.ParamsSearch.SearchNoiseBestMoveSampling.MoveSamplingConsideredMovesTemperature)];
        if (bestMove != childrenSortedByAttractiveness[0])
        {
          Node.Context.ParamsSearch.SearchNoiseBestMoveSampling.MoveSamplingMaxMoveModificationsPerGame++;
          MCTSIterator.TotalNumMovesNoiseOverridden++;
        }
        return bestMove;
      }
    }

  }
}

