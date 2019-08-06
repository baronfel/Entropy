﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SearchScorer.Common;

namespace SearchScorer.IREvalutation
{
    public class RelevancyScoreEvaluator
    {
        /// <summary>
        /// This is the number of results to include in the scoring. We use 5 because this is the number of results
        /// above the fold.
        /// </summary>
        private const int ResultsToEvaluate = 5;

        private readonly NormalizedDiscountedCumulativeGain _ndcg;

        public RelevancyScoreEvaluator(SearchClient searchClient)
        {
            _ndcg = new NormalizedDiscountedCumulativeGain(searchClient);
        }

        public async Task RunAsync(SearchScorerSettings settings)
        {
            var report = await GetReportAsync(settings);

            ConsoleUtility.WriteHeading("Scores", '=');
            Console.WriteLine($"Control:   {report.ControlReport.Score}");
            Console.WriteLine($"Treatment: {report.TreatmentReport.Score}");

            ConsoleUtility.WriteHeading("Curated Search Queries", '=');
            WriteBiggestWinnersAndLosersToConsole(report, v => v.CuratedSearchQueries);

            ConsoleUtility.WriteHeading("Feedback", '=');
            WriteBiggestWinnersAndLosersToConsole(report, v => v.FeedbackSearchQueries);

            ConsoleUtility.WriteHeading("Top Search Selections", '=');
            WriteBiggestWinnersAndLosersToConsole(report, v => v.SearchQueriesWithSelections);
        }

        private static void WriteBiggestWinnersAndLosersToConsole<T>(
            RelevancyReport report,
            Func<VariantReport, SearchQueriesReport<T>> getReport)
        {
            Console.WriteLine($"Control:   {getReport(report.ControlReport).Score}");
            Console.WriteLine($"Treatment: {getReport(report.TreatmentReport).Score}");

            var toTreatment = getReport(report.TreatmentReport)
                .Queries
                .GroupBy(x => x.Result.Input.SearchQuery)
                .ToDictionary(x => x.Key, x => x.First().Score);
            var scoreChanges = getReport(report.ControlReport)
                .Queries
                .GroupBy(x => x.Result.Input.SearchQuery)
                .ToDictionary(x => x.Key, x => toTreatment[x.Key] - x.First().Score)
                .OrderBy(x => x.Key)
                .ToList();
            WriteSearchQueriesAndScoresToConsole(
                "Biggest Winners",
                scoreChanges.Where(x => x.Value > 0).OrderByDescending(x => x.Value).Take(20));
            WriteSearchQueriesAndScoresToConsole(
                "Biggest Losers",
                scoreChanges.Where(x => x.Value < 0).OrderBy(x => x.Value).Take(20));
        }

        private static void WriteSearchQueriesAndScoresToConsole(
            string heading,
            IEnumerable<KeyValuePair<string, double>> pairs)
        {
            var pairList = pairs.ToList();
            ConsoleUtility.WriteHeading($"{heading} ({pairList.Count})", '-');
            var longestSearchQuery = pairList.Max(x => x.Key.Length);
            foreach (var pair in pairList)
            {
                Console.WriteLine($"{pair.Key.PadRight(longestSearchQuery)} => {pair.Value:+0.0000;-0.0000;0}");
            }
        }

        private async Task<RelevancyReport> GetReportAsync(SearchScorerSettings settings)
        {
            var topQueries = TopSearchQueriesCsvReader.Read(settings.TopSearchQueriesCsvPath);
            var topSearchReferrals = GoogleAnalyticsSearchReferralsCsvReader.Read(settings.GoogleAnalyticsSearchReferralsCsvPath);

            var controlReport = await GetVariantReport(
                settings.ControlBaseUrl,
                settings,
                topQueries,
                topSearchReferrals);

            var treatmentReport = await GetVariantReport(
                settings.TreatmentBaseUrl,
                settings,
                topQueries,
                topSearchReferrals);

            return new RelevancyReport(
                controlReport,
                treatmentReport);
        }

