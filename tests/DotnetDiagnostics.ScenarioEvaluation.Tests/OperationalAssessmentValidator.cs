using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetDiagnostics.ScenarioEvaluation.Tests;

internal sealed record OperationalParsedAssessment(
    IReadOnlyList<OperationalClaimAssessment> Claims,
    bool ClaimTextAliasNormalized,
    OperationalUncertaintyQuality Uncertainty,
    string UncertaintyRationale,
    IReadOnlyList<OperationalUnsupportedCertaintyFinding> UnsupportedCertaintyFindings,
    IReadOnlyList<string> UsefulNextSteps,
    IReadOnlyList<string> UnhelpfulNextSteps,
    string Summary,
    IReadOnlyList<OperationalAssessmentDiagnostic> IgnoredAnnotations,
    int IgnoredAnnotationCount,
    bool IgnoredAnnotationsTruncated);

internal sealed class OperationalAssessmentValidationException : JsonException
{
    public OperationalAssessmentValidationException(
        string message,
        OperationalAssessmentDiagnostic firstFailure,
        IReadOnlyList<OperationalAssessmentDiagnostic> diagnostics,
        int diagnosticCount,
        bool diagnosticsTruncated,
        int additionalPropertyCount)
        : base(message)
    {
        FirstFailure = firstFailure;
        Diagnostics = diagnostics;
        DiagnosticCount = diagnosticCount;
        DiagnosticsTruncated = diagnosticsTruncated;
        AdditionalPropertyCount = additionalPropertyCount;
    }

    public OperationalAssessmentDiagnostic FirstFailure { get; }

    public IReadOnlyList<OperationalAssessmentDiagnostic> Diagnostics { get; }

    public int DiagnosticCount { get; }

    public bool DiagnosticsTruncated { get; }

    public int AdditionalPropertyCount { get; }
}

internal static class OperationalAssessmentValidator
{
    internal const int MaximumJsonDepth = 32;

    internal const int MaximumDiagnostics = 64;

    internal const int MaximumDiagnosticBytes = 8_192;

    private static readonly JsonSerializerOptions DiagnosticJsonOptions = CreateDiagnosticJsonOptions();

    private static readonly string[] RootCanonicalProperties =
    [
        "claims",
        "uncertainty",
        "uncertaintyRationale",
        "unsupportedCertaintyFindings",
        "usefulNextSteps",
        "unhelpfulNextSteps",
        "summary",
    ];

    private static readonly string[] ClaimCanonicalProperties =
    [
        "index",
        "support",
        "rationale",
    ];

    private static readonly string[] ClaimAliases = ["text"];

    private static readonly string[] FindingCanonicalProperties =
    [
        "claimIndex",
        "category",
        "rationale",
    ];

