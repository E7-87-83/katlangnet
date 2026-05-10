namespace KatLang;

public sealed record CallableArityFacts(
    int MinTopLevelArgumentCount,
    int? MaxTopLevelArgumentCount,
    bool HasTopLevelVariadic,
    int TopLevelVariadicCount)
{
    public bool HasMultipleTopLevelVariadics => TopLevelVariadicCount > 1;

    public bool AcceptsArgumentCount(int argumentCount)
        => argumentCount >= MinTopLevelArgumentCount
            && (MaxTopLevelArgumentCount is null || argumentCount <= MaxTopLevelArgumentCount.Value);
}

public static class CallableSignatureDiagnostics
{
    public static CallableArityFacts GetArityFacts(CallableSignature signature)
    {
        var topLevelVariadicCount = signature.ParameterPatterns.Count(IsTopLevelVariadicCapture);
        var minArgumentCount = signature.ParameterPatterns.Count - topLevelVariadicCount;
        var maxArgumentCount = topLevelVariadicCount > 0
            ? (int?)null
            : signature.ParameterPatterns.Count;

        return new CallableArityFacts(
            minArgumentCount,
            maxArgumentCount,
            topLevelVariadicCount > 0,
            topLevelVariadicCount);
    }

    public static int TopLevelVariadicIndex(CallableSignature signature)
    {
        for (var index = 0; index < signature.ParameterPatterns.Count; index++)
        {
            if (IsTopLevelVariadicCapture(signature.ParameterPatterns[index]))
                return index;
        }

        return -1;
    }

    public static string FormatExpectedSignature(CallableSignature signature)
        => signature.DisplayText;

    public static string FormatBadArity(CallableSignature signature, int actualArgumentCount)
        => $"Callable `{signature.DisplayText}` expects {FormatExpectedArgumentCount(GetArityFacts(signature))}, but was called with {FormatCount(actualArgumentCount, "argument")}.";

    internal static string FormatBuiltinItemCountMismatch(
        string builtinName,
        CallableSignature signature,
        int actualItemCount)
        => $"Builtin '{builtinName}' expects {FormatExpectedItemCount(GetArityFacts(signature))} for {signature.DisplayText}, but received {actualItemCount}.";

    public static string FormatMultipleTopLevelVariadics(CallableSignature signature)
        => $"Callable signature `{signature.DisplayText}` cannot contain more than one variadic parameter.";

    public static string FormatExpectedArgumentCount(CallableArityFacts facts)
    {
        if (facts.MaxTopLevelArgumentCount is null)
        {
            return facts.MinTopLevelArgumentCount == 0
                ? "any number of arguments"
                : $"at least {FormatCount(facts.MinTopLevelArgumentCount, "argument")}";
        }

        if (facts.MinTopLevelArgumentCount == facts.MaxTopLevelArgumentCount.Value)
            return FormatCount(facts.MinTopLevelArgumentCount, "argument");

        return $"between {facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} and {FormatCount(facts.MaxTopLevelArgumentCount.Value, "argument")}";
    }

    internal static string FormatExpectedItemCount(CallableArityFacts facts)
    {
        if (facts.MaxTopLevelArgumentCount is null)
        {
            return facts.MinTopLevelArgumentCount == 0
                ? "any number of item(s)"
                : $"at least {facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} item(s)";
        }

        if (facts.MinTopLevelArgumentCount == facts.MaxTopLevelArgumentCount.Value)
            return $"{facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} item(s)";

        return $"between {facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} and {facts.MaxTopLevelArgumentCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} item(s)";
    }

    internal static string FormatExpectedArgumentCountWithoutNoun(CallableArityFacts facts)
    {
        if (facts.MaxTopLevelArgumentCount is null)
        {
            return facts.MinTopLevelArgumentCount == 0
                ? "any number of"
                : $"at least {facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        if (facts.MinTopLevelArgumentCount == facts.MaxTopLevelArgumentCount.Value)
            return facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $"between {facts.MinTopLevelArgumentCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} and {facts.MaxTopLevelArgumentCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    private static string FormatCount(int count, string singularNoun)
        => count == 1 ? $"1 {singularNoun}" : $"{count.ToString(System.Globalization.CultureInfo.InvariantCulture)} {singularNoun}s";

    private static bool IsTopLevelVariadicCapture(ParameterPattern parameterPattern)
        => parameterPattern is CaptureParameterPattern { Kind: ParameterKind.Variadic };
}
