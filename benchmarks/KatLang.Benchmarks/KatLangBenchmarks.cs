using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using KatLang.Evaluation.Caching;

namespace KatLang.Benchmarks;

public enum BenchmarkCacheMode
{
	Uncached,
	Stage1,
}

public enum BenchmarkLoopMode
{
	Generic,
	Optimized,
}

internal static class KatLangBenchmarkRunner
{
	internal readonly record struct BenchmarkRunWithCacheStats(
		IReadOnlyList<decimal> Atoms,
		ZeroArgPropertyResultCacheSnapshot CacheStats);

	internal static IReadOnlyList<decimal> RunWithFrontEnd(BenchmarkScenario scenario, BenchmarkCacheMode cacheMode)
		=> RunWithFrontEnd(scenario, cacheMode, BenchmarkLoopMode.Optimized);

	internal static IReadOnlyList<decimal> RunWithFrontEnd(
		BenchmarkScenario scenario,
		BenchmarkCacheMode cacheMode,
		BenchmarkLoopMode loopMode)
		=> EvaluateWithFrontEnd(scenario, CreateCache(cacheMode), loopMode).ToAtoms();

	internal static BenchmarkRunWithCacheStats RunWithFrontEndWithStats(BenchmarkScenario scenario)
	{
		var cache = new RunScopedZeroArgPropertyResultCache();
		return new BenchmarkRunWithCacheStats(
			EvaluateWithFrontEnd(scenario, cache, BenchmarkLoopMode.Optimized).ToAtoms(),
			cache.GetSnapshot());
	}

	internal static IReadOnlyList<decimal> RunPrepared(BenchmarkScenario scenario, BenchmarkCacheMode cacheMode)
		=> RunPrepared(scenario, cacheMode, BenchmarkLoopMode.Optimized);

	internal static IReadOnlyList<decimal> RunPrepared(
		BenchmarkScenario scenario,
		BenchmarkCacheMode cacheMode,
		BenchmarkLoopMode loopMode)
	{
		return EvaluatePrepared(scenario, CreateCache(cacheMode), loopMode).ToAtoms();
	}

	internal static BenchmarkRunWithCacheStats RunPreparedWithStats(BenchmarkScenario scenario)
	{
		var cache = new RunScopedZeroArgPropertyResultCache();
		return new BenchmarkRunWithCacheStats(
			EvaluatePrepared(scenario, cache, BenchmarkLoopMode.Optimized).ToAtoms(),
			cache.GetSnapshot());
	}

	private static IZeroArgPropertyResultCache CreateCache(BenchmarkCacheMode cacheMode)
		=> cacheMode switch
		{
			BenchmarkCacheMode.Uncached => UncachedZeroArgPropertyResultCache.CreateForRun(),
			BenchmarkCacheMode.Stage1 => new RunScopedZeroArgPropertyResultCache(),
			_ => throw new InvalidOperationException($"Unknown benchmark cache mode '{cacheMode}'."),
		};

	private static bool EnableLoopOptimization(BenchmarkLoopMode loopMode)
		=> loopMode switch
		{
			BenchmarkLoopMode.Generic => false,
			BenchmarkLoopMode.Optimized => true,
			_ => throw new InvalidOperationException($"Unknown benchmark loop mode '{loopMode}'."),
		};

