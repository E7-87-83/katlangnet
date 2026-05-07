namespace KatLang.Optimizations.Loops;

internal static partial class LoopOptimizer
{
    internal static bool TryEvaluateWhile(
        Algorithm step,
        IReadOnlyList<Result> stateValues,
        Evaluator.EvalCtx ctx,
        IReadOnlyList<(string Name, Result Value)> valEnv,
        Func<Result, EvalResult<Result>> genericFallback,
        out EvalResult<Result> result)
    {
        var plan = TryBuildLoopPlanTemplate(LoopKind.While, step, stateValues.Count, ctx, valEnv);
        if (plan is null)
        {
            result = default;
            return false;
        }

        ctx.LoopDiagnostics?.RecordOptimizedLoopHit();
        var frame = new LoopRunFrame(plan, valEnv, stateValues);
        while (true)
        {
            ctx.LoopDiagnostics?.RecordLoopIteration();
            frame.BeginIteration();

            for (var i = 0; i < plan.NextStateOutputs.Count; i++)
            {
                var outputR = EvalTopLevelLoopExprPlan(plan.NextStateOutputs[i], frame);
                if (outputR.IsError)
                {
                    result = outputR.Error;
                    return true;
                }

                if (outputR.Value.EmittedCount == 0)
                {
                    ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop expression emitted no state value");
                    result = genericFallback(frame.CurrentStateResult());
                    return true;
                }

                frame.SetScratchSlot(i, outputR.Value.ToResult());
            }

            var continuationR = EvalTopLevelLoopExprPlan(plan.ContinuationOutput!, frame);
            if (continuationR.IsError)
            {
                result = continuationR.Error;
                return true;
            }

            if (continuationR.Value.EmittedCount == 0)
            {
                ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop continuation emitted no value");
                result = genericFallback(frame.CurrentStateResult());
                return true;
            }

            var contR = continuationR.Value.AsNum() is { } cont
                ? EvalResult<decimal>.Ok(cont)
                : Evaluator.ExpectInt(continuationR.Value.ToResult());
            if (contR.IsError)
            {
                result = contR.Error;
                return true;
            }

            if (contR.Value == 0)
            {
                result = EvalResult<Result>.Ok(frame.CurrentStateResult());
                return true;
            }

            if (!frame.TryCommitScratchFast())
            {
                ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop next-state arity changed");
                result = genericFallback(frame.CurrentStateResult());
                return true;
            }
        }
    }

    internal static bool TryEvaluateRepeat(
        Algorithm step,
        long count,
        IReadOnlyList<Result> stateValues,
        Evaluator.EvalCtx ctx,
        IReadOnlyList<(string Name, Result Value)> valEnv,
        Func<long, Result, EvalResult<Result>> genericFallback,
        out EvalResult<Result> result)
    {
        var plan = TryBuildLoopPlanTemplate(LoopKind.Repeat, step, stateValues.Count, ctx, valEnv);
        if (plan is null)
        {
            result = default;
            return false;
        }

        ctx.LoopDiagnostics?.RecordOptimizedLoopHit();
        var frame = new LoopRunFrame(plan, valEnv, stateValues);
        for (var iteration = 0L; iteration < count; iteration++)
        {
            ctx.LoopDiagnostics?.RecordLoopIteration();
            frame.BeginIteration();

            for (var i = 0; i < plan.NextStateOutputs.Count; i++)
            {
                var outputR = EvalTopLevelLoopExprPlan(plan.NextStateOutputs[i], frame);
                if (outputR.IsError)
                {
                    result = outputR.Error;
                    return true;
                }

                if (outputR.Value.EmittedCount == 0)
                {
                    ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop expression emitted no state value");
                    result = genericFallback(count - iteration, frame.CurrentStateResult());
                    return true;
                }

                frame.SetScratchSlot(i, outputR.Value.ToResult());
            }

            if (iteration == count - 1)
            {
                result = EvalResult<Result>.Ok(frame.ScratchStateResult());
                return true;
            }

            if (!frame.TryCommitScratchFast())
            {
                ctx.LoopDiagnostics?.RecordOptimizedLoopFallback("loop next-state arity changed");
                result = genericFallback(count - iteration, frame.CurrentStateResult());
                return true;
            }
        }

        result = EvalResult<Result>.Ok(frame.CurrentStateResult());
        return true;
    }

