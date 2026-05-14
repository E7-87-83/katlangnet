using System.Collections;
using System.Runtime.CompilerServices;
using KatLang.Evaluation.Caching;
using KatLang.Optimizations.Loops;
using KatLang.Optimizations.Sequences;
using KatLang.Runtime;

namespace KatLang;

/// <summary>
/// KatLang 0.75 evaluator matching the Lean specification.
/// Uses <see cref="EvalResult{T}"/> (<c>EvalM := Except Error</c>) for structured errors
/// instead of nullable returns.
/// Ownership-first lookup: local → parent chain structural → opens fallback across chain.
/// Property visibility: opens only expose PUBLIC exported properties; structural lookup sees exported properties only.
///
/// Builtins (If, While, Repeat, Atoms, Content, Range, Filter, Map, Count, Contains, First, Last, Distinct, Take, Skip, Min, Max, Sum, Avg, Reduce) are injected via a prelude algorithm in the initial
/// call stack, matching Lean's <c>preludeAlg</c>. Call dispatch switches on Algorithm kind:
/// <c>Algorithm.Builtin</c> → lazy arg resolution + <c>applyBuiltin</c>;
/// <c>Algorithm.User</c> → dual-view argument binding via <c>evalUserCall</c>.
///
/// Higher-order algorithm parameters use dual-view semantics:
/// - AlgEnv: algorithm meaning (callable/structural), resolved via <c>tryResolveArgAlgs</c>
/// - ValEnv: value meaning, resolved via independent per-expression eager evaluation
/// - <c>Eval(Param(x))</c>: checks ValEnv first, then AlgEnv as fallback
///   (0-param algorithm → auto-evaluate; multi-param → arity mismatch)
/// - <c>ResolveAlg(Param(x))</c>: checks AlgEnv before returning NotAnAlgorithm
/// </summary>
public static class Evaluator
{
    private readonly record struct ResolvedLexicalProperty(
        Algorithm? Owner,
        Property Binding,
        Algorithm ResolvedAlgorithm);

    private static readonly ConditionalWeakTable<ScopeCtx, Algorithm> ScopeOwnerAlgorithms = new();

    // ── EvalCtx (Lean: EvalCtx) ─────────────────────────────────────────────

    /// <summary>
    /// Evaluation context threaded through resolution and evaluation.
    /// Wraps the algorithm chain (current algorithm + enclosing callers) used for
    /// both lexical resolution and runtime dispatch.
    /// AlgEnv carries algorithm-typed parameter bindings for higher-order dispatch.
    /// Lean: structure EvalCtx where callStack : List Algorithm; algEnv : AlgEnv := [].
    /// </summary>
    internal readonly record struct EvalCtx(
        IReadOnlyList<Algorithm> CallStack,
        IReadOnlyList<(string Name, Algorithm Value)> AlgEnv,
        IReadOnlyList<(string Name, CountedResult Value)> CountedParamEnv,
        IReadOnlyList<(string Name, CountedResult Value)> VariadicStreamEnv,
        IZeroArgPropertyResultCache ZeroArgPropertyResultCache,
        bool EnableLoopOptimization,
        LoopOptimizationDiagnostics? LoopDiagnostics,
        bool EnableSequencePipelineOptimization,
        SequencePipelineDiagnostics? SequenceDiagnostics)
    {
        public static readonly EvalCtx Empty = new([], [], [], [], UncachedZeroArgPropertyResultCache.Instance, true, null, true, null);

        /// <summary>Lean: EvalCtx.push — prepend an algorithm to the call stack.</summary>
        public EvalCtx Push(Algorithm alg)
            => new(
                Prepend(alg, CallStack),
                AlgEnv,
                CountedParamEnv,
                VariadicStreamEnv,
                ZeroArgPropertyResultCache,
                EnableLoopOptimization,
                LoopDiagnostics,
                EnableSequencePipelineOptimization,
                SequenceDiagnostics);

        /// <summary>Lean: EvalCtx.head? — first algorithm in the call stack.</summary>
        public Algorithm? Head => CallStack.Count > 0 ? CallStack[0] : null;

        /// <summary>Lean: EvalCtx.withAlgEnv — replace the algorithm environment.</summary>
        public EvalCtx WithAlgEnv(IReadOnlyList<(string, Algorithm)> algEnv)
            => new(
                CallStack,
                algEnv,
                CountedParamEnv,
                VariadicStreamEnv,
                ZeroArgPropertyResultCache,
                EnableLoopOptimization,
                LoopDiagnostics,
                EnableSequencePipelineOptimization,
                SequenceDiagnostics);

        /// <summary>Replace the counted callback-parameter environment.</summary>
        public EvalCtx WithCountedParamEnv(IReadOnlyList<(string, CountedResult)> countedParamEnv)
            => new(
                CallStack,
                AlgEnv,
                countedParamEnv,
                VariadicStreamEnv,
                ZeroArgPropertyResultCache,
                EnableLoopOptimization,
                LoopDiagnostics,
                EnableSequencePipelineOptimization,
                SequenceDiagnostics);

        /// <summary>Replace bindings that carry variadic-capture stream provenance.</summary>
        public EvalCtx WithVariadicStreamEnv(IReadOnlyList<(string, CountedResult)> variadicStreamEnv)
            => new(
                CallStack,
                AlgEnv,
                CountedParamEnv,
                variadicStreamEnv,
                ZeroArgPropertyResultCache,
                EnableLoopOptimization,
                LoopDiagnostics,
                EnableSequencePipelineOptimization,
                SequenceDiagnostics);
    }

    // ── Environment types ────────────────────────────────────────────────────

    private static object ValueEnvironmentCacheIdentity(IReadOnlyList<(string, Result)> valEnv)
        => valEnv is IValueEnvironmentCacheIdentityProvider provider
            ? provider.CacheIdentity
            : valEnv;

    /// <summary>Value environment: maps parameter names to results. Lean: lookupVal (Option).</summary>
    private static Result? LookupVal(IReadOnlyList<(string Name, Result Value)> env, string name)
    {
        foreach (var (n, v) in env)
            if (n == name) return v;
        return null;
    }

    /// <summary>
    /// Counted callback-parameter environment for projected higher-order items.
    /// These bindings preserve both the normalized value and the emitted
    /// top-level count so callback params behave like <c>S:i</c>.
    /// </summary>
    private static CountedResult? LookupCountedParam(IReadOnlyList<(string Name, CountedResult Value)> env, string name)
    {
        foreach (var (n, v) in env)
            if (n == name) return v;
        return null;
    }

    internal static IReadOnlyList<(string Name, CountedResult Value)> ShadowCountedParamEnv(
        IReadOnlyList<(string Name, CountedResult Value)> env,
        IEnumerable<string> shadowedNames)
    {
        if (env.Count == 0)
            return env;

        var shadowed = new HashSet<string>(shadowedNames);
        if (shadowed.Count == 0)
            return env;

        var filtered = new List<(string Name, CountedResult Value)>(env.Count);
        var removedAny = false;
        foreach (var binding in env)
        {
            if (shadowed.Contains(binding.Name))
            {
                removedAny = true;
                continue;
            }

            filtered.Add(binding);
        }

        return removedAny ? filtered : env;
    }

    /// <summary>Algorithm environment: maps parameter names to algorithms. Lean: AlgEnv.lookup.</summary>
    private static Algorithm? LookupAlg(IReadOnlyList<(string Name, Algorithm Value)> env, string name)
    {
        foreach (var (n, v) in env)
            if (n == name) return v;
        return null;
    }

    // ── Algorithm helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Lean: Algorithm.withParent. No-op for Builtin variant.
    /// </summary>
    private static Algorithm WithParent(Algorithm alg, ScopeCtx? parent) => alg switch
    {
        Algorithm.Builtin => alg,
        _ => alg with { Parent = parent },
    };

    private static ScopeCtx AsScopeCtx(Algorithm alg)
    {
        var scope = new ScopeCtx(alg.Parent, alg.Opens, alg.Properties);
        ScopeOwnerAlgorithms.Add(scope, alg);
        return scope;
    }

    private static Algorithm? TryGetScopeOwnerAlgorithm(ScopeCtx scope)
        => ScopeOwnerAlgorithms.TryGetValue(scope, out var owner)
            ? owner
            : null;

    /// <summary>Best-effort algorithm path for internal diagnostics.</summary>
    internal static string? TryGetAlgorithmPath(Algorithm algorithm)
    {
        if (algorithm.Parent is not { } scope)
            return null;

        var name = TryGetAlgorithmNameInScope(algorithm, scope);
        if (name is null)
            return null;

        var owner = TryGetScopeOwnerAlgorithm(scope);
        var ownerPath = owner is null || ReferenceEquals(owner, algorithm)
            ? null
            : TryGetAlgorithmPath(owner);
        return ownerPath is null ? name : $"{ownerPath}.{name}";
    }

    private static string? TryGetAlgorithmNameInScope(Algorithm algorithm, ScopeCtx scope)
    {
        foreach (var property in scope.Properties)
        {
            if (WithParent(property.Value, scope).Equals(algorithm))
                return property.Name;
        }

        return null;
    }

    /// <summary>Lean: Algorithm.childOf â€” wire a child algorithm to its parent's scope context.</summary>
    private static Algorithm ChildOf(Algorithm parent, Algorithm child)
        => WithParent(child, AsScopeCtx(parent));

    /// <summary>
    /// Create a temporary algorithm from a ScopeCtx for open resolution.
    /// Lean: Algorithm.forOpens.
    /// </summary>
    private static Algorithm ForOpens(ScopeCtx sc)
        => new Algorithm.User(
            Parent: sc, Parameters: [], Opens: sc.Opens,
            Properties: [], Output: []);

    /// <summary>Lean: Algorithm.lookupProp (any visibility).</summary>
    private static Algorithm? LookupProp(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
            if (prop.Name == name) return prop.Value;
        return null;
    }

    private static Property? LookupPropBinding(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
            if (prop.Name == name) return prop;
        return null;
    }

    private static bool IsExported(Property property)
        => property.Exposure == PropertyExposure.Exported;

    private static Algorithm? LookupExportedProp(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
        {
            if (prop.Name == name && IsExported(prop))
                return prop.Value;
        }

        return null;
    }

    private static Property? LookupExportedPropBinding(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
        {
            if (prop.Name == name && IsExported(prop))
                return prop;
        }

        return null;
    }

    /// <summary>Lean: Algorithm.lookupPublicProp (public only).</summary>
    private static Algorithm? LookupPublicProp(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
            if (prop.Name == name && prop.IsPublic && IsExported(prop)) return prop.Value;
        return null;
    }

    private static Property? LookupPublicPropBinding(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
            if (prop.Name == name && prop.IsPublic && IsExported(prop)) return prop;
        return null;
    }

    /// <summary>
    /// Checks if a property exists (any visibility) in the algorithm.
    /// Used to distinguish "missing" from "exists but private" in error reporting.
    /// </summary>
    private static bool HasPropAny(Algorithm alg, string name)
    {
        foreach (var prop in alg.Properties)
            if (prop.Name == name) return true;
        return false;
    }

