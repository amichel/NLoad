﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using NLoad.LoadTest;

namespace NLoad
{
    public sealed class LoadTest<T> : ILoadTest where T : ITest, new()
    {
        private long _totalErrors;
        private long _totalIterations;
        private long _threadCount;

        List<TestRunner<T>> _testRunners;
        private HeartRateMonitor _monitor;
        private readonly LoadTestConfiguration _configuration;

        private CancellationToken _cancellationToken;
        private readonly ManualResetEvent _quitEvent = new ManualResetEvent(false);

        public event EventHandler<Heartbeat> Heartbeat;

        [ExcludeFromCodeCoverage]
        public LoadTest(LoadTestConfiguration configuration, CancellationToken cancellationToken)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            if (cancellationToken == null)
            {
                throw new ArgumentNullException("cancellationToken");
            }

            _configuration = configuration;
            _cancellationToken = cancellationToken;
        }

        #region Properties

        public LoadTestConfiguration Configuration
        {
            get { return _configuration; }
        }

        public long TotalIterations
        {
            get { return Interlocked.Read(ref _totalIterations); }
        }

        public long TotalErrors
        {
            get { return Interlocked.Read(ref _totalErrors); }
        }

        #endregion

        public LoadTestResult Run()
        {
            try
            {
                var stopWatch = Stopwatch.StartNew();

                Initialize(); //todo: make sure it supports re-running load tests

                StartTestRunners();

                var heartbeats = _monitor.Start();

                Shutdown();

                WaitForTestRunners();

                stopWatch.Stop();

                #region Move to function

                var testRuns = _testRunners.Where(k => k.Result != null && k.Result.TestRuns != null)
                    .SelectMany(k => k.Result.TestRuns)
                    .ToList();

                var result = new LoadTestResult
                {
                    TestRunnersResults = _testRunners.Where(k => k.Result != null).Select(k => k.Result),
                    TotalIterations = _totalIterations,
                    TotalRuntime = stopWatch.Elapsed,
                    TotalErrors = _totalErrors,
                    Heartbeat = heartbeats,
                    TestRuns = testRuns
                };

                if (testRuns.Any())
                {
                    result.MaxResponseTime = testRuns.Max(k => k.ResponseTime);
                    result.MinResponseTime = testRuns.Min(k => k.ResponseTime);
                    result.AverageResponseTime =
                        new TimeSpan(Convert.ToInt64((testRuns.Average(k => k.ResponseTime.Ticks))));
                }

                if (heartbeats.Any())
                {
                    result.MaxThroughput = heartbeats.Where(k => !double.IsNaN(k.Throughput)).Max(k => k.Throughput);
                    result.MinThroughput = heartbeats.Where(k => !double.IsNaN(k.Throughput)).Min(k => k.Throughput);
                    result.AverageThroughput = heartbeats.Where(k => !double.IsNaN(k.Throughput)).Average(k => k.Throughput);
                }

                #endregion

                Debug.WriteLine("Thread Count: {0}", _threadCount);

                return result;
            }
            catch (Exception e)
            {
                throw new NLoadException("An error occurred while running load test. See inner exception for details.", e);
            }
        }

        private void CreateHeartRateMonitor()
        {
            _monitor = new HeartRateMonitor(this, _cancellationToken);

            _monitor.Heartbeat += Heartbeat;

            //monitor.Heartbeat -= Heartbeat;
        }

        private void Initialize()
        {
            //todo: init once

            CreateHeartRateMonitor();

            CreateTestRunners(_configuration.NumberOfThreads);

            _testRunners.ForEach(k => k.Initialize());
        }

        private void StartTestRunners()
        {
            //todo: add error handling

            _testRunners.ForEach(k => k.Run());
        }

        private void CreateTestRunners(int count)
        {
            _testRunners = new List<TestRunner<T>>(count);

            for (var i = 0; i < count; i++)
            {
                var testRunner = new TestRunner<T>(this, _quitEvent);

                _testRunners.Add(testRunner);
            }
        }

        private void Shutdown()
        {
            _quitEvent.Set();
        }

        private void WaitForTestRunners()
        {
            //todo: replace with Task.WaitAll?

            while (_testRunners.Any(w => w.IsBusy))
            {
                Thread.Sleep(1); //todo: ???
            }
        }

        public void IncrementIterationsCounter()
        {
            Interlocked.Increment(ref _totalIterations);
        }

        public void IncrementErrorsCounter()
        {
            Interlocked.Increment(ref _totalErrors);
        }

        public void IncrementThreadCount()
        {
            Interlocked.Increment(ref _threadCount);
        }

        public void SetCancellationToken(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }
    }
}

// ui -> worker -> load test -> runners -> workers