    public static OperationalParsedAssessment Parse(
        string json,
        int claimCount,
        IReadOnlyDictionary<string, string>? additiveProperties,
        int maximumBytes,
        int maximumDiagnosticBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDiagnosticBytes);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > maximumBytes)
        {
            throw SingleFailure(
                OperationalAssessmentFailureCategory.SizeLimit,
                "$",
                null,
                null,
                $"Assessment JSON exceeded the {maximumBytes}-byte validation limit.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
        }
        catch (JsonException exception) when (
            exception.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
        {
            throw SingleFailure(
                OperationalAssessmentFailureCategory.DepthLimit,
                exception.Path ?? "$",
                null,
                null,
                $"Assessment JSON exceeded the maximum depth of {MaximumJsonDepth}.");
        }

        using (document)
        {
            var context = new ValidationContext(
                Math.Min(MaximumDiagnosticBytes, maximumDiagnosticBytes));
            ScanNameCollisions(document.RootElement, "$", context);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                context.AddFailure(
                    OperationalAssessmentFailureCategory.InvalidValueKind,
                    "$",
                    null,
                    document.RootElement.ValueKind,
                    "The assessment root must be a JSON object.");
                context.ThrowIfInvalid();
            }

            var root = document.RootElement;
            var additive = additiveProperties
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
            ValidateProperties(
                root,
                "$",
                RootCanonicalProperties,
                aliases: [],
                additive.Keys,
                context);
            ValidateTypedAdditions(root, additive, context);

            var claimElements = RequiredArray(root, "$", "claims", context);
            var claims = new List<IndexedClaimAssessment>(claimElements?.Length ?? 0);
            var claimTextAliasNormalized = false;
            if (claimElements is not null)
            {
                var failureCountBeforeClaims = context.FailureCount;
                for (var offset = 0; offset < claimElements.Length; offset++)
                {
                    var claim = claimElements[offset];
                    var path = $"$.claims[{offset}]";
                    if (claim.ValueKind != JsonValueKind.Object)
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.InvalidValueKind,
                            path,
                            null,
                            claim.ValueKind,
                            $"Assessment claim at '{path}' must be an object.");
                        continue;
                    }

                    ValidateProperties(
                        claim,
                        path,
                        ClaimCanonicalProperties,
                        ClaimAliases,
                        additive: [],
                        context);
                    var index = RequiredInt32(claim, path, "index", context);
                    if (index is not null && (index < 0 || index >= claimCount))
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.InvalidValue,
                            PropertyPath(path, "index"),
                            "index",
                            JsonValueKind.Number,
                            $"Assessment claim index {index} is outside the diagnosis.");
                        index = null;
                    }

                    var supportText = RequiredString(
                        claim,
                        path,
                        "support",
                        context);
                    var support = supportText is null
                        ? null
                        : ParseSupport(
                            supportText,
                            PropertyPath(path, "support"),
                            context);
                    var rationale = RequiredClaimText(
                        claim,
                        path,
                        context,
                        out var normalizedFromAlias);
                    claimTextAliasNormalized |= normalizedFromAlias;
                    if (index is not null && support is not null && rationale is not null)
                    {
                        claims.Add(new IndexedClaimAssessment(
                            index.Value,
                            support.Value,
                            rationale));
                    }
                }

                if (context.FailureCount == failureCountBeforeClaims)
                {
                    var duplicateIndices = claims
                        .GroupBy(value => value.Index)
                        .Where(group => group.Count() > 1)
                        .Select(group => group.Key)
                        .ToArray();
                    foreach (var duplicateIndex in duplicateIndices)
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.DuplicateProperty,
                            "$.claims",
                            "index",
                            JsonValueKind.Number,
                            $"Assessment claim index {duplicateIndex} was classified more than once.");
                    }

                    if (claims.Select(value => value.Index).Distinct().Count() != claimCount)
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.InvalidValue,
                            "$.claims",
                            "claims",
                            JsonValueKind.Array,
                            "The assessor must classify every diagnosis claim exactly once.");
                    }
                }
            }

            var findingElements = RequiredArray(
                root,
                "$",
                "unsupportedCertaintyFindings",
                context);
            var findings = new List<OperationalUnsupportedCertaintyFinding>(
                findingElements?.Length ?? 0);
            if (findingElements is not null)
            {
                var failureCountBeforeFindings = context.FailureCount;
                for (var offset = 0; offset < findingElements.Length; offset++)
                {
                    var finding = findingElements[offset];
                    var path = $"$.unsupportedCertaintyFindings[{offset}]";
                    if (finding.ValueKind != JsonValueKind.Object)
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.InvalidValueKind,
                            path,
                            null,
                            finding.ValueKind,
                            $"Unsupported-certainty finding at '{path}' must be an object.");
                        continue;
                    }

                    ValidateProperties(
                        finding,
                        path,
                        FindingCanonicalProperties,
                        aliases: [],
                        additive: [],
                        context);
                    var claimIndex = RequiredInt32(
                        finding,
                        path,
                        "claimIndex",
                        context);
                    if (claimIndex is not null
                        && (claimIndex < 0 || claimIndex >= claimCount))
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.InvalidValue,
                            PropertyPath(path, "claimIndex"),
                            "claimIndex",
                            JsonValueKind.Number,
                            $"Unsupported-certainty claim index {claimIndex} is outside the diagnosis.");
                        claimIndex = null;
                    }

                    var categoryText = RequiredString(
                        finding,
                        path,
                        "category",
                        context);
                    var category = categoryText is null
                        ? null
                        : ParseUnsupportedCertaintyCategory(
                            categoryText,
                            PropertyPath(path, "category"),
                            context);
                    var rationale = RequiredString(
                        finding,
                        path,
                        "rationale",
                        context);
                    if (claimIndex is not null
                        && category is not null
                        && rationale is not null)
                    {
                        findings.Add(new OperationalUnsupportedCertaintyFinding(
                            claimIndex.Value,
                            category.Value,
                            rationale));
                    }
                }

                if (context.FailureCount == failureCountBeforeFindings)
                {
                    foreach (var duplicate in findings
                        .GroupBy(value => (value.ClaimIndex, value.Category))
                        .Where(group => group.Count() > 1))
                    {
                        context.AddFailure(
                            OperationalAssessmentFailureCategory.DuplicateProperty,
                            "$.unsupportedCertaintyFindings",
                            "unsupportedCertaintyFindings",
                            JsonValueKind.Array,
                            "Unsupported-certainty findings must be unique by claim and category.");
                    }
                }
            }

            var usefulNextSteps = Strings(root, "$", "usefulNextSteps", context);
            var unhelpfulNextSteps = Strings(
                root,
                "$",
                "unhelpfulNextSteps",
                context);
            if (usefulNextSteps is not null && unhelpfulNextSteps is not null)
            {
                foreach (var contradiction in usefulNextSteps.Intersect(
                    unhelpfulNextSteps,
                    StringComparer.Ordinal))
                {
                    context.AddFailure(
                        OperationalAssessmentFailureCategory.ContradictoryValue,
                        "$.usefulNextSteps",
                        "usefulNextSteps",
                        JsonValueKind.Array,
                        "The same next step cannot be classified as both useful and unhelpful.");
                }
            }

            var uncertaintyText = RequiredString(
                root,
                "$",
                "uncertainty",
                context);
            var uncertainty = uncertaintyText is null
                ? null
                : ParseUncertainty(uncertaintyText, "$.uncertainty", context);
            var uncertaintyRationale = RequiredString(
                root,
                "$",
                "uncertaintyRationale",
                context);
            var summary = RequiredString(root, "$", "summary", context);

            context.ThrowIfInvalid();
            var orderedClaims = claims
                .OrderBy(value => value.Index)
                .Select(value => new OperationalClaimAssessment(
                    $"claim[{value.Index}]",
                    value.Support,
                    value.Rationale))
                .ToArray();
            return new OperationalParsedAssessment(
                orderedClaims,
                claimTextAliasNormalized,
                uncertainty!.Value,
                uncertaintyRationale!,
                findings,
                usefulNextSteps!,
                unhelpfulNextSteps!,
                summary!,
                context.RetainedAdditionalProperties,
                context.AdditionalPropertyCount,
                context.DiagnosticsTruncated);
        }
    }

    private static void ScanNameCollisions(
        JsonElement element,
        string path,
        ValidationContext context)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var exact = new HashSet<string>(StringComparer.Ordinal);
            var insensitive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                var diagnosticName = DiagnosticPropertyName(property.Name);
                var propertyPath = PropertyPath(path, diagnosticName);
                if (!exact.Add(property.Name))
                {
                    context.AddFailure(
                        OperationalAssessmentFailureCategory.DuplicateProperty,
                        propertyPath,
                        diagnosticName,
                        property.Value.ValueKind,
                        $"Assessment property '{diagnosticName}' is duplicated at '{propertyPath}'.");
                }
                else if (insensitive.TryGetValue(property.Name, out var existing)
                    && !string.Equals(existing, property.Name, StringComparison.Ordinal))
                {
                    context.AddFailure(
                        OperationalAssessmentFailureCategory.AmbiguousProperty,
                        propertyPath,
                        diagnosticName,
                        property.Value.ValueKind,
                        $"Assessment properties '{DiagnosticPropertyName(existing)}' and "
                        + $"'{diagnosticName}' differ only by case at '{path}'.");
                }
                else
                {
                    insensitive.Add(property.Name, property.Name);
                }

                ScanNameCollisions(property.Value, propertyPath, context);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                ScanNameCollisions(item, $"{path}[{index}]", context);
                index++;
            }
        }
    }

    private static void ValidateProperties(
        JsonElement element,
        string path,
        IEnumerable<string> canonical,
        IEnumerable<string> aliases,
        IEnumerable<string> additive,
        ValidationContext context)
    {
        var canonicalNames = canonical.ToHashSet(StringComparer.Ordinal);
        var aliasNames = aliases.ToHashSet(StringComparer.Ordinal);
        var additiveNames = additive.ToHashSet(StringComparer.Ordinal);
        var reviewedNames = canonicalNames
            .Concat(aliasNames)
            .Concat(additiveNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                continue;
            }

            if (canonicalNames.Contains(property.Name)
                || aliasNames.Contains(property.Name))
            {
                continue;
            }

            var diagnosticName = DiagnosticPropertyName(property.Name);
            var propertyPath = PropertyPath(path, diagnosticName);
            if (reviewedNames.Contains(property.Name)
                && !additiveNames.Contains(property.Name))
            {
                context.AddFailure(
                    OperationalAssessmentFailureCategory.AmbiguousProperty,
                    propertyPath,
                    diagnosticName,
                    property.Value.ValueKind,
                    $"Assessment property '{diagnosticName}' at '{propertyPath}' ambiguously shadows a canonical field or reviewed alias.");
                continue;
            }

            context.AddAdditionalProperty(
                propertyPath,
                diagnosticName,
                property.Value.ValueKind);
        }
    }

    private static void ValidateTypedAdditions(
        JsonElement root,
        IReadOnlyDictionary<string, string> additiveProperties,
        ValidationContext context)
    {
        foreach (var (name, expectedKind) in additiveProperties)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (expectedKind != "stringArray")
            {
                throw new InvalidOperationException(
                    $"Unsupported additive assessment property kind '{expectedKind}'.");
            }

            if (value.ValueKind != JsonValueKind.Array)
            {
                context.AddFailure(
                    OperationalAssessmentFailureCategory.InvalidValueKind,
                    PropertyPath("$", name),
                    name,
                    value.ValueKind,
                    $"Additive assessment property '{name}' must be an array of strings.");
                continue;
            }

            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    context.AddFailure(
                        OperationalAssessmentFailureCategory.InvalidValueKind,
                        $"{PropertyPath("$", name)}[{index}]",
                        name,
                        item.ValueKind,
                        $"Additive assessment property '{name}' must be an array of strings.");
                }

                index++;
            }
        }
    }

    private static JsonElement[]? RequiredArray(
        JsonElement root,
        string parentPath,
        string name,
        ValidationContext context)
    {
        var value = RequiredProperty(root, parentPath, name, context);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.Array)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValueKind,
                PropertyPath(parentPath, name),
                name,
                value.Value.ValueKind,
                $"Assessment field '{name}' must be an array.");
            return null;
        }

        return value.Value.EnumerateArray().ToArray();
    }

    private static string[]? Strings(
        JsonElement root,
        string parentPath,
        string name,
        ValidationContext context)
    {
        var values = RequiredArray(root, parentPath, name, context);
        if (values is null)
        {
            return null;
        }

        var strings = new List<string>(values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var path = $"{PropertyPath(parentPath, name)}[{index}]";
            if (values[index].ValueKind == JsonValueKind.String)
            {
                strings.Add(values[index].GetString()!);
                continue;
            }

            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValueKind,
                path,
                name,
                values[index].ValueKind,
                $"{name} values must be strings.");
            if (values[index].ValueKind == JsonValueKind.Object)
            {
                foreach (var property in values[index].EnumerateObject())
                {
                    var diagnosticName = DiagnosticPropertyName(property.Name);
                    context.AddFailure(
                        OperationalAssessmentFailureCategory.UnexpectedProperty,
                        PropertyPath(path, diagnosticName),
                        diagnosticName,
                        property.Value.ValueKind,
                        $"Next-step object property '{diagnosticName}' is not allowed because canonical next steps are strings.");
                }
            }
        }

        return strings.ToArray();
    }

    private static int? RequiredInt32(
        JsonElement root,
        string parentPath,
        string name,
        ValidationContext context)
    {
        var value = RequiredProperty(root, parentPath, name, context);
        if (value is null)
        {
            return null;
        }

        if (value.Value.ValueKind != JsonValueKind.Number
            || !value.Value.TryGetInt32(out var parsed))
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValueKind,
                PropertyPath(parentPath, name),
                name,
                value.Value.ValueKind,
                $"Assessment field '{name}' must be a 32-bit integer.");
            return null;
        }

        return parsed;
    }

    private static string? RequiredString(
        JsonElement root,
        string parentPath,
        string name,
        ValidationContext context)
    {
        var value = RequiredProperty(root, parentPath, name, context);
        return value is null
            ? null
            : NonEmptyString(
                value.Value,
                PropertyPath(parentPath, name),
                name,
                context);
    }

    private static string? RequiredClaimText(
        JsonElement claim,
        string claimPath,
        ValidationContext context,
        out bool normalizedFromAlias)
    {
        var hasCanonical = claim.TryGetProperty("rationale", out var canonical);
        var hasAlias = claim.TryGetProperty("text", out var alias);
        normalizedFromAlias = false;
        if (!hasCanonical && !hasAlias)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.MissingProperty,
                PropertyPath(claimPath, "rationale"),
                "rationale",
                JsonValueKind.Undefined,
                "Assessment field 'rationale' was missing.");
            return null;
        }

        var canonicalText = hasCanonical
            ? NonEmptyString(
                canonical,
                PropertyPath(claimPath, "rationale"),
                "rationale",
                context)
            : null;
        var aliasText = hasAlias
            ? ClaimTextAliasString(
                alias,
                PropertyPath(claimPath, "text"),
                context)
            : null;
        if (hasCanonical
            && hasAlias
            && canonicalText is not null
            && aliasText is not null
            && !string.Equals(canonicalText, aliasText, StringComparison.Ordinal))
        {
            var canonicalPath = PropertyPath(claimPath, "rationale");
            var aliasPath = PropertyPath(claimPath, "text");
            context.AddFailure(
                OperationalAssessmentFailureCategory.ContradictoryValue,
                aliasPath,
                "text",
                alias.ValueKind,
                $"Assessment claim text properties '{canonicalPath}' "
                + $"({canonical.ValueKind}) and '{aliasPath}' ({alias.ValueKind}) "
                + "must be identical when both are present.");
        }

        normalizedFromAlias = !hasCanonical && aliasText is not null;
        return canonicalText ?? aliasText;
    }

    private static string? ClaimTextAliasString(
        JsonElement value,
        string path,
        ValidationContext context)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValueKind,
                path,
                "text",
                value.ValueKind,
                $"Assessment claim text alias at '{path}' must be a non-empty "
                + $"string; observed value kind {value.ValueKind}.");
            return null;
        }

        if (value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        context.AddFailure(
            OperationalAssessmentFailureCategory.InvalidValue,
            path,
            "text",
            JsonValueKind.String,
            $"Assessment claim text alias at '{path}' must be non-empty; "
            + "observed value kind String.");
        return null;
    }

    private static string? NonEmptyString(
        JsonElement value,
        string path,
        string name,
        ValidationContext context)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValueKind,
                path,
                name,
                value.ValueKind,
                $"Assessment field '{name}' must be a non-empty string.");
            return null;
        }

        if (value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        context.AddFailure(
            OperationalAssessmentFailureCategory.InvalidValue,
            path,
            name,
            JsonValueKind.String,
            $"Assessment field '{name}' must be a non-empty string.");
        return null;
    }

    private static JsonElement? RequiredProperty(
        JsonElement root,
        string parentPath,
        string name,
        ValidationContext context)
    {
        if (root.TryGetProperty(name, out var value))
        {
            return value;
        }

        context.AddFailure(
            OperationalAssessmentFailureCategory.MissingProperty,
            PropertyPath(parentPath, name),
            name,
            JsonValueKind.Undefined,
            $"Assessment field '{name}' was missing.");
        return null;
    }

    private static OperationalClaimSupport? ParseSupport(
        string value,
        string path,
        ValidationContext context)
    {
        var parsed = value switch
        {
            "supported" => OperationalClaimSupport.Supported,
            "partiallySupported" => OperationalClaimSupport.PartiallySupported,
            "unsupported" => OperationalClaimSupport.Unsupported,
            "notAssessable" => OperationalClaimSupport.NotAssessable,
            _ => (OperationalClaimSupport?)null,
        };
        if (parsed is null)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValue,
                path,
                "support",
                JsonValueKind.String,
                "Claim support value was invalid.");
        }

        return parsed;
    }

    private static OperationalUncertaintyQuality? ParseUncertainty(
        string value,
        string path,
        ValidationContext context)
    {
        var parsed = value switch
        {
            "appropriate" => OperationalUncertaintyQuality.Appropriate,
            "unnecessaryAbstention" => OperationalUncertaintyQuality.UnnecessaryAbstention,
            "overconfident" => OperationalUncertaintyQuality.Overconfident,
            "notAssessable" => OperationalUncertaintyQuality.NotAssessable,
            _ => (OperationalUncertaintyQuality?)null,
        };
        if (parsed is null)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValue,
                path,
                "uncertainty",
                JsonValueKind.String,
                "Uncertainty value was invalid.");
        }

        return parsed;
    }

    private static OperationalUnsupportedCertaintyCategory?
        ParseUnsupportedCertaintyCategory(
            string value,
            string path,
            ValidationContext context)
    {
        var parsed = value switch
        {
            "dominance" => OperationalUnsupportedCertaintyCategory.Dominance,
            "exactness" => OperationalUnsupportedCertaintyCategory.Exactness,
            "workloadEquivalence" =>
                OperationalUnsupportedCertaintyCategory.WorkloadEquivalence,
            "causality" => OperationalUnsupportedCertaintyCategory.Causality,
            "severity" => OperationalUnsupportedCertaintyCategory.Severity,
            "regression" => OperationalUnsupportedCertaintyCategory.Regression,
            "health" => OperationalUnsupportedCertaintyCategory.Health,
            "optimizationPriority" =>
                OperationalUnsupportedCertaintyCategory.OptimizationPriority,
            "missingAsZero" =>
                OperationalUnsupportedCertaintyCategory.MissingAsZero,
            _ => (OperationalUnsupportedCertaintyCategory?)null,
        };
        if (parsed is null)
        {
            context.AddFailure(
                OperationalAssessmentFailureCategory.InvalidValue,
                path,
                "category",
                JsonValueKind.String,
                "Unsupported-certainty category was invalid.");
        }

        return parsed;
    }

    private static OperationalAssessmentValidationException SingleFailure(
        OperationalAssessmentFailureCategory category,
        string path,
        string? propertyName,
        string? valueKind,
        string message)
    {
        var diagnostic = new OperationalAssessmentDiagnostic(
            category,
            path,
            propertyName,
            valueKind);
        return new OperationalAssessmentValidationException(
            message,
            diagnostic,
            [diagnostic],
            diagnosticCount: 1,
            diagnosticsTruncated: false,
            additionalPropertyCount: 0);
    }

    private static string PropertyPath(string parentPath, string propertyName)
        => propertyName.Length > 0
            && (char.IsLetter(propertyName[0]) || propertyName[0] == '_')
            && propertyName.Skip(1).All(character =>
                char.IsLetterOrDigit(character) || character == '_')
                ? $"{parentPath}.{propertyName}"
                : $"{parentPath}[{JsonSerializer.Serialize(propertyName)}]";

    private static string DiagnosticPropertyName(string propertyName)
    {
        if (propertyName is "claims"
            or "index"
            or "support"
            or "rationale"
            or "text"
            or "uncertainty"
            or "uncertaintyRationale"
            or "unsupportedCertaintyFindings"
            or "claimIndex"
            or "category"
            or "usefulNextSteps"
            or "unhelpfulNextSteps"
            or "summary"
            or "nextSteps"
            or "posture"
            || RootCanonicalProperties.Contains(
                propertyName,
                StringComparer.OrdinalIgnoreCase)
            || ClaimCanonicalProperties.Contains(
                propertyName,
                StringComparer.OrdinalIgnoreCase)
            || ClaimAliases.Contains(
                propertyName,
                StringComparer.OrdinalIgnoreCase)
            || FindingCanonicalProperties.Contains(
                propertyName,
                StringComparer.OrdinalIgnoreCase))
        {
            return propertyName;
        }

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(propertyName)))
            .ToLowerInvariant();
        return $"annotation_{hash[..12]}";
    }

    private static JsonSerializerOptions CreateDiagnosticJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record IndexedClaimAssessment(
        int Index,
        OperationalClaimSupport Support,
        string Rationale);

    private sealed class ValidationContext(int maximumDiagnosticBytes)
    {
        private readonly List<OperationalAssessmentDiagnostic> _diagnostics = [];
        private OperationalAssessmentDiagnostic? _firstFailure;
        private string? _firstFailureMessage;

        public int DiagnosticCount { get; private set; }

        public int FailureCount { get; private set; }

        public int AdditionalPropertyCount { get; private set; }

        public bool DiagnosticsTruncated => DiagnosticCount > _diagnostics.Count;

        public IReadOnlyList<OperationalAssessmentDiagnostic> RetainedAdditionalProperties
            => _diagnostics
                .Where(value =>
                    value.Category
                    == OperationalAssessmentFailureCategory.AdditionalProperty)
                .ToArray();

        public void AddAdditionalProperty(
            string path,
            string propertyName,
            JsonValueKind valueKind)
        {
            AdditionalPropertyCount++;
            Add(new OperationalAssessmentDiagnostic(
                OperationalAssessmentFailureCategory.AdditionalProperty,
                path,
                propertyName,
                valueKind.ToString()));
        }

        public void AddFailure(
            OperationalAssessmentFailureCategory category,
            string path,
            string? propertyName,
            JsonValueKind valueKind,
            string message)
            => AddFailure(category, path, propertyName, valueKind.ToString(), message);

        public void AddFailure(
            OperationalAssessmentFailureCategory category,
            string path,
            string? propertyName,
            string? valueKind,
            string message)
        {
            var diagnostic = new OperationalAssessmentDiagnostic(
                category,
                path,
                propertyName,
                valueKind);
            FailureCount++;
            _firstFailure ??= diagnostic;
            _firstFailureMessage ??= message;
            Add(diagnostic);
        }

        public void ThrowIfInvalid()
        {
            if (_firstFailure is null)
            {
                return;
            }

            var message = _firstFailureMessage!;
            if (FailureCount > 1 || AdditionalPropertyCount > 0)
            {
                message += $" Validation found {FailureCount} canonical error(s) and "
                    + $"{AdditionalPropertyCount} additional propert"
                    + (AdditionalPropertyCount == 1 ? "y" : "ies")
                    + $"; retained {_diagnostics.Count} of {DiagnosticCount} diagnostics.";
            }

            throw new OperationalAssessmentValidationException(
                message,
                _firstFailure,
                _diagnostics.ToArray(),
                DiagnosticCount,
                DiagnosticsTruncated,
                AdditionalPropertyCount);
        }

        private void Add(OperationalAssessmentDiagnostic diagnostic)
        {
            DiagnosticCount++;
            if (_diagnostics.Count < MaximumDiagnostics
                && DiagnosticBytes([.. _diagnostics, diagnostic])
                    <= maximumDiagnosticBytes)
            {
                _diagnostics.Add(diagnostic);
                return;
            }

            if (diagnostic.Category
                    != OperationalAssessmentFailureCategory.AdditionalProperty
                && !_diagnostics.Any(value =>
                    value.Category
                    != OperationalAssessmentFailureCategory.AdditionalProperty))
            {
                while (_diagnostics.Count > 0)
                {
                    _diagnostics.RemoveAt(_diagnostics.Count - 1);
                    if (DiagnosticBytes([.. _diagnostics, diagnostic])
                        <= maximumDiagnosticBytes)
                    {
                        _diagnostics.Add(diagnostic);
                        break;
                    }
                }
            }
        }

        private static int DiagnosticBytes(
            IReadOnlyList<OperationalAssessmentDiagnostic> diagnostics)
            => JsonSerializer.SerializeToUtf8Bytes(
                diagnostics,
                DiagnosticJsonOptions).Length;
    }
}
