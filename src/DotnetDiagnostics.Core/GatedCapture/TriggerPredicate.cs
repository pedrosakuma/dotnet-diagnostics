using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace DotnetDiagnostics.Core.GatedCapture;

/// <summary>Comparison operator of a <see cref="TriggerPredicate"/>.</summary>
public enum TriggerOperator
{
    /// <summary><c>&gt;</c></summary>
    GreaterThan,

    /// <summary><c>&gt;=</c></summary>
    GreaterOrEqual,

    /// <summary><c>&lt;</c></summary>
    LessThan,

    /// <summary><c>&lt;=</c></summary>
    LessOrEqual,
}

/// <summary>
/// A single threshold comparison over a sampled <see cref="GatedCaptureMetric"/> (issue #419) —
/// e.g. <c>cpu&gt;85</c>, <c>gcHeapMb&gt;=1500</c>, <c>threadCount&gt;400</c>. Deliberately one
/// comparison; there is no rule DSL.
/// </summary>
public sealed record TriggerPredicate(GatedCaptureMetric Metric, TriggerOperator Operator, double Threshold)
{
    /// <summary>True when <paramref name="value"/> satisfies the comparison.</summary>
    public bool Evaluate(double value) => Operator switch
    {
        TriggerOperator.GreaterThan => value > Threshold,
        TriggerOperator.GreaterOrEqual => value >= Threshold,
        TriggerOperator.LessThan => value < Threshold,
        TriggerOperator.LessOrEqual => value <= Threshold,
        _ => false,
    };

    /// <summary>
    /// True when the predicate watches for an upward breach (<c>&gt;</c>/<c>&gt;=</c>) — so the
    /// "peak" observed value is the maximum; downward predicates peak at the minimum.
    /// </summary>
    public bool IsUpperBound => Operator is TriggerOperator.GreaterThan or TriggerOperator.GreaterOrEqual;

    /// <summary>Canonical round-trippable form, e.g. <c>cpu&gt;85</c>.</summary>
    public override string ToString()
        => $"{GatedCaptureMetrics.Token(Metric)}{OperatorToken(Operator)}{Threshold.ToString(CultureInfo.InvariantCulture)}";

    private static string OperatorToken(TriggerOperator op) => op switch
    {
        TriggerOperator.GreaterThan => ">",
        TriggerOperator.GreaterOrEqual => ">=",
        TriggerOperator.LessThan => "<",
        TriggerOperator.LessOrEqual => "<=",
        _ => "?",
    };

    /// <summary>
    /// Parses a predicate string such as <c>cpu&gt;85</c> (whitespace is ignored). Returns
    /// <c>false</c> with a human-readable <paramref name="error"/> for unknown metrics, missing
    /// operators, or non-numeric thresholds.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out TriggerPredicate? predicate, out string? error)
    {
        predicate = null;
        error = null;

        var raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            error = "Predicate is empty. Use <metric><op><value>, e.g. 'cpu>85'.";
            return false;
        }

        // Longest operators first so ">=" is not mis-read as ">".
        var (op, opIndex, opLength) = FindOperator(raw);
        if (op is null)
        {
            error = $"Predicate '{raw}' is missing a comparison operator (>, >=, <, <=).";
            return false;
        }

        var metricToken = raw[..opIndex].Trim();
        var valueToken = raw[(opIndex + opLength)..].Trim();

        if (!GatedCaptureMetrics.TryParse(metricToken, out var metric))
        {
            error = $"Unknown metric '{metricToken}'. Valid metrics: {string.Join(", ", GatedCaptureMetrics.Tokens)}.";
            return false;
        }

        if (!double.TryParse(valueToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold))
        {
            error = $"Threshold '{valueToken}' in predicate '{raw}' is not a number.";
            return false;
        }

        predicate = new TriggerPredicate(metric.Value, op.Value, threshold);
        return true;
    }

    private static (TriggerOperator? Op, int Index, int Length) FindOperator(string text)
    {
        var ge = text.IndexOf(">=", StringComparison.Ordinal);
        if (ge >= 0) return (TriggerOperator.GreaterOrEqual, ge, 2);

        var le = text.IndexOf("<=", StringComparison.Ordinal);
        if (le >= 0) return (TriggerOperator.LessOrEqual, le, 2);

        var gt = text.IndexOf('>', StringComparison.Ordinal);
        if (gt >= 0) return (TriggerOperator.GreaterThan, gt, 1);

        var lt = text.IndexOf('<', StringComparison.Ordinal);
        if (lt >= 0) return (TriggerOperator.LessThan, lt, 1);

        return (null, -1, 0);
    }
}