        private async Task<VariantReport> GetVariantReport(
            string baseUrl,
            SearchScorerSettings settings,
            IReadOnlyDictionary<string, int> topQueries,
            IReadOnlyDictionary<string, int> topSearchReferrals)
        {
            var curatedSearchQueriesTask = GetCuratedSearchQueriesScoreAsync(baseUrl, settings, topQueries, topSearchReferrals);
            var feedbackSearchQueriesTask = GetFeedbackSearchQueriesScoreAsync(baseUrl, settings);
            var topSearchSelectionsTask = GetTopSearchSelectionsScoreAsync(baseUrl, settings, topQueries);

            await Task.WhenAll(
                curatedSearchQueriesTask,
                feedbackSearchQueriesTask,
                topSearchSelectionsTask);

            var score = new[]
            {
                curatedSearchQueriesTask.Result.Score,
                feedbackSearchQueriesTask.Result.Score,
                topSearchSelectionsTask.Result.Score,
            }.Average();

            return new VariantReport(
                score,
                curatedSearchQueriesTask.Result,
                feedbackSearchQueriesTask.Result,
                topSearchSelectionsTask.Result);
        }

        private async Task<SearchQueriesReport<CuratedSearchQuery>> GetCuratedSearchQueriesScoreAsync(
            string baseUrl,
            SearchScorerSettings settings,
            IReadOnlyDictionary<string, int> topQueries,
            IReadOnlyDictionary<string, int> topSearchReferrals)
        {
            var minQueryCount = topQueries.Min(x => x.Value);
            var adjustedTopQueries = topQueries.ToDictionary(
                x => x.Key,
                x =>
                {
                    if (topSearchReferrals.TryGetValue(x.Key, out var referrals))
                    {
                        return Math.Max(x.Value - referrals, minQueryCount);
                    }

                    return x.Value;
                });

            var scores = RelevancyScoreBuilder.FromCuratedSearchQueriesCsv(settings.CuratedSearchQueriesCsvPath);

            var results = await ProcessAsync(
                scores,
                baseUrl);

            return WeightByTopQueries(adjustedTopQueries, results);
        }

        private async Task<SearchQueriesReport<FeedbackSearchQuery>> GetFeedbackSearchQueriesScoreAsync(
            string baseUrl,
            SearchScorerSettings settings)
        {
            var scores = RelevancyScoreBuilder.FromFeedbackSearchQueriesCsv(settings.FeedbackSearchQueriesCsvPath);

            var results = await ProcessAsync(
                scores,
                baseUrl);

            return WeightEvently(results);
        }

        private async Task<SearchQueriesReport<SearchQueryWithSelections>> GetTopSearchSelectionsScoreAsync(
            string baseUrl,
            SearchScorerSettings settings,
            IReadOnlyDictionary<string, int> topQueries)
        {
            var topSearchSelectionScores = RelevancyScoreBuilder.FromTopSearchSelectionsCsv(settings.TopSearchSelectionsCsvPath);

            // Take the the top search selection data by query frequency.
            var selectionsOfTopQueries = topSearchSelectionScores
                .Where(x => topQueries.ContainsKey(x.SearchQuery))
                .OrderByDescending(x => topQueries[x.SearchQuery])
                .Take(1000);

            var results = await ProcessAsync(
                selectionsOfTopQueries,
                baseUrl);

            return WeightByTopQueries(topQueries, results);
        }

        private static SearchQueriesReport<T> WeightEvently<T>(ConcurrentBag<RelevancyScoreResult<T>> results)
        {
            var totalCount = 1.0 * results.Count;

            var weightedResults = results
                .Select(x => new WeightedRelevancyScoreResult<T>(
                    x,
                    x.ResultScore / totalCount))
                .ToList();

            return new SearchQueriesReport<T>(weightedResults);
        }