    private static LoopPlanTemplate? TryBuildLoopPlanTemplate(
        LoopKind kind,
        Algorithm step,
        int stateArity,
        Evaluator.EvalCtx ctx,
        IReadOnlyList<(string Name, Result Value)> parentValEnv)
    {
        ctx.LoopDiagnostics?.RecordLoopPlanBuild();

        if (stateArity <= 0 || step is not Algorithm.User userStep)
        {
            RecordLoopPlanFallbackDiagnostic(kind, step, stateArity, ctx, "loop plan unsupported step shape");
            return null;
        }

        if (userStep.FindDuplicatePropName() is not null || userStep.Params.Count != stateArity)
        {
            RecordLoopPlanFallbackDiagnostic(kind, step, stateArity, ctx, "loop plan parameter/property shape mismatch");
            return null;
        }

        var expectedOutputCount = kind == LoopKind.While ? stateArity + 1 : stateArity;
        if (userStep.Output.Count != expectedOutputCount)
        {
            RecordLoopPlanFallbackDiagnostic(kind, step, stateArity, ctx, "loop plan output arity mismatch");
            return null;
        }

        var loopCtx = ShadowLoopStepCountedParamEnv(ctx, userStep);
        var iterationCtx = loopCtx.Push(userStep);
        var tempPlanBuild = BuildLoopTempPlans(
            userStep,
            userStep.Params,
            iterationCtx,
            parentValEnv,
            includeDiagnostics: ctx.LoopDiagnostics is not null);
        var tempPlans = tempPlanBuild.Plans;

        var nextStateOutputs = new List<LoopExprPlan>(stateArity);
        var requiresPerIterationCacheIdentity = false;
        for (var i = 0; i < stateArity; i++)
        {
            var plan = BuildLoopExprPlan(userStep.Output[i], userStep.Params, iterationCtx, parentValEnv, tempPlans);
            if (!plan.IsFullyPlanned)
                requiresPerIterationCacheIdentity = true;
            nextStateOutputs.Add(plan.Plan);
        }

        LoopExprPlan? continuationOutput = null;
        if (kind == LoopKind.While)
        {
            var plan = BuildLoopExprPlan(userStep.Output[stateArity], userStep.Params, iterationCtx, parentValEnv, tempPlans);
            if (!plan.IsFullyPlanned)
                requiresPerIterationCacheIdentity = true;
            continuationOutput = plan.Plan;
        }

        string? diagnosticKey = null;
        if (ctx.LoopDiagnostics is { } diagnostics)
        {
            var expressionDiagnostics = BuildLoopExpressionDiagnostics(nextStateOutputs, continuationOutput);
            diagnosticKey = diagnostics.RecordLoopPlanDiagnostic(
                LoopPlanIdentity(kind, step),
                LoopKindName(kind),
                stateArity,
                optimized: true,
                fallbackReason: null,
                temps: tempPlanBuild.Diagnostics,
                expressions: expressionDiagnostics);
        }

        return new LoopPlanTemplate(
            kind,
            userStep,
            stateArity,
            tempPlans,
            nextStateOutputs,
            continuationOutput,
            requiresPerIterationCacheIdentity,
            loopCtx,
            diagnosticKey);
    }

    private static Evaluator.EvalCtx ShadowLoopStepCountedParamEnv(
        Evaluator.EvalCtx ctx,
        Algorithm.User userStep)
        => ctx.WithCountedParamEnv(Evaluator.ShadowCountedParamEnv(ctx.CountedParamEnv, userStep.Params));

    private static void RecordLoopPlanFallbackDiagnostic(
        LoopKind kind,
        Algorithm step,
        int stateArity,
        Evaluator.EvalCtx ctx,
        string reason)
    {
        ctx.LoopDiagnostics?.RecordOptimizedLoopFallback(reason);
        var diagnosticKey = ctx.LoopDiagnostics?.RecordLoopPlanDiagnostic(
            LoopPlanIdentity(kind, step),
            LoopKindName(kind),
            stateArity,
            optimized: false,
            fallbackReason: reason,
            temps: [],
            expressions: []);
        ctx.LoopDiagnostics?.RecordLoopPlanExecution(diagnosticKey);
    }

    private static string LoopKindName(LoopKind kind)
        => kind switch
        {
            LoopKind.While => "while",
            LoopKind.Repeat => "repeat",
            _ => kind.ToString(),
        };

    private static string LoopPlanIdentity(LoopKind kind, Algorithm step)
    {
        var stepPath = Evaluator.TryGetAlgorithmPath(step) ?? "(anonymous)";
        return $"{stepPath}.{LoopKindName(kind)}";
    }
}
