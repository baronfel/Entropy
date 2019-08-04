﻿using System.Collections.Generic;
using SearchScorer.Feedback;

namespace SearchScorer.Common
{
    public class TestSearchQuery
    {
        public TestSearchQuery(
            SearchQuerySource source,
            FeedbackDisposition feedbackDisposition,
            string searchQuery,
            IReadOnlyList<Bucket> buckets,
            IReadOnlyList<string> mostRelevantPackageIds)
        {
            Source = source;
            FeedbackDisposition = feedbackDisposition;
            SearchQuery = searchQuery;
            Buckets = buckets;
            MostRelevantPackageIds = mostRelevantPackageIds;
        }

        public SearchQuerySource Source { get; }
        public FeedbackDisposition FeedbackDisposition { get; }
        public string SearchQuery { get;  }
        public IReadOnlyList<Bucket> Buckets { get;  }
        public IReadOnlyList<string> MostRelevantPackageIds { get;  }

        public override string ToString()
        {
            return $"{SearchQuery} => {string.Join(" | ", MostRelevantPackageIds)}";
        }
    }
}