        private static SearchQueriesReport<T> WeightByTopQueries<T>(
            IReadOnlyDictionary<string, int> topQueries,
            ConcurrentBag<RelevancyScoreResult<T>> results)
        {
            // Weight the queries that came from top search selections by their query count.
            var totalQueryCount = 0;
            var resultsAndWeights = new List<KeyValuePair<RelevancyScoreResult<T>, int>>();
            foreach (var result in results)
            {
                var queryCount = topQueries[result.Input.SearchQuery];
                resultsAndWeights.Add(new KeyValuePair<RelevancyScoreResult<T>, int>(result, queryCount));
                totalQueryCount += queryCount;
            }

            var weightedResults = new List<WeightedRelevancyScoreResult<T>>();
            foreach (var pair in resultsAndWeights)
            {
                weightedResults.Add(new WeightedRelevancyScoreResult<T>(
                    pair.Key,
                    1.0 * pair.Value / totalQueryCount));
            }

            return new SearchQueriesReport<T>(weightedResults);
        }

        private async Task<ConcurrentBag<RelevancyScoreResult<T>>> ProcessAsync<T>(
            IEnumerable<SearchQueryRelevancyScores<T>> queries,
            string baseUrl)
        {
            var work = new ConcurrentBag<SearchQueryRelevancyScores<T>>(queries);
            var output = new ConcurrentBag<RelevancyScoreResult<T>>();

            var workers = Enumerable
                .Range(0, 16)
                .Select(async x =>
                {
                    await Task.Yield();

                    while (work.TryTake(out var query))
                    {
                        var result = await _ndcg.ScoreAsync(query, baseUrl, ResultsToEvaluate);
                        Console.WriteLine($"[{baseUrl}] {query.SearchQuery} => {result.ResultScore}");
                        output.Add(result);
                    }
                })
                .ToList();

            await Task.WhenAll(workers);

            return output;
        }

        private class WeightedResult
        {
            RelevancyScoreResult Result { get; }
            public int Weight { get; }
        }
    }

    public class NormalizedDiscountedCumulativeGain
    {
        private readonly SearchClient _searchClient;

        public NormalizedDiscountedCumulativeGain(SearchClient searchClient)
        {
            _searchClient = searchClient;
        }

        public async Task<RelevancyScoreResult<T>> ScoreAsync<T>(
            SearchQueryRelevancyScores<T> query,
            string baseUrl,
            int resultsToEvaluate)
        {
            var response = await _searchClient.SearchAsync(
                baseUrl,
                query.SearchQuery,
                resultsToEvaluate);

            if (!query.PackageIdToScore.Any() || query.PackageIdToScore.Max(x => x.Value) == 0)
            {
                return new RelevancyScoreResult<T>(
                    0,
                    query,
                    response);
            }

            var patternToScorePairs = new List<KeyValuePair<Regex, int>>();
            foreach (var pair in query.PackageIdToScore.Where(x => x.Value > 0))
            {
                if (WildcardUtility.IsWildcard(pair.Key))
                {
                    patternToScorePairs.Add(new KeyValuePair<Regex, int>(
                        WildcardUtility.GetPackageIdWildcareRegex(pair.Key),
                        pair.Value));
                }
            }

            // Determine the score for each of the returns package IDs.
            var scores = new List<int>();
            for (var i = 0; i < response.Data.Count; i++)
            {
                var packageId = response.Data[i].Id;
                if (query.PackageIdToScore.TryGetValue(packageId, out var score))
                {
                    scores.Add(score);
                }
                else
                {
                    // It might be that the score map contains wildcards. Let's try those.
                    foreach (var pair in patternToScorePairs)
                    {
                        if (pair.Key.IsMatch(packageId))
                        {
                            scores.Add(pair.Value);
                            continue;
                        }
                    }

                    scores.Add(0);
                }
            }

            // Determine the ideal scores by taking the top N scores.
            var idealScores = query
                .PackageIdToScore
                .Select(x => x.Value)
                .OrderByDescending(x => x)
                .Take(resultsToEvaluate);

            // Calculate the NDCG.
            var resultScore = NDCG(scores, idealScores);

            return new RelevancyScoreResult<T>(
                resultScore,
                query,
                response);
        }

        private static double NDCG(IEnumerable<int> scores, IEnumerable<int> idealScores)
        {
            return DCG(scores) / DCG(idealScores);
        }

        private static double DCG(IEnumerable<int> scores)
        {
            var sum = 0.0;
            var i = 1;

            foreach (var score in scores)
            {
                sum += score / Math.Log(i + 1, 2);
                i++;
            }

            return sum;
        }
    }
}