    private static bool ConditionalBranchesDefineProperty(Algorithm alg, string name)
    {
        if (alg is not Algorithm.Conditional conditional)
            return false;

        foreach (var branch in conditional.Branches)
        {
            foreach (var prop in branch.Body.Properties)
            {
                if (prop.Name == name)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Human-readable constructor kind for diagnostics.
    /// Lean: Expr.kind.
    /// </summary>
    internal static string ExprKind(Expr e) => e switch
    {
        Expr.Param => "param",
        Expr.Num => "num",
        Expr.StringLiteral => "stringLiteral",
        Expr.Unary => "unary",
        Expr.Binary => "binary",
        Expr.Index => "index",
        Expr.SequenceSupply => "sequenceSupply",
        Expr.Resolve => "resolve",
        Expr.Block => "block",
        Expr.Call => "call",
        Expr.DotCall => "dotCall",
        Expr.Grace => "grace",
        Expr.NativeCall => "nativeCall",
        _ => "unknown",
    };

    /// <summary>
    /// Predicate defining which expression forms are allowed in open position.
    /// Only structural references to libraries are permitted.
    /// Lean: Expr.isOpenForm.
    /// </summary>
    private static bool IsOpenForm(Expr e) => e is
        Expr.Block or Expr.Resolve or Expr.DotCall(_, _, null);

    /// <summary>
    /// Extract a descriptive name from an open expression for error messages.
    /// Lean: openExprName.
    /// </summary>
    internal static string OpenExprName(Expr e) => e switch
    {
        Expr.Resolve(var n) => n,
        Expr.Param(var n) => n,
        Expr.Num(var n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Expr.StringLiteral(var s) => $"'{s}'",
        Expr.Unary(var op, var operand) => op switch
        {
            UnaryOp.Minus => $"-{OpenExprUnaryOperandName(operand)}",
            UnaryOp.Not => $"not {OpenExprUnaryOperandName(operand)}",
            _ => $"({ExprKind(e)})",
        },
        Expr.Binary(var op, var left, var right) => $"({OpenExprName(left)} {OpenExprBinaryOp(op)} {OpenExprName(right)})",
        Expr.Index(var target, var selector) => $"{OpenExprName(target)}[{OpenExprName(selector)}]",
        Expr.DotCall(var o, var n, var argsOpt) => argsOpt is null
            ? OpenExprName(o) + "." + n
            : OpenExprName(o) + "." + n + "(...)",
        Expr.Call(var f, _) => OpenExprName(f) + "(...)",
        Expr.Grace(var inner, var weight) => weight < 0
            ? "~" + OpenExprName(inner)
            : OpenExprName(inner) + "~",
        Expr.Block => "(inline library)",
        Expr.SequenceSupply(var a, var b) => OpenExprName(a) + "..." + OpenExprName(b),
        _ => $"({ExprKind(e)})",
    };

    private static string OpenExprUnaryOperandName(Expr expr) => expr switch
    {
        Expr.Param or Expr.Resolve or Expr.Num or Expr.StringLiteral or Expr.DotCall or Expr.Index
            => OpenExprName(expr),
        _ => $"({OpenExprName(expr)})",
    };

    private static string OpenExprBinaryOp(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Sub => "-",
        BinaryOp.Mul => "*",
        BinaryOp.Div => "/",
        BinaryOp.IDiv => "div",
        BinaryOp.Mod => "mod",
        BinaryOp.Pow => "^",
        BinaryOp.Lt => "<",
        BinaryOp.Gt => ">",
        BinaryOp.Le => "<=",
        BinaryOp.Ge => ">=",
        BinaryOp.Eq => "==",
        BinaryOp.Ne => "!=",
        BinaryOp.And => "and",
        BinaryOp.Or => "or",
        BinaryOp.Xor => "xor",
        _ => "?",
    };

    private static string ExprDiagnosticName(Expr expr) => expr switch
    {
        Expr.Block(var algorithm) when algorithm.Params.Count == 0
            && algorithm.Opens.Count == 0
            && algorithm.Properties.Count == 0
            => $"({string.Join(", ", algorithm.Output.Select(ExprDiagnosticName))})",
        Expr.Binary(var op, var left, var right) => $"{ExprDiagnosticName(left)} {OpenExprBinaryOp(op)} {ExprDiagnosticName(right)}",
        _ => OpenExprName(expr),
    };

    private static string BinaryExprDiagnosticName(BinaryOp op, Expr left, Expr right)
        => $"{ExprDiagnosticName(left)} {OpenExprBinaryOp(op)} {ExprDiagnosticName(right)}";

    // ── Error context helpers ──────────────────────────────────────────────

    private static ErrorContext CtxOpen(string key) => new OpenResolutionContext(key);
    private static ErrorContext CtxCall(Expr f) => new CallContext(OpenExprName(f));
    private static ErrorContext CtxProperty(string name) => new PropertyEvaluationContext(name);
    private static ErrorContext CtxDotCall(Expr obj, string name) => new DotCallContext(OpenExprName(obj), name);

    // ── Error context helper ────────────────────────────────────────────────

    /// <summary>
    /// Attach context to any error raised by the given result.
    /// Lean: withCtx.
    /// </summary>
    private static EvalResult<T> WithCtx<T>(ErrorContext context, EvalResult<T> result) =>
        result.IsError
            ? new EvalError.WithContext(context, result.Error) { Span = result.Error.Span }
            : result;

    private static EvalResult<T> WithCtx<T>(string context, EvalResult<T> result)
        => WithCtx(new TextErrorContext(context), result);

    private static EvalResult<T> WithSpan<T>(SourceSpan? span, EvalResult<T> result) =>
        result.IsError && result.Error.Span is null
            ? (result.Error with { Span = span })
            : result;

    private static EvalResult<T> WithPropertyContextOnMissingOutput<T>(string name, SourceSpan? span, EvalResult<T> result)
    {
        if (result.IsError && result.Error is EvalError.MissingOutput)
            return WithSpan<T>(span, new EvalError.WithContext(CtxProperty(name), result.Error));

        return WithSpan(span, result);
    }

    private static EvalResult<T> MissingImplicitArguments<T>(IReadOnlyList<string> paramNames, SourceSpan? span)
    {
        var inner = new EvalError.UnresolvedImplicitParams(paramNames) { Span = span };
        return new EvalError.WithContext(new ImplicitParameterContext(paramNames, 0), inner) { Span = span };
    }

    /// <summary>Returns the <see cref="SourceSpan"/> of the first output expression that has one.</summary>
    private static SourceSpan? FirstSpan(IReadOnlyList<Expr> output)
    {
        foreach (var e in output)
            if (e.Span is { } s) return s;
        return null;
    }

    // ── Lexical lookup (direct — no opens, used for open resolution) ────────

    /// <summary>Lean: lookupInParentsDirect (Option).</summary>
    private static Algorithm? LookupInParentsDirect(ScopeCtx sc, string name)
    {
            foreach (var prop in sc.Properties)
            {
                if (prop.Name == name)
                    return WithParent(prop.Value, sc);
        }

        return sc.Parent is { } parent ? LookupInParentsDirect(parent, name) : null;
    }

    /// <summary>
    /// Direct lexical lookup: local properties + parent chain only (no opens).
    /// Lean: lookupLexicalDirect (Option).
    /// </summary>
    private static Algorithm? LookupLexicalDirect(Algorithm alg, string name)
    {
            var local = LookupProp(alg, name);
            if (local is not null)
                return ChildOf(alg, local);

        return alg.Parent is { } sc ? LookupInParentsDirect(sc, name) : null;
    }

    /// <summary>
    /// Unwired parent-chain lookup: returns algorithm as stored at its definition site,
    /// without rewiring parent.
    /// Lean: lookupInParentsDirectUnwired.
    /// </summary>
    private static Algorithm? LookupInParentsDirectUnwired(ScopeCtx sc, string name)
    {
        foreach (var prop in sc.Properties)
        {
            if (prop.Name == name)
                return prop.Value; // no wiring
        }
        return sc.Parent is { } parent ? LookupInParentsDirectUnwired(parent, name) : null;
    }

    /// <summary>
    /// Unwired direct lexical lookup: same search path as LookupLexicalDirect
    /// but returns algorithms without rewiring to the caller.
    /// Lean: lookupLexicalDirectUnwired.
    /// </summary>
    private static Algorithm? LookupLexicalDirectUnwired(Algorithm alg, string name)
    {
        var local = LookupProp(alg, name);
        if (local is not null)
            return local; // no wiring
        return alg.Parent is { } sc ? LookupInParentsDirectUnwired(sc, name) : null;
    }

    /// <summary>
    /// Public-only unwired parent-chain lookup: returns public properties only, unwired.
    /// Lean: lookupInParentsDirectUnwiredPublic.
    /// </summary>
    private static Algorithm? LookupInParentsDirectUnwiredPublic(ScopeCtx sc, string name)
    {
        foreach (var prop in sc.Properties)
        {
            if (prop.Name == name && prop.IsPublic && IsExported(prop))
                return prop.Value; // no wiring, public only
        }
        return sc.Parent is { } parent ? LookupInParentsDirectUnwiredPublic(parent, name) : null;
    }

    /// <summary>
    /// Public-only unwired direct lexical lookup: searches local then parent chain
    /// for public properties only, returning algorithms unwired (definition-site parent preserved).
    /// Lean: lookupLexicalDirectUnwiredPublic.
    /// </summary>
    private static Algorithm? LookupLexicalDirectUnwiredPublic(Algorithm alg, string name)
    {
        var local = LookupPublicProp(alg, name);
        if (local is not null)
            return local; // no wiring, public only
        return alg.Parent is { } sc ? LookupInParentsDirectUnwiredPublic(sc, name) : null;
    }

    // ── Open resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves an open expression to a library algorithm.
    /// Lean: resolveOpen → EvalM Algorithm.
    /// </summary>
    private static EvalResult<Algorithm> ResolveOpen(Expr openExpr, EvalCtx ctx)
        => ResolveAlgForOpen(openExpr, ctx);

    /// <summary>
    /// A resolved open: its canonical dedup key, original expression, and resolved algorithm.
    /// Lean: ResolvedOpen (key, expr, lib).
    /// </summary>
    private readonly record struct ResolvedOpen(string Key, Expr Expr, Algorithm Lib);

    /// <summary>
    /// A single hit from open lookup: which provider supplied it, the library, and the child algorithm.
    /// Lean: OpenHit (provider, lib, child).
    /// </summary>
    private readonly record struct OpenHit(string Provider, Algorithm Lib, Property Binding);

    /// <summary>
    /// Resolve all opens of an algorithm upfront.
    /// Deduplicates named opens by <c>openExprName</c> (first occurrence wins) to avoid
    /// repeated resolution and spurious ambiguity from duplicate opens.
    /// Inline blocks are never deduplicated (each gets a unique positional key).
    /// Validates all open expressions first for fail-fast diagnostics.
    /// Lean: resolveAllOpens → EvalM (List ResolvedOpen).
    /// </summary>
    private static EvalResult<IReadOnlyList<ResolvedOpen>> ResolveAllOpens(
        Algorithm alg, EvalCtx ctx)
    {
        if (alg.Opens.Count == 0)
            return EvalResult<IReadOnlyList<ResolvedOpen>>.Ok([]);

        // Deduplicate by key (first occurrence wins); inline blocks use positional keys
        var seen = new HashSet<string>();
        var deduped = new List<(string Key, Expr Expr)>();
        for (var i = 0; i < alg.Opens.Count; i++)
        {
            var openExpr = alg.Opens[i];
            var key = openExpr is Expr.Block
                ? $"(inline#{i})"  // unique per original position, never deduped
                : OpenExprName(openExpr);
            if (seen.Add(key))
                deduped.Add((key, openExpr));
        }

        // Validate all open expressions first (fail-fast with clear errors)
        foreach (var (key, openExpr) in deduped)
        {
            if (!IsOpenForm(openExpr))
                return new EvalError.BadOpenForm($"{ExprKind(openExpr)}: {key}");
        }

        // Then resolve (each open wrapped with context using its dedup key)
        var result = new List<ResolvedOpen>(deduped.Count);
        foreach (var (key, openExpr) in deduped)
        {
            var libResult = WithCtx(
                CtxOpen(key),
                ResolveOpen(openExpr, ctx));
            if (libResult.IsError) return libResult.Error;
            result.Add(new ResolvedOpen(key, openExpr, libResult.Value));
        }
        return EvalResult<IReadOnlyList<ResolvedOpen>>.Ok(result);
    }

    /// <summary>
    /// Searches opened namespaces for a name using public-only property lookup.
    /// Returns Ok(null) if no open provides the name publicly.
    /// Returns Ok(alg) if exactly one open provides it publicly.
    /// Returns Err(AmbiguousOpen) if multiple opens provide it publicly.
    /// Lean: lookupOpens → EvalM (Option Algorithm).
    /// </summary>
    private static EvalResult<ResolvedLexicalProperty?> LookupOpens(
        Algorithm alg, string name, EvalCtx ctx)
    {
        if (alg.Opens.Count == 0) return EvalResult<ResolvedLexicalProperty?>.Ok(null);

        var innerCtx = ctx.Push(alg);
        var resolvedResult = ResolveAllOpens(alg, innerCtx);
        if (resolvedResult.IsError) return resolvedResult.Error;

        var hits = new List<OpenHit>();

        // Public-only filtering: only public properties visible through opens
        foreach (var ri in resolvedResult.Value)
        {
            var binding = LookupPublicPropBinding(ri.Lib, name);
            if (binding is not null)
                hits.Add(new OpenHit(ri.Key, ri.Lib, binding));
        }

        if (hits.Count == 0)
            return EvalResult<ResolvedLexicalProperty?>.Ok(null);
        if (hits.Count == 1)
        {
            var hit = hits[0];
            return EvalResult<ResolvedLexicalProperty?>.Ok(
                new ResolvedLexicalProperty(
                    hit.Lib,
                    hit.Binding,
                    ChildOf(hit.Lib, hit.Binding.Value)));
        }
        return new EvalError.AmbiguousOpen(name, hits.Select(h => h.Provider).ToList());
    }

    private static ResolvedLexicalProperty? LookupInParentsDirectBinding(ScopeCtx sc, string name)
    {
        foreach (var prop in sc.Properties)
        {
            if (prop.Name == name)
            {
                return new ResolvedLexicalProperty(
                    TryGetScopeOwnerAlgorithm(sc),
                    prop,
                    WithParent(prop.Value, sc));
            }
        }

        return sc.Parent is { } parent
            ? LookupInParentsDirectBinding(parent, name)
            : null;
    }

    // ── Lexical resolution (ownership-first) ────────────────────────────────

    /// <summary>
    /// Open-based lookup in parent chain (helper for LookupOpensInChain).
    /// Checks opens at each level of the parent chain as fallback.
    /// Lean: lookupOpensInParentChain → EvalM (Option Algorithm).
    /// </summary>
    private static EvalResult<ResolvedLexicalProperty?> LookupOpensInParentChain(
        ScopeCtx sc, string name, EvalCtx ctx)
    {
        var tempAlg = ForOpens(sc);
        var openResult = LookupOpens(tempAlg, name, ctx);
        if (openResult.IsError) return openResult.Error;
        if (openResult.Value is not null)
            return EvalResult<ResolvedLexicalProperty?>.Ok(openResult.Value);

        return sc.Parent is { } parent
            ? LookupOpensInParentChain(parent, name, ctx)
            : EvalResult<ResolvedLexicalProperty?>.Ok(null);
    }

    /// <summary>
    /// Open-based lookup across the algorithm chain (current first, then parents).
    /// Checks opens at each level of the parent chain as fallback.
    /// Lean: lookupOpensInChain → EvalM (Option Algorithm).
    /// </summary>
    private static EvalResult<ResolvedLexicalProperty?> LookupOpensInChain(
        Algorithm alg, string name, EvalCtx ctx)
    {
        // Try opens at current level
        var openResult = LookupOpens(alg, name, ctx);
        if (openResult.IsError) return openResult.Error;
        if (openResult.Value is not null)
            return EvalResult<ResolvedLexicalProperty?>.Ok(openResult.Value);

        // Try parent chain
        return alg.Parent is { } sc
            ? LookupOpensInParentChain(sc, name, ctx)
            : EvalResult<ResolvedLexicalProperty?>.Ok(null);
    }

    /// <summary>
    /// Resolve-only open lookup for the hot <see cref="Expr.Resolve"/> path.
    /// This preserves the same public-only open and ambiguity rules as
    /// <see cref="LookupOpens"/>, but avoids carrying binding metadata when the
    /// caller only needs the wired algorithm.
    /// </summary>
    private static EvalResult<Algorithm?> LookupOpensResolvedAlgorithm(
        Algorithm alg, string name, EvalCtx ctx)
    {
        if (alg.Opens.Count == 0) return EvalResult<Algorithm?>.Ok(null);

        var innerCtx = ctx.Push(alg);
        var resolvedResult = ResolveAllOpens(alg, innerCtx);
        if (resolvedResult.IsError) return resolvedResult.Error;

        (Algorithm Lib, Algorithm Child)? firstHit = null;
        List<string>? providers = null;

        foreach (var resolvedOpen in resolvedResult.Value)
        {
            var child = LookupPublicProp(resolvedOpen.Lib, name);
            if (child is null)
                continue;

            providers ??= [];
            providers.Add(resolvedOpen.Key);
            firstHit ??= (resolvedOpen.Lib, child);
        }

        if (providers is null)
            return EvalResult<Algorithm?>.Ok(null);
        if (providers.Count == 1)
        {
            var (lib, child) = firstHit!.Value;
            return EvalResult<Algorithm?>.Ok(ChildOf(lib, child));
        }

        return new EvalError.AmbiguousOpen(name, providers);
    }

    private static EvalResult<Algorithm?> LookupOpensInParentChainResolvedAlgorithm(
        ScopeCtx sc, string name, EvalCtx ctx)
    {
        var tempAlg = ForOpens(sc);
        var openResult = LookupOpensResolvedAlgorithm(tempAlg, name, ctx);
        if (openResult.IsError) return openResult.Error;
        if (openResult.Value is not null)
            return EvalResult<Algorithm?>.Ok(openResult.Value);

        return sc.Parent is { } parent
            ? LookupOpensInParentChainResolvedAlgorithm(parent, name, ctx)
            : EvalResult<Algorithm?>.Ok(null);
    }

    private static EvalResult<Algorithm?> LookupOpensInChainResolvedAlgorithm(
        Algorithm alg, string name, EvalCtx ctx)
    {
        var openResult = LookupOpensResolvedAlgorithm(alg, name, ctx);
        if (openResult.IsError) return openResult.Error;
        if (openResult.Value is not null)
            return EvalResult<Algorithm?>.Ok(openResult.Value);

        return alg.Parent is { } sc
            ? LookupOpensInParentChainResolvedAlgorithm(sc, name, ctx)
            : EvalResult<Algorithm?>.Ok(null);
    }

    /// <summary>
    /// Resolve-only lexical lookup for hot algorithm-resolution paths.
    /// Mirrors <see cref="LookupLexical"/> semantics, but returns only the wired
    /// algorithm so plain <see cref="Expr.Resolve"/> callers avoid binding/owner packaging.
    /// </summary>
    private static EvalResult<Algorithm> LookupLexicalResolvedAlgorithm(
        Algorithm alg, string name, EvalCtx ctx)
    {
        var direct = LookupLexicalDirect(alg, name);
        if (direct is not null)
            return EvalResult<Algorithm>.Ok(direct);

        var opensResult = LookupOpensInChainResolvedAlgorithm(alg, name, ctx);
        if (opensResult.IsError) return opensResult.Error;
        if (opensResult.Value is { } openAlgorithm)
            return EvalResult<Algorithm>.Ok(openAlgorithm);

        return new EvalError.UnknownName(name);
    }

    /// <summary>
    /// Fast path for plain lexical name resolution.
    /// This keeps <see cref="ResolveAlg"/> semantics intact while letting nearby
    /// synthetic callers resolve a name without allocating an <see cref="Expr.Resolve"/> wrapper.
    /// </summary>
    private static EvalResult<Algorithm> ResolveNamedAlgorithm(
        string name, SourceSpan? span, EvalCtx ctx)
    {
        if (ctx.CallStack.Count == 0)
            return new EvalError.UnknownName(name) { Span = span };

        var result = LookupLexicalResolvedAlgorithm(ctx.CallStack[0], name, ctx);
        return result.IsError && result.Error.Span is null
            ? result.Error with { Span = span }
            : result;
    }

    internal static bool ResolvesToBuiltinAlgorithm(string name, BuiltinId builtinId, EvalCtx ctx)
    {
        var result = ResolveNamedAlgorithm(name, span: null, ctx);
        return !result.IsError
            && result.Value is Algorithm.Builtin(var resolvedBuiltinId)
            && resolvedBuiltinId == builtinId;
    }

    /// <summary>
    /// Full lexical lookup with ownership-first model:
    /// 1. Local properties (owned by this algorithm — any visibility)
    /// 2. Parent chain structural properties (owned by ancestors — any visibility, no opens)
    /// 3. Opens as fallback across the entire chain (public only)
    /// Structural ownership always takes precedence over opens.
    /// Lean: lookupLexical → EvalM Algorithm.
    /// </summary>
    private static EvalResult<ResolvedLexicalProperty> LookupLexical(
        Algorithm alg, string name, EvalCtx ctx)
    {
        // 1. Local properties (any visibility)
        var local = LookupPropBinding(alg, name);
        if (local is not null)
            return EvalResult<ResolvedLexicalProperty>.Ok(
                new ResolvedLexicalProperty(
                    alg,
                    local,
                    ChildOf(alg, local.Value)));

        // 2. Parent chain structural only (any visibility, no opens)
        if (alg.Parent is { } sc)
        {
            var structural = LookupInParentsDirectBinding(sc, name);
            if (structural is not null)
                return EvalResult<ResolvedLexicalProperty>.Ok(structural.Value);
        }

        // 3. Opens fallback across the entire chain (public only)
        var opensResult = LookupOpensInChain(alg, name, ctx);
        if (opensResult.IsError) return opensResult.Error;
        if (opensResult.Value is { } openBinding)
            return EvalResult<ResolvedLexicalProperty>.Ok(openBinding);

        return new EvalError.UnknownName(name);
    }

    // ── Wire parent ─────────────────────────────────────────────────────────

    /// <summary>Lean: wireToCaller.</summary>
    private static Algorithm WireToCaller(EvalCtx ctx, Algorithm alg)
    {
        if (ctx.CallStack.Count > 0)
            return ChildOf(ctx.CallStack[0], alg);
        return alg;
    }

    /// <summary>Coerce a Result to decimal, or raise TypeMismatch for strings, BadArity otherwise. Lean: expectInt.</summary>
    internal static EvalResult<decimal> ExpectInt(Result r)
    {
        if (r is Result.Str)
            return new EvalError.TypeMismatch("Expected a number, got a string");
        var v = r.AsNum();
        return v is not null
            ? EvalResult<decimal>.Ok(v.Value)
            : new EvalError.BadArity();
    }

    private static EvalResult<decimal> RequireNumericScalarOperand(BinaryOp op, string side, Result value)
    {
        var number = value.AsNum();
        return number is not null
            ? EvalResult<decimal>.Ok(number.Value)
            : new EvalError.TypeMismatch(NumericScalarOperandMessage(OpenExprBinaryOp(op), side, value));
    }

    private static string NumericScalarOperandMessage(string operatorName, string side, Result value)
        => $"operator `{operatorName}` expects numeric scalar operands, but the {side} operand was {DescribeNumericScalarOperand(value)}";

    private static string DescribeNumericScalarOperand(Result value) => value switch
    {
        Result.Group(var items) => $"a group with {items.Count} {Pluralize(items.Count, "item")}: {FormatResultForDiagnostic(value)}",
        Result.Str(var text) => $"a string: '{text}'",
        Result.Atom(var number) => $"numeric value {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        _ => $"a value: {FormatResultForDiagnostic(value)}",
    };

    private static string Pluralize(int count, string singular)
        => count == 1 ? singular : singular + "s";

    internal static string FormatResultForDiagnostic(Result value) => value switch
    {
        Result.Atom(var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Result.Str(var text) => $"'{text}'",
        Result.Group(var items) => $"({string.Join(", ", items.Select(FormatResultForDiagnostic))})",
        _ => "value",
    };

    /// <summary>
    /// Require an exact integer-valued number for integer-only builtins.
    /// Lean's core uses <c>Int</c> directly, while C# allows decimals and must reject fractional values explicitly.
    /// </summary>
    private static EvalResult<decimal> ExpectWholeInt(Result r, string description)
    {
        var valueR = ExpectInt(r);
        if (valueR.IsError) return valueR.Error;
        if (valueR.Value != Math.Truncate(valueR.Value))
            return new EvalError.IllegalInEval($"{description} must be an integer");
        return valueR;
    }

    /// <summary>
    /// Evaluate and validate the arguments for <c>range(start, stop)</c>.
    /// This is the single range-boundary validation path used by both the
    /// builtin and sequence-pipeline direct range iteration.
    /// </summary>
    private static EvalResult<InclusiveRange> EvalBuiltinRangeArguments(
        IReadOnlyList<Algorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (args.Count != 2)
            return WrongBuiltinArity(BuiltinId.@range, args.Count);

        var startR = EvalAlgOutput(args[0], ctx, valEnv);
        if (startR.IsError) return startR.Error;
        var startIntR = ExpectWholeInt(startR.Value, "range start");
        if (startIntR.IsError) return startIntR.Error;

        var stopR = EvalAlgOutput(args[1], ctx, valEnv);
        if (stopR.IsError) return stopR.Error;
        var stopIntR = ExpectWholeInt(stopR.Value, "range stop");
        if (stopIntR.IsError) return stopIntR.Error;

        return EvalResult<InclusiveRange>.Ok(new InclusiveRange(startIntR.Value, stopIntR.Value));
    }

    /// <summary>Enumerate the validated inclusive integer bounds for <c>range(start, stop)</c>.</summary>
    internal static IEnumerable<decimal> EnumerateInclusiveRangeValues(InclusiveRange range)
    {
        if (range.Start <= range.Stop)
        {
            for (var current = range.Start; current <= range.Stop; current += 1m)
                yield return current;
        }
        else
        {
            for (var current = range.Start; current >= range.Stop; current -= 1m)
                yield return current;
        }
    }

    /// <summary>Count the values that <see cref="EnumerateInclusiveRangeValues"/> would produce.</summary>
    internal static long CountInclusiveRangeValues(InclusiveRange range)
    {
        var count = Math.Abs(range.Stop - range.Start) + 1m;
        return count > long.MaxValue ? long.MaxValue : (long)count;
    }

    /// <summary>
    /// Build the inclusive integer result for <c>range(start, stop)</c>.
    /// Counts upward when <c>start &lt;= stop</c> and downward otherwise.
    /// </summary>
    private static Result BuildInclusiveRange(InclusiveRange range)
        => Result.FromItems(EnumerateInclusiveRangeValues(range).Select(static value => new Result.Atom(value)));

    /// <summary>
    /// Split a step result into (state, continue-flag).
    /// Convention: the last atom is the continue flag (nonzero = keep going).
    /// Lean: splitCont.
    /// </summary>
    private static EvalResult<(Result Next, decimal Cont)> SplitCont(Result output)
    {
        switch (output)
        {
            case Result.Atom(var n):
                return EvalResult<(Result, decimal)>.Ok((new Result.Atom(n), n));
            case Result.Group(var items) when items.Count > 0:
            {
                var lastR = ExpectInt(items[^1]);
                if (lastR.IsError) return lastR.Error;
                var state = new Result.Group(items.Take(items.Count - 1).ToList()).Normalize();
                return EvalResult<(Result, decimal)>.Ok((state, lastR.Value));
            }
            default:
                return new EvalError.BadArity();
        }
    }

    // ── Bind parameters ─────────────────────────────────────────────────────

    /// <summary>Lean: bindParams → EvalM ValEnv. Errors with ArityMismatch.</summary>
    private static EvalResult<IReadOnlyList<(string, Result)>> BindParams(
        IReadOnlyList<string> paramNames,
        IReadOnlyList<Result> values)
    {
        if (paramNames.Count != values.Count)
            return new EvalError.ArityMismatch(paramNames.Count, values.Count);

        var result = new List<(string, Result)>(paramNames.Count);
        for (var i = 0; i < paramNames.Count; i++)
            result.Add((paramNames[i], values[i]));
        return EvalResult<IReadOnlyList<(string, Result)>>.Ok(result);
    }

    /// <summary>
    /// Argument passing rule: a single atom is wrapped in a one-element list;
    /// a group is unpacked into its elements. Lean: unpackArgs.
    /// </summary>
    private static IReadOnlyList<Result> UnpackArgs(Result r) => r switch
    {
        Result.Atom(var n) => [new Result.Atom(n)],
        Result.Str _ => [r],
        Result.Group(var items) => items,
        _ => [],
    };

    private static bool PreserveCallArgBoundary(IReadOnlyList<bool>? preserveArgBoundaries, int index) =>
        preserveArgBoundaries is not null
        && index < preserveArgBoundaries.Count
        && preserveArgBoundaries[index];

    private readonly record struct VariadicCallItem(
        Result? Value,
        Algorithm? Algorithm,
        EvalError? ValueError);

    private readonly record struct ResolvedArgumentAlgorithm(
        Algorithm Algorithm,
        bool SuppliesSequence);

    private readonly record struct UserCallBindings(
        IReadOnlyList<(string, Result)> ValueBindings,
        IReadOnlyList<(string, CountedResult)> CountedBindings,
        IReadOnlyList<(string, CountedResult)> VariadicStreamBindings,
        IReadOnlyList<(string, Algorithm)> AlgorithmBindings);

    private readonly record struct CountedParameterPatternBindings(
        IReadOnlyList<(string, CountedResult)> CountedBindings,
        IReadOnlyList<(string, CountedResult)> VariadicStreamBindings);

    private readonly record struct FlatFixedCallSlot(
        Result? Value,
        Algorithm? Algorithm,
        EvalError? ValueError);

    private readonly record struct FlatFixedUserCallBindings(
        EvalCtx Context,
        IReadOnlyList<(string, Result)> ValueEnvironment);

    private readonly record struct EvaluatedSlotBindings(
        IReadOnlyList<(string Name, Result Value)> ValueBindings,
        IReadOnlyList<(string Name, CountedResult Value)> CountedBindings,
        IReadOnlyList<(string Name, CountedResult Value)> VariadicStreamBindings);

    private enum GenericLoopStepBindingShape
    {
        Legacy,
        Patterned,
        FlatFixed,
        FlatVariadic,
    }

    private readonly record struct GenericLoopStepBindingSelection(
        GenericLoopStepBindingShape Shape,
        CallableBindingPlan? Plan);

    private readonly record struct CallableArgumentBindings<T>(
        IReadOnlyList<(string ParameterName, T Item)> NormalBindings,
        string? VariadicParameterName,
        IReadOnlyList<T> VariadicItems);

    private readonly record struct FlatVariadicBindingLayout(
        CallableSignature Signature,
        string VariadicName);

    private readonly record struct VariadicCapture(
        string Name,
        Result Value,
        CountedResult CountedValue);

    private readonly record struct ParameterPatternInput(
        Result? Value,
        Algorithm? Algorithm,
        EvalError? ValueError,
        IReadOnlyList<Result>? ExplicitGroupItems);

    private static bool HasStructuredParameterPattern(Algorithm algorithm)
        => algorithm.ParameterPatterns.Any(static parameter => parameter is GroupParameterPattern);

    // User-call routing uses CallableBindingPlan.RequiresPatternedBinding.
    // This helper remains for runtime paths that inspect Algorithm patterns
    // directly, including callbacks, evaluated loop slots, and loop fallbacks.
    private static bool UsesPatternBinding(Algorithm algorithm)
        => HasStructuredParameterPattern(algorithm);

    private static CountedResult? TryGetVariadicCaptureStream(Expr expr, EvalCtx ctx)
    {
        if (expr is not Expr.Param(var name))
            return null;

        return LookupCountedParam(ctx.VariadicStreamEnv, name);
    }

    private static CallableBindingPlan? TryCreateUserLoopStepBindingPlan(Algorithm step)
    {
        if (step is not Algorithm.User userStep)
            return null;

        try
        {
            var signature = CallableSignature.FromUserAlgorithm("loop step", userStep);
            return CallableBindingPlan.FromSignature(signature);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsOptimizedLoopShapeEligible(
        Algorithm step,
        out string? fallbackReason)
    {
        var plan = TryCreateUserLoopStepBindingPlan(step);
        if (plan is null)
        {
            fallbackReason = null;
            return true;
        }

        if (plan.RequiresPatternedBinding || plan.HasTopLevelVariadic)
        {
            fallbackReason = "variadic loop step";
            return false;
        }

        fallbackReason = null;
        return true;
    }

    private static GenericLoopStepBindingSelection SelectGenericLoopStepBinding(Algorithm step)
    {
        var plan = TryCreateUserLoopStepBindingPlan(step);
        if (plan is null)
            return new GenericLoopStepBindingSelection(GenericLoopStepBindingShape.Legacy, Plan: null);

        if (plan.RequiresPatternedBinding)
            return new GenericLoopStepBindingSelection(GenericLoopStepBindingShape.Patterned, plan);

        if (plan.TryGetFlatVariadicLayout(out _, out _, out _))
            return new GenericLoopStepBindingSelection(GenericLoopStepBindingShape.FlatVariadic, plan);

        if (plan.TryGetFlatFixedLayout(out _))
            return new GenericLoopStepBindingSelection(GenericLoopStepBindingShape.FlatFixed, plan);

        return new GenericLoopStepBindingSelection(GenericLoopStepBindingShape.Legacy, plan);
    }

    private static bool ShouldPreserveLoopStepSequenceSupplyExpressionBoundaries(
        Algorithm step,
        GenericLoopStepBindingSelection bindingSelection)
        => bindingSelection.Shape switch
        {
            GenericLoopStepBindingShape.Patterned => true,
            GenericLoopStepBindingShape.Legacy => UsesPatternBinding(step),
            _ => false,
        };

    private static bool TryGetFlatVariadicBindingLayout(
        CallableBindingPlan plan,
        out FlatVariadicBindingLayout layout)
    {
        if (!plan.TryGetFlatVariadicLayout(out var prefix, out var variadic, out var suffix))
        {
            layout = default;
            return false;
        }

        layout = new FlatVariadicBindingLayout(
            plan.Signature,
            variadic.Name);
        return true;
    }

    private static bool TryGetLegacyFlatVariadicBindingLayout(
        Algorithm algorithm,
        string callableName,
        out FlatVariadicBindingLayout layout)
    {
        var parameters = algorithm.Parameters;
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            if (parameter.Kind != ParameterKind.Variadic)
                continue;

            var signature = new CallableSignature(
                callableName,
                parameters
                    .Select(static parameter => new CallableParameter(parameter.Name, parameter.Kind))
                    .ToArray());
            layout = new FlatVariadicBindingLayout(
                signature,
                parameter.Name);
            return true;
        }

        layout = default;
        return false;
    }

    private static bool TryGetPlanDerivedFlatFixedParameterNames(
        CallableBindingPlan plan,
        out IReadOnlyList<string> parameterNames)
    {
        if (!plan.TryGetFlatFixedLayout(out var captures))
        {
            parameterNames = [];
            return false;
        }

        parameterNames = captures.Select(static capture => capture.Name).ToArray();
        return true;
    }

    private static EvalResult<CallableArgumentBindings<T>> BindCallableArguments<T>(
        CallableSignature signature,
        IReadOnlyList<T> items,
        Func<int, int, EvalError> arityMismatch)
    {
        if (signature.Validate() is { } validationError)
            return validationError;

        var variadicIndex = signature.VariadicParameterIndex;
        if (variadicIndex < 0)
        {
            if (items.Count != signature.Parameters.Count)
                return arityMismatch(signature.Parameters.Count, items.Count);

            return EvalResult<CallableArgumentBindings<T>>.Ok(new CallableArgumentBindings<T>(
                signature.Parameters.Zip(items, static (parameter, item) => (parameter.Name, item)).ToList(),
                VariadicParameterName: null,
                VariadicItems: []));
        }

        var requiredNormalItemCount = signature.RequiredNormalParameterCount;
        if (items.Count < requiredNormalItemCount)
            return arityMismatch(requiredNormalItemCount, items.Count);

        var suffixCount = signature.Parameters.Count - variadicIndex - 1;
        var suffixStart = items.Count - suffixCount;
        var normalBindings = new List<(string ParameterName, T Item)>(requiredNormalItemCount);

        for (var index = 0; index < variadicIndex; index++)
            normalBindings.Add((signature.Parameters[index].Name, items[index]));

        for (var suffixIndex = 0; suffixIndex < suffixCount; suffixIndex++)
        {
            var parameterIndex = variadicIndex + 1 + suffixIndex;
            var itemIndex = suffixStart + suffixIndex;
            normalBindings.Add((signature.Parameters[parameterIndex].Name, items[itemIndex]));
        }

        var variadicItems = items
            .Skip(variadicIndex)
            .Take(suffixStart - variadicIndex)
            .ToList();

        return EvalResult<CallableArgumentBindings<T>>.Ok(new CallableArgumentBindings<T>(
            normalBindings,
            signature.Parameters[variadicIndex].Name,
            variadicItems));
    }

    private static EvalResult<CallableArgumentBindings<BindingInputSlot>> BindItemsToFlatVariadicLayout(
        FlatVariadicBindingLayout layout,
        IReadOnlyList<BindingInputSlot> items,
        Func<int, int, EvalError> arityMismatch)
        => BindCallableArguments(layout.Signature, items, arityMismatch);

    private static VariadicCapture CreateVariadicCapture(string name, IReadOnlyList<Result> capturedValues)
    {
        var capturedResult = Result.FromItems(capturedValues);
        return new VariadicCapture(
            name,
            capturedResult,
            new CountedResult(capturedResult, capturedValues.Count));
    }

    private static EvalResult<IReadOnlyList<Result>?> TryGetExplicitGroupItems(
        Expr argExpr,
        EvalCtx argEvalCtx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (argExpr is Expr.Block(var algorithm))
        {
            var wired = WireToCaller(argEvalCtx, algorithm);
            if (wired.Params.Count == 0)
            {
                var slotsR = EvalAlgOutputSlots(wired, argEvalCtx, valEnv);
                if (slotsR.IsError) return slotsR.Error;
                return EvalResult<IReadOnlyList<Result>?>.Ok(slotsR.Value);
            }
        }

        return EvalResult<IReadOnlyList<Result>?>.Ok(null);
    }

    private static EvalResult<IReadOnlyList<Result>> GetGroupPatternItems(ParameterPatternInput input)
    {
        if (input.Value is Result.Group(var items))
            return EvalResult<IReadOnlyList<Result>>.Ok(items);

        if (input.ExplicitGroupItems is not null)
            return EvalResult<IReadOnlyList<Result>>.Ok(input.ExplicitGroupItems);

        return input.ValueError ?? new EvalError.BadArity();
    }

    private static EvalResult<UserCallBindings> BindParameterPattern(
        ParameterPattern pattern,
        ParameterPatternInput input,
        bool allowAlgorithmBindings)
    {
        switch (pattern)
        {
            case CaptureParameterPattern { Kind: ParameterKind.Normal } capture:
            {
                var valueBindings = new List<(string, Result)>(1);
                var algorithmBindings = new List<(string, Algorithm)>(1);

                if (input.Value is not null)
                    valueBindings.Add((capture.Name, input.Value));

                if (allowAlgorithmBindings && input.Algorithm is not null)
                    algorithmBindings.Add((capture.Name, input.Algorithm));

                if (input.Value is null && (!allowAlgorithmBindings || input.Algorithm is null))
                    return input.ValueError ?? new EvalError.BadArity();

                return EvalResult<UserCallBindings>.Ok(new UserCallBindings(valueBindings, [], [], algorithmBindings));
            }

            case CaptureParameterPattern { Kind: ParameterKind.Variadic }:
                return new EvalError.BadArity();

            case GroupParameterPattern group:
            {
                var itemsR = GetGroupPatternItems(input);
                if (itemsR.IsError && group.Items.Count == 1 && input.Value is not null)
                {
                    itemsR = EvalResult<IReadOnlyList<Result>>.Ok([input.Value]);
                }

                if (itemsR.IsError) return itemsR.Error;

                var nestedInputs = itemsR.Value
                    .Select(static item => new ParameterPatternInput(item, Algorithm: null, ValueError: null, ExplicitGroupItems: null))
                    .ToList();
                return BindParameterPatternList(
                    group.Items,
                    nestedInputs,
                    allowAlgorithmBindings: false,
                    (required, actual) => new EvalError.ArityMismatch(required, actual));
            }

            default:
                return new EvalError.BadArity();
        }
    }

    private static EvalResult<UserCallBindings> BindParameterPatternList(
        IReadOnlyList<ParameterPattern> patterns,
        IReadOnlyList<ParameterPatternInput> inputs,
        bool allowAlgorithmBindings,
        Func<int, int, EvalError> arityMismatch)
    {
        var variadicIndex = -1;
        for (var index = 0; index < patterns.Count; index++)
        {
            if (patterns[index] is not CaptureParameterPattern { Kind: ParameterKind.Variadic })
                continue;

            if (variadicIndex >= 0)
                return new EvalError.BadArity();

            variadicIndex = index;
        }

        var valueBindings = new List<(string, Result)>();
        var countedBindings = new List<(string, CountedResult)>();
        var variadicStreamBindings = new List<(string, CountedResult)>();
        var algorithmBindings = new List<(string, Algorithm)>();

        void AddBindings(UserCallBindings bindings)
        {
            valueBindings.AddRange(bindings.ValueBindings);
            countedBindings.AddRange(bindings.CountedBindings);
            variadicStreamBindings.AddRange(bindings.VariadicStreamBindings);
            algorithmBindings.AddRange(bindings.AlgorithmBindings);
        }

        EvalResult<bool> BindOne(int patternIndex, int inputIndex)
        {
            var boundR = BindParameterPattern(patterns[patternIndex], inputs[inputIndex], allowAlgorithmBindings);
            if (boundR.IsError) return boundR.Error;

            AddBindings(boundR.Value);
            return EvalResult<bool>.Ok(true);
        }

        if (variadicIndex < 0)
        {
            if (patterns.Count != inputs.Count)
                return arityMismatch(patterns.Count, inputs.Count);

            for (var index = 0; index < patterns.Count; index++)
            {
                var boundR = BindOne(index, index);
                if (boundR.IsError) return boundR.Error;
            }

            return EvalResult<UserCallBindings>.Ok(new UserCallBindings(valueBindings, countedBindings, variadicStreamBindings, algorithmBindings));
        }

        var requiredCount = patterns.Count - 1;
        if (inputs.Count < requiredCount)
            return arityMismatch(requiredCount, inputs.Count);

        for (var index = 0; index < variadicIndex; index++)
        {
            var boundR = BindOne(index, index);
            if (boundR.IsError) return boundR.Error;
        }

        var suffixCount = patterns.Count - variadicIndex - 1;
        var suffixInputStart = inputs.Count - suffixCount;
        for (var suffixIndex = 0; suffixIndex < suffixCount; suffixIndex++)
        {
            var boundR = BindOne(variadicIndex + 1 + suffixIndex, suffixInputStart + suffixIndex);
            if (boundR.IsError) return boundR.Error;
        }

        var variadicCapture = (CaptureParameterPattern)patterns[variadicIndex];
        var capturedValues = new List<Result>(suffixInputStart - variadicIndex);
        for (var inputIndex = variadicIndex; inputIndex < suffixInputStart; inputIndex++)
        {
            var input = inputs[inputIndex];
            if (input.Value is null)
                return input.ValueError ?? new EvalError.BadArity();

            capturedValues.Add(input.Value);
        }

        var capture = CreateVariadicCapture(variadicCapture.Name, capturedValues);
        valueBindings.Add((capture.Name, capture.Value));
        countedBindings.Add((capture.Name, capture.CountedValue));
        variadicStreamBindings.Add((capture.Name, capture.CountedValue));

        return EvalResult<UserCallBindings>.Ok(new UserCallBindings(valueBindings, countedBindings, variadicStreamBindings, algorithmBindings));
    }

    private static EvalResult<UserCallBindings> BindPatternedUserCall(
        Algorithm callee,
        Algorithm wiredArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string? calleeName)
    {
        var argExprs = wiredArgs.Output;
        var signature = CallableSignature.FromAlgorithm(calleeName ?? "<anonymous>", callee);

        var maybeAlgsR = TryResolveArgAlgs(wiredArgs, ctx);
        if (maybeAlgsR.IsError) return maybeAlgsR.Error;

        var maybeAlgs = maybeAlgsR.Value;
        var argEvalCtx = ctx.Push(wiredArgs);
        var inputs = new List<ParameterPatternInput>(argExprs.Count);

        for (var index = 0; index < argExprs.Count; index++)
        {
            var argExpr = argExprs[index];
            var maybeAlg = index < maybeAlgs.Count ? maybeAlgs[index] : null;
            var evalR = Eval(argExpr, argEvalCtx, valEnv);
            IReadOnlyList<Result>? explicitGroupItems = null;

            if (evalR.IsOk)
            {
                var explicitGroupItemsR = TryGetExplicitGroupItems(argExpr, argEvalCtx, valEnv);
                if (explicitGroupItemsR.IsError) return explicitGroupItemsR.Error;
                explicitGroupItems = explicitGroupItemsR.Value;
            }

            inputs.Add(new ParameterPatternInput(
                evalR.IsOk ? evalR.Value : null,
                maybeAlg,
                evalR.IsError ? evalR.Error : null,
                explicitGroupItems));
        }

        return BindParameterPatternList(
            callee.ParameterPatterns,
            inputs,
            allowAlgorithmBindings: true,
            (required, actual) => new EvalError.ArityMismatch(required, actual)
            {
                Signature = signature,
            });
    }

    private static EvalResult<IReadOnlyList<BindingInputSlot>> BuildVariadicBindingInputSlots(
        Algorithm wiredArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        IReadOnlyList<bool>? preserveArgBoundaries = null)
    {
        var argExprs = wiredArgs.Output;
        var maybeAlgsR = TryResolveArgAlgs(wiredArgs, ctx);
        if (maybeAlgsR.IsError) return maybeAlgsR.Error;

        var maybeAlgs = maybeAlgsR.Value;
        var argEvalCtx = ctx.Push(wiredArgs);
        var items = new List<BindingInputSlot>();

        for (var index = 0; index < argExprs.Count; index++)
        {
            var argExpr = argExprs[index];
            var maybeAlg = index < maybeAlgs.Count ? maybeAlgs[index] : null;
            var preserveArgBoundary = PreserveCallArgBoundary(preserveArgBoundaries, index);
            var expandDotReceiver = ShouldExpandFlatVariadicDotReceiver(preserveArgBoundaries, index, preserveArgBoundary);

            // Dot-call receiver injection can mark the leading receiver boundary
            // as preserved; leading flat variadic receivers may clear that mark.
            if (expandDotReceiver || (argExpr is Expr.SequenceSupply && !preserveArgBoundary))
            {
                var suppliedR = expandDotReceiver
                    ? EvalFlatVariadicDotReceiverCounted(argExpr, ctx, argEvalCtx, valEnv)
                    : EvalCounted(argExpr, argEvalCtx, valEnv);
                if (suppliedR.IsError)
                    return suppliedR.Error;

                foreach (var value in CountedTopLevelValues(suppliedR.Value))
                    items.Add(BindingInputSlot.FromUserCallItem(value, algorithm: null, valueError: null));

                continue;
            }

            var forwardedStream = preserveArgBoundary
                ? null
                : TryGetVariadicCaptureStream(argExpr, ctx);
            if (forwardedStream is not null)
            {
                items.Add(BindingInputSlot.FromUserCallItem(
                    forwardedStream.Value.Value,
                    maybeAlg,
                    valueError: null,
                    variadicSlotEmittedCount: forwardedStream.Value.EmittedCount));
                continue;
            }

            var evaluatedR = EvalCounted(argExpr, argEvalCtx, valEnv);
            if (evaluatedR.IsOk)
            {
                items.Add(BindingInputSlot.FromUserCallItem(
                    evaluatedR.Value.Value,
                    maybeAlg,
                    valueError: null,
                    variadicSlotEmittedCount: evaluatedR.Value.EmittedCount));
                continue;
            }

            if (maybeAlg is not null)
            {
                items.Add(BindingInputSlot.FromUserCallItem(value: null, algorithm: maybeAlg, valueError: evaluatedR.Error));
                continue;
            }

            return evaluatedR.Error;
        }

        return EvalResult<IReadOnlyList<BindingInputSlot>>.Ok(items);
    }

    private static bool ShouldExpandFlatVariadicDotReceiver(
        IReadOnlyList<bool>? preserveArgBoundaries,
        int index,
        bool preserveArgBoundary)
        => preserveArgBoundaries is not null
        && index == 0
        && !preserveArgBoundary;

    private static EvalResult<CountedResult> EvalFlatVariadicDotReceiverCounted(
        Expr receiver,
        EvalCtx ctx,
        EvalCtx argEvalCtx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (receiver is Expr.Block(var algorithm))
        {
            var wired = WireToCaller(ctx, algorithm);
            if (wired.Params.Count == 0)
                return WithSpan(receiver.Span ?? FirstSpan(wired.Output), EvalAlgOutputCounted(wired, ctx, valEnv));
        }

        return EvalCounted(receiver, argEvalCtx, valEnv);
    }

    private static EvalError VariadicBindingArityMismatch(
        string? calleeName,
        int requiredNormalItemCount,
        int actualItemCount,
        CallableSignature? signature = null)
        => string.IsNullOrWhiteSpace(calleeName)
            ? new EvalError.ArityMismatch(requiredNormalItemCount, actualItemCount)
            : new EvalError.VariadicArityMismatch(calleeName, requiredNormalItemCount, actualItemCount)
            {
                Signature = signature,
            };

    private static EvalResult<UserCallBindings> BindVariadicUserCall(
        Algorithm callee,
        Algorithm wiredArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        FlatVariadicBindingLayout layout,
        string? calleeName,
        IReadOnlyList<bool>? preserveArgBoundaries = null)
    {
        var itemsR = BuildVariadicBindingInputSlots(wiredArgs, ctx, valEnv, preserveArgBoundaries);
        if (itemsR.IsError) return itemsR.Error;

        var items = itemsR.Value;
        var boundItemsR = BindItemsToFlatVariadicLayout(
            layout,
            items,
            (required, actual) => VariadicBindingArityMismatch(calleeName, required, actual, layout.Signature));
        if (boundItemsR.IsError) return boundItemsR.Error;

        var boundItems = boundItemsR.Value;
        var valueBindings = new List<(string, Result)>(layout.Signature.Parameters.Count);
        var countedBindings = new List<(string, CountedResult)>(1);
        var variadicStreamBindings = new List<(string, CountedResult)>(1);
        var algorithmBindings = new List<(string, Algorithm)>();

        EvalResult<bool> BindNormalParameter(string parameterName, BindingInputSlot item)
        {
            if (item.Value is not null)
                valueBindings.Add((parameterName, item.Value));

            if (item.Algorithm is not null)
                algorithmBindings.Add((parameterName, item.Algorithm));

            if (item.Value is null && item.Algorithm is null)
                return item.ValueError ?? new EvalError.BadArity();

            return EvalResult<bool>.Ok(true);
        }

        foreach (var (parameterName, item) in boundItems.NormalBindings)
        {
            var boundR = BindNormalParameter(parameterName, item);
            if (boundR.IsError) return boundR.Error;
        }

        var capturedValues = new List<Result>(boundItems.VariadicItems.Count);
        foreach (var item in boundItems.VariadicItems)
        {
            if (item.Value is null)
                return item.ValueError ?? new EvalError.BadArity();

            if (item.VariadicSlotEmittedCount is { } emittedCount)
            {
                capturedValues.AddRange(CountedTopLevelValues(new CountedResult(item.Value, emittedCount)));
                continue;
            }

            capturedValues.Add(item.Value);
        }

        if ((boundItems.VariadicParameterName ?? layout.VariadicName) is not { } variadicName)
            return new EvalError.BadArity();

        var captured = CreateVariadicCapture(variadicName, capturedValues);
        valueBindings.Add((captured.Name, captured.Value));
        countedBindings.Add((captured.Name, captured.CountedValue));
        variadicStreamBindings.Add((captured.Name, captured.CountedValue));

        return EvalResult<UserCallBindings>.Ok(
            new UserCallBindings(valueBindings, countedBindings, variadicStreamBindings, algorithmBindings));
    }

    private static EvalCtx WithUserCallBindingEnvironments(
        EvalCtx ctx,
        UserCallBindings bindings,
        IEnumerable<string> shadowedNames)
    {
        var shadowed = shadowedNames.ToArray();
        return ctx
            .WithAlgEnv(Concat(bindings.AlgorithmBindings, ctx.AlgEnv))
            .WithCountedParamEnv(Concat(bindings.CountedBindings, ShadowCountedParamEnv(ctx.CountedParamEnv, shadowed)))
            .WithVariadicStreamEnv(Concat(bindings.VariadicStreamBindings, ShadowCountedParamEnv(ctx.VariadicStreamEnv, shadowed)));
    }

    private static EvalCtx WithCountedParameterEnvironments(
        EvalCtx ctx,
        IReadOnlyList<(string, CountedResult)> countedBindings,
        IReadOnlyList<(string, CountedResult)> variadicStreamBindings,
        IEnumerable<string> shadowedNames)
    {
        var shadowed = shadowedNames.ToArray();
        return ctx
            .WithCountedParamEnv(Concat(countedBindings, ShadowCountedParamEnv(ctx.CountedParamEnv, shadowed)))
            .WithVariadicStreamEnv(Concat(variadicStreamBindings, ShadowCountedParamEnv(ctx.VariadicStreamEnv, shadowed)));
    }

    private static EvalResult<FlatFixedUserCallBindings> BindFlatFixedUserCallArguments(
        CallableSignature signature,
        IReadOnlyList<string> parameterNames,
        Algorithm wiredArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var argExprs = wiredArgs.Output;
        var paramCount = parameterNames.Count;

        // Try to resolve each arg as algorithm (for AlgEnv bindings)
        var maybeAlgsR = TryResolveArgAlgs(wiredArgs, ctx);
        if (maybeAlgsR.IsError) return maybeAlgsR.Error;
        var maybeAlgs = maybeAlgsR.Value;

        // Lean: let argEvalCtx := EvalCtx.push wiredArgs ctx
        var argEvalCtx = ctx.Push(wiredArgs);

        var slots = new List<FlatFixedCallSlot>(argExprs.Count);

        for (var i = 0; i < argExprs.Count; i++)
        {
            var argExpr = argExprs[i];
            if (argExpr is Expr.SequenceSupply)
            {
                // Flat fixed calls expand bare sequence-supply args. Dot-call
                // fixed receivers that must stay one boundary are wrapped before
                // this path, so they do not arrive here as Expr.SequenceSupply.
                var suppliedR = EvalCounted(argExpr, argEvalCtx, valEnv);
                if (suppliedR.IsError) return suppliedR.Error;

                foreach (var value in CountedTopLevelValues(suppliedR.Value))
                    slots.Add(new FlatFixedCallSlot(value, Algorithm: null, ValueError: null));

                continue;
            }

            var maybeAlg = i < maybeAlgs.Count ? maybeAlgs[i] : null;
            var evalR = Eval(argExpr, argEvalCtx, valEnv);
            if (evalR.IsOk)
            {
                slots.Add(new FlatFixedCallSlot(evalR.Value, maybeAlg, ValueError: null));
            }
            else if (maybeAlg is not null)
            {
                slots.Add(new FlatFixedCallSlot(Value: null, maybeAlg, evalR.Error));
            }
            else
            {
                return evalR.Error;
            }
        }

        if (slots.Count > paramCount)
            return new EvalError.ArityMismatch(paramCount, slots.Count) { Signature = signature };

        var algBindings = new List<(string, Algorithm)>();
        var valueParams = new List<string>();
        var valueResults = new List<Result>();

        for (var i = 0; i < paramCount; i++)
        {
            if (i >= slots.Count)
            {
                valueParams.Add(parameterNames[i]);
                continue;
            }

            var slot = slots[i];
            if (slot.Algorithm is not null)
                algBindings.Add((parameterNames[i], slot.Algorithm));

            if (slot.Value is not null)
            {
                valueParams.Add(parameterNames[i]);
                valueResults.Add(slot.Value);
            }
        }

        var argEnvR = BindParams(valueParams, valueResults);
        if (argEnvR.IsError)
        {
            if (argEnvR.Error is EvalError.ArityMismatch arityMismatch)
                return arityMismatch with { Signature = signature };

            return argEnvR.Error;
        }

        var shadowedStreamEnv = ShadowCountedParamEnv(ctx.VariadicStreamEnv, parameterNames);
        var boundCtx = ctx
            .WithAlgEnv(Concat(algBindings, ctx.AlgEnv))
            .WithCountedParamEnv(ShadowCountedParamEnv(ctx.CountedParamEnv, parameterNames))
            .WithVariadicStreamEnv(shadowedStreamEnv);
        var boundEnv = Concat(argEnvR.Value, valEnv);
        return EvalResult<FlatFixedUserCallBindings>.Ok(new FlatFixedUserCallBindings(boundCtx, boundEnv));
    }

    // ── Result helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Extract top-level items from a result into a list.
    /// Atom → [Atom]; Group → its items. Lean: Result.toItems.
    /// </summary>
    private static void ResultItems(List<Result> into, Result r)
    {
        switch (r)
        {
            case Result.Atom:
            case Result.Str:
                into.Add(r);
                break;
            case Result.Group(var items):
                into.AddRange(items);
                break;
        }
    }

    /// <summary>
    /// Evaluate <c>target:selector</c> through the shared one-level projected
    /// selection semantics.
    /// Construction preserves structure; selection projects content.
    /// </summary>
    private static EvalResult<CountedResult> EvalIndexSelectionCounted(
        Expr target,
        Expr selector,
        SourceSpan? span,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var targetR = Eval(target, ctx, valEnv);
        if (targetR.IsError) return targetR.Error;

        var nR = EvalInt(selector, ctx, valEnv);
        if (nR.IsError) return nR.Error;

        var n = nR.Value;
        if (n < 0 || n != Math.Floor(n))
            return new EvalError.BadIndex() { Span = span };

        var selected = targetR.Value.SelectProjected((int)n);
        if (selected is null)
            return new EvalError.BadIndex() { Span = span };

        return EvalResult<CountedResult>.Ok(
            new CountedResult(selected.Value.Value, selected.Value.EmittedCount));
    }

    /// <summary>
    /// Lean: <c>resultToExpr</c>. Reify a normalized result as an expression that
    /// evaluates back to the same shape.
    /// </summary>
    private static Expr EmptyResultExpr()
        => new Expr.Block(new Algorithm.Builtin(BuiltinId.@empty));

    private static Expr ResultToExpr(Result result) => result switch
    {
        Result.Atom(var n) => new Expr.Num(n),
        Result.Str(var s) => new Expr.StringLiteral(s),
        Result.Group(var items) when items.Count == 0 => EmptyResultExpr(),
        Result.Group(var items) => new Expr.Block(new Algorithm.User(
            Parent: null,
            Parameters: [],
            Opens: [],
            Properties: [],
            Output: items.Select(ResultToExpr).ToList())),
        _ => EmptyResultExpr(),
    };

    /// <summary>Lean: <c>Algorithm.ofExpr</c>.</summary>
    private static Algorithm AlgorithmOfExpr(Expr expr) => new Algorithm.User(
        Parent: null,
        Parameters: [],
        Opens: [],
        Properties: [],
        Output: [expr]);

    /// <summary>
    /// Counted evaluation result: the normalized value paired with the number of
    /// top-level values emitted at the current algorithm boundary.
    /// Helpers whose names end in <c>Counted</c> preserve this pair instead of
    /// collapsing it to just <see cref="Result"/>.
    /// Lean: <c>CountedResult</c>.
    /// </summary>
    internal readonly record struct CountedResult(Result Value, int EmittedCount);

    /// <summary>
    /// Evaluated bounds for the inclusive integer <c>range(start, stop)</c>
    /// builtin. The bounds have already passed range's whole-integer validation.
    /// </summary>
    internal readonly record struct InclusiveRange(decimal Start, decimal Stop);

    /// <summary>
    /// Collected sequence input records the items captured by <c>values...</c>
    /// plus the prepared outer-item stream used by the current builtin.
    /// </summary>
    private readonly record struct CollectedSequenceBuiltinInput(
        IReadOnlyList<IReadOnlyList<Result>> PerInputItems,
        IReadOnlyList<Result> FlattenedItems)
    {
        public int TotalItemCount => FlattenedItems.Count;

        public bool AnyInputEmpty => PerInputItems.Any(static items => items.Count == 0);
    }

    /// <summary>
    /// Prepared input for current sequence builtin handlers.
    /// Numeric builtins cache the flattened numeric projection of the collected
    /// top-level items.
    /// </summary>
    private readonly record struct PreparedSequenceBuiltinInput(
        CollectedSequenceBuiltinInput Collected,
        IReadOnlyList<decimal>? NumericItems = null)
    {
        public IReadOnlyList<Result> FlattenedItems => Collected.FlattenedItems;
    }

    private abstract record PreparedSequenceBuiltinSuffixArg
    {
        public sealed record AlgorithmArg(KatLang.Algorithm AlgorithmValue) : PreparedSequenceBuiltinSuffixArg;

        public sealed record ValueArg(Result ResultValue) : PreparedSequenceBuiltinSuffixArg;

        public sealed record WholeNumberArg(decimal WholeNumberValue) : PreparedSequenceBuiltinSuffixArg;
    }

    /// <summary>
    /// Validate the output shape required by counted builtins that must emit
    /// exactly one top-level value. Non-empty grouped values are valid; empty
    /// results and multiple top-level outputs are rejected.
    /// Lean: <c>expectSingleValueWith</c>.
    /// </summary>
    private static EvalResult<Result> ExpectSingleEmittedValue(CountedResult output, string errorMessage)
        => output.EmittedCount == 1
            ? EvalResult<Result>.Ok(output.Value)
            : new EvalError.WithContext(
                errorMessage,
                new EvalError.BadArity());

    /// <summary>
    /// Validate the output shape required by <c>reduce</c>.
    /// Lean: <c>expectSingleAccumulator</c>.
    /// </summary>
    private static EvalResult<Result> ExpectSingleAccumulator(CountedResult output)
        => ExpectSingleEmittedValue(output, "reduce step must return a single accumulator value");

    /// <summary>
    /// Validate the output shape required by <c>map</c>.
    /// Lean: <c>expectSingleMappedElement</c>.
    /// </summary>
    private static EvalResult<Result> ExpectSingleMappedElement(CountedResult output)
        => ExpectSingleEmittedValue(output, "map transform must return a single element");

    // ── Pattern matching (for conditional algorithms) ────────────────────────

    /// <summary>
    /// Match a pattern against a Result, returning accumulated bindings on success.
    /// Lean: matchPattern.
    /// </summary>
    private static IReadOnlyList<(string, Result)>? MatchPattern(Pattern pattern, Result result)
    {
        switch (pattern)
        {
            case Pattern.Bind(var name):
                return [(name, result)];

            case Pattern.LitInt(var n):
                return result is Result.Atom(var v) && v == n
                    ? []
                    : null;

            case Pattern.LitString(var s):
                return result is Result.Str(var sv) && sv == s
                    ? []
                    : null;

            case Pattern.Group(var items):
                // Result.normalize collapses group [x] → x, so a singleton
                // group pattern (e.g. "(b)") must also match a non-group result
                // by treating it as if it were group [result].
                if (result is Result.Group(var rs))
                {
                    if (rs.Count != items.Count) return null;
                }
                else if (items.Count == 1)
                {
                    rs = [result];
                }
                else
                {
                    return null;
                }
                var bindings = new List<(string, Result)>();
                for (var i = 0; i < items.Count; i++)
                {
                    var sub = MatchPattern(items[i], rs[i]);
                    if (sub is null) return null;
                    bindings.AddRange(sub);
                }
                return bindings;

            default:
                return null;
        }
    }

    /// <summary>
    /// Try branches in order. Returns the first matching branch and its bindings.
    /// Lean: matchBranches.
    /// </summary>
    private static (CondBranch Branch, IReadOnlyList<(string, Result)> Bindings)? MatchBranches(
        IReadOnlyList<CondBranch> branches, Result arg)
    {
        foreach (var branch in branches)
        {
            var bindings = MatchPattern(branch.Pattern, arg);
            if (bindings is not null)
                return (branch, bindings);
        }
        return null;
    }

    /// <summary>
    /// Match a top-level conditional call head against the explicit arguments
    /// supplied at the call site.
    ///
    /// Ordinary direct conditional calls preserve explicit argument slots at
    /// the top level: a non-group head expects exactly one explicit argument,
    /// while a group head expects one explicit argument per group item. Nested
    /// grouped structure is still matched through <see cref="MatchPattern"/>.
    /// </summary>
    private static IReadOnlyList<(string, Result)>? MatchCallPattern(
        Pattern pattern,
        IReadOnlyList<Result> explicitArgs)
    {
        if (pattern is Pattern.Group(var items))
        {
            if (items.Count != explicitArgs.Count)
                return null;

            var bindings = new List<(string, Result)>();
            for (var i = 0; i < items.Count; i++)
            {
                var sub = MatchPattern(items[i], explicitArgs[i]);
                if (sub is null)
                    return null;
                bindings.AddRange(sub);
            }

            return bindings;
        }

        return explicitArgs.Count == 1 ? MatchPattern(pattern, explicitArgs[0]) : null;
    }

    private static (CondBranch Branch, IReadOnlyList<(string, Result)> Bindings)? MatchCallBranches(
        IReadOnlyList<CondBranch> branches,
        IReadOnlyList<Result> explicitArgs)
    {
        foreach (var branch in branches)
        {
            var bindings = MatchCallPattern(branch.Pattern, explicitArgs);
            if (bindings is not null)
                return (branch, bindings);
        }

        return null;
    }

    private static IReadOnlyList<(string, CountedResult)>? MatchCountedPattern(
        Pattern pattern,
        CountedResult result)
    {
        switch (pattern)
        {
            case Pattern.Bind(var name):
                return [(name, result)];

            case Pattern.LitInt(var n):
                return result.Value is Result.Atom(var v) && v == n
                    ? []
                    : null;

            case Pattern.LitString(var s):
                return result.Value is Result.Str(var sv) && sv == s
                    ? []
                    : null;

            case Pattern.Group(var items):
                IReadOnlyList<Result> members;
                if (result.Value is Result.Group(var groupedMembers))
                {
                    if (groupedMembers.Count != items.Count)
                        return null;

                    members = groupedMembers;
                }
                else if (items.Count == 1)
                {
                    members = [result.Value];
                }
                else
                {
                    return null;
                }

                var bindings = new List<(string, CountedResult)>();
                for (var i = 0; i < items.Count; i++)
                {
                    var sub = MatchCountedPattern(items[i], new CountedResult(members[i], members[i].ValueCount()));
                    if (sub is null)
                        return null;

                    bindings.AddRange(sub);
                }

                return bindings;

            default:
                return null;
        }
    }

    private static IReadOnlyList<(string, CountedResult)>? MatchCountedCallPattern(
        Pattern pattern,
        IReadOnlyList<CountedResult> explicitArgs)
    {
        if (pattern is Pattern.Group(var items))
        {
            if (items.Count != explicitArgs.Count)
                return null;

            var bindings = new List<(string, CountedResult)>();
            for (var i = 0; i < items.Count; i++)
            {
                var sub = MatchCountedPattern(items[i], explicitArgs[i]);
                if (sub is null)
                    return null;

                bindings.AddRange(sub);
            }

            return bindings;
        }

        return explicitArgs.Count == 1 ? MatchCountedPattern(pattern, explicitArgs[0]) : null;
    }

    private static (CondBranch Branch, IReadOnlyList<(string, CountedResult)> Bindings)? MatchCountedCallBranches(
        IReadOnlyList<CondBranch> branches,
        IReadOnlyList<CountedResult> explicitArgs)
    {
        foreach (var branch in branches)
        {
            var bindings = MatchCountedCallPattern(branch.Pattern, explicitArgs);
            if (bindings is not null)
                return (branch, bindings);
        }

        return null;
    }

    /// <summary>
    /// Compatibility fallback for manually constructed core conditionals.
    /// Surface clause elaboration should already classify whole same-name
    /// plain-binder clause groups as ordinary <see cref="Algorithm.User"/>
    /// values in the parser. This helper intentionally keeps only the stricter
    /// flat multi-binder raw <see cref="Algorithm.Conditional"/> core shape
    /// call-compatible with ordinary user-call semantics so evaluator fallback
    /// does not silently broaden to bare single-binder conditionals.
    /// </summary>
    private static Algorithm.User? TryGetFlatBinderUserEquivalent(Algorithm callee)
    {
        if (callee is not Algorithm.Conditional cond || cond.Branches.Count != 1)
            return null;

        var paramNames = cond.Branches[0].Pattern.TryGetFlatMultiBinderParams();
        if (paramNames is null)
            return null;

        return ChildOf(callee, cond.Branches[0].Body) is Algorithm.User body
            ? (Algorithm.User)body.WithParameters(Algorithm.NormalParameters(paramNames))
            : null;
    }

    /// <summary>
    /// Evaluate a conditional algorithm against an already-assembled argument
    /// shape. Used both for ordinary conditional calls and for builtins like
    /// higher-order sequence callbacks after the iterated item has already
    /// been projected through the same one-level rule as <c>:</c>.
    /// Lean: <c>evalConditionalShape</c>.
    /// </summary>
    private static EvalResult<Result> EvalConditionalShape(
        Algorithm callee,
        Result argShape,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        if (callee.HasDuplicateBranchPatterns())
            return new EvalError.DuplicateBranchPattern();

        var match = MatchBranches(callee.Branches, argShape);
        if (match is null)
            return new EvalError.NoMatchingBranch(calleeName);

        var (branch, bindings) = match.Value;
        var wiredBody = ChildOf(callee, branch.Body);
        var shadowedNames = bindings.Select(static binding => binding.Item1).ToArray();
        var newCtx = ctx.Push(callee)
            .WithCountedParamEnv(ShadowCountedParamEnv(ctx.CountedParamEnv, shadowedNames))
            .WithVariadicStreamEnv(ShadowCountedParamEnv(ctx.VariadicStreamEnv, shadowedNames));
        var newEnv = Concat(bindings, valEnv);
        return EvalAlgOutput(wiredBody, newCtx, newEnv);
    }

    /// <summary>
    /// Reify a pre-evaluated counted argument as a zero-parameter algorithm
    /// that preserves the same value and emitted top-level count.
    /// </summary>
    private static Algorithm CountedArgAlgorithm(CountedResult arg)
    {
        IReadOnlyList<Expr> output = arg.EmittedCount switch
        {
            0 => [EmptyResultExpr()],
            1 => [ResultToExpr(arg.Value)],
            _ => arg.Value.ToItems().Select(ResultToExpr).ToList(),
        };

        return new Algorithm.User(
            Parent: null,
            Parameters: [],
            Opens: [],
            Properties: [],
            Output: output);
    }

    /// <summary>
    /// Ordinary call-style unpacking for a pre-evaluated explicit callback
    /// argument. A final explicit arg may still unpack across the remaining
    /// parameters, matching <c>callee(S:i)</c>.
    /// </summary>
    private static IReadOnlyList<CountedResult> UnpackCountedArg(CountedResult arg)
        => UnpackArgs(arg.Value)
            .Select(value => new CountedResult(value, value.ValueCount()))
            .ToList();

    /// <summary>
    /// Bind callback parameters while preserving the projected emitted count of
    /// the iterated item. This keeps callback params behaving like <c>S:i</c>
    /// without making them callable algorithms.
    /// </summary>
    private static EvalResult<IReadOnlyList<(string, CountedResult)>> BindCountedCallbackParams(
        IReadOnlyList<string> paramNames,
        IReadOnlyList<CountedResult> args)
    {
        if (args.Count > paramNames.Count)
            return new EvalError.ArityMismatch(paramNames.Count, args.Count);

        var boundValues = new List<CountedResult>(paramNames.Count);
        for (var argIndex = 0; argIndex < args.Count; argIndex++)
        {
            var isFinalArg = argIndex == args.Count - 1;
            var remainingParams = paramNames.Count - boundValues.Count;

            if (isFinalArg && remainingParams > 1)
            {
                boundValues.AddRange(UnpackCountedArg(args[argIndex]));
                break;
            }

            boundValues.Add(args[argIndex]);
        }

        if (boundValues.Count != paramNames.Count)
            return new EvalError.ArityMismatch(paramNames.Count, boundValues.Count);

        var bindings = new List<(string, CountedResult)>(paramNames.Count);
        for (var i = 0; i < paramNames.Count; i++)
            bindings.Add((paramNames[i], boundValues[i]));

        return EvalResult<IReadOnlyList<(string, CountedResult)>>.Ok(bindings);
    }

    private static EvalResult<CountedParameterPatternBindings> BindCountedParameterPattern(
        ParameterPattern pattern,
        CountedResult input)
    {
        switch (pattern)
        {
            case CaptureParameterPattern { Kind: ParameterKind.Normal } capture:
                return EvalResult<CountedParameterPatternBindings>.Ok(new CountedParameterPatternBindings(
                    [(capture.Name, input)],
                    []));

            case CaptureParameterPattern { Kind: ParameterKind.Variadic }:
                return new EvalError.BadArity();

            case GroupParameterPattern group:
            {
                var items = input.Value switch
                {
                    Result.Group(var groupedItems) => (IReadOnlyList<Result>)groupedItems,
                    _ when group.Items.Count == 1 => [input.Value],
                    _ => null,
                };

                if (items is null)
                    return new EvalError.BadArity();

                var nestedInputs = items
                    .Select(static item => new CountedResult(item, item.ValueCount()))
                    .ToList();
                return BindCountedParameterPatternList(
                    group.Items,
                    nestedInputs,
                    (required, actual) => new EvalError.ArityMismatch(required, actual));
            }

            default:
                return new EvalError.BadArity();
        }
    }

    private static EvalResult<CountedParameterPatternBindings> BindCountedParameterPatternList(
        IReadOnlyList<ParameterPattern> patterns,
        IReadOnlyList<CountedResult> inputs,
        Func<int, int, EvalError> arityMismatch)
    {
        var variadicIndex = -1;
        for (var index = 0; index < patterns.Count; index++)
        {
            if (patterns[index] is not CaptureParameterPattern { Kind: ParameterKind.Variadic })
                continue;

            if (variadicIndex >= 0)
                return new EvalError.BadArity();

            variadicIndex = index;
        }

        var bindings = new List<(string, CountedResult)>();
        var variadicStreamBindings = new List<(string, CountedResult)>();

        EvalResult<bool> BindOne(int patternIndex, int inputIndex)
        {
            var boundR = BindCountedParameterPattern(patterns[patternIndex], inputs[inputIndex]);
            if (boundR.IsError) return boundR.Error;

            bindings.AddRange(boundR.Value.CountedBindings);
            variadicStreamBindings.AddRange(boundR.Value.VariadicStreamBindings);
            return EvalResult<bool>.Ok(true);
        }

        if (variadicIndex < 0)
        {
            if (patterns.Count != inputs.Count)
                return arityMismatch(patterns.Count, inputs.Count);

            for (var index = 0; index < patterns.Count; index++)
            {
                var boundR = BindOne(index, index);
                if (boundR.IsError) return boundR.Error;
            }

            return EvalResult<CountedParameterPatternBindings>.Ok(new CountedParameterPatternBindings(bindings, variadicStreamBindings));
        }

        var requiredCount = patterns.Count - 1;
        if (inputs.Count < requiredCount)
            return arityMismatch(requiredCount, inputs.Count);

        for (var index = 0; index < variadicIndex; index++)
        {
            var boundR = BindOne(index, index);
            if (boundR.IsError) return boundR.Error;
        }

        var suffixCount = patterns.Count - variadicIndex - 1;
        var suffixInputStart = inputs.Count - suffixCount;
        for (var suffixIndex = 0; suffixIndex < suffixCount; suffixIndex++)
        {
            var boundR = BindOne(variadicIndex + 1 + suffixIndex, suffixInputStart + suffixIndex);
            if (boundR.IsError) return boundR.Error;
        }

        var variadicCapture = (CaptureParameterPattern)patterns[variadicIndex];
        var capturedValues = inputs
            .Skip(variadicIndex)
            .Take(suffixInputStart - variadicIndex)
            .Select(static input => input.Value)
            .ToList();
        var capturedResult = Result.FromItems(capturedValues);
        var captured = new CountedResult(capturedResult, capturedValues.Count);
        bindings.Add((variadicCapture.Name, captured));
        variadicStreamBindings.Add((variadicCapture.Name, captured));

        return EvalResult<CountedParameterPatternBindings>.Ok(new CountedParameterPatternBindings(bindings, variadicStreamBindings));
    }

    /// <summary>
    /// Higher-order callbacks keep the collected item value shape for pattern
    /// matching, while the counted callback-param view still uses the same
    /// one-level projection rule as <c>S:i</c> for callback param operations
    /// like <c>x.count</c>.
    /// </summary>
    private static CountedResult CountedSequenceCallbackItem(CountedResult item)
    {
        var projected = item.Value.ProjectIteratedContent();
        return new CountedResult(projected.Value, projected.EmittedCount);
    }

    /// <summary>
    /// Evaluate a resolved algorithm against pre-evaluated callback arguments
    /// that preserve their emitted top-level counts.
    /// </summary>
    private static EvalResult<CountedResult> EvalResolvedCallbackCallCounted(
        Algorithm callee,
        IReadOnlyList<CountedResult> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        switch (callee)
        {
            case Algorithm.Builtin(var builtin):
                return ApplyBuiltinCounted(
                    builtin,
                    args.Select(CountedArgAlgorithm).ToList(),
                    ctx,
                    valEnv);

            case Algorithm.Conditional:
                if (TryGetFlatBinderUserEquivalent(callee) is { } simpleCallee)
                {
                    if (simpleCallee.Output.Count == 0)
                        return new EvalError.MissingOutput();

                    var countedEnvR = BindCountedCallbackParams(simpleCallee.Params, args);
                    if (countedEnvR.IsError) return countedEnvR.Error;

                    var newCtx = WithCountedParameterEnvironments(ctx, countedEnvR.Value, [], simpleCallee.Params);
                    return EvalAlgOutputCounted(simpleCallee, newCtx, valEnv);
                }

                return EvalConditionalCallbackCallCounted(callee, args, ctx, valEnv, calleeName);

            default:
            {
                if (callee.Output.Count == 0)
                    return new EvalError.MissingOutput();

                if (UsesPatternBinding(callee))
                {
                    var countedPatternEnvR = BindCountedParameterPatternList(
                        callee.ParameterPatterns,
                        args,
                        (required, actual) => new EvalError.ArityMismatch(required, actual));
                    if (countedPatternEnvR.IsError) return countedPatternEnvR.Error;

                    var patternBindings = countedPatternEnvR.Value;
                    var patternCtx = WithCountedParameterEnvironments(
                        ctx,
                        patternBindings.CountedBindings,
                        patternBindings.VariadicStreamBindings,
                        patternBindings.CountedBindings.Select(static binding => binding.Item1));
                    return EvalAlgOutputCounted(callee, patternCtx, valEnv);
                }

                var countedEnvR = BindCountedCallbackParams(callee.Params, args);
                if (countedEnvR.IsError) return countedEnvR.Error;

                var newCtx = WithCountedParameterEnvironments(ctx, countedEnvR.Value, [], callee.Params);
                return EvalAlgOutputCounted(callee, newCtx, valEnv);
            }
        }
    }

    /// <summary>
    /// Non-counted wrapper for callback dispatch that still preserves projected
    /// item emitted counts internally where downstream operations depend on
    /// them.
    /// </summary>
    private static EvalResult<Result> EvalResolvedCallbackCall(
        Algorithm callee,
        IReadOnlyList<CountedResult> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        var callbackR = EvalResolvedCallbackCallCounted(callee, args, ctx, valEnv, calleeName);
        return callbackR.IsError
            ? callbackR.Error
            : EvalResult<Result>.Ok(callbackR.Value.Value);
    }

    /// <summary>
    /// Evaluate a higher-order sequence callback on one iterated item.
    /// </summary>
    private static EvalResult<Result> EvalSequenceCallbackCall(
        Algorithm callee,
        CountedResult item,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
        => EvalResolvedCallbackCall(callee, [CountedSequenceCallbackItem(item)], ctx, valEnv, calleeName);

    /// <summary>
    /// Counted variant of <see cref="EvalSequenceCallbackCall"/>.
    /// </summary>
    private static EvalResult<CountedResult> EvalSequenceCallbackCallCounted(
        Algorithm callee,
        CountedResult item,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
        => EvalResolvedCallbackCallCounted(callee, [CountedSequenceCallbackItem(item)], ctx, valEnv, calleeName);

    /// <summary>
    /// Evaluate an algorithm's output expressions and count how many top-level
    /// values they emitted at the current algorithm boundary.
    /// A grouped block expression counts as one value, while multiple top-level
    /// output expressions count separately.
    /// Lean: <c>evalAlgOutputCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalAlgOutputCountedCore(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (alg is Algorithm.Builtin(var builtin))
            return EvalBuiltinValueCounted(builtin);

        var dupProp = alg.FindDuplicatePropName();
        if (dupProp is not null)
            return new EvalError.DuplicateProperty(dupProp);

        if (alg is Algorithm.User { Output: { Count: 0 } })
            return new EvalError.MissingOutput();

        var innerCtx = ctx.Push(alg);
        var results = new List<Result>();
        var emittedCount = 0;

        foreach (var expr in alg.Output)
        {
            var countedR = EvalCounted(expr, innerCtx, valEnv);
            if (countedR.IsError) return countedR.Error;
            if (countedR.Value.EmittedCount != 0)
                results.Add(countedR.Value.Value);
            emittedCount += countedR.Value.EmittedCount;
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(Result.FromItems(results), emittedCount));
    }

    private static EvalResult<CountedResult> EvalAlgOutputCounted(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalAlgOutputCountedCore(alg, ctx, valEnv);

    private static EvalResult<CountedResult> EvalProgramOutputCounted(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalAlgOutputCountedCore(alg, ctx, valEnv);

    private static EvalResult<CountedResult> EvalBuiltinValueCounted(BuiltinId builtin)
        => builtin == BuiltinId.@empty
            ? EvalResult<CountedResult>.Ok(new CountedResult(new Result.Group([]), 0))
            : WrongBuiltinArity(builtin, 0);

    private static EvalError EmptyBuiltinCallSyntaxError()
    {
        var name = BuiltinRegistry.EmptyBuiltinName;
        return new EvalError.IllegalInEval($"`{name}` is a builtin constant; use `{name}` without call syntax.");
    }

    private static EvalResult<ZeroArgPropertyResult> EvaluateZeroArgPropertyResult(
        Algorithm resolvedAlgorithm,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var countedR = EvalAlgOutputCounted(resolvedAlgorithm, ctx, valEnv);
        if (countedR.IsError)
            return countedR.Error;

        return EvalResult<ZeroArgPropertyResult>.Ok(
            new ZeroArgPropertyResult(countedR.Value.Value, countedR.Value.EmittedCount));
    }

    private static bool IsRuntimePreludeProperty(Property binding)
        => PreludeAlg.Properties.Any(property => ReferenceEquals(property, binding))
            || MathAlgorithm.Properties.Any(property => ReferenceEquals(property, binding));

    private static EvalResult<ZeroArgPropertyResult> GetOrEvaluateZeroArgPropertyResult(
        Algorithm? owner,
        Property binding,
        ZeroArgPropertyAccessKind accessKind,
        Algorithm resolvedAlgorithm,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (owner is null)
            return EvaluateZeroArgPropertyResult(resolvedAlgorithm, ctx, valEnv);

        // Runtime-prelude builtins are excluded because some are nondeterministic
        // or host-dependent; a full cacheability/purity model is intentionally deferred.
        if (IsRuntimePreludeProperty(binding) || resolvedAlgorithm is Algorithm.Builtin)
            return EvaluateZeroArgPropertyResult(resolvedAlgorithm, ctx, valEnv);

        return ctx.ZeroArgPropertyResultCache.GetOrEvaluate(
            new ZeroArgPropertyExecution(
                owner,
                binding,
                accessKind,
                ValueEnvironmentCacheIdentity(valEnv),
                ctx.AlgEnv,
                ctx.CountedParamEnv),
            () => EvaluateZeroArgPropertyResult(resolvedAlgorithm, ctx, valEnv));
    }

    private static EvalResult<Result> EvalZeroArgPropertyAccess(
        Algorithm? owner,
        Property binding,
        ZeroArgPropertyAccessKind accessKind,
        Algorithm resolvedAlgorithm,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var propertyR = GetOrEvaluateZeroArgPropertyResult(owner, binding, accessKind, resolvedAlgorithm, ctx, valEnv);
        return propertyR.IsError
            ? propertyR.Error
            : EvalResult<Result>.Ok(propertyR.Value.Value);
    }

    private static EvalResult<Result> EvalZeroArgPropertyAccess(
        ResolvedLexicalProperty resolvedProperty,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalZeroArgPropertyAccess(
            resolvedProperty.Owner,
            resolvedProperty.Binding,
            ZeroArgPropertyAccessKind.Lexical,
            resolvedProperty.ResolvedAlgorithm,
            ctx,
            valEnv);

    private static EvalResult<CountedResult> EvalZeroArgPropertyAccessCounted(
        Algorithm? owner,
        Property binding,
        ZeroArgPropertyAccessKind accessKind,
        Algorithm resolvedAlgorithm,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var propertyR = GetOrEvaluateZeroArgPropertyResult(owner, binding, accessKind, resolvedAlgorithm, ctx, valEnv);
        return propertyR.IsError
            ? propertyR.Error
            : EvalResult<CountedResult>.Ok(new CountedResult(propertyR.Value.Value, propertyR.Value.EmittedCount));
    }

    private static EvalResult<CountedResult> EvalZeroArgPropertyAccessCounted(
        ResolvedLexicalProperty resolvedProperty,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalZeroArgPropertyAccessCounted(
            resolvedProperty.Owner,
            resolvedProperty.Binding,
            ZeroArgPropertyAccessKind.CountedLexical,
            resolvedProperty.ResolvedAlgorithm,
            ctx,
            valEnv);

    /// <summary>
    /// Evaluate a conditional algorithm against an already-assembled argument
    /// shape, preserving the selected branch's top-level emitted output count.
    /// Lean: <c>evalConditionalShapeCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalConditionalShapeCounted(
        Algorithm callee,
        Result argShape,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        if (callee.HasDuplicateBranchPatterns())
            return new EvalError.DuplicateBranchPattern();

        var match = MatchBranches(callee.Branches, argShape);
        if (match is null)
            return new EvalError.NoMatchingBranch(calleeName);

        var (branch, bindings) = match.Value;
        var wiredBody = ChildOf(callee, branch.Body);
        var shadowedNames = bindings.Select(static binding => binding.Item1).ToArray();
        var newCtx = ctx.Push(callee)
            .WithCountedParamEnv(ShadowCountedParamEnv(ctx.CountedParamEnv, shadowedNames))
            .WithVariadicStreamEnv(ShadowCountedParamEnv(ctx.VariadicStreamEnv, shadowedNames));
        var newEnv = Concat(bindings, valEnv);
        return EvalAlgOutputCounted(wiredBody, newCtx, newEnv);
    }

    private static EvalResult<CountedResult> EvalConditionalCallbackCallCounted(
        Algorithm callee,
        IReadOnlyList<CountedResult> explicitArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        if (callee.HasDuplicateBranchPatterns())
            return new EvalError.DuplicateBranchPattern();

        var match = MatchCountedCallBranches(callee.Branches, explicitArgs);
        if (match is null)
            return new EvalError.NoMatchingBranch(calleeName);

        var (branch, bindings) = match.Value;
        var wiredBody = ChildOf(callee, branch.Body);
        var newCtx = WithCountedParameterEnvironments(
            ctx.Push(callee),
            bindings,
            [],
            bindings.Select(static binding => binding.Item1));
        var newEnv = Concat(bindings.Select(static binding => (binding.Item1, binding.Item2.Value)).ToList(), valEnv);
        return EvalAlgOutputCounted(wiredBody, newCtx, newEnv);
    }

    private static bool ReducerAccumulatorSideHasTopLevelVariadic(Algorithm.User reducer)
    {
        try
        {
            var signature = CallableSignature.FromUserAlgorithm("reduce step", reducer);
            var plan = CallableBindingPlan.FromSignature(signature);
            return plan.TopLevelPatternList.Nodes
                .Skip(1)
                .Any(static node => node is VariadicCaptureBindingNode { IsTopLevel: true });
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static EvalResult<CountedResult> EvalReducerAccumulatorVariadicCallbackCallCounted(
        Algorithm.User callee,
        IReadOnlyList<CountedResult> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (callee.Output.Count == 0)
            return new EvalError.MissingOutput();

        var countedPatternEnvR = BindCountedParameterPatternList(
            callee.ParameterPatterns,
            args,
            (required, actual) => new EvalError.ArityMismatch(required, actual));
        if (countedPatternEnvR.IsError) return countedPatternEnvR.Error;

        var patternBindings = countedPatternEnvR.Value;
        var callbackCtx = WithCountedParameterEnvironments(
            ctx,
            patternBindings.CountedBindings,
            patternBindings.VariadicStreamBindings,
            patternBindings.CountedBindings.Select(static binding => binding.Item1));
        return EvalAlgOutputCounted(callee, callbackCtx, valEnv);
    }

    /// <summary>
    /// Evaluate a <c>reduce</c> step on one collected iteration item. Reducers
    /// with a top-level variadic accumulator parameter bind accumulator state
    /// slots like loop state; other reducers keep ordinary structural
    /// accumulator binding.
    /// </summary>
    private static EvalResult<CountedResult> EvalSequenceReduceStepCounted(
        Algorithm callee,
        CountedResult element,
        Result accumulator,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        var elementArg = CountedSequenceCallbackItem(element);
        if (callee is Algorithm.User userReducer && ReducerAccumulatorSideHasTopLevelVariadic(userReducer))
        {
            var accumulatorSlots = accumulator.ToItems();
            var args = new List<CountedResult>(1 + accumulatorSlots.Count) { elementArg };
            foreach (var slot in accumulatorSlots)
                args.Add(new CountedResult(slot, slot.ValueCount()));

            return EvalReducerAccumulatorVariadicCallbackCallCounted(userReducer, args, ctx, valEnv);
        }

        return EvalResolvedCallbackCallCounted(
            callee,
            [elementArg, new CountedResult(accumulator, accumulator.ValueCount())],
            ctx,
            valEnv,
            calleeName);
    }

    /// <summary>
    /// Recover the top-level values emitted at one algorithm boundary from a
    /// counted result.
    /// A grouped value emitted as one top-level result stays grouped, while a
    /// multi-output result is expanded back to its top-level items.
    /// </summary>
    private static List<Result> CountedTopLevelValues(CountedResult output)
    {
        var items = new List<Result>();
        AddCountedTopLevelValues(items, output);
        return items;
    }

    private static void AddCountedTopLevelValues(List<Result> into, CountedResult output)
    {
        if (output.EmittedCount == 0)
            return;

        if (output.EmittedCount == 1)
        {
            into.Add(output.Value);
            return;
        }

        ResultItems(into, output.Value);
    }

    private enum SequenceSupplySide
    {
        Left,
        Right
    }

    private static string SequenceSupplySideName(SequenceSupplySide side)
        => side == SequenceSupplySide.Left ? "left" : "right";

    private static EvalError SequenceSupplyMissingOutput(SequenceSupplySide side, SourceSpan? span)
        => new EvalError.SequenceSupplyMissingOutput(SequenceSupplySideName(side)) { Span = span };

    private static bool IsMissingOutputError(EvalError error) => error switch
    {
        EvalError.MissingOutput => true,
        EvalError.WithContext(_, var inner) => IsMissingOutputError(inner),
        _ => false,
    };

    private static EvalResult<IReadOnlyList<Result>> EvalAlgorithmOutputSequenceSupplyItems(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        SequenceSupplySide side,
        SourceSpan? span)
    {
        if (alg is Algorithm.Builtin(var builtin))
        {
            var builtinR = EvalBuiltinValueCounted(builtin);
            return builtinR.IsError
                ? builtinR.Error
                : EvalResult<IReadOnlyList<Result>>.Ok(CountedTopLevelValues(builtinR.Value));
        }

        var dupProp = alg.FindDuplicatePropName();
        if (dupProp is not null)
            return new EvalError.DuplicateProperty(dupProp);

        if (alg is Algorithm.User { Output.Count: 0 })
            return SequenceSupplyMissingOutput(side, span);

        var innerCtx = ctx.Push(alg);
        var items = new List<Result>();

        foreach (var expr in alg.Output)
        {
            var countedR = EvalCounted(expr, innerCtx, valEnv);
            if (countedR.IsError)
                return IsMissingOutputError(countedR.Error)
                    ? SequenceSupplyMissingOutput(side, expr.Span ?? span)
                    : countedR.Error;

            AddCountedTopLevelValues(items, countedR.Value);
        }

        return EvalResult<IReadOnlyList<Result>>.Ok(items);
    }

    private static EvalResult<IReadOnlyList<Result>> EvalSequenceSupplyOperandItems(
        Expr expr,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        SequenceSupplySide side)
    {
        if (expr is Expr.Block(var alg))
        {
            var wired = WireToCaller(ctx, alg);
            var blockSpan = expr.Span ?? FirstSpan(wired.Output);
            if (wired.Params.Count != 0)
                return MissingImplicitArguments<IReadOnlyList<Result>>(wired.Params, blockSpan);

            return EvalAlgorithmOutputSequenceSupplyItems(wired, ctx, valEnv, side, blockSpan);
        }

        var outputR = EvalCounted(expr, ctx, valEnv);
        if (outputR.IsError)
            return IsMissingOutputError(outputR.Error)
                ? SequenceSupplyMissingOutput(side, expr.Span)
                : outputR.Error;

        return EvalResult<IReadOnlyList<Result>>.Ok(CountedTopLevelValues(outputR.Value));
    }

    private static List<Expr> SequenceSupplyLeaves(Expr expr)
    {
        var leaves = new List<Expr>();
        var stack = new Stack<Expr>();
        stack.Push(expr);

        while (stack.Count != 0)
        {
            var current = stack.Pop();
            if (current is Expr.SequenceSupply(var left, var right))
            {
                stack.Push(right);
                stack.Push(left);
                continue;
            }

            leaves.Add(current);
        }

        return leaves;
    }

    private static EvalResult<CountedResult> EvalSequenceSupplyCounted(
        Expr expr,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var leaves = SequenceSupplyLeaves(expr);
        var items = new List<Result>(leaves.Count);

        for (var index = 0; index < leaves.Count; index++)
        {
            var leaf = leaves[index];
            var side = index == 0 ? SequenceSupplySide.Left : SequenceSupplySide.Right;
            var leafR = EvalSequenceSupplyOperandItems(leaf, ctx, valEnv, side);
            if (leafR.IsError) return leafR.Error;

            items.AddRange(leafR.Value);
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(
            Result.FromItems(items),
            items.Count));
    }

    private readonly record struct BoundSequenceBuiltinArguments(
        PreparedSequenceBuiltinInput PreparedInput,
        IReadOnlyList<CountedResult> IterationItems,
        IReadOnlyList<PreparedSequenceBuiltinSuffixArg> SuffixArgs);

    private static EvalError SequenceBuiltinBindingArityMismatch(
        BuiltinId builtin,
        CallableSignature signature,
        int requiredNormalItemCount,
        int actualItemCount)
        => new EvalError.WithContext(
            CallableSignatureDiagnostics.FormatBuiltinItemCountMismatch(
                BuiltinDisplayName(builtin),
                signature,
                actualItemCount),
            new EvalError.ArityMismatch(requiredNormalItemCount, actualItemCount)
            {
                Signature = signature,
            });

    private static IReadOnlyList<ResolvedArgumentAlgorithm> WithoutSequenceSupply(
        IReadOnlyList<Algorithm> args)
        => args.Select(static arg => new ResolvedArgumentAlgorithm(arg, SuppliesSequence: false)).ToList();

    private static EvalResult<IReadOnlyList<VariadicCallItem>> BuildCallableCallItems(
        IReadOnlyList<ResolvedArgumentAlgorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var items = new List<VariadicCallItem>();
        foreach (var resolvedArg in args)
        {
            var arg = resolvedArg.Algorithm;
            var outputR = EvalAlgOutputCounted(arg, ctx, valEnv);
            if (outputR.IsOk)
            {
                if (resolvedArg.SuppliesSequence)
                {
                    foreach (var value in CountedTopLevelValues(outputR.Value))
                        items.Add(new VariadicCallItem(value, arg, ValueError: null));
                }
                else if (outputR.Value.EmittedCount == 0)
                {
                    items.Add(new VariadicCallItem(Value: null, arg, ValueError: null));
                }
                else
                {
                    items.Add(new VariadicCallItem(outputR.Value.Value, arg, ValueError: null));
                }

                continue;
            }

            items.Add(new VariadicCallItem(Value: null, arg, outputR.Error));
        }

        return EvalResult<IReadOnlyList<VariadicCallItem>>.Ok(items);
    }

    private static EvalResult<PreparedSequenceBuiltinSuffixArg> PrepareSequenceBuiltinSuffixArg(
        BuiltinId builtin,
        SequenceBuiltinSuffixArgDescriptor descriptor,
        VariadicCallItem item,
        EvalCtx ctx)
    {
        switch (descriptor.Kind)
        {
            case SequenceBuiltinSuffixArgKind.Algorithm:
                if (item.Algorithm is not null)
                {
                    return EvalResult<PreparedSequenceBuiltinSuffixArg>.Ok(
                        new PreparedSequenceBuiltinSuffixArg.AlgorithmArg(
                            NormalizeSequenceCallableSuffixAlgorithm(item.Algorithm, ctx)));
                }

                return item.ValueError ?? new EvalError.WithContext(
                    SequenceBuiltinSuffixArgErrorContext(builtin, descriptor),
                    new EvalError.BadArity());

            case SequenceBuiltinSuffixArgKind.Value:
                if (item.Value is not null)
                {
                    return EvalResult<PreparedSequenceBuiltinSuffixArg>.Ok(
                        new PreparedSequenceBuiltinSuffixArg.ValueArg(item.Value));
                }

                return item.ValueError ?? new EvalError.WithContext(
                    SequenceBuiltinSuffixArgErrorContext(builtin, descriptor),
                    new EvalError.BadArity());

            case SequenceBuiltinSuffixArgKind.WholeNumber:
            {
                if (item.Value is null)
                    return item.ValueError ?? new EvalError.WithContext(
                        SequenceBuiltinSuffixArgErrorContext(builtin, descriptor),
                        new EvalError.BadArity());

                var numeric = item.Value.SingleAtomicNumber();
                if (numeric is null || numeric.Value != Math.Truncate(numeric.Value))
                {
                    return new EvalError.WithContext(
                        SequenceBuiltinSuffixArgErrorContext(builtin, descriptor),
                        new EvalError.BadArity());
                }

                return EvalResult<PreparedSequenceBuiltinSuffixArg>.Ok(
                    new PreparedSequenceBuiltinSuffixArg.WholeNumberArg(numeric.Value));
            }

            default:
                return InternalSequenceBuiltinSuffixArgMetadataError<PreparedSequenceBuiltinSuffixArg>(
                    builtin,
                    "used an unknown suffix-argument kind");
        }
    }

    private static Algorithm NormalizeSequenceCallableSuffixAlgorithm(Algorithm algorithm, EvalCtx ctx)
    {
        if (algorithm is Algorithm.User { Params.Count: 0, Output.Count: 1 } user
            && user.Output[0] is Expr.Resolve(var name) resolve)
        {
            var resolvedR = ResolveNamedAlgorithm(name, resolve.Span, ctx);
            if (resolvedR.IsOk)
                return resolvedR.Value;
        }

        return algorithm;
    }

    private static EvalResult<IReadOnlyList<CountedResult>> EvalSequenceIterationItems(
        IReadOnlyList<Algorithm> collectionArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalSequenceIterationItems(WithoutSequenceSupply(collectionArgs), ctx, valEnv);

    private static EvalResult<IReadOnlyList<CountedResult>> EvalSequenceIterationItems(
        IReadOnlyList<ResolvedArgumentAlgorithm> collectionArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var itemsR = BuildCallableCallItems(collectionArgs, ctx, valEnv);
        if (itemsR.IsError) return itemsR.Error;

        var items = new List<CountedResult>(itemsR.Value.Count);
        foreach (var item in itemsR.Value)
        {
            if (item.Value is null && item.ValueError is null)
                continue;

            if (item.Value is null)
                return item.ValueError ?? new EvalError.BadArity();

            items.Add(new CountedResult(item.Value, 1));
        }

        return EvalResult<IReadOnlyList<CountedResult>>.Ok(items);
    }

    private static EvalResult<CollectedSequenceBuiltinInput> ApplySequenceBuiltinEmptyPolicy(
        BuiltinId builtin,
        SequenceBuiltinMetadata metadata,
        CollectedSequenceBuiltinInput collected)
    {
        return metadata.EmptyPolicy switch
        {
            SequenceBuiltinEmptyPolicy.AllowEmpty => EvalResult<CollectedSequenceBuiltinInput>.Ok(collected),
            SequenceBuiltinEmptyPolicy.RequireAnyItem when collected.TotalItemCount == 0 => new EvalError.WithContext(
                $"{BuiltinDisplayName(builtin)} requires a non-empty collection",
                new EvalError.BadArity()),
            SequenceBuiltinEmptyPolicy.RequireEachInputNonEmpty when collected.AnyInputEmpty => new EvalError.WithContext(
                $"{BuiltinDisplayName(builtin)} requires each input collection to be non-empty",
                new EvalError.BadArity()),
            _ => EvalResult<CollectedSequenceBuiltinInput>.Ok(collected),
        };
    }

    private static string DescribeSequenceItem(Result item) => item switch
    {
        Result.Atom(var n) => $"numeric value {n}",
        Result.Str(var s) => $"string value \"{s}\"",
        Result.Group(var items) when items.Count == 0 => "empty grouped value",
        Result.Group => "grouped value",
        _ => "value",
    };

    private static string NumericSequenceItemErrorContext(BuiltinId builtin, int index, Result item)
        => $"{BuiltinDisplayName(builtin)} expects each collection element to be a single numeric value; item {index} was {DescribeSequenceItem(item)}";

    private static EvalError ReduceInitialAccumulatorRequiresValueError(Algorithm initialAlg)
        => new EvalError.WithContext(
            new ReduceInitialAccumulatorContext(initialAlg.Params.ToList()),
            new EvalError.BadArity());

    private static bool IsLikelyUnevaluatedParameterError(Algorithm algorithm, EvalError error)
    {
        if (algorithm.Params.Count == 0)
            return false;

        var parameterNames = algorithm.Params.ToHashSet(StringComparer.Ordinal);
        return ErrorReferencesAnyName(error, parameterNames);
    }

    private static bool ErrorReferencesAnyName(EvalError error, IReadOnlySet<string> names)
        => error switch
        {
            EvalError.UnknownName(var name) => names.Contains(name),
            EvalError.UnresolvedImplicitParams(var paramNames) => paramNames.Any(names.Contains),
            EvalError.WithContext(_, var inner) => ErrorReferencesAnyName(inner, names),
            _ => false,
        };

    /// <summary>
    /// Evaluate <c>reduce(values..., reducer, initial)</c> while
    /// preserving the accumulator's emitted-value count for the empty-sequence
    /// case. <c>values...</c> supplies the items, and the reducer and initial
    /// accumulator are suffix parameters.
    /// The current item is passed exactly as collected by the shared
    /// <c>values...</c> top-level binding model; nested groups stay grouped.
    /// Normal accumulator parameters keep ordinary structural semantics; a
    /// top-level variadic accumulator parameter receives accumulator state
    /// slots.
    /// Lean: <c>evalReduceCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalReduceCounted(
        IReadOnlyList<CountedResult> items,
        Algorithm stepAlg,
        Algorithm initialAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var initialR = EvalAlgOutputCounted(initialAlg, ctx, valEnv);
        if (initialR.IsError)
        {
            if (IsLikelyUnevaluatedParameterError(initialAlg, initialR.Error))
                return ReduceInitialAccumulatorRequiresValueError(initialAlg);

            return initialR.Error;
        }

        var accumulator = initialR.Value;
        foreach (var item in items)
        {
            var stepR = WithCtx(
                "while evaluating reduce step (reduce passes each iterated collection item as collected; sequence parameters use values... top-level binding, nested groups stay grouped, and top-level variadic accumulator parameters receive state slots)",
                EvalSequenceReduceStepCounted(stepAlg, item, accumulator.Value, ctx, valEnv, "reduce step"));
            if (stepR.IsError) return stepR.Error;

            var nextR = ExpectSingleAccumulator(stepR.Value);
            if (nextR.IsError) return nextR.Error;

            accumulator = new CountedResult(nextR.Value, 1);
        }

        return EvalResult<CountedResult>.Ok(accumulator);
    }

    /// <summary>
    /// Evaluate <c>filter(values..., predicate)</c>. <c>values...</c> supplies
    /// the items, and <c>predicate</c> is a suffix parameter.
    /// Each iterated item is passed exactly as collected by the shared
    /// <c>values...</c> top-level binding model; nested groups stay grouped.
    /// Kept outputs remain the original sequence items.
    /// </summary>
    private static EvalResult<CountedResult> EvalFilterCounted(
        IReadOnlyList<CountedResult> items,
        Algorithm predicateAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var kept = new List<Result>();
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var truthR = EvalFilterPredicateTruth(predicateAlg, item, index, ctx, valEnv);
            if (truthR.IsError)
                return truthR.Error;

            if (truthR.Value)
                kept.Add(item.Value);
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(Result.FromItems(kept), kept.Count));
    }

    /// <summary>
    /// Evaluate a filter predicate with the same callback and truthiness rules
    /// used by generic <c>filter</c>; sequence optimizers call this to avoid
    /// duplicating callback semantics.
    /// </summary>
    internal static EvalResult<bool> EvalFilterPredicateTruth(
        Algorithm predicateAlg,
        CountedResult item,
        int index,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var predicateR = WithCtx(
            $"while evaluating filter predicate for item {index}: {FormatResultForDiagnostic(item.Value)} (filter passes each iterated collection item as collected; sequence parameters use values... top-level binding and nested groups stay grouped)",
            EvalSequenceCallbackCall(predicateAlg, item, ctx, valEnv, "filter predicate"));
        if (predicateR.IsError)
            return predicateR.Error;

        var truth = predicateR.Value.SingleAtomicTruthValue();
        if (truth is null)
        {
            return new EvalError.WithContext(
                "filter predicate must return exactly one atomic numeric value",
                new EvalError.BadArity());
        }

        return EvalResult<bool>.Ok(truth.Value);
    }

    /// <summary>
    /// Evaluate <c>map(values..., mapper)</c> while preserving the number of
    /// top-level mapped elements. <c>mapper</c> is a suffix parameter.
    /// Each callback item is passed exactly as collected by the shared
    /// <c>values...</c> top-level binding model; nested groups stay grouped.
    /// Lean: <c>evalMapCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalMapCounted(
        IReadOnlyList<CountedResult> items,
        Algorithm transformAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var mapped = new List<Result>(items.Count);
        foreach (var item in items)
        {
            var transformR = WithCtx(
                "while evaluating map transform (map passes each iterated collection item as collected; sequence parameters use values... top-level binding and nested groups stay grouped)",
                EvalSequenceCallbackCallCounted(transformAlg, item, ctx, valEnv, "map transform"));
            if (transformR.IsError) return transformR.Error;

            var mappedElementR = ExpectSingleMappedElement(transformR.Value);
            if (mappedElementR.IsError) return mappedElementR.Error;

            mapped.Add(mappedElementR.Value);
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(Result.FromItems(mapped), mapped.Count));
    }

    /// <summary>
    /// Collect top-level sequence items as single atomic numeric values.
    /// Used by numeric ordering and aggregation builtins that only accept
    /// clearly comparable numeric elements and reject strings or grouped values.
    /// Diagnostics include the 0-based item index after counted top-level
    /// extraction so numeric shape failures are easier to debug.
    /// </summary>
    private static EvalResult<List<decimal>> CollectSingleAtomicNumbers(
        BuiltinId builtin,
        IReadOnlyList<Result> elements)
    {
        var numbers = new List<decimal>(elements.Count);
        for (var index = 0; index < elements.Count; index++)
        {
            var item = elements[index];
            var numeric = item.SingleAtomicNumber();
            if (numeric is null)
            {
                return new EvalError.WithContext(
                    NumericSequenceItemErrorContext(builtin, index, item),
                    new EvalError.BadArity());
            }

            numbers.Add(numeric.Value);
        }

        return EvalResult<List<decimal>>.Ok(numbers);
    }

    private static EvalResult<PreparedSequenceBuiltinInput> PrepareSequenceBuiltinInput(
        BuiltinId builtin,
        SequenceBuiltinMetadata metadata,
        CollectedSequenceBuiltinInput collected)
    {
        var validatedItemsR = ApplySequenceBuiltinEmptyPolicy(builtin, metadata, collected);
        if (validatedItemsR.IsError) return validatedItemsR.Error;

        IReadOnlyList<decimal>? numericItems = null;
        switch (metadata.ItemShapeConstraint)
        {
            case SequenceBuiltinItemShapeConstraint.Any:
                break;

            case SequenceBuiltinItemShapeConstraint.SingleNumeric:
            {
                var numbersR = CollectSingleAtomicNumbers(builtin, validatedItemsR.Value.FlattenedItems);
                if (numbersR.IsError) return numbersR.Error;
                numericItems = numbersR.Value;
                break;
            }
        }

        return EvalResult<PreparedSequenceBuiltinInput>.Ok(
            new PreparedSequenceBuiltinInput(validatedItemsR.Value, numericItems));
    }

    private static string DescribeSequenceBuiltinSuffixArgRequirement(
        SequenceBuiltinSuffixArgKind kind)
        => kind switch
        {
            SequenceBuiltinSuffixArgKind.Algorithm => "an algorithm",
            SequenceBuiltinSuffixArgKind.Value => "exactly one value",
            SequenceBuiltinSuffixArgKind.WholeNumber => "exactly one whole-number value",
            _ => "a valid suffix argument",
        };

    private static string DescribeSequenceBuiltinSuffixArgKind(
        SequenceBuiltinSuffixArgKind kind)
        => kind switch
        {
            SequenceBuiltinSuffixArgKind.Algorithm => "algorithm",
            SequenceBuiltinSuffixArgKind.Value => "value",
            SequenceBuiltinSuffixArgKind.WholeNumber => "whole-number value",
            _ => "unknown",
        };

    private static string SequenceBuiltinSuffixArgErrorContext(
        BuiltinId builtin,
        SequenceBuiltinSuffixArgDescriptor descriptor)
        => $"{BuiltinDisplayName(builtin)} {descriptor.Name} must be {DescribeSequenceBuiltinSuffixArgRequirement(descriptor.Kind)}";

    private static EvalResult<T> InternalSequenceBuiltinSuffixArgMetadataError<T>(
        BuiltinId builtin,
        string detail)
        => new EvalError.WithContext(
            $"internal sequence metadata for {BuiltinDisplayName(builtin)} {detail}",
            new EvalError.BadArity());

    private static EvalResult<BoundSequenceBuiltinArguments> BindSequenceBuiltinArguments(
        BuiltinId builtin,
        SequenceBuiltinMetadata metadata,
        IReadOnlyList<ResolvedArgumentAlgorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var descriptor = BuiltinRegistry.GetBuiltin(builtin);
        var signature = descriptor.PlainSignature;
        var itemsR = BuildCallableCallItems(args, ctx, valEnv);
        if (itemsR.IsError) return itemsR.Error;

        var bindingsR = BindCallableArguments(
            signature,
            itemsR.Value,
            (required, actual) => SequenceBuiltinBindingArityMismatch(builtin, signature, required, actual));
        if (bindingsR.IsError) return bindingsR.Error;

        var bindings = bindingsR.Value;
        var collectionValues = new List<Result>(bindings.VariadicItems.Count);
        foreach (var item in bindings.VariadicItems)
        {
            if (item.Value is null && item.ValueError is null)
                continue;

            if (item.Value is null)
                return item.ValueError ?? new EvalError.BadArity();

            collectionValues.Add(item.Value);
        }

        var collected = new CollectedSequenceBuiltinInput([collectionValues], collectionValues);
        var preparedInputR = PrepareSequenceBuiltinInput(builtin, metadata, collected);
        if (preparedInputR.IsError) return preparedInputR.Error;

        if (bindings.NormalBindings.Count != metadata.SuffixArgs.Count)
        {
            return InternalSequenceBuiltinSuffixArgMetadataError<BoundSequenceBuiltinArguments>(
                builtin,
                "mismatched suffix arguments");
        }

        var suffixArgs = new List<PreparedSequenceBuiltinSuffixArg>(metadata.SuffixArgs.Count);
        for (var index = 0; index < metadata.SuffixArgs.Count; index++)
        {
            var preparedArgR = PrepareSequenceBuiltinSuffixArg(
                builtin,
                metadata.SuffixArgs[index],
                bindings.NormalBindings[index].Item,
                ctx);
            if (preparedArgR.IsError) return preparedArgR.Error;

            suffixArgs.Add(preparedArgR.Value);
        }

        var iterationItems = collectionValues
            .Select(static value => new CountedResult(value, 1))
            .ToList();

        return EvalResult<BoundSequenceBuiltinArguments>.Ok(
            new BoundSequenceBuiltinArguments(preparedInputR.Value, iterationItems, suffixArgs));
    }

    private static EvalResult<T> ExpectPreparedSequenceBuiltinSuffixArgAt<T>(
        BuiltinId builtin,
        IReadOnlyList<SequenceBuiltinSuffixArgDescriptor> descriptors,
        IReadOnlyList<PreparedSequenceBuiltinSuffixArg> args,
        int index,
        SequenceBuiltinSuffixArgKind expectedKind,
        Func<SequenceBuiltinSuffixArgDescriptor, PreparedSequenceBuiltinSuffixArg, EvalResult<T>> projector)
    {
        if (descriptors.Count != args.Count)
        {
            return InternalSequenceBuiltinSuffixArgMetadataError<T>(
                builtin,
                "mismatched suffix arguments");
        }

        if ((uint)index >= (uint)descriptors.Count)
        {
            return InternalSequenceBuiltinSuffixArgMetadataError<T>(
                builtin,
                $"expected suffix argument {index + 1} to have metadata kind {DescribeSequenceBuiltinSuffixArgKind(expectedKind)}");
        }

        var descriptor = descriptors[index];
        if (descriptor.Kind != expectedKind)
        {
            return InternalSequenceBuiltinSuffixArgMetadataError<T>(
                builtin,
                $"expected suffix argument {index + 1} ({descriptor.Name}) to have metadata kind {DescribeSequenceBuiltinSuffixArgKind(expectedKind)}, but found {DescribeSequenceBuiltinSuffixArgKind(descriptor.Kind)}");
        }

        return projector(descriptor, args[index]);
    }

    private static EvalResult<Algorithm> ExpectPreparedAlgorithmSuffixArg(
        BuiltinId builtin,
        IReadOnlyList<SequenceBuiltinSuffixArgDescriptor> descriptors,
        IReadOnlyList<PreparedSequenceBuiltinSuffixArg> args,
        int index)
        => ExpectPreparedSequenceBuiltinSuffixArgAt(
            builtin,
            descriptors,
            args,
            index,
            SequenceBuiltinSuffixArgKind.Algorithm,
            (descriptor, arg) => arg is PreparedSequenceBuiltinSuffixArg.AlgorithmArg(var algorithm)
                ? EvalResult<Algorithm>.Ok(algorithm)
                : InternalSequenceBuiltinSuffixArgMetadataError<Algorithm>(
                    builtin,
                    $"prepared suffix argument {index + 1} ({descriptor.Name}) did not match metadata kind {DescribeSequenceBuiltinSuffixArgKind(SequenceBuiltinSuffixArgKind.Algorithm)}"));

    private static EvalResult<decimal> ExpectPreparedWholeNumberSuffixArg(
        BuiltinId builtin,
        IReadOnlyList<SequenceBuiltinSuffixArgDescriptor> descriptors,
        IReadOnlyList<PreparedSequenceBuiltinSuffixArg> args,
        int index)
        => ExpectPreparedSequenceBuiltinSuffixArgAt(
            builtin,
            descriptors,
            args,
            index,
            SequenceBuiltinSuffixArgKind.WholeNumber,
            (descriptor, arg) => arg is PreparedSequenceBuiltinSuffixArg.WholeNumberArg(var value)
                ? EvalResult<decimal>.Ok(value)
                : InternalSequenceBuiltinSuffixArgMetadataError<decimal>(
                    builtin,
                    $"prepared suffix argument {index + 1} ({descriptor.Name}) did not match metadata kind {DescribeSequenceBuiltinSuffixArgKind(SequenceBuiltinSuffixArgKind.WholeNumber)}"));

    private static EvalResult<Result> ExpectPreparedValueSuffixArg(
        BuiltinId builtin,
        IReadOnlyList<SequenceBuiltinSuffixArgDescriptor> descriptors,
        IReadOnlyList<PreparedSequenceBuiltinSuffixArg> args,
        int index)
        => ExpectPreparedSequenceBuiltinSuffixArgAt(
            builtin,
            descriptors,
            args,
            index,
            SequenceBuiltinSuffixArgKind.Value,
            (descriptor, arg) => arg is PreparedSequenceBuiltinSuffixArg.ValueArg(var value)
                ? EvalResult<Result>.Ok(value)
                : InternalSequenceBuiltinSuffixArgMetadataError<Result>(
                    builtin,
                    $"prepared suffix argument {index + 1} ({descriptor.Name}) did not match metadata kind {DescribeSequenceBuiltinSuffixArgKind(SequenceBuiltinSuffixArgKind.Value)}"));

    private static EvalResult<IReadOnlyList<decimal>> ExpectPreparedNumericItems(
        BuiltinId builtin,
        PreparedSequenceBuiltinInput prepared)
    {
        if (prepared.NumericItems is { } numbers)
            return EvalResult<IReadOnlyList<decimal>>.Ok(numbers);

        return new EvalError.WithContext(
            $"internal sequence metadata for {BuiltinDisplayName(builtin)} did not produce numeric items",
            new EvalError.BadArity());
    }

    /// <summary>
    /// Evaluate <c>order(values...)</c> by eagerly sorting the top-level numeric
    /// sequence items in ascending order.
    /// Duplicates are preserved, groups are not flattened, strings are
    /// rejected, and empty collections stay empty.
    /// </summary>
    private static EvalResult<CountedResult> EvalOrderCounted(
        IReadOnlyList<decimal> numbers)
    {
        var sorted = numbers.ToList();
        sorted.Sort();
        return EvalResult<CountedResult>.Ok(new CountedResult(
            Result.FromItems(sorted.Select(static value => new Result.Atom(value))),
            sorted.Count));
    }

    /// <summary>
    /// Evaluate <c>orderDesc(values...)</c> by eagerly sorting the top-level
    /// numeric sequence items in descending order.
    /// Duplicates are preserved, groups are not flattened, strings are
    /// rejected, and empty collections stay empty.
    /// </summary>
    private static EvalResult<CountedResult> EvalOrderDescCounted(
        IReadOnlyList<decimal> numbers)
    {
        var sorted = numbers.ToList();
        sorted.Sort(static (left, right) => right.CompareTo(left));
        return EvalResult<CountedResult>.Ok(new CountedResult(
            Result.FromItems(sorted.Select(static value => new Result.Atom(value))),
            sorted.Count));
    }

    /// <summary>
    /// Evaluate <c>count(values...)</c> by counting the top-level sequence
    /// elements from left to right.
    /// Each atom, string, or grouped value counts as one top-level element;
    /// groups are not flattened or inspected recursively, and empty collections
    /// return <c>0</c>.
    /// Lean: <c>evalCountCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalCountCounted(
        IReadOnlyList<Result> items)
        => EvalResult<CountedResult>.Ok(new CountedResult(new Result.Atom(items.Count), 1));

    /// <summary>
    /// Evaluate <c>contains(values..., item)</c> by checking whether any
    /// extracted top-level item equals the searched suffix item under ordinary
    /// KatLang value semantics.
    /// Search is top-level only: grouped values compare structurally as grouped
    /// items and are not searched recursively.
    /// </summary>
    private static EvalResult<CountedResult> EvalContainsCounted(
        IReadOnlyList<Result> items,
        Result searchedItem)
        => EvalResult<CountedResult>.Ok(new CountedResult(
            new Result.Atom(items.Any(item => Result.ValueComparer.Equals(item, searchedItem)) ? 1 : 0),
            1));

    /// <summary>
    /// Evaluate <c>distinct(values...)</c> by removing later duplicate top-level
    /// items while preserving the original
    /// order of first occurrence. Duplicate detection follows KatLang value
    /// semantics, so atoms compare by numeric value, strings by exact string
    /// value, and groups structurally by grouped contents.
    /// </summary>
    private static EvalResult<CountedResult> EvalDistinctCounted(
        IReadOnlyList<Result> items)
    {
        var distinctItems = new List<Result>(items.Count);
        var seen = new HashSet<Result>(Result.ValueComparer);
        foreach (var item in items)
        {
            if (seen.Add(item))
                distinctItems.Add(item);
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(Result.FromItems(distinctItems), distinctItems.Count));
    }

    /// <summary>
    /// Evaluate <c>first(values...)</c> by returning the first top-level
    /// collection element unchanged.
    /// Atoms, strings, and grouped values each count as one top-level element;
    /// grouped values are preserved whole, and the collection must be non-empty.
    /// </summary>
    private static EvalResult<CountedResult> EvalFirstCounted(
        IReadOnlyList<Result> items)
    {
        if (items.Count == 0)
            return new EvalError.BadArity();

        return EvalResult<CountedResult>.Ok(new CountedResult(items[0], 1));
    }

    /// <summary>
    /// Evaluate <c>last(values...)</c> by returning the last top-level
    /// collection element unchanged.
    /// Atoms, strings, and grouped values each count as one top-level element;
    /// grouped values are preserved whole, and the collection must be non-empty.
    /// </summary>
    private static EvalResult<CountedResult> EvalLastCounted(
        IReadOnlyList<Result> items)
    {
        if (items.Count == 0)
            return new EvalError.BadArity();

        return EvalResult<CountedResult>.Ok(new CountedResult(items[^1], 1));
    }

    /// <summary>
    /// Evaluate <c>take(values..., count)</c> by returning the first
    /// <paramref name="count"/> extracted top-level items. <paramref name="count"/>
    /// is a suffix parameter.
    /// Non-positive counts return an empty sequence, oversized counts return
    /// the whole sequence, grouped values stay grouped, and original order is
    /// preserved.
    /// </summary>
    private static EvalResult<CountedResult> EvalTakeCounted(
        IReadOnlyList<Result> items,
        decimal count)
    {
        IReadOnlyList<Result> taken = count <= 0
            ? []
            : items.Take((int)count).ToList();

        return EvalResult<CountedResult>.Ok(new CountedResult(Result.FromItems(taken), taken.Count));
    }

    /// <summary>
    /// Evaluate <c>skip(values..., count)</c> by returning the extracted
    /// top-level items after the first <paramref name="count"/> items.
    /// <paramref name="count"/> is a suffix parameter. Non-positive counts leave the sequence
    /// unchanged, oversized counts return an empty sequence, grouped values
    /// stay grouped, and original order is preserved.
    /// </summary>
    private static EvalResult<CountedResult> EvalSkipCounted(
        IReadOnlyList<Result> items,
        decimal count)
    {
        IReadOnlyList<Result> remaining = count <= 0
            ? items.ToList()
            : items.Skip((int)count).ToList();

        return EvalResult<CountedResult>.Ok(new CountedResult(Result.FromItems(remaining), remaining.Count));
    }

    /// <summary>
    /// Evaluate <c>min(values...)</c> by comparing top-level sequence elements
    /// from left to right and returning the smallest numeric element.
    /// The collection must be non-empty, and each top-level element must be
    /// exactly one atomic numeric value; groups are not flattened and strings
    /// are rejected.
    /// Lean: <c>evalMinCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalMinCounted(
        IReadOnlyList<decimal> numbers)
    {
        if (numbers.Count == 0)
            return new EvalError.BadArity();

        var minimum = numbers[0];
        for (var i = 1; i < numbers.Count; i++)
        {
            if (numbers[i] < minimum)
                minimum = numbers[i];
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(new Result.Atom(minimum), 1));
    }

    /// <summary>
    /// Evaluate <c>max(values...)</c> by comparing top-level sequence elements
    /// from left to right and returning the largest numeric element.
    /// The collection must be non-empty, and each top-level element must be
    /// exactly one atomic numeric value; groups are not flattened and strings
    /// are rejected.
    /// Lean: <c>evalMaxCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalMaxCounted(
        IReadOnlyList<decimal> numbers)
    {
        if (numbers.Count == 0)
            return new EvalError.BadArity();

        var maximum = numbers[0];
        for (var i = 1; i < numbers.Count; i++)
        {
            if (numbers[i] > maximum)
                maximum = numbers[i];
        }

        return EvalResult<CountedResult>.Ok(new CountedResult(new Result.Atom(maximum), 1));
    }

    /// <summary>
    /// Evaluate <c>sum(values...)</c> by adding the top-level sequence elements
    /// from left to right.
    /// Each element must be exactly one atomic numeric value; groups are not
    /// flattened, strings are rejected, and empty collections return <c>0</c>.
    /// Implementation note: Lean <c>Int</c> is unbounded, but the C# decimal
    /// runtime can overflow; that overflow remains an implementation-only
    /// concern and is reported as <see cref="EvalError.NumericOverflow"/>.
    /// Lean: <c>evalSumCounted</c>.
    /// </summary>
    private static EvalResult<decimal> SumNumbersChecked(IReadOnlyList<decimal> numbers)
    {
        decimal total = 0;
        try
        {
            foreach (var numeric in numbers)
            {
                total = checked(total + numeric);
            }

            return EvalResult<decimal>.Ok(total);
        }
        catch (OverflowException)
        {
            return new EvalError.NumericOverflow();
        }
    }

    /// <summary>
    /// Evaluate <c>sum(values...)</c> by adding the prepared numeric elements
    /// from left to right.
    /// Lean: <c>evalSumCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalSumCounted(IReadOnlyList<decimal> numbers)
    {
        var totalR = SumNumbersChecked(numbers);
        if (totalR.IsError) return totalR.Error;

        return EvalResult<CountedResult>.Ok(new CountedResult(new Result.Atom(totalR.Value), 1));
    }

    /// <summary>
    /// Evaluate <c>avg(values...)</c> by averaging the top-level sequence
    /// elements from left to right.
    /// The collection must be non-empty, and each top-level element must be
    /// exactly one atomic numeric value; groups are not flattened and strings
    /// are rejected.
    /// Lean core still defines <c>avg</c> over <c>Int</c>, so the final quotient
    /// uses Lean's floor-style integer semantics even though C# stores runtime
    /// numbers as decimal.
    /// Implementation note: the intermediate decimal accumulation can still
    /// overflow in C#, which is reported as <see cref="EvalError.NumericOverflow"/>.
    /// Lean: <c>evalAvgCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalAvgCounted(IReadOnlyList<decimal> numbers)
    {
        if (numbers.Count == 0)
            return new EvalError.BadArity();

        var totalR = SumNumbersChecked(numbers);
        if (totalR.IsError) return totalR.Error;

        var average = Math.Floor(totalR.Value / numbers.Count);
        return EvalResult<CountedResult>.Ok(new CountedResult(new Result.Atom(average), 1));
    }

    private static EvalResult<CountedResult> ApplyBuiltinCountedSequence(
        BuiltinId builtin,
        SequenceBuiltinMetadata metadata,
        IReadOnlyList<ResolvedArgumentAlgorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var boundR = BindSequenceBuiltinArguments(builtin, metadata, args, ctx, valEnv);
        if (boundR.IsError) return boundR.Error;

        var bound = boundR.Value;

        EvalResult<CountedResult> WithPreparedFlatItems(
            Func<IReadOnlyList<Result>, EvalResult<CountedResult>> handler)
            => handler(bound.PreparedInput.FlattenedItems);

        EvalResult<CountedResult> WithPreparedNumericItems(
            Func<IReadOnlyList<decimal>, EvalResult<CountedResult>> handler)
        {
            var numbersR = ExpectPreparedNumericItems(builtin, bound.PreparedInput);
            if (numbersR.IsError) return numbersR.Error;

            return handler(numbersR.Value);
        }

        EvalResult<CountedResult> WithPreparedSuffixArgs(
            Func<IReadOnlyList<PreparedSequenceBuiltinSuffixArg>, EvalResult<CountedResult>> handler)
            => handler(bound.SuffixArgs);

        return builtin switch
        {
            BuiltinId.@filter => WithPreparedSuffixArgs(
                    preparedSuffixArgs =>
                    {
                        var predicateR = ExpectPreparedAlgorithmSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            0);
                        if (predicateR.IsError) return predicateR.Error;

                        return EvalFilterCounted(bound.IterationItems, predicateR.Value, ctx, valEnv);
                    }),
            BuiltinId.@map => WithPreparedSuffixArgs(
                    preparedSuffixArgs =>
                    {
                        var transformR = ExpectPreparedAlgorithmSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            0);
                        if (transformR.IsError) return transformR.Error;

                        return EvalMapCounted(bound.IterationItems, transformR.Value, ctx, valEnv);
                    }),
            BuiltinId.@order => WithPreparedNumericItems(EvalOrderCounted),
            BuiltinId.@orderDesc => WithPreparedNumericItems(EvalOrderDescCounted),
            BuiltinId.@count => WithPreparedFlatItems(EvalCountCounted),
            BuiltinId.@contains => WithPreparedSuffixArgs(
                    preparedSuffixArgs =>
                    {
                        var searchedItemR = ExpectPreparedValueSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            0);
                        if (searchedItemR.IsError) return searchedItemR.Error;

                        return WithPreparedFlatItems(items => EvalContainsCounted(items, searchedItemR.Value));
                    }),
            BuiltinId.@distinct => WithPreparedFlatItems(EvalDistinctCounted),
            BuiltinId.@first => WithPreparedFlatItems(EvalFirstCounted),
            BuiltinId.@last => WithPreparedFlatItems(EvalLastCounted),
            BuiltinId.@take => WithPreparedSuffixArgs(
                    preparedSuffixArgs =>
                    {
                        var countR = ExpectPreparedWholeNumberSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            0);
                        if (countR.IsError) return countR.Error;

                        return WithPreparedFlatItems(items => EvalTakeCounted(items, countR.Value));
                    }),
            BuiltinId.@skip => WithPreparedSuffixArgs(
                    preparedSuffixArgs =>
                    {
                        var countR = ExpectPreparedWholeNumberSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            0);
                        if (countR.IsError) return countR.Error;

                        return WithPreparedFlatItems(items => EvalSkipCounted(items, countR.Value));
                    }),
            BuiltinId.@min => WithPreparedNumericItems(EvalMinCounted),
            BuiltinId.@max => WithPreparedNumericItems(EvalMaxCounted),
            BuiltinId.@sum => WithPreparedNumericItems(EvalSumCounted),
            BuiltinId.@avg => WithPreparedNumericItems(EvalAvgCounted),
            BuiltinId.@reduce => WithPreparedSuffixArgs(
                    preparedSuffixArgs =>
                    {
                        var stepR = ExpectPreparedAlgorithmSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            0);
                        if (stepR.IsError) return stepR.Error;

                        var initialR = ExpectPreparedAlgorithmSuffixArg(
                            builtin,
                            metadata.SuffixArgs,
                            preparedSuffixArgs,
                            1);
                        if (initialR.IsError) return initialR.Error;

                        return EvalReduceCounted(bound.IterationItems, stepR.Value, initialR.Value, ctx, valEnv);
                    }),
            _ => WrongBuiltinArity(builtin, args.Count),
        };
    }

    /// <summary>
    /// Builtin application with counted output shape.
    /// Used by <c>reduce</c> so step validation can distinguish grouped
    /// accumulator values from multiple top-level outputs.
    /// Lean: <c>applyBuiltinCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> ApplyBuiltinCounted(
        BuiltinId builtin,
        IReadOnlyList<Algorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => ApplyBuiltinCountedResolved(builtin, WithoutSequenceSupply(args), ctx, valEnv);

    private static EvalResult<IReadOnlyList<Algorithm>> ExpandSequenceSuppliedBuiltinArguments(
        IReadOnlyList<ResolvedArgumentAlgorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var expanded = new List<Algorithm>(args.Count);
        foreach (var arg in args)
        {
            if (!arg.SuppliesSequence)
            {
                expanded.Add(arg.Algorithm);
                continue;
            }

            var outputR = EvalAlgOutputCounted(arg.Algorithm, ctx, valEnv);
            if (outputR.IsError) return outputR.Error;

            foreach (var value in CountedTopLevelValues(outputR.Value))
                expanded.Add(CountedArgAlgorithm(new CountedResult(value, 1)));
        }

        return EvalResult<IReadOnlyList<Algorithm>>.Ok(expanded);
    }

    private static EvalResult<CountedResult> ApplyBuiltinCountedResolved(
        BuiltinId builtin,
        IReadOnlyList<ResolvedArgumentAlgorithm> resolvedArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (GetSequenceBuiltinMetadata(builtin) is { } metadata)
            return ApplyBuiltinCountedSequence(builtin, metadata, resolvedArgs, ctx, valEnv);

        var expandedArgsR = ExpandSequenceSuppliedBuiltinArguments(resolvedArgs, ctx, valEnv);
        if (expandedArgsR.IsError) return expandedArgsR.Error;
        var args = expandedArgsR.Value;

        switch (builtin, args.Count)
        {
            case (BuiltinId.@empty, _):
                return EmptyBuiltinCallSyntaxError();

            case (BuiltinId.@if, 3):
            {
                var condR = EvalAlgOutput(args[0], ctx, valEnv);
                if (condR.IsError) return condR.Error;
                var truth = condR.Value.TruthValue();
                if (truth is null) return new EvalError.BadArity();
                return truth.Value
                    ? EvalAlgOutputCounted(args[1], ctx, valEnv)
                    : EvalAlgOutputCounted(args[2], ctx, valEnv);
            }

            case (BuiltinId.@while, _) when args.Count >= 2:
            {
                var initialStateR = EvalInitialLoopStateSlots(args.Skip(1).ToList(), ctx, valEnv);
                if (initialStateR.IsError) return initialStateR.Error;
                return WhileLoopCounted(args[0], initialStateR.Value, ctx, valEnv);
            }

            case (BuiltinId.@repeat, _) when args.Count >= 3:
            {
                var countR = EvalAlgOutput(args[1], ctx, valEnv);
                if (countR.IsError) return countR.Error;
                var nR = ExpectWholeInt(countR.Value, "Repeat count");
                if (nR.IsError) return nR.Error;
                var n = (long)nR.Value;
                if (n < 0) return new EvalError.IllegalInEval("Repeat count must be >= 0");

                var initialStateR = EvalInitialLoopStateSlots(args.Skip(2).ToList(), ctx, valEnv);
                if (initialStateR.IsError) return initialStateR.Error;
                return RepeatLoopCounted(args[0], n, initialStateR.Value, ctx, valEnv);
            }

            case (BuiltinId.@atoms, 1):
            {
                var atomsR = EvalAlgOutput(args[0], ctx, valEnv);
                if (atomsR.IsError) return atomsR.Error;
                var atoms = atomsR.Value.ToAtoms();
                var value = Result.FromItems(atoms.Select(n => new Result.Atom(n)));
                return EvalResult<CountedResult>.Ok(new CountedResult(value, atoms.Count));
            }

            case (BuiltinId.@content, 1):
            {
                var valueR = EvalAlgOutput(args[0], ctx, valEnv);
                if (valueR.IsError) return valueR.Error;
                var items = valueR.Value.ToItems();
                var value = Result.FromItems(items);
                return EvalResult<CountedResult>.Ok(new CountedResult(value, items.Count));
            }

            case (BuiltinId.@range, 2):
            {
                var rangeR = EvalBuiltinRangeArguments(args, ctx, valEnv);
                if (rangeR.IsError) return rangeR.Error;

                var value = BuildInclusiveRange(rangeR.Value);
                return EvalResult<CountedResult>.Ok(new CountedResult(value, value.ToAtoms().Count));
            }

            default:
                return WrongBuiltinArity(builtin, args.Count);
        }
    }

    // ── Built-in prelude ────────────────────────────────────────────────────

    private static readonly Algorithm.User MathAlgorithm = BuiltinRegistry.CreateMathAlgorithm(MathAlgorithmFlavor.Runtime);

    /// <summary>
    /// Prelude algorithm providing builtin operations in scope by default.
    /// Lean: preludeAlg. Builtins are injected into the initial call stack.
    /// All builtins and Math are public for use in opened contexts.
    /// </summary>
    private static readonly Algorithm.User PreludeAlg = BuiltinRegistry.CreateRuntimePreludeAlgorithm(MathAlgorithm);

    private static SequenceBuiltinMetadata? GetSequenceBuiltinMetadata(BuiltinId builtin)
        => BuiltinRegistry.TryGetSequenceMetadata(builtin, out var metadata) ? metadata : null;

    private static string BuiltinDisplayName(BuiltinId builtin)
        => BuiltinRegistry.GetBuiltin(builtin).Name;

    /// <summary>Lean: builtinAcceptsArity. Fixed-arity builtins stay exact; sequence builtins use callable signatures with <c>values...</c>.</summary>
    private static bool BuiltinAcceptsArity(BuiltinId builtin, int argumentCount)
        => BuiltinRegistry.GetBuiltin(builtin).AcceptsArity(argumentCount);

    /// <summary>Lean: builtinArityDesc. Human-readable expected arity for error messages.</summary>
    private static string BuiltinArityDesc(BuiltinId builtin)
        => BuiltinRegistry.GetBuiltin(builtin).DescribeArity();

    private static EvalError WrongBuiltinArity(BuiltinId builtin, int actualCount)
    {
        var descriptor = BuiltinRegistry.GetBuiltin(builtin);
        var expected = builtin == BuiltinId.@if ? descriptor.FixedArity ?? 0 : 0;

        return new EvalError.ArityMismatch(expected, actualCount)
        {
            Signature = descriptor.PlainSignature,
        };
    }

    // ── Dot-call helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Lean: resultToString. Convert a numeric Result to its canonical string representation.
    /// Only atomic numeric values are supported; other forms raise typeMismatch.
    /// Canonical representation: culture-invariant decimal string.
    /// Examples: 123 → "123", -5 → "-5", 0 → "0", 1.20 → "1.20".
    /// </summary>
    private static EvalResult<Result> ResultToString(Result r)
    {
        if (r is Result.Atom(var n))
            return EvalResult<Result>.Ok(new Result.Str(n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new EvalError.TypeMismatch("builtin property `string` expects a numeric receiver");
    }

    // ── Open resolution ───────────────────────────────────────────────────

    /// <summary>
    /// Algorithm resolution using only direct lexical lookup (no opens).
    /// Used for resolving open expressions to avoid circularity.
    /// Does not rebind opened modules into the opener scope.
    /// Resolved lexical targets still keep their definition-site parent chain.
    /// Only <c>Expr.openForm?</c> forms are permitted
    /// (structural references to libraries only).
    /// Builtins are rejected: they are not valid open targets.
    /// <para>
    /// Visibility rule: <c>open</c> never requires the opened algorithm itself to be public.
    /// It only requires the algorithm to be available (resolvable) in the current context.
    /// <c>open</c> imports only public members of that algorithm (enforced by <see cref="LookupOpens"/>).
    /// </para>
    /// Property access in open paths (<c>open A.B</c>) still requires intermediate
    /// properties to be public (normal dot-access visibility).
    /// Lean: resolveAlgForOpen → EvalM Algorithm.
    /// </summary>
    private static EvalResult<Algorithm> ResolveAlgForOpen(Expr expr, EvalCtx ctx)
    {
        switch (expr)
        {
            case Expr.SequenceSupply(var e1, var e2):
            {
                _ = e1;
                _ = e2;
                return new EvalError.BadOpenForm("sequence supply expressions cannot be opened") { Span = expr.Span };
            }

            case Expr.Block(var alg):
                return EvalResult<Algorithm>.Ok(alg); // no wiring for opens

            case Expr.Resolve(var name):
            {
                // open never requires the opened algorithm itself to be public.
                // It only requires the algorithm to be available in the current context.
                // open imports only public members (enforced later by LookupOpens).
                if (ctx.CallStack.Count > 0)
                {
                    var found = LookupLexicalDirect(ctx.CallStack[0], name);
                    if (found is not null)
                        return found is Algorithm.Builtin
                            ? new EvalError.IllegalInOpen($"builtin '{name}'") { Span = expr.Span }
                            : EvalResult<Algorithm>.Ok(found);
                }
                return new EvalError.UnknownName(name) { Span = expr.Span };
            }

            case Expr.DotCall(var target, var propName, null):
                return WithSpan(expr.Span, ResolveOpenPropAccess(target, propName, ctx));

            default:
                // Not an open form — reject with informative error
                return new EvalError.BadOpenForm($"{ExprKind(expr)}: {OpenExprName(expr)}") { Span = expr.Span };
        }
    }

    /// <summary>
    /// Shared logic for resolving property access in open expressions.
    /// Used by DotCall(target, name, null) in ResolveAlgForOpen.
    /// </summary>
    private static EvalResult<Algorithm> ResolveOpenPropAccess(
        Expr target, string propName, EvalCtx ctx)
    {
        var targetResult = ResolveAlgForOpen(target, ctx);
        if (targetResult.IsError) return targetResult.Error;

        // First check if property exists at all so ownership still wins over opens.
        var prop = LookupPropBinding(targetResult.Value, propName);
        if (prop is not null)
        {
            if (prop.Value is Algorithm.Builtin)
                return new EvalError.IllegalInOpen(
                    $"builtin not allowed in open: {OpenExprName(target)}.{propName}");

            if (!IsExported(prop))
                return new EvalError.LocalOnlyProperty(OpenExprName(target), propName, prop.Exposure);

            // Property exists; check if it's public. Keep the property bound to
            // the resolved target so open A.B preserves definition-site scope.
            if (prop.IsPublic)
                return EvalResult<Algorithm>.Ok(ChildOf(targetResult.Value, prop.Value));

            return new EvalError.NotPublicProperty(OpenExprName(target), propName);
        }
        if (ConditionalBranchesDefineProperty(targetResult.Value, propName))
            return new EvalError.LocalOnlyProperty(OpenExprName(target), propName, PropertyExposure.LocalOnlyConditionalAlgorithm);

        return new EvalError.UnknownProperty(OpenExprName(target), propName);
    }

    // ── Algorithm resolution (full — with opens) ─────────────────────────────

    /// <summary>Lean: resolveAlg → EvalM Algorithm.</summary>
    private static EvalResult<Algorithm> ResolveAlg(Expr expr, EvalCtx ctx)
    {
        switch (expr)
        {
            case Expr.SequenceSupply(var e1, var e2):
            {
                _ = e1;
                _ = e2;
                return new EvalError.NotAnAlgorithm("sequence supply expression") { Span = expr.Span };
            }

            case Expr.Block(var alg):
                return EvalResult<Algorithm>.Ok(WireToCaller(ctx, alg));

            case Expr.Resolve(var name):
                return ResolveNamedAlgorithm(name, expr.Span, ctx);

            case Expr.DotCall:
            {
                // Lean: resolveAlg (.dotCall o n args) — lift to wrapper algorithm;
                // evalDotCall handles all semantics (builtin property special cases, structural lookup, lexical fallback)
                var wrapper = new Algorithm.User(
                    Parent: null, Parameters: [], Opens: [],
                    Properties: [], Output: [expr]);
                return EvalResult<Algorithm>.Ok(WireToCaller(ctx, wrapper));
            }

            // Algorithm resolution for parameters (Lean: resolveAlg Param(x)):
            // Check AlgEnv first — if x is bound to an algorithm, return it.
            // Otherwise NotAnAlgorithm (parameters are not structurally algorithms).
            case Expr.Param(var x):
            {
                var algBound = LookupAlg(ctx.AlgEnv, x);
                if (algBound is not null)
                    return EvalResult<Algorithm>.Ok(algBound);
                return new EvalError.NotAnAlgorithm($"param({x})") { Span = expr.Span };
            }
            case Expr.Num(var n):
                return new EvalError.NotAnAlgorithm($"num({n})") { Span = expr.Span };
            case Expr.Unary:
                return new EvalError.NotAnAlgorithm("unary expression") { Span = expr.Span };
            case Expr.Binary:
                return new EvalError.NotAnAlgorithm("binary expression") { Span = expr.Span };
            case Expr.Index:
                return new EvalError.NotAnAlgorithm("index expression") { Span = expr.Span };
            case Expr.Call:
                return new EvalError.NotAnAlgorithm("call expression") { Span = expr.Span };
            case Expr.NativeCall:
                return new EvalError.NotAnAlgorithm("native call") { Span = expr.Span };
            case Expr.Grace:
                return new EvalError.NotAnAlgorithm("grace expression") { Span = expr.Span };
            case Expr.StringLiteral:
                return new EvalError.NotAnAlgorithm("string literal") { Span = expr.Span };

            default:
                throw new InvalidOperationException($"Unhandled Expr type in ResolveAlg: {expr.GetType().Name}");
        }
    }

    // ── Algorithm output evaluation ─────────────────────────────────────────

    /// <summary>
    /// Evaluate an algorithm's output expressions and collect into a single Result.
    /// Normalization invariant: outputs are always normalized at algorithm boundaries.
    /// User-defined algorithms may exist structurally without output, but forcing
    /// them in value position raises <see cref="EvalError.MissingOutput"/>.
    /// Lean: evalAlgOutput → EvalM Result.
    /// </summary>
    private static EvalResult<Result> EvalAlgOutputCore(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var countedR = EvalAlgOutputCountedCore(alg, ctx, valEnv);
        return countedR.IsError
            ? countedR.Error
            : EvalResult<Result>.Ok(countedR.Value.Value);
    }

    private static EvalResult<Result> EvalAlgOutput(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalAlgOutputCore(alg, ctx, valEnv);

    private static EvalResult<Result> EvalProgramOutput(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => EvalAlgOutputCore(alg, ctx, valEnv);

    private static Result LoopStateResult(IReadOnlyList<Result> stateSlots)
        => Result.FromItems(stateSlots);

    private static EvalResult<IReadOnlyList<Result>> EvalInitialLoopStateSlots(
        IReadOnlyList<Algorithm> initArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        // Initial loop state preserves explicit argument boundaries: repeat(Step, 3, a, b)
        // starts with two slots, while repeat(Step, 3, Pair) starts with one slot even
        // when Pair evaluates to multiple values. Step outputs define later state slots;
        // group a step result to keep one structured slot across iterations.
        var stateSlots = new List<Result>(initArgs.Count);
        foreach (var init in initArgs)
        {
            var slotR = EvalAlgOutput(init, ctx, valEnv);
            if (slotR.IsError) return slotR.Error;
            stateSlots.Add(slotR.Value);
        }

        return EvalResult<IReadOnlyList<Result>>.Ok(stateSlots);
    }

    private static EvalResult<IReadOnlyList<Result>> EvalAlgOutputSlots(
        Algorithm alg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        bool preserveSequenceSupplyExpressionBoundaries = false)
    {
        if (alg is Algorithm.Builtin(var builtin))
        {
            var countedR = EvalBuiltinValueCounted(builtin);
            return countedR.IsError
                ? countedR.Error
                : EvalResult<IReadOnlyList<Result>>.Ok(CountedTopLevelValues(countedR.Value));
        }

        if (alg.FindDuplicatePropName() is { } duplicateName)
            return new EvalError.DuplicateProperty(duplicateName);

        if (alg is Algorithm.User { Output.Count: 0 })
            return new EvalError.MissingOutput();

        var slots = new List<Result>();
        var pushedCtx = ctx.Push(alg);
        foreach (var expr in alg.Output)
        {
            var countedR = EvalCounted(expr, pushedCtx, valEnv);
            if (countedR.IsError) return countedR.Error;

            if (preserveSequenceSupplyExpressionBoundaries && expr is Expr.SequenceSupply)
            {
                if (countedR.Value.EmittedCount != 0)
                    slots.Add(countedR.Value.Value);
                continue;
            }

            slots.AddRange(CountedTopLevelValues(countedR.Value));
        }

        return EvalResult<IReadOnlyList<Result>>.Ok(slots);
    }

    private static EvalError LoopStateArityMismatch(
        Algorithm step,
        int actualStateValueCount,
        string loopName)
        => new EvalError.WithContext(
            new LoopStateBindingContext(loopName, step.Params.ToList(), actualStateValueCount),
            new EvalError.ArityMismatch(step.Params.Count, actualStateValueCount));

    private static EvalError VariadicLoopStateArityMismatch(
        Algorithm step,
        int expectedMinimumStateValueCount,
        int actualStateValueCount,
        string loopName)
        => new EvalError.WithContext(
            new VariadicLoopStateBindingContext(
                loopName,
                step.Parameters.Select(static parameter => parameter.DisplayName).ToList(),
                expectedMinimumStateValueCount,
                actualStateValueCount),
            new EvalError.ArityMismatch(expectedMinimumStateValueCount, actualStateValueCount));

    private static EvalResult<IReadOnlyList<(string Name, Result Value)>> BindEvaluatedSlotValueBindings(
        FlatVariadicBindingLayout layout,
        IReadOnlyList<(string ParameterName, BindingInputSlot Item)> normalBindings,
        VariadicCapture variadicCapture)
    {
        var valueBindings = new List<(string Name, Result Value)>(layout.Signature.Parameters.Count);
        var normalBindingIndex = 0;

        foreach (var parameter in layout.Signature.Parameters)
        {
            if (parameter.Kind == ParameterKind.Variadic)
            {
                valueBindings.Add((variadicCapture.Name, variadicCapture.Value));
                continue;
            }

            if (normalBindingIndex >= normalBindings.Count)
                return new EvalError.BadArity();

            var binding = normalBindings[normalBindingIndex++];
            if (binding.Item.Value is null)
                return new EvalError.BadArity();

            valueBindings.Add((binding.ParameterName, binding.Item.Value));
        }

        if (normalBindingIndex != normalBindings.Count)
            return new EvalError.BadArity();

        return EvalResult<IReadOnlyList<(string Name, Result Value)>>.Ok(valueBindings);
    }

    private static EvalResult<EvaluatedSlotBindings> BindEvaluatedSlotsToParameters(
        Algorithm algorithm,
        IReadOnlyList<Result> evaluatedSlots,
        string callableName,
        GenericLoopStepBindingSelection bindingSelection,
        Func<int, int, EvalError> fixedArityMismatch,
        Func<int, int, EvalError> variadicArityMismatch)
    {
        // Evaluated slots are already Result values. This helper only applies
        // parameter layout; it does not evaluate argument expressions, unpack a
        // final grouped argument, or apply dot-call receiver boundary rules.
        EvalResult<EvaluatedSlotBindings> BindPatternedSlots()
        {
            var inputs = evaluatedSlots
                .Select(static slot => new ParameterPatternInput(slot, Algorithm: null, ValueError: null, ExplicitGroupItems: null))
                .ToList();
            var bindingsR = BindParameterPatternList(
                algorithm.ParameterPatterns,
                inputs,
                allowAlgorithmBindings: false,
                fixedArityMismatch);
            if (bindingsR.IsError) return bindingsR.Error;

            return EvalResult<EvaluatedSlotBindings>.Ok(new EvaluatedSlotBindings(
                bindingsR.Value.ValueBindings,
                bindingsR.Value.CountedBindings,
                bindingsR.Value.VariadicStreamBindings));
        }

        EvalResult<EvaluatedSlotBindings> BindFlatFixedSlots()
        {
            if (algorithm.Params.Count != evaluatedSlots.Count)
                return fixedArityMismatch(algorithm.Params.Count, evaluatedSlots.Count);

            var boundR = BindParams(algorithm.Params, evaluatedSlots);
            if (boundR.IsError) return boundR.Error;

            return EvalResult<EvaluatedSlotBindings>.Ok(new EvaluatedSlotBindings(boundR.Value, [], []));
        }

        EvalResult<EvaluatedSlotBindings> BindFlatVariadicSlots(FlatVariadicBindingLayout layout)
        {
            var inputSlots = evaluatedSlots
                .Select(BindingInputSlot.FromEvaluatedValue)
                .ToArray();

            var boundItemsR = BindItemsToFlatVariadicLayout(
                layout,
                inputSlots,
                variadicArityMismatch);
            if (boundItemsR.IsError) return boundItemsR.Error;

            var boundItems = boundItemsR.Value;
            var capturedValues = new List<Result>(boundItems.VariadicItems.Count);
            foreach (var item in boundItems.VariadicItems)
            {
                if (item.Value is null)
                    return new EvalError.BadArity();

                capturedValues.Add(item.Value);
            }

            var variadicName = boundItems.VariadicParameterName
                ?? layout.VariadicName;
            if (variadicName is null)
                return new EvalError.BadArity();

            var variadicCapture = CreateVariadicCapture(variadicName, capturedValues);

            var valueBindingsR = BindEvaluatedSlotValueBindings(
                layout,
                boundItems.NormalBindings,
                variadicCapture);
            if (valueBindingsR.IsError) return valueBindingsR.Error;

            return EvalResult<EvaluatedSlotBindings>.Ok(new EvaluatedSlotBindings(
                valueBindingsR.Value,
                [(variadicCapture.Name, variadicCapture.CountedValue)],
                [(variadicCapture.Name, variadicCapture.CountedValue)]));
        }

        EvalResult<EvaluatedSlotBindings> BindLegacyShape()
        {
            if (UsesPatternBinding(algorithm))
                return BindPatternedSlots();

            return TryGetLegacyFlatVariadicBindingLayout(algorithm, callableName, out var legacyLayout)
                ? BindFlatVariadicSlots(legacyLayout)
                : BindFlatFixedSlots();
        }

        EvalResult<EvaluatedSlotBindings> BindSelectedFlatVariadicShape()
        {
            return bindingSelection.Plan is not null
                && TryGetFlatVariadicBindingLayout(bindingSelection.Plan, out var layout)
                ? BindFlatVariadicSlots(layout)
                : BindLegacyShape();
        }

        return bindingSelection.Shape switch
        {
            GenericLoopStepBindingShape.Patterned => BindPatternedSlots(),
            GenericLoopStepBindingShape.FlatFixed => BindFlatFixedSlots(),
            GenericLoopStepBindingShape.FlatVariadic => BindSelectedFlatVariadicShape(),
            _ => BindLegacyShape(),
        };
    }

    private static EvalResult<EvaluatedSlotBindings> BindLoopStepState(
        Algorithm step,
        IReadOnlyList<Result> stateSlots,
        string loopName,
        GenericLoopStepBindingSelection bindingSelection)
    {
        // Loop state slots are produced by initial loop arguments or previous
        // step output. They are already evaluated and must not use ordinary
        // call-site behavior such as sequence-supply slot expansion.
        return BindEvaluatedSlotsToParameters(
            step,
            stateSlots,
            "loop step",
            bindingSelection,
            (_, actual) => LoopStateArityMismatch(step, actual, loopName),
            (required, actual) => VariadicLoopStateArityMismatch(step, required, actual, loopName));
    }

    internal static EvalResult<Result> ApplyBinaryOperator(
        BinaryOp op,
        Expr left,
        Expr right,
        Result leftValue,
        Result rightValue,
        SourceSpan? span)
    {
        var leftEmpty = leftValue is Result.Group(var leftItems) && leftItems.Count == 0;
        var rightEmpty = rightValue is Result.Group(var rightItems) && rightItems.Count == 0;
        if (leftEmpty || rightEmpty)
        {
            if (op == BinaryOp.Eq)
                return EvalResult<Result>.Ok(new Result.Atom(leftEmpty == rightEmpty ? 1 : 0));
            if (op == BinaryOp.Ne)
                return EvalResult<Result>.Ok(new Result.Atom(leftEmpty != rightEmpty ? 1 : 0));
            if (leftEmpty && rightEmpty) return EvalResult<Result>.Ok(new Result.Group([]));
            if (leftEmpty) return EvalResult<Result>.Ok(rightValue);
            return EvalResult<Result>.Ok(leftValue);
        }

        if (leftValue is Result.Str(var leftString) && rightValue is Result.Str(var rightString))
        {
            return op switch
            {
                BinaryOp.Eq => EvalResult<Result>.Ok(new Result.Atom(leftString == rightString ? 1 : 0)),
                BinaryOp.Ne => EvalResult<Result>.Ok(new Result.Atom(leftString != rightString ? 1 : 0)),
                _ => new EvalError.TypeMismatch("Strings only support == and != operators") { Span = span },
            };
        }

        if (leftValue is Result.Str || rightValue is Result.Str)
            return new EvalError.TypeMismatch("Cannot apply operator to string and non-string operands") { Span = span };

        var binaryContext = $"while evaluating `{BinaryExprDiagnosticName(op, left, right)}`";
        var xR = RequireNumericScalarOperand(op, "left", leftValue);
        if (xR.IsError) return new EvalError.WithContext(binaryContext, xR.Error) { Span = span };
        var yR = RequireNumericScalarOperand(op, "right", rightValue);
        if (yR.IsError) return new EvalError.WithContext(binaryContext, yR.Error) { Span = span };
        decimal x = xR.Value, y = yR.Value;
        if ((op is BinaryOp.Div or BinaryOp.IDiv or BinaryOp.Mod) && y == 0)
            return new EvalError.DivByZero() { Span = span };

        if (op == BinaryOp.Pow)
            return EvalPow(span, x, y);

        decimal result;
        try
        {
            result = op switch
            {
                BinaryOp.Add => x + y,
                BinaryOp.Sub => x - y,
                BinaryOp.Mul => x * y,
                BinaryOp.Div => x / y,
                BinaryOp.IDiv => Math.Truncate(x / y),
                BinaryOp.Mod => x % y,
                BinaryOp.Lt => x < y ? 1 : 0,
                BinaryOp.Gt => x > y ? 1 : 0,
                BinaryOp.Le => x <= y ? 1 : 0,
                BinaryOp.Ge => x >= y ? 1 : 0,
                BinaryOp.Eq => x == y ? 1 : 0,
                BinaryOp.Ne => x != y ? 1 : 0,
                BinaryOp.And => x != 0 && y != 0 ? 1 : 0,
                BinaryOp.Or => x != 0 || y != 0 ? 1 : 0,
                BinaryOp.Xor => (x != 0) != (y != 0) ? 1 : 0,
                _ => 0,
            };
        }
        catch (OverflowException)
        {
            return new EvalError.NumericOverflow() { Span = span };
        }

        return EvalResult<Result>.Ok(new Result.Atom(result));
    }

    /// <summary>Evaluate an expression and coerce to decimal. Lean: evalInt.</summary>
    private static EvalResult<decimal> EvalInt(
        Expr expr, EvalCtx ctx, IReadOnlyList<(string, Result)> valEnv)
    {
        var r = Eval(expr, ctx, valEnv);
        if (r.IsError) return r.Error;
        return ExpectInt(r.Value);
    }

    private static EvalResult<IReadOnlyList<Result>> RunStepSlots(
        Algorithm step,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        IReadOnlyList<Result> stateSlots,
        string loopName)
    {
        var bindingSelection = SelectGenericLoopStepBinding(step);
        var boundR = BindLoopStepState(step, stateSlots, loopName, bindingSelection);
        if (boundR.IsError) return boundR.Error;

        var shadowedCountedParamEnv = ShadowCountedParamEnv(ctx.CountedParamEnv, step.Params);
        var shadowedStreamEnv = ShadowCountedParamEnv(ctx.VariadicStreamEnv, step.Params);
        var stepCtx = ctx
            .WithCountedParamEnv(Concat(boundR.Value.CountedBindings, shadowedCountedParamEnv))
            .WithVariadicStreamEnv(Concat(boundR.Value.VariadicStreamBindings, shadowedStreamEnv));
        return EvalAlgOutputSlots(
            step,
            stepCtx,
            Concat(boundR.Value.ValueBindings, valEnv),
            preserveSequenceSupplyExpressionBoundaries: ShouldPreserveLoopStepSequenceSupplyExpressionBoundaries(step, bindingSelection));
    }

    /// <summary>Run a step algorithm with the given state bound to its params. Lean: runStep.</summary>
    private static EvalResult<Result> RunStep(
        Algorithm step, EvalCtx ctx, IReadOnlyList<(string, Result)> valEnv, Result state, string loopName)
    {
        var outputSlotsR = RunStepSlots(step, ctx, valEnv, UnpackArgs(state), loopName);
        return outputSlotsR.IsError
            ? outputSlotsR.Error
            : EvalResult<Result>.Ok(LoopStateResult(outputSlotsR.Value));
    }

    private static EvalResult<(IReadOnlyList<Result> NextStateSlots, decimal Continue)> SplitContSlots(
        IReadOnlyList<Result> outputSlots)
    {
        if (outputSlots.Count == 0)
            return new EvalError.BadArity();

        if (outputSlots.Count == 1)
        {
            if (outputSlots[0] is Result.Atom(var number))
                return EvalResult<(IReadOnlyList<Result>, decimal)>.Ok((outputSlots, number));

            return new EvalError.BadArity();
        }

        var contR = ExpectInt(outputSlots[^1]);
        if (contR.IsError) return contR.Error;
        return EvalResult<(IReadOnlyList<Result>, decimal)>.Ok((outputSlots.Take(outputSlots.Count - 1).ToList(), contR.Value));
    }

    // ── Builtins ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a builtin operation to lazily-resolved argument algorithms.
    /// Lean: applyBuiltin → EvalM Result.
    /// </summary>
    private static EvalResult<Result> ApplyBuiltin(
        BuiltinId builtin,
        IReadOnlyList<Algorithm> args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => ApplyBuiltinResolved(builtin, WithoutSequenceSupply(args), ctx, valEnv);

    private static EvalResult<Result> ApplyBuiltinResolved(
        BuiltinId builtin,
        IReadOnlyList<ResolvedArgumentAlgorithm> resolvedArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (GetSequenceBuiltinMetadata(builtin) is { } metadata)
        {
            var countedR = ApplyBuiltinCountedSequence(builtin, metadata, resolvedArgs, ctx, valEnv);
            if (countedR.IsError) return countedR.Error;
            return EvalResult<Result>.Ok(countedR.Value.Value);
        }

        var expandedArgsR = ExpandSequenceSuppliedBuiltinArguments(resolvedArgs, ctx, valEnv);
        if (expandedArgsR.IsError) return expandedArgsR.Error;
        var args = expandedArgsR.Value;

        switch (builtin, args.Count)
        {
            case (BuiltinId.@empty, _):
                return EmptyBuiltinCallSyntaxError();

            // if(cond, thenBranch, elseBranch): standard 3-arg conditional.
            case (BuiltinId.@if, 3):
            {
                var condR = EvalAlgOutput(args[0], ctx, valEnv);
                if (condR.IsError) return condR.Error;
                var truth = condR.Value.TruthValue();
                if (truth is null) return new EvalError.BadArity();
                return truth.Value
                    ? EvalAlgOutput(args[1], ctx, valEnv)
                    : EvalAlgOutput(args[2], ctx, valEnv);
            }

            // while(step, init...)
            case (BuiltinId.@while, _) when args.Count >= 2:
            {
                var initialStateR = EvalInitialLoopStateSlots(args.Skip(1).ToList(), ctx, valEnv);
                if (initialStateR.IsError) return initialStateR.Error;
                return WhileLoop(args[0], initialStateR.Value, ctx, valEnv);
            }

            // repeat(step, count, init...)
            case (BuiltinId.@repeat, _) when args.Count >= 3:
            {
                var countR = EvalAlgOutput(args[1], ctx, valEnv);
                if (countR.IsError) return countR.Error;
                var nR = ExpectWholeInt(countR.Value, "Repeat count");
                if (nR.IsError) return nR.Error;
                var n = (long)nR.Value;
                if (n < 0) return new EvalError.IllegalInEval("Repeat count must be >= 0");
                var initialStateR = EvalInitialLoopStateSlots(args.Skip(2).ToList(), ctx, valEnv);
                if (initialStateR.IsError) return initialStateR.Error;
                return RepeatLoop(args[0], n, initialStateR.Value, ctx, valEnv);
            }

            // atoms(alg) — flatten to atoms
            case (BuiltinId.@atoms, 1):
            {
                var atomsR = EvalAlgOutput(args[0], ctx, valEnv);
                if (atomsR.IsError) return atomsR.Error;
                var atoms = atomsR.Value.ToAtoms();
                return EvalResult<Result>.Ok(
                    Result.FromItems(atoms.Select(n => new Result.Atom(n))));
            }

            case (BuiltinId.@content, 1):
            {
                var valueR = EvalAlgOutput(args[0], ctx, valEnv);
                if (valueR.IsError) return valueR.Error;
                return EvalResult<Result>.Ok(Result.FromItems(valueR.Value.ToItems()));
            }

            // range(start, stop) — inclusive integer sequence, ascending or descending.
            case (BuiltinId.@range, 2):
            {
                var rangeR = EvalBuiltinRangeArguments(args, ctx, valEnv);
                if (rangeR.IsError) return rangeR.Error;

                return EvalResult<Result>.Ok(BuildInclusiveRange(rangeR.Value));
            }

            default:
            {
                return WrongBuiltinArity(builtin, args.Count);
            }
        }
    }

    private static CountedResult CountedLoopStateResult(IReadOnlyList<Result> stateSlots)
        => new(LoopStateResult(stateSlots), stateSlots.Count);

    /// <summary>Lean: While loop → EvalM Result.</summary>
    private static EvalResult<Result> WhileLoop(
        Algorithm step,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var countedR = WhileLoopCounted(step, initialStateSlots, ctx, valEnv);
        return countedR.IsError
            ? countedR.Error
            : EvalResult<Result>.Ok(countedR.Value.Value);
    }

    private static EvalResult<CountedResult> WhileLoopCounted(
        Algorithm step,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        ctx.LoopDiagnostics?.RecordLoopExecution();

        if (!ctx.EnableLoopOptimization)
        {
            ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop optimization disabled");
            return WhileLoopGenericCounted(step, initialStateSlots, ctx, valEnv);
        }

        if (!IsOptimizedLoopShapeEligible(step, out var fallbackReason))
        {
            ctx.LoopDiagnostics?.RecordOptimizedLoopFallback(fallbackReason!);
            return WhileLoopGenericCounted(step, initialStateSlots, ctx, valEnv);
        }

        if (initialStateSlots.Any(static slot => slot is not Result.Atom))
        {
            ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("non-scalar loop state slot");
            return WhileLoopGenericCounted(step, initialStateSlots, ctx, valEnv);
        }

        if (step.Params.Count != initialStateSlots.Count)
            return LoopStateArityMismatch(step, initialStateSlots.Count, "while");

        return LoopOptimizer.TryEvaluateWhile(
            step,
            initialStateSlots,
            ctx,
            valEnv,
            fallbackState => WhileLoopGenericCounted(step, UnpackArgs(fallbackState), ctx, valEnv),
            out var optimizedResult)
            ? optimizedResult
            : WhileLoopGenericCounted(step, initialStateSlots, ctx, valEnv);
    }

    private static EvalResult<Result> WhileLoopGeneric(
        Algorithm step,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var countedR = WhileLoopGenericCounted(step, initialStateSlots, ctx, valEnv);
        return countedR.IsError
            ? countedR.Error
            : EvalResult<Result>.Ok(countedR.Value.Value);
    }

    private static EvalResult<CountedResult> WhileLoopGenericCounted(
        Algorithm step,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var stateSlots = initialStateSlots.ToList();
        while (true)
        {
            var outputSlotsR = RunStepSlots(step, ctx, valEnv, stateSlots, "while");
            if (outputSlotsR.IsError) return outputSlotsR.Error;
            var splitR = SplitContSlots(outputSlotsR.Value);
            if (splitR.IsError) return splitR.Error;
            var (nextStateSlots, cont) = splitR.Value;
            if (cont == 0) return EvalResult<CountedResult>.Ok(CountedLoopStateResult(stateSlots));
            stateSlots = nextStateSlots.ToList();
        }
    }

    /// <summary>Lean: Repeat loop → EvalM Result.</summary>
    private static EvalResult<Result> RepeatLoop(
        Algorithm step,
        long count,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var countedR = RepeatLoopCounted(step, count, initialStateSlots, ctx, valEnv);
        return countedR.IsError
            ? countedR.Error
            : EvalResult<Result>.Ok(countedR.Value.Value);
    }

    private static EvalResult<CountedResult> RepeatLoopCounted(
        Algorithm step,
        long count,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        ctx.LoopDiagnostics?.RecordLoopExecution();

        if (count == 0)
            return EvalResult<CountedResult>.Ok(CountedLoopStateResult(initialStateSlots));

        if (!ctx.EnableLoopOptimization)
        {
            ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop optimization disabled");
            return RepeatLoopGenericCounted(step, count, initialStateSlots, ctx, valEnv);
        }

        if (!IsOptimizedLoopShapeEligible(step, out var fallbackReason))
        {
            ctx.LoopDiagnostics?.RecordOptimizedLoopFallback(fallbackReason!);
            return RepeatLoopGenericCounted(step, count, initialStateSlots, ctx, valEnv);
        }

        if (initialStateSlots.Any(static slot => slot is not Result.Atom))
        {
            ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("non-scalar loop state slot");
            return RepeatLoopGenericCounted(step, count, initialStateSlots, ctx, valEnv);
        }

        if (step.Params.Count != initialStateSlots.Count)
            return LoopStateArityMismatch(step, initialStateSlots.Count, "repeat");

        return LoopOptimizer.TryEvaluateRepeat(
            step,
            count,
            initialStateSlots,
            ctx,
            valEnv,
            (remainingCount, fallbackState) => RepeatLoopGenericCounted(step, remainingCount, UnpackArgs(fallbackState), ctx, valEnv),
            out var optimizedResult)
            ? optimizedResult
            : RepeatLoopGenericCounted(step, count, initialStateSlots, ctx, valEnv);
    }

    private static EvalResult<Result> RepeatLoopGeneric(
        Algorithm step,
        long count,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var countedR = RepeatLoopGenericCounted(step, count, initialStateSlots, ctx, valEnv);
        return countedR.IsError
            ? countedR.Error
            : EvalResult<Result>.Ok(countedR.Value.Value);
    }

    private static EvalResult<CountedResult> RepeatLoopGenericCounted(
        Algorithm step,
        long count,
        IReadOnlyList<Result> initialStateSlots,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var stateSlots = initialStateSlots.ToList();
        for (var k = 0; k < count; k++)
        {
            var outputSlotsR = RunStepSlots(step, ctx, valEnv, stateSlots, "repeat");
            if (outputSlotsR.IsError) return outputSlotsR.Error;
            stateSlots = outputSlotsR.Value.ToList();
        }
        return EvalResult<CountedResult>.Ok(CountedLoopStateResult(stateSlots));
    }

    // ── Main eval ───────────────────────────────────────────────────────────

    /// <summary>Lean: eval → EvalM Result.</summary>
    private static EvalResult<Result> Eval(
        Expr expr,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        switch (expr)
        {
            case Expr.Num(var n):
                return EvalResult<Result>.Ok(new Result.Atom(n));

            case Expr.StringLiteral(var s):
                return EvalResult<Result>.Ok(new Result.Str(s));

            case Expr.Param(var name):
            {
                // Dual-view parameter evaluation (Lean: eval Param(x)):
                // 1. Counted callback-param env (projected higher-order item meaning)
                // 2. ValEnv (ordinary value meaning)
                // 3. AlgEnv fallback (algorithm meaning):
                //    - 0-param algorithm → auto-evaluate (thunk semantics)
                //    - multi-param algorithm → arityMismatch (needs explicit call)
                var counted = LookupCountedParam(ctx.CountedParamEnv, name);
                if (counted is not null) return EvalResult<Result>.Ok(counted.Value.Value);

                var val = LookupVal(valEnv, name);
                if (val is not null) return EvalResult<Result>.Ok(val);
                var algBound = LookupAlg(ctx.AlgEnv, name);
                if (algBound is not null)
                {
                    if (algBound.Params.Count == 0)
                        return WithSpan(expr.Span, EvalAlgOutput(algBound, ctx, valEnv));
                    return new EvalError.ArityMismatch(algBound.Params.Count, 0) { Span = expr.Span };
                }
                return new EvalError.UnknownName(name) { Span = expr.Span };
            }

            case Expr.Unary(var unaryOp, var operand):
            {
                // Empty result propagation through unary operators.
                var operandR = Eval(operand, ctx, valEnv);
                if (operandR.IsError) return operandR.Error;
                if (operandR.Value is Result.Group(var uItems) && uItems.Count == 0)
                    return EvalResult<Result>.Ok(new Result.Group([]));
                if (operandR.Value is Result.Str)
                    return new EvalError.TypeMismatch("Unary operator is not supported for strings") { Span = expr.Span };
                var vR = ExpectInt(operandR.Value);
                if (vR.IsError) return vR.Error;
                var unaryResult = unaryOp switch
                {
                    UnaryOp.Minus => -vR.Value,
                    UnaryOp.Not => vR.Value == 0 ? 1m : 0m,
                    _ => 0m,
                };
                return EvalResult<Result>.Ok(new Result.Atom(unaryResult));
            }

            case Expr.Binary(var op, var left, var right):
            {
                // Evaluate both sides as Result first so empty results can propagate.
                var lR = Eval(left, ctx, valEnv);
                if (lR.IsError) return lR.Error;
                var rR = Eval(right, ctx, valEnv);
                if (rR.IsError) return rR.Error;
                return ApplyBinaryOperator(op, left, right, lR.Value, rR.Value, expr.Span);
            }

            case Expr.SequenceSupply:
            {
                var sequenceSupplyR = EvalSequenceSupplyCounted(expr, ctx, valEnv);
                return sequenceSupplyR.IsError
                    ? sequenceSupplyR.Error
                    : EvalResult<Result>.Ok(sequenceSupplyR.Value.Value);
            }

            case Expr.Block(var alg):
            {
                var wired = WireToCaller(ctx, alg);
                if (wired.Params.Count == 0)
                    return WithSpan(expr.Span ?? FirstSpan(wired.Output), EvalAlgOutput(wired, ctx, valEnv));
                var blockSpan = expr.Span ?? FirstSpan(wired.Output);
                return MissingImplicitArguments<Result>(wired.Params, blockSpan);
            }

            case Expr.Resolve(var name):
            {
                if (ctx.CallStack.Count == 0)
                    return new EvalError.UnknownName(name) { Span = expr.Span };

                var resolvedR = LookupLexical(ctx.CallStack[0], name, ctx);
                if (resolvedR.IsError)
                {
                    var err = resolvedR.Error;
                    return err.Span is null ? err with { Span = expr.Span } : err;
                }

                if (resolvedR.Value.ResolvedAlgorithm.Params.Count != 0)
                {
                    return WithSpan<Result>(
                        expr.Span,
                        new EvalError.WithContext(
                            CtxProperty(name),
                            new EvalError.ArityMismatch(resolvedR.Value.ResolvedAlgorithm.Params.Count, 0)));
                }

                return WithPropertyContextOnMissingOutput(name, expr.Span,
                    EvalZeroArgPropertyAccess(resolvedR.Value, ctx, valEnv));
            }

            case Expr.DotCall(var dotTarget, var dotName, var dotArgs):
                // Lean: eval (.dotCall o n argsOpt) => withCtx (CtxMsg.dotCall o n) do evalDotCall
                return WithSpan(expr.Span, WithCtx(CtxDotCall(dotTarget, dotName),
                    EvalDotCall(dotTarget, dotName, dotArgs, ctx, valEnv)));

            case Expr.Call(var func, var argsAlg):
                return WithSpan(expr.Span,
                    EvalCallExpr(func, argsAlg, ctx, valEnv));

            case Expr.Index(var target, var selector):
            {
                var selectionR = EvalIndexSelectionCounted(target, selector, expr.Span, ctx, valEnv);
                return selectionR.IsError
                    ? selectionR.Error
                    : EvalResult<Result>.Ok(selectionR.Value.Value);
            }

            case Expr.NativeCall(var fnName, var argNames):
                return EvalNativeCall(fnName, argNames, valEnv);

            // Catch-all: uses Expr.kind for clear diagnostics
            default:
                return new EvalError.IllegalInEval(ExprKind(expr)) { Span = expr.Span };
        }
    }

    /// <summary>
    /// Evaluate an expression together with the number of top-level values it
    /// emits at the current algorithm boundary.
    /// Calls and name resolution propagate the callee's emitted output count.
    /// Block expressions count as one grouped value when non-empty. Sequence supply
    /// emits the immediate supplied items from each operand. All other value
    /// expressions emit either zero values (empty result) or one value.
    /// Lean: <c>evalCounted</c>.
    /// </summary>
    internal static EvalResult<CountedResult> EvalCounted(
        Expr expr,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        switch (expr)
        {
            case Expr.Param(var name):
            {
                var counted = LookupCountedParam(ctx.CountedParamEnv, name);
                if (counted is not null)
                    return EvalResult<CountedResult>.Ok(counted.Value);

                var val = LookupVal(valEnv, name);
                if (val is not null)
                    return EvalResult<CountedResult>.Ok(new CountedResult(val, val.ValueCount()));

                var algBound = LookupAlg(ctx.AlgEnv, name);
                if (algBound is not null)
                {
                    if (algBound.Params.Count == 0)
                        return WithSpan(expr.Span, EvalAlgOutputCounted(algBound, ctx, valEnv));
                    return new EvalError.ArityMismatch(algBound.Params.Count, 0) { Span = expr.Span };
                }

                return new EvalError.UnknownName(name) { Span = expr.Span };
            }

            case Expr.SequenceSupply:
                return EvalSequenceSupplyCounted(expr, ctx, valEnv);

            case Expr.Block(var alg):
            {
                var wired = WireToCaller(ctx, alg);
                if (wired.Params.Count == 0)
                {
                    var blockR = WithSpan(expr.Span ?? FirstSpan(wired.Output), EvalAlgOutput(wired, ctx, valEnv));
                    if (blockR.IsError) return blockR.Error;
                    return EvalResult<CountedResult>.Ok(new CountedResult(blockR.Value, blockR.Value.ValueCount()));
                }

                var blockSpan = expr.Span ?? FirstSpan(wired.Output);
                return MissingImplicitArguments<CountedResult>(wired.Params, blockSpan);
            }

            case Expr.Resolve(var name):
            {
                if (ctx.CallStack.Count == 0)
                    return new EvalError.UnknownName(name) { Span = expr.Span };

                var resolvedR = LookupLexical(ctx.CallStack[0], name, ctx);
                if (resolvedR.IsError)
                {
                    var err = resolvedR.Error;
                    return err.Span is null ? err with { Span = expr.Span } : err;
                }

                if (resolvedR.Value.ResolvedAlgorithm.Params.Count != 0)
                {
                    return WithSpan<CountedResult>(
                        expr.Span,
                        new EvalError.WithContext(
                            CtxProperty(name),
                            new EvalError.ArityMismatch(resolvedR.Value.ResolvedAlgorithm.Params.Count, 0)));
                }

                return WithPropertyContextOnMissingOutput(name, expr.Span,
                    EvalZeroArgPropertyAccessCounted(resolvedR.Value, ctx, valEnv));
            }

            case Expr.DotCall(var dotTarget, var dotName, var dotArgs):
                return WithSpan(expr.Span, WithCtx(CtxDotCall(dotTarget, dotName),
                    EvalDotCallCounted(dotTarget, dotName, dotArgs, ctx, valEnv)));

            case Expr.Call(var func, var argsAlg):
                return WithSpan(expr.Span,
                    EvalCallCountedExpr(func, argsAlg, ctx, valEnv));

            case Expr.Index(var target, var selector):
                return WithSpan(expr.Span,
                    EvalIndexSelectionCounted(target, selector, expr.Span, ctx, valEnv));

            default:
            {
                var resultR = Eval(expr, ctx, valEnv);
                if (resultR.IsError) return resultR.Error;
                return EvalResult<CountedResult>.Ok(new CountedResult(resultR.Value, resultR.Value.ValueCount()));
            }
        }
    }

    private static EvalResult<Result> EvalNativeCall(
        string fnName,
        IReadOnlyList<string> argNames,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var args = new decimal[argNames.Count];
        for (var i = 0; i < argNames.Count; i++)
        {
            var val = LookupVal(valEnv, argNames[i]);
            if (val is null) return new EvalError.UnknownName(argNames[i]);
            var num = val.AsNum();
            if (num is null)
                return val is Result.Str
                    ? new EvalError.TypeMismatch("Expected a number, got a string")
                    : new EvalError.BadArity();
            args[i] = num.Value;
        }

        decimal result;
        try
        {
            switch (fnName)
            {
                case "Abs": result = Math.Abs(args[0]); break;
                case "Ceil": result = Math.Ceiling(args[0]); break;
                case "Floor": result = Math.Floor(args[0]); break;
                case "Round": result = Math.Round(args[0]); break;
                case "Sign": result = (decimal)Math.Sign(args[0]); break;
                case "Sqrt": result = NormalizeDoubleResult(Math.Sqrt((double)args[0])); break;
                case "Ln": result = NormalizeDoubleResult(Math.Log((double)args[0])); break;
                case "Lg": result = NormalizeDoubleResult(Math.Log10((double)args[0])); break;
                case "Sin": result = NormalizeDoubleResult(Math.Sin((double)args[0])); break;
                case "Asin": result = NormalizeDoubleResult(Math.Asin((double)args[0])); break;
                case "Cos": result = NormalizeDoubleResult(Math.Cos((double)args[0])); break;
                case "Acos": result = NormalizeDoubleResult(Math.Acos((double)args[0])); break;
                case "Tan": result = NormalizeDoubleResult(Math.Tan((double)args[0])); break;
                case "Atan": result = NormalizeDoubleResult(Math.Atan((double)args[0])); break;
                case "Atan2": result = NormalizeDoubleResult(Math.Atan2((double)args[0], (double)args[1])); break;
                case "Pow": result = NormalizeDoubleResult(Math.Pow((double)args[0], (double)args[1])); break;
                case "Log": result = NormalizeDoubleResult(Math.Log((double)args[0], (double)args[1])); break;
                case "Rand": result = (decimal)Random.Shared.NextDouble(); break;
                case "RandInt": result = (decimal)Random.Shared.Next((int)args[0], (int)args[1] + 1); break;
                default:
                    return new EvalError.IllegalInEval($"unknown native function: {fnName}");
            }
        }
        catch (OverflowException)
        {
            return new EvalError.NumericOverflow();
        }

        return EvalResult<Result>.Ok(new Result.Atom(result));
    }

    /// <summary>
    /// Normalize a double result from a native math function before converting to decimal.
    /// Rounds to 15 significant digits and snaps near-zero values to exactly 0.
    /// This eliminates floating-point residue (e.g. Sin(Pi) ≈ 1.2e-16 → 0).
    /// </summary>
    private static decimal NormalizeDoubleResult(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new OverflowException(); // caught by caller → NumericOverflow

        if (value == 0.0)
            return 0m;

        int digits = 15 - 1 - (int)Math.Floor(Math.Log10(Math.Abs(value)));
        if (digits < 0) digits = 0;
        if (digits > 15) digits = 15;

        var rounded = Math.Round(value, digits);

        if (Math.Abs(rounded) < 1e-15)
            return 0m;

        return (decimal)rounded;
    }

    // ── Resolve argument expressions to algorithms (lazy) ───────────────────

    /// <summary>
    /// Resolve each output expression of args to sub-algorithms.
    /// Lean: resolveArgAlgs — wraps only liftable errors (notAnAlgorithm,
    /// illegalInEval) in trivial algorithms for lazy evaluation via evalAlgOutput.
    /// All other errors (unknownName, unknownProperty, ambiguousOpen, etc.)
    /// are propagated immediately to preserve precise diagnostics.
    /// </summary>
    /// <summary>
    /// Treat simple zero-parameter inline block expressions uniformly as
    /// value/output structures in argument position.
    /// This rule is shared by builtin lazy-argument preparation and higher-order
    /// probing; callability is not inferred from output count, so both
    /// <c>{123}</c> and <c>{1, 2}</c> stay on the value side. Blocks with
    /// parameters, properties, or opens may still resolve as algorithms.
    /// </summary>
    private static bool ShouldWrapArgExprAsValue(Expr expr) => expr switch
    {
        Expr.Block(var algorithm)
            when algorithm.Params.Count == 0
                && algorithm.Opens.Count == 0
                && algorithm.Properties.Count == 0 => true,
        _ => false,
    };

    private static Algorithm WrapArgExprAsValue(Expr expr, EvalCtx ctx)
        => WireToCaller(
            ctx,
            new Algorithm.User(
                Parent: null,
                Parameters: [],
                Opens: [],
                Properties: [],
                Output: [expr]));

    private static bool ShouldWrapBuiltinArgExprAsValue(
        Expr expr,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => ShouldWrapArgExprAsValue(expr)
            || expr is Expr.Param(var name)
                && (LookupCountedParam(ctx.CountedParamEnv, name) is not null
                    || LookupVal(valEnv, name) is not null);

    private static EvalResult<IReadOnlyList<Algorithm>> ResolveArgAlgs(
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var resolvedR = ResolveArgAlgsWithSequenceSupply(argsAlg, ctx, valEnv);
        return resolvedR.IsError
            ? resolvedR.Error
            : EvalResult<IReadOnlyList<Algorithm>>.Ok(resolvedR.Value.Select(static arg => arg.Algorithm).ToList());
    }

    private static EvalResult<IReadOnlyList<ResolvedArgumentAlgorithm>> ResolveArgAlgsWithSequenceSupply(
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var result = new List<ResolvedArgumentAlgorithm>(argsAlg.Output.Count);
        foreach (var argExpr in argsAlg.Output)
        {
            var suppliesSequence = argExpr is Expr.SequenceSupply;
            if (ShouldWrapBuiltinArgExprAsValue(argExpr, ctx, valEnv))
            {
                result.Add(new ResolvedArgumentAlgorithm(WrapArgExprAsValue(argExpr, ctx), suppliesSequence));
                continue;
            }

            var r = ResolveAlg(argExpr, ctx);
            if (r.IsOk)
            {
                result.Add(new ResolvedArgumentAlgorithm(r.Value, suppliesSequence));
            }
            else if (IsLiftableError(r.Error))
            {
                // Wrap liftable non-resolvable expressions in a trivial algorithm.
                // evalAlgOutput will evaluate the expression lazily when needed.
                var wrapper = new Algorithm.User(
                    Parent: null, Parameters: [], Opens: [],
                    Properties: [], Output: [argExpr]);
                result.Add(new ResolvedArgumentAlgorithm(WireToCaller(ctx, wrapper), suppliesSequence));
            }
            else
            {
                // Propagate genuine lookup/semantic failures immediately.
                return r.Error;
            }
        }
        return EvalResult<IReadOnlyList<ResolvedArgumentAlgorithm>>.Ok(result);
    }

    /// <summary>
    /// Errors that indicate an expression simply isn't an algorithm form and can
    /// safely be deferred to lazy evaluation (wrapping in Algorithm.ofExpr).
    /// </summary>
    private static bool IsLiftableError(EvalError error) => error switch
    {
        EvalError.NotAnAlgorithm => true,
        EvalError.IllegalInEval => true,
        EvalError.WithContext(_, var inner) => IsLiftableError(inner),
        _ => false,
    };

    /// <summary>
    /// Try to resolve each argument expression to an algorithm.
    /// Returns Some(alg) for expressions that resolve, null for those that don't.
    /// Simple zero-parameter inline blocks are intentionally treated as
    /// value/output structures here, regardless of whether they emit one value
    /// or many, so higher-order probing never grants them callable AlgEnv
    /// bindings based on output count.
    /// Lean: tryResolveArgAlgs.
    /// </summary>
    private static EvalResult<IReadOnlyList<Algorithm?>> TryResolveArgAlgs(
        Algorithm argsAlg, EvalCtx ctx)
    {
        var result = new List<Algorithm?>(argsAlg.Output.Count);
        foreach (var argExpr in argsAlg.Output)
        {
            if (ShouldWrapArgExprAsValue(argExpr))
            {
                result.Add(null);
                continue;
            }

            var r = ResolveAlg(argExpr, ctx);
            if (r.IsOk)
            {
                result.Add(r.Value);
            }
            else if (IsLiftableError(r.Error))
            {
                result.Add(null);
            }
            else
            {
                return r.Error;
            }
        }
        return EvalResult<IReadOnlyList<Algorithm?>>.Ok(result);
    }

    /// <summary>
    /// Bind algorithm-typed parameters: zip parameter names with algorithms.
    /// Only includes entries where the argument resolved to an algorithm.
    /// Lean: bindAlgParams.
    /// </summary>
    private static IReadOnlyList<(string, Algorithm)> BindAlgParams(
        IReadOnlyList<string> paramNames,
        IReadOnlyList<Algorithm?> algs)
    {
        var result = new List<(string, Algorithm)>();
        var count = Math.Min(paramNames.Count, algs.Count);
        for (var i = 0; i < count; i++)
        {
            if (algs[i] is { } alg)
                result.Add((paramNames[i], alg));
        }
        return result;
    }

    // ── Call evaluation ─────────────────────────────────────────────────────

    /// <summary>
    /// Lean: evalCall → EvalM Result.
    /// 1. Resolve callee.
    /// 2. If builtin: resolve args lazily as algorithms, dispatch to applyBuiltin.
    /// 3. If user-defined: delegate to EvalUserCall (dual-view argument binding).
    /// </summary>
    private static EvalResult<Result> EvalCall(
        Expr func,
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var calleeR = ResolveAlg(func, ctx);
        if (calleeR.IsError) return calleeR.Error;
        return EvalResolvedCall(calleeR.Value, argsAlg, ctx, valEnv, OpenExprName(func));
    }

    /// <summary>
    /// Counted call evaluation for <c>reduce</c> step validation.
    /// Lean: <c>evalCallCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalCallCounted(
        Expr func,
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var calleeR = ResolveAlg(func, ctx);
        if (calleeR.IsError) return calleeR.Error;
        return EvalResolvedCallCounted(calleeR.Value, argsAlg, ctx, valEnv, OpenExprName(func));
    }

    /// <summary>
    /// Context-aware call evaluation for expression position.
    /// </summary>
    private static EvalResult<Result> EvalCallExpr(
        Expr func,
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var calleeR = ResolveAlg(func, ctx);
        if (calleeR.IsError)
            return new EvalError.WithContext(CtxCall(func), calleeR.Error) { Span = calleeR.Error.Span };

        if (TryEvaluateSequencePipeline(
            SequencePipelineInvocation.PlainCall(func, argsAlg, calleeR.Value),
            ctx,
            valEnv,
            out var sequencePipelineR))
            return WithCtx(
                CtxCall(func),
                sequencePipelineR.IsError
                    ? sequencePipelineR.Error
                    : EvalResult<Result>.Ok(sequencePipelineR.Value.Value));

        return WithCtx(CtxCall(func), EvalResolvedCall(calleeR.Value, argsAlg, ctx, valEnv, OpenExprName(func)));
    }

    /// <summary>
    /// Counted expression-position call evaluation mirroring <see cref="EvalCallExpr"/>.
    /// </summary>
    private static EvalResult<CountedResult> EvalCallCountedExpr(
        Expr func,
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var calleeR = ResolveAlg(func, ctx);
        if (calleeR.IsError)
            return new EvalError.WithContext(CtxCall(func), calleeR.Error) { Span = calleeR.Error.Span };

        if (TryEvaluateSequencePipeline(
            SequencePipelineInvocation.PlainCall(func, argsAlg, calleeR.Value),
            ctx,
            valEnv,
            out var sequencePipelineR))
            return WithCtx(CtxCall(func), sequencePipelineR);

        return WithCtx(CtxCall(func), EvalResolvedCallCounted(calleeR.Value, argsAlg, ctx, valEnv, OpenExprName(func)));
    }

    // ── Conditional algorithm call (Lean: evalConditionalCall) ──────────────

    /// <summary>
    /// Evaluates a conditional algorithm call.
    /// 1. Evaluate argument expressions eagerly.
    /// 2. Assemble full argument Result shape (preserving grouping for pattern matching).
    /// 3. Try branches in order; first match wins.
    /// 4. Evaluate selected branch body with pattern bindings prepended to env.
    /// 5. If no branch matches, raise NoMatchingBranch error.
    ///
    /// <para><b>Full-input-specification rule</b>: the branch body receives input
    /// bindings ONLY from the matched pattern. No extra implicit parameters are
    /// inferred. Free identifiers in the body resolve through ordinary lexical /
    /// property / open / builtin lookup, or produce unknownName at runtime.</para>
    ///
    /// <para><b>Assumes uniform output arity</b>: after validation
    /// (<see cref="CondBranch.TopLevelOutputArity"/>), all branches produce the
    /// same top-level output arity. The evaluator does not re-check this at
    /// runtime.</para>
    ///
    /// Lean: evalConditionalCall.
    /// </summary>
    private static EvalResult<Result> EvalConditionalCall(
        Algorithm callee, Algorithm args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        var wiredArgs = WireToCaller(ctx, args);
        var argExprs = wiredArgs.Output;
        var argEvalCtx = ctx.Push(wiredArgs);

        // Evaluate all argument expressions eagerly
        var argResults = new List<Result>();
        foreach (var expr in argExprs)
        {
            var r = Eval(expr, argEvalCtx, valEnv);
            if (r.IsError) return r.Error;
            argResults.Add(r.Value);
        }

        if (callee.HasDuplicateBranchPatterns())
            return new EvalError.DuplicateBranchPattern();

        var match = MatchCallBranches(callee.Branches, argResults);
        if (match is null)
            return new EvalError.NoMatchingBranch(calleeName);

        var (branch, bindings) = match.Value;
        var wiredBody = ChildOf(callee, branch.Body);
        var shadowedNames = bindings.Select(static binding => binding.Item1).ToArray();
        var newCtx = ctx.Push(callee)
            .WithCountedParamEnv(ShadowCountedParamEnv(ctx.CountedParamEnv, shadowedNames))
            .WithVariadicStreamEnv(ShadowCountedParamEnv(ctx.VariadicStreamEnv, shadowedNames));
        var newEnv = Concat(bindings, valEnv);
        return EvalAlgOutput(wiredBody, newCtx, newEnv);
    }

    /// <summary>
    /// Counted conditional call evaluation.
    /// The argument matching semantics are unchanged; only the selected branch's
    /// emitted top-level output count is preserved.
    /// Lean: <c>evalConditionalCallCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalConditionalCallCounted(
        Algorithm callee, Algorithm args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName = "conditional")
    {
        var wiredArgs = WireToCaller(ctx, args);
        var argExprs = wiredArgs.Output;
        var argEvalCtx = ctx.Push(wiredArgs);

        var argResults = new List<Result>();
        foreach (var expr in argExprs)
        {
            var r = Eval(expr, argEvalCtx, valEnv);
            if (r.IsError) return r.Error;
            argResults.Add(r.Value);
        }

        if (callee.HasDuplicateBranchPatterns())
            return new EvalError.DuplicateBranchPattern();

        var match = MatchCallBranches(callee.Branches, argResults);
        if (match is null)
            return new EvalError.NoMatchingBranch(calleeName);

        var (branch, bindings) = match.Value;
        var wiredBody = ChildOf(callee, branch.Body);
        var shadowedNames = bindings.Select(static binding => binding.Item1).ToArray();
        var newCtx = ctx.Push(callee)
            .WithCountedParamEnv(ShadowCountedParamEnv(ctx.CountedParamEnv, shadowedNames))
            .WithVariadicStreamEnv(ShadowCountedParamEnv(ctx.VariadicStreamEnv, shadowedNames));
        var newEnv = Concat(bindings, valEnv);
        return EvalAlgOutputCounted(wiredBody, newCtx, newEnv);
    }

    // ── User-defined call (Lean: evalUserCall) ────────────────────────────

    /// <summary>
    /// Shared user-defined call binding logic (Lean: evalUserCall).
    /// Dual-view semantics: each original argument expression is independently
    /// interpreted in two ways:
    /// <list type="bullet">
    ///   <item>Structural algorithm resolution → AlgEnv (callable meaning)</item>
    ///   <item>Eager value evaluation → ValEnv (value meaning)</item>
    /// </list>
    /// If both succeed, the parameter gets both meanings (dual-view).
    /// If only algorithm resolution succeeds, only AlgEnv is bound.
    /// If only value evaluation succeeds, only ValEnv is bound.
    /// If both fail, the eager-evaluation error is propagated. Zero-parameter
    /// inline block arguments are excluded from the AlgEnv side by
    /// <c>TryResolveArgAlgs</c>; they remain ordinary value/output structures
    /// regardless of output count.
    ///
    /// Flat fixed calls bind call-site structure: each comma argument is one
    /// argument expression, while a bare sequence-supply expression explicitly
    /// contributes its supplied top-level items. Multi-output values from normal
    /// expressions, including <c>.content</c>, remain one argument expression.
    /// Earlier explicit argument positions remain distinct on the eager value
    /// side even if some later arguments bind only through AlgEnv.
    /// </summary>
    private static EvalResult<Result> EvalUserCall(
        Algorithm callee, Algorithm args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        IReadOnlyList<bool>? preserveArgBoundaries = null,
        string? calleeName = null)
    {
        var wiredArgs = WireToCaller(ctx, args);

        if (callee.Output.Count == 0)
            return new EvalError.MissingOutput();

        var signature = CallableSignature.FromAlgorithm(calleeName ?? "<anonymous>", callee);
        var bindingPlan = CallableBindingPlan.FromSignature(signature);

        if (bindingPlan.RequiresPatternedBinding)
        {
            var bindingsR = BindPatternedUserCall(callee, wiredArgs, ctx, valEnv, calleeName);
            if (bindingsR.IsError) return bindingsR.Error;

            var bindings = bindingsR.Value;
            var groupedCtx = WithUserCallBindingEnvironments(ctx, bindings, callee.Params);
            var groupedEnv = Concat(bindings.ValueBindings, valEnv);
            return EvalAlgOutput(callee, groupedCtx, groupedEnv);
        }

        if (TryGetFlatVariadicBindingLayout(bindingPlan, out var variadicLayout))
        {
            var bindingsR = BindVariadicUserCall(callee, wiredArgs, ctx, valEnv, variadicLayout, calleeName, preserveArgBoundaries);
            if (bindingsR.IsError) return bindingsR.Error;

            var bindings = bindingsR.Value;
            var variadicCtx = WithUserCallBindingEnvironments(ctx, bindings, callee.Params);
            var variadicEnv = Concat(bindings.ValueBindings, valEnv);
            return EvalAlgOutput(callee, variadicCtx, variadicEnv);
        }

        if (!TryGetPlanDerivedFlatFixedParameterNames(bindingPlan, out var flatFixedParams))
            flatFixedParams = callee.Params;

        var flatBindingsR = BindFlatFixedUserCallArguments(signature, flatFixedParams, wiredArgs, ctx, valEnv);
        if (flatBindingsR.IsError) return flatBindingsR.Error;

        var flatBindings = flatBindingsR.Value;
        return EvalAlgOutput(callee, flatBindings.Context, flatBindings.ValueEnvironment);
    }

    /// <summary>
    /// Dispatches an already-resolved callee.
    /// </summary>
    private static EvalResult<Result> EvalResolvedCall(
        Algorithm callee,
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName,
        IReadOnlyList<bool>? preserveArgBoundaries = null)
    {
        if (callee is Algorithm.Builtin(var builtinId))
        {
            var argAlgsR = ResolveArgAlgsWithSequenceSupply(argsAlg, ctx, valEnv);
            if (argAlgsR.IsError) return argAlgsR.Error;
            return ApplyBuiltinResolved(builtinId, argAlgsR.Value, ctx, valEnv);
        }

        if (TryGetFlatBinderUserEquivalent(callee) is { } simpleCallee)
            return EvalUserCall(simpleCallee, argsAlg, ctx, valEnv, preserveArgBoundaries, calleeName);

        if (callee is Algorithm.Conditional)
            return EvalConditionalCall(callee, argsAlg, ctx, valEnv, calleeName);

        return EvalUserCall(callee, argsAlg, ctx, valEnv, preserveArgBoundaries, calleeName);
    }

    /// <summary>
    /// Counted user-defined call evaluation.
    /// Call semantics are unchanged; only the final emitted output count of the
    /// callee is preserved.
    /// Lean: <c>evalUserCallCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalUserCallCounted(
        Algorithm callee, Algorithm args,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        IReadOnlyList<bool>? preserveArgBoundaries = null,
        string? calleeName = null)
    {
        var wiredArgs = WireToCaller(ctx, args);

        if (callee.Output.Count == 0)
            return new EvalError.MissingOutput();

        var signature = CallableSignature.FromAlgorithm(calleeName ?? "<anonymous>", callee);
        var bindingPlan = CallableBindingPlan.FromSignature(signature);

        if (bindingPlan.RequiresPatternedBinding)
        {
            var bindingsR = BindPatternedUserCall(callee, wiredArgs, ctx, valEnv, calleeName);
            if (bindingsR.IsError) return bindingsR.Error;

            var bindings = bindingsR.Value;
            var groupedCtx = WithUserCallBindingEnvironments(ctx, bindings, callee.Params);
            var groupedEnv = Concat(bindings.ValueBindings, valEnv);
            return EvalAlgOutputCounted(callee, groupedCtx, groupedEnv);
        }

        if (TryGetFlatVariadicBindingLayout(bindingPlan, out var variadicLayout))
        {
            var bindingsR = BindVariadicUserCall(callee, wiredArgs, ctx, valEnv, variadicLayout, calleeName, preserveArgBoundaries);
            if (bindingsR.IsError) return bindingsR.Error;

            var bindings = bindingsR.Value;
            var variadicCtx = WithUserCallBindingEnvironments(ctx, bindings, callee.Params);
            var variadicEnv = Concat(bindings.ValueBindings, valEnv);
            return EvalAlgOutputCounted(callee, variadicCtx, variadicEnv);
        }

        if (!TryGetPlanDerivedFlatFixedParameterNames(bindingPlan, out var flatFixedParams))
            flatFixedParams = callee.Params;

        var flatBindingsR = BindFlatFixedUserCallArguments(signature, flatFixedParams, wiredArgs, ctx, valEnv);
        if (flatBindingsR.IsError) return flatBindingsR.Error;

        var flatBindings = flatBindingsR.Value;
        return EvalAlgOutputCounted(callee, flatBindings.Context, flatBindings.ValueEnvironment);
    }

    /// <summary>
    /// Counted dispatch for an already-resolved effective callee.
    /// </summary>
    private static EvalResult<CountedResult> EvalResolvedCallCounted(
        Algorithm callee,
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        string calleeName,
        IReadOnlyList<bool>? preserveArgBoundaries = null)
    {
        if (callee is Algorithm.Builtin(var builtinId))
        {
            var argAlgsR = ResolveArgAlgsWithSequenceSupply(argsAlg, ctx, valEnv);
            if (argAlgsR.IsError) return argAlgsR.Error;
            return ApplyBuiltinCountedResolved(builtinId, argAlgsR.Value, ctx, valEnv);
        }

        if (TryGetFlatBinderUserEquivalent(callee) is { } simpleCallee)
            return EvalUserCallCounted(simpleCallee, argsAlg, ctx, valEnv, preserveArgBoundaries, calleeName);

        if (callee is Algorithm.Conditional)
            return EvalConditionalCallCounted(callee, argsAlg, ctx, valEnv, calleeName);

        return EvalUserCallCounted(callee, argsAlg, ctx, valEnv, preserveArgBoundaries, calleeName);
    }

    // ── DotCall evaluation ────────────────────────────────────────────────

    /// <summary>
    /// Evaluates dotCall: <c>a.f</c> or <c>a.f(args)</c>
    /// Smart dispatch:
    /// 1. Value-based intrinsic (string) → evaluate target, convert numeric result to string
    /// 2. Structural property found (navigation-only):
    ///    - No args + 0-param → value access
    ///    - No args + has params → arity mismatch error
    ///    - Has args → delegate to EvalUserCall (dual-view binding, no receiver injection)
    /// 3. No property → lexical fallback (receiver injection via callLexicalWithReceiver)
    /// When resolveAlg returns notAnAlgorithm (e.g. numeric literal target),
    /// value-based intrinsics are checked before lexical fallback.
    /// Structural property calls use the same higher-order binding logic as normal
    /// user-defined calls (both delegate to EvalUserCall).
    /// Lean: evalDotCall.
    /// </summary>
    private static EvalResult<Result> EvalDotCall(
        Expr target, string name, Algorithm? argsOpt,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (name == "Output")
            return new EvalError.SpecialOutputAccess();

        if (TryEvaluateSequencePipeline(
            SequencePipelineInvocation.DotCall(target, name, argsOpt),
            ctx,
            valEnv,
            out var sequencePipelineR))
            return sequencePipelineR.IsError
                ? sequencePipelineR.Error
                : EvalResult<Result>.Ok(sequencePipelineR.Value.Value);

        // Lean: let targetAlg <- resolveAlg target ctx
        // Extension-property rule: if target is a value-producing expression (not an algorithm),
        // ResolveAlg returns NotAnAlgorithm — check value-based intrinsics first,
        // then fall back to lexical lookup so that
        //   e.P      → P(e)
        //   e.P(a,b) → P(e, a, b)
        // works for any receiver expression, including literals and parenthesized expressions.
        // The injected receiver remains one argument boundary.
        // Other errors (e.g. UnknownName) propagate as before.
        var targetResult = ResolveAlg(target, ctx);
        if (targetResult.IsError)
        {
            if (targetResult.Error is EvalError.NotAnAlgorithm)
            {
                // Value-only target (e.g. numeric literal): check value-based intrinsics
                if (name == "string")
                {
                    var val = Eval(target, ctx, valEnv);
                    if (val.IsError) return val.Error;
                    return ResultToString(val.Value);
                }
                return CallLexicalWithReceiver(name, target, argsOpt, ctx, valEnv);
            }
            return targetResult.Error;
        }
        var targetAlg = targetResult.Value;

        // Value-based intrinsic: "string" — evaluate algorithm output and convert
        if (name == "string")
        {
            var val = EvalAlgOutput(targetAlg, ctx, valEnv);
            if (val.IsError) return val.Error;
            return ResultToString(val.Value);
        }

        // Structural: property of target (exported only; private export remains accessible)
        var prop = LookupPropBinding(targetAlg, name);
        if (prop is not null)
        {
            if (!IsExported(prop))
                return new EvalError.LocalOnlyProperty(OpenExprName(target), name, prop.Exposure);

            var wired = ChildOf(targetAlg, prop.Value);
            if (argsOpt is null)
            {
                var simpleCallee = TryGetFlatBinderUserEquivalent(wired);
                if (simpleCallee is not null)
                    return new EvalError.ArityMismatch(simpleCallee.Params.Count, 0);

                if (wired is Algorithm.Conditional)
                    return new EvalError.NoMatchingBranch(name);

                // No args: 0-param → value access, has params → arity error
                if (wired.Params.Count == 0)
                    return EvalZeroArgPropertyAccess(targetAlg, prop, ZeroArgPropertyAccessKind.Structural, wired, ctx, valEnv);
                return new EvalError.ArityMismatch(wired.Params.Count, 0);
            }

            return EvalResolvedCall(wired, argsOpt, ctx, valEnv, name);
        }

        if (ConditionalBranchesDefineProperty(targetAlg, name))
            return new EvalError.LocalOnlyProperty(OpenExprName(target), name, PropertyExposure.LocalOnlyConditionalAlgorithm);

        // Lexical fallback (receiver injection via callLexicalWithReceiver)
        return CallLexicalWithReceiver(name, target, argsOpt, ctx, valEnv);
    }

    /// <summary>
    /// Resolves name lexically and calls with receiver prepended to args.
    /// The injected receiver remains one argument expression for flat fixed
    /// user calls; sequence builtin dot-call expansion is handled before this path.
    /// Delegates to EvalCall to get builtin dispatch for free.
    ///
    /// DotCall lexical fallback to "while" and "repeat" keeps explicit init
    /// arguments intact; the loop builtin turns each init argument into one
    /// initial state slot after structural property lookup has had priority.
    ///
    /// Lean: callLexicalWithReceiver.
    /// </summary>
    private readonly record struct SequenceBuiltinDotCall(
        BuiltinId Builtin,
        IReadOnlyList<ResolvedArgumentAlgorithm> Args);

    /// <summary>
    /// Sequence builtins in dot-call form pass the receiver as one counted
    /// source to the shared sequence collector.
    /// A direct inline receiver block first exposes its inner algorithm output
    /// count, which strips exactly one receiver-scoping block layer for forms
    /// like <c>(1, 2, 3).take(2)</c> while still keeping
    /// <c>((1, 2, 3)).take(2)</c> and named grouped helpers grouped.
    /// The resulting counted receiver is reified as one ordinary leading
    /// source, and any extra dot-call arguments still follow the plain-call
    /// argument path.
    /// This keeps plain-call boundary preservation unchanged while making
    /// <c>receiver.builtin(...)</c> operate on the same top-level collection
    /// that <c>receiver:i</c> and higher-order callback projection observe.
    /// </summary>
    private static EvalResult<CountedResult> EvalSequenceBuiltinDotReceiverCounted(
        Expr receiver,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (receiver is Expr.Block(var algorithm))
        {
            var wired = WireToCaller(ctx, algorithm);
            if (wired.Params.Count == 0)
                return WithSpan(receiver.Span ?? FirstSpan(wired.Output), EvalAlgOutputCounted(wired, ctx, valEnv));
        }

        return EvalCounted(receiver, ctx, valEnv);
    }

    private static EvalResult<IReadOnlyList<ResolvedArgumentAlgorithm>> SequenceBuiltinDotReceiverArgs(
        Expr receiver,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var receiverR = EvalSequenceBuiltinDotReceiverCounted(receiver, ctx, valEnv);
        if (receiverR.IsError) return receiverR.Error;

        return EvalResult<IReadOnlyList<ResolvedArgumentAlgorithm>>.Ok(
            [new ResolvedArgumentAlgorithm(CountedArgAlgorithm(receiverR.Value), SuppliesSequence: true)]);
    }

    private static EvalResult<SequenceBuiltinDotCall?> TryBuildSequenceBuiltinDotCall(
        string name,
        Expr receiver,
        Algorithm? extraArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var calleeR = ResolveNamedAlgorithm(name, span: null, ctx);
        if (calleeR.IsError
            || calleeR.Value is not Algorithm.Builtin(var builtin)
            || GetSequenceBuiltinMetadata(builtin) is null)
        {
            return EvalResult<SequenceBuiltinDotCall?>.Ok(null);
        }

        var receiverArgAlgsR = SequenceBuiltinDotReceiverArgs(receiver, ctx, valEnv);
        if (receiverArgAlgsR.IsError) return receiverArgAlgsR.Error;

        var argAlgs = new List<ResolvedArgumentAlgorithm>(receiverArgAlgsR.Value);

        if (extraArgs is not null)
        {
            var extraArgAlgsR = ResolveArgAlgsWithSequenceSupply(extraArgs, ctx, valEnv);
            if (extraArgAlgsR.IsError) return extraArgAlgsR.Error;
            if (builtin == BuiltinId.@reduce && extraArgAlgsR.Value.Count == 1)
                return ReduceInitialAccumulatorRequiresValueError(extraArgAlgsR.Value[0].Algorithm);

            argAlgs.AddRange(extraArgAlgsR.Value);
        }

        return EvalResult<SequenceBuiltinDotCall?>.Ok(
            new SequenceBuiltinDotCall(builtin, argAlgs));
    }

    private static bool TryGetParenthesizedSequenceSuppliedReceiver(Expr receiver, out Expr suppliedReceiver)
    {
        if (receiver is Expr.Block({ Opens.Count: 0, Properties.Count: 0, Params.Count: 0, Output.Count: 1 } algorithm)
            && algorithm.Output[0] is Expr.SequenceSupply sequenceSupply)
        {
            suppliedReceiver = sequenceSupply;
            return true;
        }

        suppliedReceiver = receiver;
        return false;
    }

    private static bool CanBindReceiverAsLeadingFlatVariadic(Algorithm callee, string name)
    {
        var effectiveCallee = TryGetFlatBinderUserEquivalent(callee) ?? callee;
        if (effectiveCallee is not Algorithm.User)
            return false;

        var signature = CallableSignature.FromAlgorithm(name, effectiveCallee);
        var plan = CallableBindingPlan.FromSignature(signature);
        return plan.TryGetFlatVariadicLayout(out var prefix, out _, out _)
            && prefix.Count == 0;
    }

    private static (Algorithm Args, IReadOnlyList<bool> PreserveArgBoundaries) BuildLexicalReceiverCallArgs(
        Algorithm callee,
        string name,
        Expr receiver,
        Algorithm? extraArgs)
    {
        var receiverExpr = receiver;
        var receiverBindsToLeadingVariadic = CanBindReceiverAsLeadingFlatVariadic(callee, name);
        var preserveReceiverBoundary = !receiverBindsToLeadingVariadic;
        // Parenthesized receiver sequence supply, as in (Arg...).F, can feed the
        // receiver's top-level items only to leading flat variadic receiver params.
        // Fixed receiver params keep the receiver as one argument boundary.
        if (TryGetParenthesizedSequenceSuppliedReceiver(receiver, out var suppliedReceiver)
            && receiverBindsToLeadingVariadic)
        {
            receiverExpr = suppliedReceiver;
        }

        var outputExprs = new List<Expr> { receiverExpr };
        var preserveArgBoundaries = new List<bool> { preserveReceiverBoundary };
        if (extraArgs is not null)
        {
            outputExprs.AddRange(extraArgs.Output);
            for (var i = 0; i < extraArgs.Output.Count; i++)
                preserveArgBoundaries.Add(false);
        }

        return (
            new Algorithm.User(
                Parent: null, Parameters: [], Opens: [],
                Properties: [], Output: outputExprs),
            preserveArgBoundaries);
    }

    private static bool TryEvaluateSequencePipeline(
        SequencePipelineInvocation invocation,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv,
        out EvalResult<CountedResult> result)
    {
        var services = new SequencePipelineEvaluationServices(
            GetDotCallLexicalBuiltinFallbackReason: (target, name, expectedBuiltin) =>
                GetDotCallLexicalBuiltinFallbackReason(target, name, expectedBuiltin, ctx),
            EvaluateDotReceiverIterationItems: receiver => EvaluateDotReceiverIterationItemsForSequenceOptimizer(receiver, ctx, valEnv),
            EvaluateSequenceIterationItems: collectionArgs => EvalSequenceIterationItems(collectionArgs, ctx, valEnv),
            ResolveArgumentAlgorithms: args => ResolveArgAlgs(args, ctx, valEnv),
            ResolveAlgorithm: expr => ResolveAlg(expr, ctx),
            EvaluateRangeCallArguments: (function, args, callSpan) => EvaluateRangeCallArgumentsForSequenceOptimizer(function, args, callSpan, ctx, valEnv));

        return SequencePipelineOptimizer.TryExecute(
            invocation,
            services,
            ctx,
            valEnv,
            ctx.SequenceDiagnostics,
            out result);
    }

    /// <summary>
    /// Semantic dot-receiver item collection shared with the sequence optimizer;
    /// this preserves the generic dot-call sequence builtin boundary rules.
    /// </summary>
    private static EvalResult<IReadOnlyList<CountedResult>> EvaluateDotReceiverIterationItemsForSequenceOptimizer(
        Expr receiver,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var receiverR = EvalSequenceBuiltinDotReceiverCounted(receiver, ctx, valEnv);
        if (receiverR.IsError)
            return receiverR.Error;

        return EvalResult<IReadOnlyList<CountedResult>>.Ok(
            CountedTopLevelValues(receiverR.Value)
                .Select(static item => new CountedResult(item, 1))
                .ToList());
    }

    /// <summary>
    /// Evaluate already-recognized builtin <c>range(...)</c> arguments for the
    /// sequence optimizer while preserving the generic range call diagnostics.
    /// </summary>
    private static EvalResult<InclusiveRange> EvaluateRangeCallArgumentsForSequenceOptimizer(
        Expr function,
        Algorithm argsAlg,
        SourceSpan? callSpan,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
        => WithSpan(callSpan, WithCtx(CtxCall(function), EvalBuiltinRangeCallArguments(argsAlg, ctx, valEnv)));

    private static EvalResult<InclusiveRange> EvalBuiltinRangeCallArguments(
        Algorithm argsAlg,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var argAlgsR = ResolveArgAlgsWithSequenceSupply(argsAlg, ctx, valEnv);
        if (argAlgsR.IsError) return argAlgsR.Error;

        var expandedArgsR = ExpandSequenceSuppliedBuiltinArguments(argAlgsR.Value, ctx, valEnv);
        if (expandedArgsR.IsError) return expandedArgsR.Error;

        return EvalBuiltinRangeArguments(expandedArgsR.Value, ctx, valEnv);
    }

    /// <summary>
    /// Check whether a dot call would fall through to a specific lexical
    /// builtin after structural shadowing rules are applied.
    /// </summary>
    private static string? GetDotCallLexicalBuiltinFallbackReason(
        Expr target,
        string name,
        BuiltinId expectedBuiltin,
        EvalCtx ctx)
    {
        var targetResult = ResolveAlg(target, ctx);
        if (targetResult.IsOk)
        {
            if (LookupPropBinding(targetResult.Value, name) is not null)
                return $"{name} is shadowed by a structural property";

            if (ConditionalBranchesDefineProperty(targetResult.Value, name))
                return $"{name} is shadowed by a conditional structural property";
        }
        else if (targetResult.Error is not EvalError.NotAnAlgorithm)
        {
            return $"{name} receiver resolution failed";
        }

        var calleeR = ResolveNamedAlgorithm(name, span: null, ctx);
        if (calleeR.IsError
            || calleeR.Value is not Algorithm.Builtin(var builtin)
            || builtin != expectedBuiltin)
        {
            return $"{name} does not resolve to builtin";
        }

        return null;
    }

    private static EvalResult<Result> CallLexicalWithReceiver(
        string name, Expr receiver, Algorithm? extraArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var sequenceDotCallR = TryBuildSequenceBuiltinDotCall(name, receiver, extraArgs, ctx, valEnv);
        if (sequenceDotCallR.IsError) return sequenceDotCallR.Error;
        if (sequenceDotCallR.Value is { } sequenceDotCall)
            return ApplyBuiltinResolved(sequenceDotCall.Builtin, sequenceDotCall.Args, ctx, valEnv);

        var calleeR = ResolveNamedAlgorithm(name, span: null, ctx);
        if (calleeR.IsError) return calleeR.Error;
        var (combinedArgs, preserveArgBoundaries) = BuildLexicalReceiverCallArgs(calleeR.Value, name, receiver, extraArgs);
        return EvalResolvedCall(calleeR.Value, combinedArgs, ctx, valEnv, name, preserveArgBoundaries);
    }

    /// <summary>
    /// Counted dotCall evaluation for <c>reduce</c> step validation.
    /// Lean: <c>evalDotCallCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> EvalDotCallCounted(
        Expr target, string name, Algorithm? argsOpt,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        if (name == "Output")
            return new EvalError.SpecialOutputAccess();

        if (TryEvaluateSequencePipeline(
            SequencePipelineInvocation.DotCall(target, name, argsOpt),
            ctx,
            valEnv,
            out var sequencePipelineR))
            return sequencePipelineR;

        var targetResult = ResolveAlg(target, ctx);
        if (targetResult.IsError)
        {
            if (targetResult.Error is EvalError.NotAnAlgorithm)
            {
                if (name == "string")
                {
                    var val = Eval(target, ctx, valEnv);
                    if (val.IsError) return val.Error;
                    var outR = ResultToString(val.Value);
                    if (outR.IsError) return outR.Error;
                    return EvalResult<CountedResult>.Ok(new CountedResult(outR.Value, outR.Value.ValueCount()));
                }
                return CallLexicalWithReceiverCounted(name, target, argsOpt, ctx, valEnv);
            }

            return targetResult.Error;
        }

        var targetAlg = targetResult.Value;

        if (name == "string")
        {
            var val = EvalAlgOutput(targetAlg, ctx, valEnv);
            if (val.IsError) return val.Error;
            var outR = ResultToString(val.Value);
            if (outR.IsError) return outR.Error;
            return EvalResult<CountedResult>.Ok(new CountedResult(outR.Value, outR.Value.ValueCount()));
        }

        var prop = LookupPropBinding(targetAlg, name);
        if (prop is not null)
        {
            if (!IsExported(prop))
                return new EvalError.LocalOnlyProperty(OpenExprName(target), name, prop.Exposure);

            var wired = ChildOf(targetAlg, prop.Value);
            if (argsOpt is null)
            {
                var simpleCallee = TryGetFlatBinderUserEquivalent(wired);
                if (simpleCallee is not null)
                    return new EvalError.ArityMismatch(simpleCallee.Params.Count, 0);

                if (wired is Algorithm.Conditional)
                    return new EvalError.NoMatchingBranch(name);

                if (wired.Params.Count == 0)
                    return EvalZeroArgPropertyAccessCounted(targetAlg, prop, ZeroArgPropertyAccessKind.CountedStructural, wired, ctx, valEnv);
                return new EvalError.ArityMismatch(wired.Params.Count, 0);
            }

            return EvalResolvedCallCounted(wired, argsOpt, ctx, valEnv, name);
        }

        if (ConditionalBranchesDefineProperty(targetAlg, name))
            return new EvalError.LocalOnlyProperty(OpenExprName(target), name, PropertyExposure.LocalOnlyConditionalAlgorithm);

        return CallLexicalWithReceiverCounted(name, target, argsOpt, ctx, valEnv);
    }

    /// <summary>
    /// Counted lexical fallback with receiver injection.
    /// Mirrors <see cref="CallLexicalWithReceiver"/>.
    /// Lean: <c>callLexicalWithReceiverCounted</c>.
    /// </summary>
    private static EvalResult<CountedResult> CallLexicalWithReceiverCounted(
        string name, Expr receiver, Algorithm? extraArgs,
        EvalCtx ctx,
        IReadOnlyList<(string, Result)> valEnv)
    {
        var sequenceDotCallR = TryBuildSequenceBuiltinDotCall(name, receiver, extraArgs, ctx, valEnv);
        if (sequenceDotCallR.IsError) return sequenceDotCallR.Error;
        if (sequenceDotCallR.Value is { } sequenceDotCall)
            return ApplyBuiltinCountedResolved(sequenceDotCall.Builtin, sequenceDotCall.Args, ctx, valEnv);

        var calleeR = ResolveNamedAlgorithm(name, span: null, ctx);
        if (calleeR.IsError) return calleeR.Error;
        var (combinedArgs, preserveArgBoundaries) = BuildLexicalReceiverCallArgs(calleeR.Value, name, receiver, extraArgs);
        return EvalResolvedCallCounted(calleeR.Value, combinedArgs, ctx, valEnv, name, preserveArgBoundaries);
    }

    // ── Entry points ────────────────────────────────────────────────────────

    /// <summary>
    /// Run evaluation on an expression with prelude in scope.
    /// Lean: runResult → EvalM Result.
    /// </summary>
    public static EvalResult<Result> Run(Expr expr)
        => Run(expr, new RunScopedZeroArgPropertyResultCache());

    internal static EvalResult<Result> Run(
        Expr expr,
        IZeroArgPropertyResultCache zeroArgPropertyResultCache)
        => Run(expr, zeroArgPropertyResultCache, enableLoopOptimization: true);

    internal static EvalResult<Result> Run(
        Expr expr,
        IZeroArgPropertyResultCache zeroArgPropertyResultCache,
        bool enableLoopOptimization)
        => Run(expr, zeroArgPropertyResultCache, enableLoopOptimization, loopDiagnostics: null);

    internal static EvalResult<Result> Run(
        Expr expr,
        IZeroArgPropertyResultCache zeroArgPropertyResultCache,
        bool enableLoopOptimization,
        LoopOptimizationDiagnostics? loopDiagnostics)
        => Run(
            expr,
            zeroArgPropertyResultCache,
            enableLoopOptimization,
            loopDiagnostics,
            enableSequencePipelineOptimization: true,
            sequenceDiagnostics: null);

    internal static EvalResult<Result> Run(
        Expr expr,
        IZeroArgPropertyResultCache zeroArgPropertyResultCache,
        bool enableLoopOptimization,
        LoopOptimizationDiagnostics? loopDiagnostics,
        bool enableSequencePipelineOptimization,
        SequencePipelineDiagnostics? sequenceDiagnostics)
    {
        if (AlgorithmValidation.FindFirstExplicitParameterOutputViolation(expr) is { } violation)
            return new EvalError.ExplicitParametersRequireOutput() { Span = violation.Span };

        ArgumentNullException.ThrowIfNull(zeroArgPropertyResultCache);

        var ctx = new EvalCtx(
            [PreludeAlg],
            [],
            [],
            [],
            zeroArgPropertyResultCache,
            enableLoopOptimization,
            loopDiagnostics,
            enableSequencePipelineOptimization,
            sequenceDiagnostics);
        return expr is Expr.Block(var alg)
            ? EvalRootProgram(alg, expr.Span, ctx)
            : Eval(expr, ctx, []);
    }

    internal static EvalResult<CountedResult> RunCounted(Expr expr)
        => RunCounted(expr, new RunScopedZeroArgPropertyResultCache());

    internal static EvalResult<CountedResult> RunCounted(
        Expr expr,
        IZeroArgPropertyResultCache zeroArgPropertyResultCache)
    {
        if (AlgorithmValidation.FindFirstExplicitParameterOutputViolation(expr) is { } violation)
            return new EvalError.ExplicitParametersRequireOutput() { Span = violation.Span };

        ArgumentNullException.ThrowIfNull(zeroArgPropertyResultCache);

        var ctx = new EvalCtx(
            [PreludeAlg],
            [],
            [],
            [],
            zeroArgPropertyResultCache,
            EnableLoopOptimization: true,
            LoopDiagnostics: null,
            EnableSequencePipelineOptimization: true,
            SequenceDiagnostics: null);
        return expr is Expr.Block(var alg)
            ? EvalRootProgramCounted(alg, expr.Span, ctx)
            : EvalCounted(expr, ctx, []);
    }

    private static EvalResult<Result> EvalRootProgram(Algorithm alg, SourceSpan? span, EvalCtx ctx)
    {
        var wired = WireToCaller(ctx, alg);
        if (wired.Params.Count == 0)
        {
            var result = EvalProgramOutput(wired, ctx, []);
            if (result.IsError
                && result.Error is EvalError.MissingOutput
                && wired is Algorithm.User { Output.Count: 0 })
            {
                return new EvalError.WithContext(new ProgramEvaluationContext(), result.Error)
                {
                    Span = result.Error.Span ?? span,
                };
            }

            return result;
        }

        var blockSpan = span ?? FirstSpan(wired.Output);
        return MissingImplicitArguments<Result>(wired.Params, blockSpan);
    }

    private static EvalResult<CountedResult> EvalRootProgramCounted(Algorithm alg, SourceSpan? span, EvalCtx ctx)
    {
        var wired = WireToCaller(ctx, alg);
        if (wired.Params.Count == 0)
        {
            var result = EvalProgramOutputCounted(wired, ctx, []);
            if (result.IsError
                && result.Error is EvalError.MissingOutput
                && wired is Algorithm.User { Output.Count: 0 })
            {
                return new EvalError.WithContext(new ProgramEvaluationContext(), result.Error)
                {
                    Span = result.Error.Span ?? span,
                };
            }

            return result;
        }

        var blockSpan = span ?? FirstSpan(wired.Output);
        return MissingImplicitArguments<CountedResult>(wired.Params, blockSpan);
    }

    /// <summary>
    /// Run evaluation and flatten to atoms.
    /// Lean: runFlat → EvalM (List Int).
    /// </summary>
    public static EvalResult<IReadOnlyList<decimal>> RunFlat(Expr expr)
    {
        var r = Run(expr);
        if (r.IsError) return r.Error;
        return EvalResult<IReadOnlyList<decimal>>.Ok(r.Value.ToAtoms());
    }

    // ── Utility ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Integer exponents use exact decimal exponentiation by squaring.
    /// Negative integers are handled as a decimal reciprocal of the positive power.
    /// Non-integer exponents use approximate <see cref="Math.Pow(double, double)"/> via double,
    /// then normalize the result using the evaluator's standard floating-point cleanup.
    /// </summary>
    internal static EvalResult<Result> EvalPow(SourceSpan? span, decimal b, decimal exp)
    {
        try
        {
            var powR = DecimalPow(b, exp);
            if (powR.IsError)
                return powR.Error with { Span = span };
            return EvalResult<Result>.Ok(new Result.Atom(powR.Value));
        }
        catch (OverflowException)
        {
            return new EvalError.NumericOverflow() { Span = span };
        }
    }

    private static EvalResult<decimal> DecimalPow(decimal b, decimal exp)
    {
        if (exp != decimal.Truncate(exp))
            return EvalResult<decimal>.Ok(NormalizeDoubleResult(Math.Pow((double)b, (double)exp)));

        var exponent = decimal.ToInt64(exp);
        if (exponent < 0)
        {
            if (b == 0)
                return new EvalError.IllegalInEval("zero cannot be raised to a negative integer exponent");

            var absExponent = exponent == long.MinValue
                ? (ulong)long.MaxValue + 1UL
                : (ulong)(-exponent);

            var positivePower = DecimalPowNonNegative(b, absExponent);
            if (positivePower == 0)
                throw new OverflowException();
            return EvalResult<decimal>.Ok(1m / positivePower);
        }

        return EvalResult<decimal>.Ok(DecimalPowNonNegative(b, (ulong)exponent));
    }

    private static decimal DecimalPowNonNegative(decimal b, ulong exponent)
    {
        decimal result = 1m;
        var baseVal = b;
        var remainingExponent = exponent;

        while (remainingExponent > 0)
        {
            if ((remainingExponent & 1UL) == 1UL)
                result = checked(result * baseVal);

            remainingExponent >>= 1;
            if (remainingExponent > 0)
                baseVal = checked(baseVal * baseVal);
        }

        return result;
    }

    private static IReadOnlyList<T> Prepend<T>(T item, IReadOnlyList<T> list)
        => new PrependedReadOnlyList<T>(item, list);

    private sealed class PrependedReadOnlyList<T> : IReadOnlyList<T>
    {
        private readonly T _head;
        private readonly IReadOnlyList<T> _tail;

        public PrependedReadOnlyList(T head, IReadOnlyList<T> tail)
        {
            _head = head;
            _tail = tail;
            Count = tail.Count + 1;
        }

        public int Count { get; }

        public T this[int index]
            => index switch
            {
                0 => _head,
                > 0 when index <= _tail.Count => _tail[index - 1],
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            };

        public IEnumerator<T> GetEnumerator()
        {
            yield return _head;
            foreach (var item in _tail)
                yield return item;
        }

        IEnumerator IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private static IReadOnlyList<T> Concat<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var result = new List<T>(a.Count + b.Count);
        result.AddRange(a);
        result.AddRange(b);
        return result;
    }
}