	private static Result EvaluateWithFrontEnd(
		BenchmarkScenario scenario,
		IZeroArgPropertyResultCache cache,
		BenchmarkLoopMode loopMode)
	{
		var frontEndResult = FrontEndPipeline.Process(scenario.Source);
		var errors = frontEndResult.Diagnostics
			.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
			.Select(diagnostic => diagnostic.Message)
			.ToArray();

		if (errors.Length > 0)
		{
			throw new InvalidOperationException(
				$"Benchmark scenario '{scenario.Id}' failed in front-end processing:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
		}

		var result = Evaluator.Run(
			new Expr.Block(frontEndResult.ElaboratedRoot),
			cache,
			EnableLoopOptimization(loopMode));
		if (result.IsError)
		{
			throw new InvalidOperationException(
				$"Front-end benchmark scenario '{scenario.Id}' failed during evaluation: {result.Error}");
		}

		return result.Value;
	}

	private static Result EvaluatePrepared(
		BenchmarkScenario scenario,
		IZeroArgPropertyResultCache cache,
		BenchmarkLoopMode loopMode)
	{
		var result = Evaluator.Run(
			new Expr.Block(scenario.PreparedRoot),
			cache,
			EnableLoopOptimization(loopMode));
		if (result.IsError)
		{
			throw new InvalidOperationException(
				$"Prepared benchmark scenario '{scenario.Id}' failed during timed evaluation: {result.Error}");
		}

		return result.Value;
	}
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class ParseAndEvaluateBenchmarks
{
	private static readonly BenchmarkScenario RepeatedZeroArgPropertyReuseScenario = BenchmarkScenarioCatalog.RepeatedZeroArgPropertyReuse;
	private static readonly BenchmarkScenario ScalarHelperSumCallsScenario = BenchmarkScenarioCatalog.ScalarHelperSumCalls;
	private static readonly BenchmarkScenario NestedPropertyChainsScenario = BenchmarkScenarioCatalog.NestedPropertyChains;
	private static readonly BenchmarkScenario SequenceHeavyBuiltinsScenario = BenchmarkScenarioCatalog.SequenceHeavyBuiltins;
	private static readonly BenchmarkScenario PropertyRichSharedSubcomputationsScenario = BenchmarkScenarioCatalog.PropertyRichSharedSubcomputations;
	private static readonly BenchmarkScenario RealisticWhileCalculationScenario = BenchmarkScenarioCatalog.RealisticWhileCalculation;
	private static readonly BenchmarkScenario GcdWhileLoopScenario = BenchmarkScenarioCatalog.GcdWhileLoop;
	private static readonly BenchmarkScenario RepeatManyIterationsScenario = BenchmarkScenarioCatalog.RepeatManyIterations;
	private static readonly BenchmarkScenario NestedCapturedParentLoopScenario = BenchmarkScenarioCatalog.NestedCapturedParentLoop;

	[Params(BenchmarkCacheMode.Uncached, BenchmarkCacheMode.Stage1)]
	public BenchmarkCacheMode CacheMode { get; set; }

	[Params(BenchmarkLoopMode.Generic, BenchmarkLoopMode.Optimized)]
	public BenchmarkLoopMode LoopMode { get; set; }

	[Benchmark(Baseline = true)]
	public IReadOnlyList<decimal> RepeatedZeroArgPropertyReuse()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(RepeatedZeroArgPropertyReuseScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> ScalarHelperSumCalls()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(ScalarHelperSumCallsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> NestedPropertyChains()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(NestedPropertyChainsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> SequenceHeavyBuiltins()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(SequenceHeavyBuiltinsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> PropertyRichSharedSubcomputations()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(PropertyRichSharedSubcomputationsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> RealisticWhileCalculation()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(RealisticWhileCalculationScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> GcdWhileLoop()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(GcdWhileLoopScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> RepeatManyIterations()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(RepeatManyIterationsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> NestedCapturedParentLoop()
		=> KatLangBenchmarkRunner.RunWithFrontEnd(NestedCapturedParentLoopScenario, CacheMode, LoopMode);
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class PreparedEvaluationBenchmarks
{
	private static readonly BenchmarkScenario RepeatedZeroArgPropertyReuseScenario = BenchmarkScenarioCatalog.RepeatedZeroArgPropertyReuse;
	private static readonly BenchmarkScenario ScalarHelperSumCallsScenario = BenchmarkScenarioCatalog.ScalarHelperSumCalls;
	private static readonly BenchmarkScenario NestedPropertyChainsScenario = BenchmarkScenarioCatalog.NestedPropertyChains;
	private static readonly BenchmarkScenario SequenceHeavyBuiltinsScenario = BenchmarkScenarioCatalog.SequenceHeavyBuiltins;
	private static readonly BenchmarkScenario PropertyRichSharedSubcomputationsScenario = BenchmarkScenarioCatalog.PropertyRichSharedSubcomputations;
	private static readonly BenchmarkScenario RealisticWhileCalculationScenario = BenchmarkScenarioCatalog.RealisticWhileCalculation;
	private static readonly BenchmarkScenario GcdWhileLoopScenario = BenchmarkScenarioCatalog.GcdWhileLoop;
	private static readonly BenchmarkScenario RepeatManyIterationsScenario = BenchmarkScenarioCatalog.RepeatManyIterations;
	private static readonly BenchmarkScenario NestedCapturedParentLoopScenario = BenchmarkScenarioCatalog.NestedCapturedParentLoop;

	[Params(BenchmarkCacheMode.Uncached, BenchmarkCacheMode.Stage1)]
	public BenchmarkCacheMode CacheMode { get; set; }

	[Params(BenchmarkLoopMode.Generic, BenchmarkLoopMode.Optimized)]
	public BenchmarkLoopMode LoopMode { get; set; }

	[Benchmark(Baseline = true)]
	public IReadOnlyList<decimal> RepeatedZeroArgPropertyReuse()
		=> KatLangBenchmarkRunner.RunPrepared(RepeatedZeroArgPropertyReuseScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> ScalarHelperSumCalls()
		=> KatLangBenchmarkRunner.RunPrepared(ScalarHelperSumCallsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> NestedPropertyChains()
		=> KatLangBenchmarkRunner.RunPrepared(NestedPropertyChainsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> SequenceHeavyBuiltins()
		=> KatLangBenchmarkRunner.RunPrepared(SequenceHeavyBuiltinsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> PropertyRichSharedSubcomputations()
		=> KatLangBenchmarkRunner.RunPrepared(PropertyRichSharedSubcomputationsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> RealisticWhileCalculation()
		=> KatLangBenchmarkRunner.RunPrepared(RealisticWhileCalculationScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> GcdWhileLoop()
		=> KatLangBenchmarkRunner.RunPrepared(GcdWhileLoopScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> RepeatManyIterations()
		=> KatLangBenchmarkRunner.RunPrepared(RepeatManyIterationsScenario, CacheMode, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> NestedCapturedParentLoop()
		=> KatLangBenchmarkRunner.RunPrepared(NestedCapturedParentLoopScenario, CacheMode, LoopMode);
}

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class LoopStage2Benchmarks
{
	private static readonly BenchmarkScenario MinimalRepeatLoopScenario = BenchmarkScenarioCatalog.MinimalRepeatLoop;
	private static readonly BenchmarkScenario MinimalWhileLoopScenario = BenchmarkScenarioCatalog.MinimalWhileLoop;
	private static readonly BenchmarkScenario ArithmeticWhileLoopScenario = BenchmarkScenarioCatalog.ArithmeticWhileLoop;
	private static readonly BenchmarkScenario CapturedParentLoopScenario = BenchmarkScenarioCatalog.CapturedParentLoop;
	private static readonly BenchmarkScenario NestedRepeatedCallLoopScenario = BenchmarkScenarioCatalog.NestedRepeatedCallLoop;
	private static readonly BenchmarkScenario SquareFreeCountInlineLoopScenario = BenchmarkScenarioCatalog.SquareFreeCountInlineLoop;
	private static readonly BenchmarkScenario SquareFreeCountLocalTempLoopScenario = BenchmarkScenarioCatalog.SquareFreeCountLocalTempLoop;

	[Params(BenchmarkLoopMode.Generic, BenchmarkLoopMode.Optimized)]
	public BenchmarkLoopMode LoopMode { get; set; }

	[Benchmark]
	public IReadOnlyList<decimal> MinimalRepeatLoop()
		=> KatLangBenchmarkRunner.RunPrepared(MinimalRepeatLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> MinimalWhileLoop()
		=> KatLangBenchmarkRunner.RunPrepared(MinimalWhileLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> ArithmeticWhileLoop()
		=> KatLangBenchmarkRunner.RunPrepared(ArithmeticWhileLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> CapturedParentLoop()
		=> KatLangBenchmarkRunner.RunPrepared(CapturedParentLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> NestedRepeatedCallLoop()
		=> KatLangBenchmarkRunner.RunPrepared(NestedRepeatedCallLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> SquareFreeCountInlineLoop()
		=> KatLangBenchmarkRunner.RunPrepared(SquareFreeCountInlineLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);

	[Benchmark]
	public IReadOnlyList<decimal> SquareFreeCountLocalTempLoop()
		=> KatLangBenchmarkRunner.RunPrepared(SquareFreeCountLocalTempLoopScenario, BenchmarkCacheMode.Stage1, LoopMode);
}
