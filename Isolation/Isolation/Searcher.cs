﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Isolation
{
    public class Searcher
    {
        #region singleton

        private static readonly Lazy<Searcher> Singelton = new Lazy<Searcher>(() => new Searcher());
        public static Searcher I { get { return Singelton.Value; } }

        #endregion

        private SearchConfig _config;

        // if end game, DONT ALWAYS DO ALPHA BETA, try to just walk our longest possible path
        private Func<IBestMoveGetter> MoveGetter
        {
            get
            {
                if (_config.GameMode == GameMode.End)
                {
                    return () => new LongestPath();
                }
                
                return () => new AlphaBetaWithStats();
            }
        }

        public void Initialize(SearchConfig config)
        {
            _config = config;

            // tell move timer about timeout
            MoveTimer.I.SetTimeout(config.MoveTimeout);
        }

        public BoardSpace GetMyNextMove(Board board)
        {
            // start the timer
            MoveTimer.I.StartTimer();

            // cancel all tasks that are now irrelevant
            foreach (var task in _tasksByOpponentMove.Where(x => !x.Key.Equals(board.LastMove)))
            {
                task.Value.CancelSource.Cancel();
            }

            TimeSpan? asyncTimeElapsed = null;

            IBestMoveResult bestMoveResult;

            if (board.LastMove == null || !_tasksByOpponentMove.ContainsKey(board.LastMove))
            {
                // we were not precomputing this path, so we need to start fresh
                bestMoveResult = MoveGetter().BestMove(board, _config, MoveTimer.I, new CancellationToken());
            }
            else
            {
                // we already started down this branch, so use the result from the task
                bestMoveResult = _tasksByOpponentMove[board.LastMove].BestMoveTask.Result;

                // check how long that took us
                asyncTimeElapsed = _tasksByOpponentMove[board.LastMove].Timer.GetTimeElapsed();
            }

            // stop timer and grab timer stats
            var percentRemaining = MoveTimer.I.GetPercentOfTimeRemaining();
            var elapsed = MoveTimer.I.GetTimeElapsed();
            var timedOut = MoveTimer.I.Timeout();
            MoveTimer.I.ResetTimer();

            // report stats
            if (_config.ReportStatistics)
            {
                Console.WriteLine("Time Taken (s): " + elapsed.TotalSeconds);
                Console.WriteLine("Time Left (%): " + percentRemaining * 100); 
                if (asyncTimeElapsed != null)
                {
                    Console.WriteLine("Async Time Taken (s): " + asyncTimeElapsed.Value.TotalSeconds);
                }
                Console.WriteLine(bestMoveResult.ToString());
            }

            // if we have enough time remaining, increase the depth limit
            if (percentRemaining > _config.PercentTimeLeftToIncrementDepthLimit)
            {
                // if we didn't do the search async, just increase depth
                if (asyncTimeElapsed == null)
                {
                    _config.DepthLimit++;
                }
                else
                {
                    // only increase depth if async time was decent
                    var asyncPercentTimeLeft = (_config.MoveTimeout.TotalMilliseconds - asyncTimeElapsed.Value.TotalMilliseconds) / _config.MoveTimeout.TotalMilliseconds;
                    if (asyncPercentTimeLeft > _config.PercentTimeLeftToIncrementDepthLimit/2)
                    {
                        _config.DepthLimit++;
                    }
                }
            }
            
            // if we timed out, decrease depth limit so we stop shooting ourself in the foot
            if (timedOut)
            {
                _config.DepthLimit--;
            }

            var emptySpaces = board.GetEmptySpacesRemaining();

            // figure out where we are in the game
            if (_config.GameMode == GameMode.Beginning)
            {
                if (emptySpaces <= 40)
                {
                    _config.DepthLimit--;
                    _config.GameMode = GameMode.Middle;
                    HeuristicCache.I.ClearCache();
                }
            }
            else if (_config.GameMode == GameMode.Middle)
            {
                // if we think we're going to win, maybe switch to end game
                if (bestMoveResult.Score == int.MaxValue && bestMoveResult.Move != null)
                {
                    // if this move guarentees a win, switch to end game
                    var scoreAfterThisMove = HeuristicCache.I.Evaluate(board.Copy().Move(bestMoveResult.Move), _config.Heuristic);
                    if (scoreAfterThisMove == int.MaxValue)
                    {
                        _config.DepthLimit = 30;
                        _config.GameMode = GameMode.End;
                        HeuristicCache.I.ClearCache();
                    }
                }
                else // we don't think we're going to win yet
                {
                    // if it's time for end game, switch
                    if (emptySpaces <= 28)
                    {
                        _config.DepthLimit = 30;
                        _config.GameMode = GameMode.End;
                        HeuristicCache.I.ClearCache();
                    }
                    // if we think we're going to lose and have some time left, try to find our best move b/c alpha-beta will pick a random one
                    else if (bestMoveResult.Score == int.MinValue && !timedOut)
                    {
                        var tasksByDepthLimit = new Dictionary<int, AsyncSearchTask>();

                        // simultaneously search at smaller depths to get our best move that isn't -infinity
                        for (var newDepthLimit = _config.DepthLimit - 1; newDepthLimit > 0; newDepthLimit--)
                        {
                            var newConfig = new SearchConfig(_config) { DepthLimit = newDepthLimit };

                            // new task with timeout set at our remaining time
                            var task = new AsyncSearchTask(_config.MoveTimeout.Subtract(elapsed));

                            task.BestMoveTask = Task.Factory.StartNew(() =>
                            {
                                task.Timer.StartTimer();
                                var result = MoveGetter().BestMove(board, newConfig, task.Timer, task.CancelSource.Token);
                                task.Timer.StopTimer();
                                return result;
                            }, task.CancelSource.Token);

                            tasksByDepthLimit[newDepthLimit] = task;
                        }

                        foreach (var kvp in tasksByDepthLimit.OrderBy(x => x.Key))
                        {
                            var newBestMove = kvp.Value.BestMoveTask.Result;

                            // if we think we're going to lose or we timed out, cancel all deeper tasks because they will also think we're going to lose
                            if (newBestMove.Score == int.MinValue || kvp.Value.Timer.Timeout())
                            {
                                var kvpClosure = kvp;
                                foreach (var task in tasksByDepthLimit.Where(x => x.Key > kvpClosure.Key).Select(x => x.Value).Where(x => !x.BestMoveTask.IsCompleted))
                                {
                                    task.CancelSource.Cancel();
                                }
                                break;
                            }

                            // if we found a better move, use it
                            if (newBestMove.Score > bestMoveResult.Score)
                            {
                                bestMoveResult = newBestMove;
                            }
                        }

                        // if we still think we're gauarenteed to lose, switch to end game to maximize moves left
                        if (bestMoveResult.Score == int.MinValue && emptySpaces <= 30) 
                        {
                            _config.DepthLimit = 30;
                            _config.GameMode = GameMode.End;
                            HeuristicCache.I.ClearCache();
                        }
                    }
                }
            }

            return bestMoveResult.Move;
        }

        public void PreComputeNextMove(Board board)
        {
            // cancel any tasks that are running from last round
            foreach (var task in _tasksByOpponentMove.Select(x => x.Value).Where(x => !x.BestMoveTask.IsCompleted))
            {
                task.CancelSource.Cancel();
            }

            // remove all saved tasks
            _tasksByOpponentMove.Clear();

            // for every possible opponent move, pretend like the opponent picks it and start computing my response
            foreach (var move in board.GetValidMoves())
            {
                var newBoard = board.Copy().Move(move);

                var task = new AsyncSearchTask(TimeSpan.MaxValue);

                task.BestMoveTask = Task.Factory.StartNew(() =>
                    {
                        task.Timer.StartTimer();
                        var result = MoveGetter().BestMove(newBoard, _config, MoveTimer.I, task.CancelSource.Token);
                        task.Timer.StopTimer();
                        return result;
                    }, task.CancelSource.Token);

                _tasksByOpponentMove[move] = task;
            }
        }

        private readonly Dictionary<BoardSpace, AsyncSearchTask> _tasksByOpponentMove = new Dictionary<BoardSpace, AsyncSearchTask>();
    }

    public class AsyncSearchTask
    {
        public AsyncSearchTask(TimeSpan timeout)
        {
            CancelSource = new CancellationTokenSource();
            Timer = new MoveTimer();
            Timer.SetTimeout(timeout);
        }

        public Task<IBestMoveResult> BestMoveTask { get; set; }
        public CancellationTokenSource CancelSource { get; private set; }

        // special timer for an async task, not used for timeout
        public MoveTimer Timer { get; private set; }
    }
}