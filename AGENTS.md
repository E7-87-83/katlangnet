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
- `count` means the number of evaluated top-level values.
- Do not treat `arity` and `count` as interchangeable.
- Implicit semicolon by adjacency: adjacent complete expressions are output-joined. A semicolon may be written explicitly as `;`, implied by a physical newline, or implied by same-line expression adjacency. All forms have the same meaning, precedence, and associativity — including precedence with postfix `...` — so `1 2`, newline-separated `1` and `2`, and `1 ; 2` parse identically, and `A B...`, newline-separated `A` and `B...`, and `A ; B...` all parse as `(A ; B)...` (write `A ; (B...)` to apply `...` to `B` alone). This applies uniformly in root output, algorithm output bodies, brace output/body blocks, explicit parenthesized groups, and call argument lists, and it is ordinary output-stream joining that must not create synthetic row/group boundaries. Definition bodies and open targets are line-bounded: a newline ends a definition body unless an explicit `;` or a leading-dot continuation carries it on, and ends an open target list unless an explicit comma (trailing or leading) or a leading-dot continuation carries it on; a `(`- or `{`-led line never continues either, because a physical newline never continues a closed expression into a call. `Output = expr` is a definition with a line-bounded body too, and explicit and implicit output never mix in either direction: `Output = A` newline `B` is the mixing diagnostic, while `Output = A ; B` and `Output = A` newline `; B` keep the join inside the explicit output.
- Comma is never implicit. `F(1 2)` means the one-argument call `F(1 ; 2)`, never `F(1, 2)`; multiple arguments and output slots always require explicit commas. Adjacency is a pure syntax rule: parsing must not depend on callable arity or other semantic information, must never split identifiers or numbers (`ab` and `12` stay single tokens), and must never become multiplication (`2(3)` is the adjacency `2 ; 3`). A token sequence that begins a property, clause, open, or `Output =` definition is a definition, not an adjacent expression; consequently an expression following a definition on the same line joins into that definition's body.
- Postfix continuations win over adjacency on the same physical line only. Implicit semicolon is inserted only when the next token cannot legally continue the current expression; a `(` or `{` after a callable target (resolve, dot-call, grace) on the same physical line is a call delimiter even across whitespace, so `F (1, 2)` is the call `F(1, 2)`, `A.B (1)` is the dot call `A.B(1)`, and `values.map { n * 2 }` is `values.map{n * 2}`. A physical newline never continues a closed expression into a call: newline-separated `F` + `(1, 2)` is the output join `F ; (1, 2)`, newline-separated `A.B` + `(1)` is `A.B ; (1)`, and a `(`- or `{`-led line after a definition body is a following output row, not call arguments appended to that body. For a multiline call, open the delimiter before the newline (`F(` newline `1, 2` newline `)`); an already-open argument list or brace block spans lines normally. Direct calls and dot calls use the same rule. Indexing `:` is also same-physical-line only: `Pair:0` (with or without spaces around `:`) indexes, while a `:`-led line never continues the previous expression and is a parse error. Postfix grace `~` is same-physical-line only too: a `~`-led line is its own prefix-grace row, never postfix grace on the previous line's identifier. Binary operators never continue a closed expression across a newline — `A` newline `-1` is the output join `A ; -1`, never the subtraction; write a trailing operator (`A -` newline `1`) to continue arithmetic. Comments are semantically invisible for all of these line decisions: `A // note` newline `-1` parses exactly like `A` newline `-1`. A `.`-led line is the intentionally supported exception — it continues the dot-call chain for method-chain layout. Note that an explicit `;` continues a definition body across the line boundary (`P = F` newline `; (1)` defines `P = F ; 1`); use `Output = ...` or restructure when a separate row after a definition is intended. Explicit separators still force structure (`F ; (1)` joins, also across a newline; `F, (1)` is comma structure), and non-callable targets never become calls (`2 (3)` is still adjacency). The `...` operator token itself is line-bound and operand-tight: it must appear on the same physical line as the expression it follows and takes a right operand only when the operand immediately follows the dots with no whitespace; adjacency joining is otherwise line-insensitive.
- Comma has higher priority than semicolon: `1, 2 ; 3` emits `1, 2, 3`, not `1, (2 ; 3)`. Explicit grouping remains protected: `(1, 2) ; 3` emits grouped `(1, 2)` followed by `3`, and `(1, 2, 3)` is one grouped triple.
- Result-window row separation is display-only; it must not be used as evidence of semantic grouping.
- Ellipsis `...` is the sequence supply operator: `x...y` supplies the result sequence of `x` followed by the result sequence of `y`, and it is not a normal argument separator. The right operand must immediately follow `...` with no whitespace: `x...y` and `x ...y` are the supply pair, while `x... y` ends the supply postfix and the spaced expression joins as an implicit `;`, meaning `(x...) ; y` — write supply pairs tight. Postfix `expr...` supplies `expr` followed by nothing and does not continue onto the next physical line. It is value-equivalent to `expr...empty`, but the spellings normalize differently when more output follows: written-out `expr...empty` is an ordinary tight-right supply whose right operand absorbs later output joins (`A...empty ; C` is `A...(empty ; C)`), while postfix `A...` lets later output continue after the spread (`A... ; C` is `(A...) ; C`).
- Postfix `...` has lower priority than semicolon, explicit or implicit: `X(a ; b...)`, `X(a b...)`, and `X(a` newline `b...)` all mean `X((a ; b)...)`; use `X(a ; (b...))` only when that grouped supply shape is intended. The absorption is symmetric in comma/semicolon order: `X(a ; b, c...)` and `X(a, b ; c...)` both mean `X((a ; b ; c)...)` — earlier chain contributions are never stranded outside the supply. Without any join in the list, comma slots stay structural and the supply stays local to its slot (`X(a..., b)` is a two-argument call; `A, B...` is two output slots). Postfix `...` applies to the output chain accumulated to its left at the point where it appears and never reaches forward across later output joins: `A ; B... ; C`, `A B... C`, and newline-separated `A` + `B...` + `C` all mean `((A ; B)...) ; C`, never `(A ; B ; C)...`.
- Comma `,` separates output slots and call arguments.
- This convention especially matters for sequence builtins such as `filter`, `map`, `order`, `orderDesc`, `count`, `first`, `last`, `min`, `max`, `sum`, `avg`, and `reduce`.
- Sequence builtins and sequence-supply behavior must stay consistent across Lean and C#.
- Keep plain-call and dot-call sequence behavior aligned, including receiver expansion rules and grouped-value behavior.
- Changes to builtins, preludes, or intrinsic metadata may require synchronized updates in evaluator, front-end assumptions, semantics, tests, tutorial, and generator guidance.

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
