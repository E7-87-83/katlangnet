namespace KatLang;

/// <summary>
/// Explicit KatLang front-end boundary.
/// Starts from raw syntax and produces an elaborated program ready for
/// evaluator-facing and semantic-model consumers.
/// </summary>
internal static class FrontEndPipeline
{
    internal static FrontEndResult Process(string source)
        => ProcessWithoutModuleElaboration(Parser.ParseSyntax(source));

    internal static FrontEndResult Process(
        string source,
        Func<string, string>? downloadCode,
        IEnumerable<string>? allowedHosts = null)
        => ProcessWithModuleElaboration(Parser.ParseSyntax(source), downloadCode, allowedHosts);

    internal static FrontEndResult Process(string source, RunOptions? options)
    {
        if (options?.DownloadCode is not null)
            return Process(source, options.DownloadCode, options.AllowedHosts);

        return Process(source);
    }

    private static FrontEndResult ProcessWithoutModuleElaboration(SyntaxParseResult syntaxResult)
    {
        var diagnostics = new List<Diagnostic>(syntaxResult.Diagnostics);
        var loadDiagnostics = LoadElaborationGuard.CreateUnavailableDiagnostics(syntaxResult.SyntaxRoot);

        if (loadDiagnostics.Count > 0)
        {
            diagnostics.AddRange(loadDiagnostics);
            return new FrontEndResult(syntaxResult.SyntaxRoot, diagnostics);
        }

        return FinalizeElaboration(syntaxResult.SyntaxRoot, diagnostics);
    }

    private static FrontEndResult ProcessWithModuleElaboration(
        SyntaxParseResult syntaxResult,
        Func<string, string>? downloadCode,
        IEnumerable<string>? allowedHosts)
    {
        var diagnostics = new List<Diagnostic>(syntaxResult.Diagnostics);

        var loadDiagnosticStart = diagnostics.Count;
        var loader = new ModuleLoader(diagnostics, downloadCode, allowedHosts);
        var loadElaboratedRoot = loader.Elaborate(syntaxResult.SyntaxRoot);
        var loadDiagnosticsEnd = diagnostics.Count;

        if (LoadElaborationGuard.TryFindFirstUnresolvedLoad(loadElaboratedRoot, out _))
        {
            diagnostics.Add(LoadElaborationGuard.CreatePostElaborationInvariantDiagnostic(loadElaboratedRoot));
            return new FrontEndResult(loadElaboratedRoot, diagnostics);
        }

        return FinalizeElaboration(
            loadElaboratedRoot,
            diagnostics,
            canEvaluateAfterLoadErrors:
                !syntaxResult.HasErrors &&
                diagnostics
                    .Skip(loadDiagnosticStart)
                    .Take(loadDiagnosticsEnd - loadDiagnosticStart)
                    .Any(static d => d.Severity == DiagnosticSeverity.Error));
    }

    private static FrontEndResult FinalizeElaboration(
        Algorithm loadElaboratedRoot,
        List<Diagnostic> diagnostics,
        bool canEvaluateAfterLoadErrors = false)
    {
        var (parameterizedRoot, parameterDiagnostics) = ParameterDetector.Detect(loadElaboratedRoot);
        diagnostics.AddRange(parameterDiagnostics);

        var implicitResolvedRoot = ImplicitArgumentResolver.Resolve(parameterizedRoot);
        var propertyExposedRoot = PropertyExposureResolver.Resolve(implicitResolvedRoot);
        return new FrontEndResult(propertyExposedRoot, diagnostics, canEvaluateAfterLoadErrors);
    }
}

/// <summary>
/// Raw syntax result produced directly by the recursive-descent parser.
/// No front-end elaboration passes have run yet.
/// </summary>
internal sealed record SyntaxParseResult(Algorithm Root, IReadOnlyList<Diagnostic> Diagnostics)
{
    public Algorithm SyntaxRoot => Root;

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// Internal front-end result after load elaboration, parameter detection,
/// implicit argument resolution, and property exposure analysis.
/// </summary>
internal sealed record FrontEndResult(
    Algorithm ElaboratedRoot,
    IReadOnlyList<Diagnostic> Diagnostics,
    bool CanEvaluateAfterLoadErrors = false)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public ParseResult ToParseResult() => new(ElaboratedRoot, Diagnostics);
}
