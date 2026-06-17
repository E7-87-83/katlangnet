# KatLang Agent Instructions

## Project Overview

- These instructions are for Codex and other AI coding agents working in this repository.
- `AGENTS.md` is the canonical shared agent-instructions file for this repo.
- KatLang's authoritative language model is `lean/KatLang.lean`.
- The C# implementation in `src/KatLang/` must stay semantically aligned with Lean.
- If Lean and C# are both wrong for the same bug, fix both together when feasible.
- Keep this file operational and concise.

## Core Architecture

- `lean/KatLang.lean`: source of truth for AST shape, evaluation rules, and invariants.
- `lean/CoreTests.lean`, `lean/AstDemo.lean`: Lean-side regression and AST compatibility checks.
- `src/KatLang/`: C# AST, parser, front-end elaboration, evaluator, diagnostics, and public API.
- `src/KatLang/Semantics/`: editor-facing semantic tooling only; it is not the evaluator and not the normative semantics layer.
- `tests/KatLang.Tests/`: parser, evaluator, elaboration, semantics, and integration regression coverage.
- `tutorial.md`, `KatLang.ebnf`, and generator prompt/agent files must stay aligned with real language behavior.

## Language Semantics And Design Rules

- Lean 4 wins over implementation convenience, performance, or stylistic preference.
- If Lean is ambiguous for a requested behavior change, stop and clarify before implementing.
- Do not invent syntax or semantics that are not in Lean unless the task explicitly includes the Lean change.
- Do not add new operators, convenience syntax, implicit coercions, hidden fallbacks, or AST simplifications that erase Lean distinctions.
- Preserve structural distinctions in the AST and runtime model. In particular, `.dotCall`, `open`, and `Output = expr` are language-level constructs, not incidental parser sugar.
- `Output = expr` is reserved result syntax. `Algo.Output` and `Algo.Output(...)` are invalid.
- Ownership-first lookup is fundamental. Keep lookup behavior aligned across evaluator, parser/front-end elaboration, parameter detection, and semantic tooling.
- `open Name` may target a lexically visible private head, but `open` only exposes public members.
- `open` is a declaration/import directive, not an output expression: it takes ONE comma-separated target list (`open A, B, C`; string targets use single quotes — `open 'url', A`), parsed by a dedicated comma-list parser into individual targets (resolve, argumentless dot-call path, block, or string-load sugar). Each algorithm allows at most one `open` declaration. The first target must begin on the same physical line as `open` (`open` newline `A` is a missing-target error and `A` stays a separate row). Comma keeps its normal explicit line-continuation behavior — `open A,` newline `B` and `open A` newline `, B` both continue the list — and a leading `.` continues a dotted target (`open A` newline `.B` is `open A.B`), but plain newline adjacency never continues `open`: `open Math` newline `Math.Pi` is an open plus a report row. `;` and same-line adjacency are NOT open-target separators (`open A ; B` and `open A B` report a missing-comma diagnostic, never two targets). Sequence supply `...` is not open-target syntax for any atom kind, including string targets: `open A...`, `open A...B`, `open A, B...`, and `open 'url'...` are targeted parse errors, never list-like opens.
- `open 'url'` is front-end sugar for load elaboration, not a core AST construct.
- Dot-call uses structural lookup first and lexical fallback second. Structural lookup, receiver injection, fallback order, and diagnostics must stay consistent across Lean and C#.
- Ordinary lexical dot-call passes the receiver as one leading argument boundary. `A.B(C, D)` means `B(A, C, D)`, not a call where `A`'s top-level values are spread before `C` and `D`.
- Only sequence/variadic builtin dot-call paths may opt into receiver top-level expansion, and that expansion must remain explicit in builtin metadata/evaluator handling.

## Lean/C# Consistency Requirements

