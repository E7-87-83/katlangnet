# Semantic Alignment Manifest

Lean is authoritative for observable KatLang semantics. If Lean is silent or ambiguous, stop and ask before changing C# semantics.

Each row maps a semantic area to its Lean witness, C# owner, C# tests, and whether Lean update or review is required.

| Semantic area | Lean witness | C# owner | C# tests | Lean update required? |
|---|---|---|---|---|
| AST / core syntax shape | `lean/KatLang.lean` AST section | `src/KatLang/Ast.cs`, `Parser.cs` | `ParserTests`, `AstDemo` | Yes |
| Front-end elaboration / load/open invariants | `lean/KatLang.lean` load/open invariants | `FrontEndPipeline.cs`, `ModuleLoader.cs`, `LoadElaborationGuard.cs` | `ParserTests`, module/load tests | Yes |
| Lookup / ownership / open | `lean/KatLang.lean` lookup/open definitions | `Evaluator.cs` lookup/open paths | `EvaluatorTests` lookup/open coverage | Yes |
| Dot-call receiver rules | `lean/KatLang.lean` dot-call semantics | `Evaluator.cs` dot-call paths | `EvaluatorTests` dot-call coverage | Yes |
| Evaluation / user calls | `lean/KatLang.lean` evaluator/user-call definitions | `Evaluator.cs` | `EvaluatorTests` | Yes |
| Variadic binding | `lean/KatLang.lean` variadic binding facts | `Evaluator.cs`, `CallableBindingPlan` parity tests | `EvaluatorTests`, `CallableBindingPlanParityTests` | Yes for observable semantics |
| Grouped / recursive parameter patterns | `lean/KatLang.lean` parameter pattern semantics | `Evaluator.cs`, `Parser.cs` | `EvaluatorTests`, `ParserTests` | Yes |
| Conditionals | `lean/KatLang.lean` conditional matching | `Ast.cs`, `Parser.cs`, `Evaluator.cs` | `EvaluatorTests`, `ParserTests` | Yes |
| Loops, generic semantics | `lean/KatLang.lean` loop semantics | `Evaluator.cs` generic loop paths | `EvaluatorTests` loop coverage | Yes |
| Optimized loops | Generic Lean loop semantics; optimizer is C#-only | `Optimizations/Loops/*`, optimized gates in `Evaluator.cs` | loop optimizer / optimized-vs-generic tests | No Lean update; equivalence tests required |
| Callbacks / higher-order sequence behavior | `lean/KatLang.lean` callback/sequence semantics | `Evaluator.cs` callback paths | `EvaluatorTests`, builtin tests | Yes for observable behavior |
| Builtin observable behavior | `lean/KatLang.lean` builtin semantics | `BuiltinRegistry.cs`, `Evaluator.cs` | `BuiltinRuntimeParityTests`, `BuiltinRegistryParityTests` | Yes for observable behavior |
| Sequence supply / ellipsis | `lean/KatLang.lean` sequence-supply semantics | `Parser.cs`, `Evaluator.cs` sequence supply paths | `SequenceSupplyTests`, `EvaluatorTests` sequence-supply tests | Yes |
| `atoms` / `content` / count facts | `lean/KatLang.lean` result/content/count definitions | `Evaluator.cs`, builtin registry | `EvaluatorTests`, builtin tests | Yes |
| Error kinds / arity facts | `lean/KatLang.lean` error constructors/facts where modeled | `EvalError.cs`, `KatLangError.cs`, diagnostics | `EvaluatorTests`, `KatLangEngineTests` | Yes for error kind/structured payload; no for wording-only |
| Callable surface metadata | Lean parameter facts where observable | `CallableSignature.cs`, `CallableSignatureDiagnostics.cs` | `CallableSignatureTests`, parity tests | Yes only if observable signature/binding semantics change |
| `CallableBindingPlan` | C# metadata only | `CallableBindingPlan.cs` | `CallableBindingPlanTests`, parity tests | No, unless it changes observable runtime behavior |
| `BindingInputSlot` | C# runtime data only | `Runtime/BindingInputSlot.cs` | `BindingInputSlotTests` | No |
| Semantic model / editor | C# tooling only | `src/KatLang/Semantics/*` | `SemanticModelTests` | No |
| Diagnostic wording only | C# presentation only | `KatLangError.cs` etc. | diagnostic tests | No, if error kind/structured payload unchanged |
| Memoization / caches | C# optimization/runtime boundary for zero-argument cache and explicit fresh-call mode | evaluator/cache code | evaluator/cache tests | No; equivalence tests if behavior could change |
| Generator prompts / docs | C# tooling/docs | prompt/docs files | prompt harness if relevant | No, unless grammar/semantics change |

## Known gaps

- Lean semantic assertions in `lean/CoreTests.lean` use `#guard`, so regressions fail `lake build CoreTests`. Remaining `#eval` commands are demo/inspection output.
- There is no automated C# <-> Lean output comparison. Lean does not parse surface KatLang, so a comparison bridge is intentionally deferred.
- Lean currently does not model the C# `Math` runtime surface such as `Math.Random` and `Math.RandomInt`, nor the C# zero-argument property cache. Changes to bounded random generation and recursive fresh cache bypass are C# runtime/docs/test work unless the Lean model grows those surfaces.

## Process

1. Identify the semantic area you are changing.
2. Find the matching row in this manifest.
3. If Lean update is required, update Lean first or in the same change, then update C# and tests.
4. If the change is C# implementation/tooling only, C# tests are sufficient.
5. If the change is optimization-only, add or preserve equivalence tests against the generic path.
6. If unsure, stop and ask before changing observable semantics.