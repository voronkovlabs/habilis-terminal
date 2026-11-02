using System.Runtime.CompilerServices;
using Amazon.SageMaker;
using Amazon.SageMaker.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.SageMaker;

/// <summary>Which jobs to list: name part and status (blank: any), at most <paramref name="MaxJobs"/> in total.</summary>
internal sealed record JobQuery(string NameContains, string Status, int MaxJobs);

/// <summary>The SageMaker calls the plugin makes (read-only); a fake stands in for it in the tests.</summary>
internal interface ISageMakerApi : IDisposable
{
    /// <summary>Job summaries, newest first, page by page; stops once <see cref="JobQuery.MaxJobs"/> were returned.</summary>
    IAsyncEnumerable<IReadOnlyList<TrainingJobSummary>> ListTrainingJobsAsync(JobQuery query, CancellationToken cancellationToken);

    Task<DescribeTrainingJobResponse> DescribeTrainingJobAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The real SageMaker client for one profile and region.</summary>
internal sealed class SageMakerApi(AwsSession session) : ISageMakerApi
{
    /// <summary>The largest page ListTrainingJobs returns.</summary>
    private const int PageSize = 100;

    private readonly AmazonSageMakerClient _client = new(session.Credentials, session.Configure(new AmazonSageMakerConfig()));

    public async IAsyncEnumerable<IReadOnlyList<TrainingJobSummary>> ListTrainingJobsAsync(JobQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var remaining = query.MaxJobs;
        string? next = null;
        do
        {
            var request = new ListTrainingJobsRequest
            {
                SortBy = SortBy.CreationTime,
                SortOrder = SortOrder.Descending,
                MaxResults = Math.Min(PageSize, remaining),
                NextToken = next,
            };
            if (query.NameContains.Length > 0)
            {
                request.NameContains = query.NameContains;
            }

            if (query.Status.Length > 0)
            {
                request.StatusEquals = TrainingJobStatus.FindValue(query.Status);
            }

            var response = await _client.ListTrainingJobsAsync(request, cancellationToken).ConfigureAwait(false);
            var page = response.TrainingJobSummaries ?? [];
            remaining -= page.Count;
            yield return page;
            next = response.NextToken;
        }
        while (!string.IsNullOrEmpty(next) && remaining > 0);
    }

    public Task<DescribeTrainingJobResponse> DescribeTrainingJobAsync(string name, CancellationToken cancellationToken) =>
        _client.DescribeTrainingJobAsync(new DescribeTrainingJobRequest { TrainingJobName = name }, cancellationToken);

    public void Dispose() => _client.Dispose();
}