- `Parser.Parse(...)` and `ParseResult` are elaborated front-end outputs, not raw syntax trees.
- The raw syntax boundary is `Parser.ParseSyntax(...)`.
- `FrontEndPipeline.Process(...)` is the explicit C# front-end path for elaboration passes such as load elaboration, parameter detection, implicit argument resolution, and property exposure resolution.
- Default parse/run entry points reject unresolved `load`; only elaboration-enabled paths may consume it.
- When semantics change, update all affected layers together: Lean, C# parser/elaboration/evaluator, `src/KatLang/Semantics/`, tests, and user-facing docs.
- Avoid duplicating semantic rules across parser, evaluator, parameter detection, and semantic model code. Reuse the owning logic when possible.
- Most C# cache-like runtime machinery is implementation-only and should not be mirrored in Lean merely for performance parity. The zero-parameter property cache is the explicit exception: property-style access `A` may use the per-run cache, while explicit call `A()` bypasses that property’s cache entry. This `A` vs `A()` distinction is core KatLang semantics and is modeled in Lean.
- Lean core numeric semantics use `Int`; the current C# runtime uses `decimal`. Do not silently widen or reinterpret numeric behavior without checking Lean first.

## Builtins And Sequence Supply Conventions

- `arity` means the structural count of top-level output slots.
- `count` means the number of evaluated top-level values after sequence values are opened by the consuming operation.
- Do not treat `arity` and `count` as interchangeable.
- Comma and allowed expression adjacency are expression-list separators. `1, 2, 3`, `1 2 3`, and newline-separated `1`/`2`/`3` all produce three expression-list slots in contexts where adjacency is allowed. Root output consumes a bare expression list as output slots; call syntax consumes it as argument slots; parentheses materialize it as one sequence/group value. Parsing must not depend on callable arity, runtime values, inferred types, or any other semantic information.
- Semicolon `;` is not supported as expression syntax. It is not an alternative separator or sequence constructor. The parser reports: "Semicolon is not supported as an expression separator. Use comma or adjacency for separate expressions, or parentheses for one grouped value." Use comma/adjacency for separate slots and parentheses for one grouped/sequence-valued slot, e.g. `sum((10, 20, 30))`, `take((1, 2, 3), 2)`, and row-like values as `Reports = (row1), (row2)`.
- Postfix continuations win over adjacency on the same physical line only. A `(` or `{` after a callable target on the same physical line is a call delimiter even across whitespace, while a physical newline never continues a closed expression into a call. For multiline calls, open the delimiter before the newline. Indexing `:`, postfix grace `~`, binary operators, and postfix `...` are also same-physical-line only. A leading `.` is the supported method-chain continuation. Definition bodies, explicit `Output = ...` bodies, and open target lists are line-bounded: a newline ends the body, so an expression on a following line — at any indentation — is a separate output row parsed by the surrounding output/algorithm context, never a body continuation. (Same-line adjacency, an already-open delimiter, a same-line binary operator, and a leading `.` still continue the body's single expression. Root output and algorithm/brace bodies, by contrast, do use newline adjacency as expression-list separation.)
- Ellipsis `...` is the postfix sequence supply operator. `expr...` opens the evaluated sequence value of `expr` into the surrounding structural context. It never consumes a right operand: `A...B`, `A...empty`, `A... B`, and `A...` newline `B` all parse as expression lists beginning with `A...`. `A... ; B` is invalid semicolon syntax.
- Postfix `...` binds to its immediate operand before expression-list handling. `X(a b...)` and `X(a` newline `b...)` parse as `X(a, b...)`. To spread a value plus another argument as separate slots, use comma or adjacency: `f(A..., B)` and `f(A...B)` are both two-argument forms. To make one grouped argument containing a supply plus another value, write `f((A..., B))`.
- Top-level variadic parameters are strict one-slot destructuring parameters. `Name(values...) = body` consumes exactly one argument slot at that pattern level, then binds `values` to that slot's immediate sequence items. Prefix and suffix parameters still consume their own structural slots, so explicit `expr...` may over-supply a variadic signature if it opens too many slots.
- Ordinary lexical dot-call passes the receiver as one leading argument boundary. `A.B(C, D)` means `B(A, C, D)`, not a call where `A`'s top-level values are spread before `C` and `D`. If `B` has a leading `values...` parameter, that parameter may destructure the one receiver slot after ordinary receiver injection.
- Sequence builtins such as `filter`, `map`, `order`, `orderDesc`, `count`, `first`, `last`, `min`, `max`, `sum`, `avg`, and `reduce` follow the same strict one-slot `values...` convention. Prefer one sequence-valued collection slot, such as `count(Values)`, `sum((10, 20, 30))`, or `take((1, 2, 3), 2)` for suffix builtins. Inline comma remains structural (`count(1, 2, 3)` is an arity error); `count(Values...)` is also an arity error when `Values...` opens more than the one collection slot.
- Sequence builtins and sequence-supply behavior must stay consistent across Lean and C#.
- Changes to builtins, preludes, intrinsic metadata, or sequence syntax require synchronized updates in evaluator, front-end assumptions, semantics, tests, tutorial, EBNF, and generator guidance.

## Editor Semantics
- `src/KatLang/Semantics/` derives editor-facing meaning from parsed and elaborated ASTs.
- Build semantic models from `Parser.Parse(...)` / `ParseResult`, not from raw syntax.
- Only source-backed identifiers may produce semantic sites, resolutions, declarations, or spans.
- Synthetic constructs must not invent source spans.
- If editor-visible behavior changes, update `src/KatLang/Semantics/` and `tests/KatLang.Tests/SemanticModelTests.cs` together.
- Preserve exact source-span invariants for hover, references, go-to-definition, classification, and callable-property metadata.

## Testing Expectations

- Prefer minimal, semantics-preserving, reviewable changes.
- Add or update focused tests near the changed layer.
- Include negative coverage when failure modes are meaningful.
- When changing language behavior, update Lean tests and C# tests together.
- If a change crosses parser, evaluator, semantics, or docs boundaries, cover the affected layers in the same task when feasible.

## Documentation Expectations

- Update `KatLang.ebnf` when lexer/parser grammar changes.
- Update `tutorial.md` when user-facing behavior changes.
- Update generator-facing files when syntax, builtins, `Output`, `open`/`load`, or recommended code-generation idioms change.
- In this repo that usually includes `.github/agents/katlang-generator.agent.md` and any related generator prompt assets.
- When generator guidance changes, explicitly check both `.github/agents/katlang-generator.agent.md` and `experimental/prompts/katlang-generator.txt`.

## Coding Guidance

- Prefer small changes that fix the root semantic issue without widening scope unnecessarily.
- Do not introduce new AST shapes unless strongly justified by Lean or an existing architectural boundary.
- Preserve the current parser/evaluator/tooling boundaries instead of re-encoding the same rule in multiple places.
- Keep diagnostics structured, source-positioned, user-friendly, and phrased in KatLang terms.
- If a change is implementation-only optimization, say so explicitly.

## Validation

Run the full validation script from repo root:

```powershell
pwsh .\scripts\validate-all.ps1
```

This runs the C# test suite, `git diff --check`, and both Lean targets:

```powershell
lake build CoreTests
lake build AstDemo
```

Manual fallback:

```powershell
dotnet test .\KatLang.slnx -p:UseSharedCompilation=false
git diff --check
Push-Location .\lean
lake build CoreTests
lake build AstDemo
Pop-Location
```

Lean CoreTests now use `#guard` for semantic assertions, so a failing assertion fails `lake build CoreTests`. Remaining `#eval` lines are demo/inspection output only.

## Lean/C# Semantic Alignment

Before editing, classify the change using `src/KatLang/SEMANTIC-ALIGNMENT.md`.

- Observable semantics require Lean consideration and usually Lean updates/parity tests.
- C# implementation/tooling-only changes require C# tests; Lean updates are not required.
- Optimization-only changes do not change Lean, but require equivalence tests against the generic path.
- Diagnostic wording-only changes do not require Lean if the structured error kind/payload is unchanged.
- Grammar or AST changes usually require Lean review.

If in doubt, the manifest's "Lean update required?" column is authoritative. If Lean is silent or ambiguous, stop and ask.

## Do Not

- Do not silently change language semantics.
- Do not let Lean and C# diverge.
- Do not treat `AGENTS.md` as a long design essay.
- Do not let multiple agent-instruction files drift into conflicting guidance.
- Do not add convenience syntax, hidden fallbacks, or duplicated semantic logic just to make a local change easier.
