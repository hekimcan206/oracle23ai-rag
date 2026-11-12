using System.Diagnostics.Metrics;

namespace OracleRag.Api.Infrastructure;

/// <summary>Business metrics: retrieval latency per store and how often the service declines to answer.</summary>
internal sealed class RagMetrics : IDisposable
{
    public const string MeterName = "OracleRag";

    private readonly Meter _meter = new(MeterName);
    private readonly Histogram<double> _retrieval;
    private readonly Counter<long> _answers;

    public RagMetrics()
    {
        _retrieval = _meter.CreateHistogram<double>("rag.retrieval.duration", unit: "ms", description: "Vector search time as seen by the API, per store and mode.");
        _answers = _meter.CreateCounter<long>("rag.answers", description: "Answers given, tagged with whether the service abstained.");
    }

    public void Retrieval(string store, string mode, double milliseconds) =>
        _retrieval.Record(milliseconds, new KeyValuePair<string, object?>("store", store), new KeyValuePair<string, object?>("mode", mode));

    public void Answer(string store, bool abstained) =>
        _answers.Add(1, new KeyValuePair<string, object?>("store", store), new KeyValuePair<string, object?>("abstained", abstained));

    public void Dispose() => _meter.Dispose();
}
