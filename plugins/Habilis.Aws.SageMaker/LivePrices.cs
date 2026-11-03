using System.Globalization;
using System.Text.Json;
using Amazon;
using Amazon.Pricing;
using Amazon.Pricing.Model;
using Habilis.Aws.Common;

namespace Habilis.Aws.SageMaker;

/// <summary>The AWS Price List call the plugin makes (read-only); a fake stands in for it in the tests.</summary>
internal interface IPricingApi : IDisposable
{
    /// <summary>The on-demand training price per instance-hour in USD, or null when the price list has none.</summary>
    Task<decimal?> TrainingHourlyAsync(string instanceType, string region, CancellationToken cancellationToken);
}

/// <summary>
/// The AWS Price List API (issue #67): <c>pricing:GetProducts</c> for service <c>AmazonSageMaker</c>, filtered to the
/// instance, the job's region and the Training component. The API answers from us-east-1 for every region.
/// </summary>
internal sealed class PricingApi(AwsSession session) : IPricingApi
{
    private readonly AmazonPricingClient _client = new(session.Credentials, Configure(session));

    public async Task<decimal?> TrainingHourlyAsync(string instanceType, string region, CancellationToken cancellationToken)
    {
        var response = await _client.GetProductsAsync(
            new GetProductsRequest
            {
                ServiceCode = "AmazonSageMaker",
                FormatVersion = "aws_v1",
                MaxResults = 10,
                Filters =
                [
                    new Filter { Type = FilterType.TERM_MATCH, Field = "instanceName", Value = instanceType },
                    new Filter { Type = FilterType.TERM_MATCH, Field = "regionCode", Value = region },
                    new Filter { Type = FilterType.TERM_MATCH, Field = "component", Value = "Training" },
                ],
            },
            cancellationToken).ConfigureAwait(false);
        return LivePrices.HourlyFrom(response.PriceList ?? []);
    }

    public void Dispose() => _client.Dispose();

    private static AmazonPricingConfig Configure(AwsSession session)
    {
        var config = session.Configure(new AmazonPricingConfig());
        config.RegionEndpoint = RegionEndpoint.USEast1;
        return config;
    }
}

/// <summary>
/// Prices for cost estimates: the AWS Price List API first, cached for the app's run, then the price table
/// (<see cref="PriceTable"/>) for whatever the API could not answer. A failing API (no <c>pricing:GetProducts</c>
/// permission, no network) is not asked again for a while; the estimate says the table was used and why.
/// </summary>
internal sealed class LivePrices(Func<PriceTable> table, Func<AwsSession, IPricingApi>? connect, Func<DateTime> clock)
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    private readonly Dictionary<(string Region, string Type), decimal?> _cache = [];
    private readonly Lock _lock = new();
    private (DateTime Until, string Reason)? _failure;

    /// <summary>The price table with the live prices of <paramref name="types"/> in the session's region over it.</summary>
    public async Task<PriceTable> ForAsync(IEnumerable<string> types, AwsSession session, CancellationToken cancellationToken)
    {
        var fallback = table();
        if (connect is null)
        {
            return fallback;
        }

        var region = session.Region;
        List<string> missing;
        lock (_lock)
        {
            missing = types.Distinct(StringComparer.OrdinalIgnoreCase).Where(t => !_cache.ContainsKey((region, t))).ToList();
            if (_failure is { } failure && failure.Until <= clock())
            {
                _failure = null;
            }
        }

        if (missing.Count > 0 && _failure is null)
        {
            try
            {
                using var api = connect(session);
                var found = await Task.WhenAll(missing.Select(async type => (type, price: await api.TrainingHourlyAsync(type, region, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
                lock (_lock)
                {
                    foreach (var (type, price) in found)
                    {
                        _cache[(region, type)] = price;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                lock (_lock)
                {
                    _failure = (clock() + RetryAfterFailure, AwsErrors.Describe(ex, "Reading prices from the AWS Price List", session).Message);
                }
            }
        }

        lock (_lock)
        {
            var live = _cache.Where(c => c.Key.Region == region && c.Value is not null).ToDictionary(c => c.Key.Type, c => c.Value!.Value, StringComparer.OrdinalIgnoreCase);
            var problem = _failure is { } failure ? $"Live prices are not available ({failure.Reason}); the price table is used." : null;
            return fallback.WithLive(live, region, problem);
        }
    }

    /// <summary>The hourly USD price from GetProducts' price list items (the first on-demand price per hour).</summary>
    public static decimal? HourlyFrom(IEnumerable<string> priceList)
    {
        foreach (var item in priceList)
        {
            try
            {
                using var document = JsonDocument.Parse(item);
                if (!document.RootElement.TryGetProperty("terms", out var terms) || !terms.TryGetProperty("OnDemand", out var onDemand))
                {
                    continue;
                }

                foreach (var term in onDemand.EnumerateObject())
                {
                    if (!term.Value.TryGetProperty("priceDimensions", out var dimensions))
                    {
                        continue;
                    }

                    foreach (var dimension in dimensions.EnumerateObject())
                    {
                        var value = dimension.Value;
                        var unit = value.TryGetProperty("unit", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                        if (unit.StartsWith("Hr", StringComparison.OrdinalIgnoreCase) &&
                            value.TryGetProperty("pricePerUnit", out var perUnit) && perUnit.TryGetProperty("USD", out var usd) &&
                            decimal.TryParse(usd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var price) && price > 0)
                        {
                            return price;
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // Not a price list item: try the next one.
            }
        }

        return null;
    }
}
