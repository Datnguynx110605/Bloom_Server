using System.Diagnostics.Metrics;

namespace Bloom.Catalog.Observability;

/// <summary>Instruments of 08-catalog §10; labels per plan R26.</summary>
public sealed class CatalogMetrics
{
    public const string MeterName = "Bloom.Catalog";

    private static readonly Dictionary<string, string> Kinds = new()
    {
        ["list_categories"] = "categories", ["search_products"] = "search", ["get_product"] = "product",
    };

    private readonly Histogram<double> _read, _search, _getPricing;
    private readonly Counter<long> _reviews;
    private readonly Dictionary<string, long[]> _reads = Kinds.Values.ToDictionary(k => k, _ => new long[2]); // [hits, misses]

    public CatalogMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _read = meter.CreateHistogram<double>("catalog_read_seconds", "s");
        _search = meter.CreateHistogram<double>("search_seconds", "s");
        _getPricing = meter.CreateHistogram<double>("get_pricing_seconds", "s");
        _reviews = meter.CreateCounter<long>("reviews_total");
        meter.CreateObservableGauge("catalog_cache_hit_ratio", () => _reads
            .Select(r => (Kind: r.Key, Hits: Interlocked.Read(ref r.Value[0]), Misses: Interlocked.Read(ref r.Value[1])))
            .Where(r => r.Hits + r.Misses > 0) // no sample before the first read
            .Select(r => new Measurement<double>((double)r.Hits / (r.Hits + r.Misses), new KeyValuePair<string, object?>("kind", r.Kind))));
    }

    /// <param name="endpoint"><c>list_categories</c>, <c>search_products</c> or <c>get_product</c>.</param>
    public void Read(string endpoint, bool hit, double seconds)
    {
        _read.Record(seconds, new("endpoint", endpoint), new("cache", hit ? "hit" : "miss"));
        Interlocked.Increment(ref _reads[Kinds[endpoint]][hit ? 0 : 1]);
    }

    /// <summary>The Postgres search query (cache misses).</summary>
    public void Search(double seconds) => _search.Record(seconds);

    public void GetPricing(double seconds) => _getPricing.Record(seconds);

    /// <param name="action"><c>create</c>, <c>edit</c>, <c>delete</c>, <c>hide</c> or <c>unhide</c>.</param>
    public void Review(string action) => _reviews.Add(1, new KeyValuePair<string, object?>("action", action));
}
