# KatLang comma, semicolon, and sequence supply design report

Research date: 2026-06-16.

This report is a source-grounded design snapshot, not an implementation proposal. It ranks sources by recency and issue number as requested: newer issues override older issues, #123 and #124 override #104, and the #104 sub-issue truth order is #112 > #111 > #108 > #106 > #105.

Primary issue sources: [#75](https://github.com/katlangorg/katlangnet/issues/75), [#101](https://github.com/katlangorg/katlangnet/issues/101), [#103](https://github.com/katlangorg/katlangnet/issues/103), [#104](https://github.com/katlangorg/katlangnet/issues/104), [#105](https://github.com/katlangorg/katlangnet/issues/105), [#106](https://github.com/katlangorg/katlangnet/issues/106), [#108](https://github.com/katlangorg/katlangnet/issues/108), [#111](https://github.com/katlangorg/katlangnet/issues/111), [#112](https://github.com/katlangorg/katlangnet/issues/112), [#119](https://github.com/katlangorg/katlangnet/issues/119), [#122](https://github.com/katlangorg/katlangnet/issues/122), [#123](https://github.com/katlangorg/katlangnet/issues/123), [#124](https://github.com/katlangorg/katlangnet/issues/124).

Primary repository anchors: `AGENTS.md`, `KatLang.ebnf`, `tutorial.md`, `.github/agents/katlang-generator.agent.md`, `experimental/prompts/katlang-generator.txt`, `src/KatLang/Ast.cs`, `src/KatLang/Parser.cs`, `src/KatLang/Evaluator.cs`, `src/KatLang/KatLangEngine.cs`, `src/KatLang/Runtime/BindingInputSlot.cs`, `lean/KatLang.lean`, `lean/CoreTests.lean`, `tests/KatLang.Tests/ParserTests.cs`, `tests/KatLang.Tests/SequenceSupplyTests.cs`, `tests/KatLang.Tests/KatLangEngineTests.cs`, and callable binding plan tests.

## 1. Current highest-truth design summary

The highest-truth current model is #122 + #123 + #124, with #111/#112 still authoritative for flat variadic allocation and dot-call receiver allocation.

Core model:

1. Comma `,` creates structural slots/items/arguments. It is the only syntax for separate call arguments and ordinary comma output slots. It is never implicit.
2. Semicolon `;` joins outputs inside the current output chain. Same-line adjacency and newline adjacency are implicit semicolon/output-join forms where the grammar allows adjacency.
3. Same-line adjacency and newline adjacency imply output joining, not comma. `F(1 2)` is `F(1 ; 2)`, one argument expression, not `F(1, 2)`.
4. Comma binds tighter than output join. Mixed comma/join/supply lists are canonicalized so `1, 2 ; 3` emits the flat output stream `1, 2, 3`, not `1, (2 ; 3)`.
5. Parentheses create explicit grouping and preserve visible group boundaries. `(1, 2) ; 3` emits a grouped `(1, 2)` item followed by `3`; `(1, 2, 3)` is one grouped triple.
6. Sequence supply `...` is postfix sequence supply/spread. It is source-level postfix-only and internally unary in C# and Lean.
7. `...` supplies the accumulated output chain to its left at the point where it appears. Later output continues after the spread. `A ; B... ; C` canonicalizes as `((A ; B)...) ; C`, not `(A ; B ; C)...`.
8. Fixed parameter calls must not auto-spread ordinary multi-output expressions. `Add(Pair)` does not silently become `Add(10, 20)` when `Pair = 10, 20`; explicit `Pair...` is required.
9. Flat variadic captures have special allocation/supply rules. Argument segments are allocated to prefix, variadic, and suffix first; only segments assigned to the flat variadic capture supply their emitted top-level items after allocation.
10. Dot-call receiver behavior must match canonical call behavior. `receiver.Property(args...)` behaves like `Property(receiver, args...)`, not `Property(receiver..., args...)`, except sequence/variadic builtin paths with explicit builtin metadata and explicitly sequence-supplied receivers.

Current implementation match:

- C# has `Expr.OutputJoin(Left, Right)` and unary `Expr.SequenceSupply(Operand)`.
- Lean has `.outputJoin : Expr -> Expr -> Expr` and unary `.sequenceSupply : Expr -> Expr`.
- `Parser.ParseSyntax(...)` preserves AST-level distinctions; `Parser.Parse(...)` runs front-end elaboration.
- `RunResult.ToDisplayString()` displays emitted top-level values as visual rows. This display can hide whether flat values came from comma or output join, but it preserves explicit grouped parentheses.

## 2. Historical evolution

### 1. Earlier semicolon/result-join model (#75)

Problem: `A = 1, 2`, `F = a; 3`, `A.F` was expected to produce `1, 2, 3`, but produced `(1, 2), 3` in the old model. #75 established semicolon as a result/output join operator: evaluate both sides and join immediate results at the current output level, without creating a new group, merging properties, or recursively flattening groups.

Surviving principle: `;` is output stream composition, separate from comma structure. Current `Evaluator.EvalOutputJoinCounted` flattens `OutputJoin` leaves into emitted top-level items while grouped items remain grouped through emitted-count handling.

### 2. #101 stricter structural semantics

#101 removed implicit final-argument unpacking in flat fixed calls. `Pair = (10, 20); Add(x, y) = x + y; Add(Pair)` should be a bad arity or grouped-argument failure, not a hidden `Add(10, 20)`.

Surviving principle: fixed calls preserve expression boundaries. Parser behavior must not depend on callable arity.

### 3. #103 explicit `...` as result-stream spread/supply

#103 added explicit `expr...` so programmers can deliberately supply a multi-output expression into call argument slots after #101 removed hidden fixed-call spreading. Examples: `Add(Pair...)` for `Pair = 10, 20`, and `Add(Pair.content...)` for a grouped pair.

Surviving principle: `...` is the explicit opt-in for source-level sequence supply. `.content` opens one visible group level but is not itself fixed-call spreading.

### 4. #104 experiment where `...` tried to replace semicolon result join

#104 attempted to remove semicolon result join and make `x...y` a combined stream-supply/join construction, with postfix `x...` as shorthand for `x...empty`. This was motivated by avoiding two visually similar separators, `,` and `;`.

Current truth status: superseded by #122/#123/#124. The current model explicitly rejects binary/right-operand `...`; `x...y` is `(x...) ; y`.

Surviving principle from #104: ordinary multi-output expressions still do not generally auto-spread, and variadic forwarding is narrow/provenance-based.

### 5. #105/#106/#108 ergonomic regressions around variadic forwarding

#105 observed that user-defined variadic dot-call receivers such as `(1, 2).Mean` should behave naturally for `Mean(vector...)`. #106 observed that a variadic capture used inside another call should not require both `values...` in the parameter definition and `values...` again at the call site. #108 generalized this to grouped/nested variadic captures and loop-step cases.

Surviving principle: bare references to bindings created by real variadic captures can carry variadic stream provenance. Expansion occurs only when the target binding position is a compatible flat top-level variadic capture. It is not name equality and not broad auto-spreading.

### 6. #111 general flat variadic-slot sequence supply rule

#111 broadened ergonomics beyond provenance forwarding. If an ordinary argument segment is assigned to a flat variadic parameter, it supplies its emitted top-level items after prefix/variadic/suffix allocation. This makes `Qmean(Vector)`, `Qmean(Vector...)`, and `range(1,10).Qmean()` equivalent when `Qmean(args...)` captures the segment in `args...`.

Surviving principle: allocation before supply. Fixed, prefix, suffix, grouped, and patterned boundaries are preserved.

### 7. #112 dot-call receiver allocation rule

#112 corrected dot-call receivers to use the same allocation rule as canonical calls. `Values.Sum` should behave like `Sum(Values)`, not `Sum(Values...)`, especially when `Sum(values..., last)` has a required suffix.

Surviving principle: ordinary lexical dot-call injects the receiver as one leading argument segment. The segment may carry emitted-count metadata into a flat variadic slot after allocation, but it is not pre-expanded.

### 8. #122 split output joining and sequence supply again

#122 reversed the #104 unification. It restored `;` as explicit output joining and kept postfix `...` only as sequence supply/spread. It also clarified display: result-window rows are presentation only, while explicit grouping remains semantic.

Surviving principle: `;` joins output streams; `...` supplies/spreads; comma remains structural.

### 9. #123 normalization of implicit semicolon, call continuation, precedence, and `...`

#123 generalized output join to same-line adjacency and newline adjacency. It also established call delimiter newline boundaries, same-line postfix indexing, open declaration grammar, comma/semicolon/supply normalization, and the accumulated-left-chain rule for `...`.

Surviving principle: explicit `;`, same-line adjacency, newline adjacency, and definition-separated output contributions share the same canonical output-chain logic where allowed. Comma is never implicit.

### 10. #124 postfix-only unary sequence supply cleanup

#124 removed source-level tight-right binary sequence supply and changed internal `SequenceSupply(left, right)` to unary `SequenceSupply(operand)` in both C# and Lean. It also required `F(X...Y)` to be one joined argument and `F(X..., Y)` to be two argument slots.

Surviving principle: `...` has no source-level or internal right operand. Any later token is an ordinary output contribution.

## 3. Operator semantics table

| Operator/form | Surface syntax | Internal meaning | Structural boundary? | Joins outputs? | Supplies/spreads outputs? | Fixed calls | Flat variadic calls | Examples |
|---|---|---|---|---|---|---|---|---|
| Comma | `a, b` | Separate output slots or call argument slots; root/algorithm output list entries unless normalized through mixed join/supply precedence | Yes | No by itself | No | `F(a, b)` is two slots | Allocation sees separate segments | `F(1, 2)`, `1, 2`, `A, B...` |
| Semicolon | `a ; b` | `Expr.OutputJoin(a, b)` / Lean `.outputJoin a b` | No new group; concatenates emitted top-level streams | Yes | No by itself | `F(a ; b)` is one argument expression | If assigned to a flat variadic capture, emitted items can be captured | `1 ; 2`, `(1, 2) ; 3` |
| Same-line adjacency | `a b` | Implicit `Expr.OutputJoin(a, b)` when `b` starts an independent expression | No new group | Yes | No | `F(1 2)` is one argument `F(1 ; 2)` | Same as semicolon when assigned to variadic | `1 2`, `A B...` |
| Newline adjacency | `a` newline `b` | Implicit `Expr.OutputJoin(a, b)` in output contexts and open delimiters; not in line-bounded definition bodies/open target lists | No new group | Yes | No | In `F(1` newline `2)` it is one joined argument inside already-open call | Same as semicolon when assigned to variadic | `1` newline `2`, `A` newline `B...` |
| Sequence supply | `expr...` | Unary `Expr.SequenceSupply(expr)` / Lean `.sequenceSupply expr`; supplies top-level items of operand | No group; explicit parentheses can group supplied result | No by itself, but follows output-chain precedence | Yes | `F(Pair...)` can supply fixed slots | Explicit pre-binding supply can satisfy prefix/suffix slots | `Pair...`, `F(X...Y)`, `F(X..., Y)` |
| Parentheses | `(expr)` | `Expr.Block` wrapping a zero-param algorithm; explicit grouped value when evaluated as expression | Yes | Inside group, adjacency/`;` still join into the group output | Only if `...` applied | Fixed call receives one grouped argument unless supplied/opened explicitly | Flat variadic receives one grouped item unless content/supply opens top-level items | `(1, 2)`, `(1 ; 2)`, `(B...)` |
| Braces | `{...}` | Algorithm/body block with scope; can be callable/callback value | Yes, algorithm boundary | Body output rows join like output context | If supplied, body output items can be supplied | As argument, one expression boundary unless supplied | If assigned to flat variadic, emitted top-level items may be captured | `{ 1, 2 }`, `values.map{n * 2}` |
| `.content` | `Pair.content` or `content(Pair)` | Opens/projects one visible group level; not hidden fixed-call spreading | Opens one visible group level but does not erase all grouping | No | No by itself; combine with `...` for fixed calls | `Add(Pair.content)` remains one expression boundary and fails for `Add(x,y)` | If assigned to flat variadic, emitted top-level items may be consumed after allocation; explicit `Pair.content...` supplies before binding | `Add(Pair.content...)` |
| Dot-call receiver | `receiver.Property(args...)` | Ordinary lexical fallback builds canonical `Property(receiver, args...)`; structural/builtin paths have explicit metadata | Receiver is one leading segment | No by itself | Only explicit receiver supply or sequence builtin metadata | Does not spread receiver into fixed params | Segment may supply emitted top-level items only after allocation into leading flat variadic | `Values.Sum(7)`, `(Values...).Sum` |

## 4. Precedence and grouping rules

Expected canonical ideas:

```katlang
1 2
1
2
1 ; 2
```

All represent output joining and should evaluate to two emitted top-level values. Parser tests and display tests verify that same-line adjacency, newline adjacency, and explicit `;` share the same `OutputJoin` shape in output contexts.

But:

```katlang
1, 2
```

is comma structure, not the same source AST as output join. It may display the same values as `1 ; 2`, but the parser represents it as two output slots unless mixed comma/join/supply normalization intentionally folds a list into an output chain.

Examples:

| Source | Canonical shape / result idea | Equivalent to | Distinct from |
|---|---|---|---|
| `1, 2 ; 3` | Flat output chain emitting `1`, `2`, `3`; comma binds tighter and the join concatenates streams | `1, 2` newline `3`, `1, 2 3` | `(1, 2) ; 3` |
| `1 ; 2, 3` | Flat output chain emitting `1`, `2`, `3` | `1` newline `2, 3` | `1 ; (2, 3)` if grouped explicitly |
| `1 2` | `1 ; 2` | newline `1`/`2`, explicit `1 ; 2` | `1, 2` as AST structure |
| `1` newline `2` | `1 ; 2` in output context | `1 2`, `1 ; 2` | A definition body without explicit `;` continuation |
| `(1 2)` | One grouped value whose contents are `1 ; 2` | `(1 ; 2)` | `1 2` at root |
| `(1 ; 2)` | One grouped joined value | `(1 2)` | `1 ; 2` at root |
| `(1, 2) ; 3` | Grouped `(1, 2)` item followed by `3` | None of the flat ungrouped cases | `1, 2 ; 3`, `(1, 2, 3)` |
| `1, 2, 3` | Three comma slots/top-level outputs | May display like joined stream | `(1, 2, 3)` |
| `(1, 2, 3)` | One grouped triple | None of the flat output cases | `1, 2, 3` |

The current parser implements this in `ParseOutputLineExprs`, `ParseOutputOperatorExpression`, and `NormalizeCommaOutputJoinPrecedence`. The EBNF and tutorial describe the same model.

## 5. Call-boundary rules

Fixed calls preserve argument expression boundaries.

```katlang
F = a + b

F(1, 2)
F(1 ; 2)
F(1 2)
F((1 ; 2)...)
```

Expected behavior:

- `F(1, 2)` has two argument slots and succeeds for `F = a + b` / `F(a,b)` style two-parameter calls.
- `F(1 ; 2)` has one argument expression whose result is an output sequence. It is not silently split into two fixed arguments.
- `F(1 2)` is the same one-argument shape as `F(1 ; 2)`, not `F(1, 2)`.
- `F((1 ; 2)...)` explicitly supplies the joined outputs as argument items, so it can satisfy fixed slots.

Parser behavior must not depend on callable arity. This is explicit in #101, #123, `AGENTS.md`, `KatLang.ebnf`, the tutorial, parser comments, and `KatLangEngineTests.Run_CallArgumentAdjacency_ReportsOneArgumentNotTwo`.

Fixed-call multi-output property example:

```katlang
Pair = 10, 20
Add(x, y) = x + y

Add(Pair)
Add(Pair...)
```

Expected principle:

- `Add(Pair)` must not silently become `Add(10, 20)`. Current tests assert arity-shaped failure.
- `Add(Pair...)` is explicit sequence supply and succeeds.
- For grouped content, `Add(Pair.content)` still does not supply fixed arguments by itself, while `Add(Pair.content...)` does.

Call delimiter newline rule:

- `F (1, 2)` is `F(1, 2)` because the delimiter is on the same physical line.
- `F` newline `(1, 2)` is `F ; (1, 2)`, not `F(1, 2)`.
- A multiline call must open before the newline: `F(` newline `1, 2` newline `)`.

## 6. Sequence supply `...` rules

Current intended `...` behavior from #123/#124:

- `...` is postfix-only and unary internally.
- It has no source-level right operand.
- It supplies the output chain accumulated to its left at the point where it appears.
- `A ; B...`, `A B...`, and `A` newline `B...` canonicalize like `(A ; B)...`.
- `A ; B... ; C`, `A B... C`, and `A` newline `B...` newline `C` canonicalize like `((A ; B)...) ; C`.
- `A ; (B...)` remains distinct from `A ; B...`.
- `F(X...Y)` is one joined argument: `F((X...) ; Y)`.
- `F(X..., Y)` is two argument slots.

Examples:

| Source | Current canonical idea |
|---|---|
| `A ; B...` | `(A ; B)...` |
| `A B...` | `(A ; B)...` |
| `A` newline `B...` | `(A ; B)...` |
| `A ; B... ; C` | `((A ; B)...) ; C` |
| `A B... C` | `((A ; B)...) ; C` |
| `A ; (B...)` | `A ; grouped(B...)`, distinct from `(A ; B)...` |
| `X...` newline `Y` | `(X...) ; Y` |
| `X... Y` | `(X...) ; Y` |
| `F(X...Y)` | One argument: `(X...) ; Y` |
| `F(X..., Y)` | Two argument slots: `X...` and `Y` |

Verification against current repo:

- `Ast.cs` documents unary `Expr.SequenceSupply(Expr Operand)`.
- `lean/KatLang.lean` defines unary `.sequenceSupply : Expr -> Expr`.
- `Parser.cs` constructs supply only through `CreateSequenceSupply` and states `A...B` is `(A...) ; B`.
- `ParserTests` includes `Parse_EllipsisFollowedByExpression_IsPostfixSupplyThenOutputJoin`, `Parse_CallArgument_PostfixSupplyJoinVsCommaSpread_DiffersInArgumentCount`, and the #123 precedence tests.
- `SequenceSupplyTests` includes fixed-call, dot-call, suffix, and postfix-then-join examples.
- `tutorial.md`, `KatLang.ebnf`, `AGENTS.md`, `.github/agents/katlang-generator.agent.md`, and `experimental/prompts/katlang-generator.txt` match the #123/#124 model.
- Older #119 newline-after-postfix-continuation is superseded; current tests assert a line-ending postfix ellipsis does not continue supply to the next line.

No current mismatch found in inspected sources. One exact spelling, `Count(Pair.content)`, is not called out in the focused tests read for this report; adjacent tests cover `Count(Pair) == 1`, `Add(Pair.content)` failure for fixed calls, and `Pair.content...` explicit supply. Add a focused regression if that exact flat-variadic `.content` spelling becomes design-critical.

## 7. Comma and `...` interaction

#123's tricky cases are current truth:

```katlang
F(a, b, c) = a + b + c

F(1 ; 2, 3...)
F(1, 2 ; 3...)
F(1 2, 3...)
F(1
  2, 3...)
```

Expected canonical parse:

```katlang
F((1 ; 2 ; 3)...)
```

Expected result:

```katlang
6
```

Why: comma binds tighter than semicolon, but when an ungrouped output join appears anywhere in the comma list, the parser canonicalizes the whole comma-separated contribution into an accumulated output chain. If a sequence supply appears after earlier chain contributions, it absorbs the chain accumulated to its left into the supply operand.

Slot-local cases must not over-flatten:

```katlang
A, B...
X(a..., b)
```

These have no output-join trigger in the comma list. Comma slots stay structural, and `B...` / `a...` remains local to its own comma slot. This distinction is explicitly implemented in `NormalizeCommaOutputJoinPrecedence`, `AppendOutputChainContribution`, and `AbsorbChainIntoSupply`, and asserted in parser tests.

## 8. Variadic binding and sequence supply

The authoritative chain is #105 -> #106 -> #108 -> #111 -> #112, with #112 highest among those.

Current principles:

- Variadic capture provenance forwarding: bindings created by `values...`, `(history...)`, `((history...), previous)`, etc. can carry stream provenance.
- Provenance forwarding is not name equality. It depends on source slot provenance plus target assignment to a compatible flat top-level variadic capture.
- Flat variadic-slot sequence supply: ordinary argument segments assigned to the flat variadic capture supply their emitted top-level items after prefix/variadic/suffix allocation.
- Allocation happens before supply. Explicit source `expr...` is stronger and happens before binding.
- Fixed, prefix, suffix, grouped, and patterned boundaries are preserved.
- Dot-call receivers use canonical call allocation, not receiver pre-expansion.

Qmean examples verified by `SequenceSupplyTests`:

```katlang
Vector = range(1,10)
Qmean(args...) = Math.Sqrt(args.map{x * x}.sum / args.count)

Qmean(Vector)
Qmean(Vector...)
range(1,10).Qmean()
```

These can be equivalent because `Vector` is one argument segment assigned to `args...`. After allocation, the segment supplies its emitted top-level items into the flat variadic capture. Explicit `Vector...` supplies before binding but lands in the same all-variadic shape. Dot-call injects the receiver as the leading segment, and because the callee has a leading flat variadic capture with no required suffix, the segment supplies into that capture after allocation.

Suffix examples verified by `SequenceSupplyTests`:

```katlang
Values = 10, 20
Sum(values..., last) = values.sum + last

Sum(Values, 7)   // 37
Sum(Values)      // failure; suffix required / normal boundary preserved
Sum(Values...)   // 30; explicit source supply can satisfy suffix
Values.Sum       // failure like Sum(Values), not Sum(Values...)
Values.Sum(7)    // 37 like Sum(Values, 7)
```

The key difference is allocation timing. `Sum(Values, 7)` assigns `Values` to `values...` and `7` to `last`, then `Values` supplies `10, 20` inside the variadic capture. `Sum(Values...)` expands to `10, 20` before binding, so `10` is variadic and `20` can satisfy `last`. `Values.Sum` is canonical `Sum(Values)`, not `Sum(Values...)`.

Grouped examples:

```katlang
Pair = (10, 20)
Count(args...) = args.count

Count(Pair)          // 1
Pair.Count()         // 1
Count(Pair.content)  // group opened one visible level; add direct test if this exact spelling matters
Count(Pair.content...) // explicit source supply of opened content
```

Current tests explicitly assert `Count(Pair) == 1` and `Pair.Count() == 1`, showing visible grouping is preserved under flat variadic-slot supply. Fixed-call tests assert `Add(Pair.content)` does not spread by itself, and `Add(Pair.content...)` does. The design rule is that grouping is opened only by explicit projection/content semantics or by explicit source supply; there is no recursive flattening.

Grouped/patterned variadic boundaries:

```katlang
CountGroup((values...)) = values.count
CountGroup((1, 2, 3)) // 3
CountGroup(1, 2, 3)   // failure
```

Callable binding plan tests assert that `CountGroup((values...))` is a grouped/nested variadic shape, not a top-level flat variadic signature. Flat variadic-slot supply must not apply to this grouped pattern as if it were `CountGroup(values...)`.

## 9. Dot-call receiver rules

Invariant:

```katlang
receiver.Property(args...)
```

behaves like:

```katlang
Property(receiver, args...)
```

not:

```katlang
Property(receiver..., args...)
```

Why this matters: without this invariant, dot-call syntax would hide a stronger pre-binding expansion than canonical calls. That would reintroduce the hidden auto-spreading that #101 and #112 reject.

Current implementation:

- `Evaluator.BuildLexicalReceiverCallArgs` injects the receiver as the first argument expression.
- For leading flat variadic callees, the receiver segment may carry emitted-count metadata and supply into the variadic capture after allocation.
- A parenthesized sequence-supplied receiver such as `(Values...).Sum` can opt into pre-binding supply for a leading flat variadic receiver parameter.
- Fixed receiver parameters preserve the receiver boundary, even for `(Pair...).Add`; tests assert arity-shaped failure.
- Sequence builtins have separate metadata-driven dot-call receiver behavior and are not the ordinary lexical fallback.

Examples:

```katlang
Values = 10, 20
Sum(values..., last) = values.sum + last

Values.Sum       // like Sum(Values): failure
Values.Sum(7)    // like Sum(Values, 7): 37
(Values...).Sum  // explicit receiver supply: can satisfy leading variadic forms
```

User-defined variadic receiver examples from #105/#112 are both preserved under the refined rule: `(1, 2).Mean` can work when `Mean(vector...)` has a leading flat variadic capture, while `Values.Sum` with a required suffix still behaves like `Sum(Values)`, not `Sum(Values...)`.

## 10. Result display versus semantic structure

Investigated examples:

```katlang
1, 2
1 ; 2
1 2
(1, 2)
(1 ; 2)
(1, 2) ; 3
1, 2 ; 3
```

Current behavior:

- `RunResult.ToDisplayString()` shows multiple emitted top-level outputs as separate visual rows when emitted count is greater than one.
- `1, 2`, `1 ; 2`, and `1 2` can all display as two rows: `1` and `2`.
- `(1, 2)` and `(1 ; 2)` display as one grouped value `(1, 2)`.
- `(1, 2) ; 3` displays grouped `(1, 2)` on one row and `3` on another.
- `1, 2 ; 3` displays as three flat rows: `1`, `2`, `3`.

Answers:

- Are comma and semicolon represented distinctly in AST? Yes. Comma is represented structurally as multiple output/argument entries in `Algorithm.Output`; semicolon is `Expr.OutputJoin`. Mixed comma/join/supply lists can be normalized into a single `OutputJoin`/`SequenceSupply` shape, intentionally losing original comma placement for those mixed cases while preserving semantic grouping.
- Are they flattened before evaluation? Evaluation of `OutputJoin` flattens output-join leaves into emitted top-level items using counted results. Comma output lists are also emitted as top-level items by algorithm output evaluation. Explicit `Expr.Block` grouping remains a grouped item.
- Does result display hide structural differences? Yes for flat top-level streams. Display is presentation-oriented and does not reveal whether flat rows came from comma or `OutputJoin`. It does preserve visible parentheses for grouped values.
- Is there a debug/canonical display that can expose the difference? The current public display does not. `Parser.ParseSyntax(...)` and tests expose AST shape; `DemoApp` contains an internal pretty-printer helper, but there is no public canonical/debug display API identified in this audit.
- Would adding explicit `OutputJoin` in AST improve robustness or only presentation? `OutputJoin` already exists in both C# and Lean, so adding it is not relevant. The open question is whether to expose richer output-origin/provenance or a debug AST display. That would be presentation/tooling/provenance work, not a core AST addition.
- Are there tests that verify semantic structure, not only display string? Yes. `ParserTests` assert `Expr.OutputJoin`, `Expr.SequenceSupply`, grouped `Expr.Block`, argument counts, and normalization shapes. `SequenceSupplyTests` and callable binding plan tests assert runtime binding behavior. `KatLangEngineTests` assert display behavior.

Risk assessment: the current internal AST is robust enough to distinguish comma and output join before evaluation. After evaluation and public display, flat results intentionally do not preserve source-origin distinction. That is aligned with #122, but can confuse design discussions if display rows are mistaken for semantic grouping.

## 11. Current implementation audit

Concept representation map:

| Concept | Current files/classes/functions | Notes |
|---|---|---|
| AST shape | `src/KatLang/Ast.cs`, `lean/KatLang.lean` | `Expr.OutputJoin` binary; `Expr.SequenceSupply` unary; Lean mirrors both. |
| Comma list / delimited output list | `Parser.ParseOutputLineExprs`, `Algorithm.Output` | Comma creates separate entries unless mixed join/supply normalization folds them. |
| Output chain / semicolon | `Parser.ParseOutputOperatorExpression`, `Evaluator.EvalOutputJoinCounted`, Lean `outputJoinLeaves` and eval path | Explicit `;` and adjacency build `OutputJoin`; evaluator flattens leaves into emitted top-level items. |
| Implicit adjacency / newline joining | `Parser.StartsImplicitAdjacencySemicolon`, `Parser.MayContinueClosedExpression`, algorithm loop `AppendOutputContribution` | Same-line adjacency always where expression can start; newline adjacency in output contexts; definition bodies/open targets remain line-bounded. |
| Comma/output/supply normalization | `Parser.NormalizeCommaOutputJoinPrecedence`, `AppendOutputChainContribution`, `AbsorbChainIntoSupply` | Implements #123 absorption, slot-local preservation, and no forward reach. |
| Sequence supply | `Expr.SequenceSupply`, `Parser.CreateSequenceSupply`, `Evaluator.EvalSequenceSupplyCounted`, Lean `peelSequenceSupply` | Unary postfix; nested supplies peeled stack-safely. |
| Call argument binding | `Evaluator.BindFixedUserCall`, `ResolveArgAlgsWithSequenceSupply`, `BindPatternedUserCall` | Fixed calls expand only explicit sequence-supply args; ordinary multi-output args stay one boundary. |
| Variadic capture binding | `BindVariadicUserCall`, `BuildVariadicBindingInputSlots`, `BindingInputSlot.VariadicSlotEmittedCount`, Lean variadic item/stream functions | Allocation before supply; provenance and emitted-count metadata drive flat variadic capture behavior. |
| Dot-call receiver binding | `BuildLexicalReceiverCallArgs`, `TryGetParenthesizedSequenceSuppliedReceiver`, `HasLeadingFlatVariadicParameter`, Lean `prepareLexicalDotCallArgs` | Ordinary lexical receiver is one leading segment; explicit supplied receiver and sequence builtins are special cases. |
| Result display | `RunResult.ToDisplayString`, `TopLevelDisplayRows`, `Format` | Multiple emitted top-level items display as rows; grouped values keep parentheses. |
| Editor semantics | `src/KatLang/Semantics/*`, semantic tests | Must consume parsed/elaborated ASTs; source-backed identifiers only. |
| Docs/tutorial/generator | `AGENTS.md`, `KatLang.ebnf`, `tutorial.md`, `.github/agents/katlang-generator.agent.md`, `experimental/prompts/katlang-generator.txt` | Inspected current guidance matches #123/#124, not #104. |

Implementation match to latest intended model:

- Matches #123/#124 for postfix unary `...`, same-line/newline adjacency, comma never implicit, comma/supply absorption, slot-local preservation, call delimiter newline boundary, indexing same-line boundary, and open target grammar.
- Matches #111/#112 for flat variadic-slot supply and dot-call receiver allocation.
- Matches #122 display separation: display rows are presentation, not semantic grouping.
- Known caveat: the public display is not a structural debugger and intentionally hides comma versus join origin for flat streams.

## 12. Regression matrix

| Category | Example | Expected behavior |
|---|---|---|
| Root output context | `1` newline `2` | Parses/evaluates like `1 ; 2`; emits `1`, `2`. |
| Algorithm body/output context | `A = { 1` newline `2 }` | Body output joins; equivalent to `{ 1 ; 2 }`. |
| Explicit `Output = ...` | `Output = 1 ; 3` | Valid explicit joined output. |
| Explicit/implicit mixing | `Output = 1` newline `3` | Parse diagnostic: explicit output cannot mix with implicit row. |
| Parenthesized group | `(1 2)` | One grouped value equivalent to `(1 ; 2)`, displayed `(1, 2)`. |
| Call argument list | `Add(x, y)=x+y` newline `Add(1 2)` | One argument `1 ; 2`; arity-shaped failure for two-parameter `Add`. |
| Dot-call receiver | `Pair = 10,20; Add(x,y)=x+y; Pair.Add` | Failure; receiver does not spread into fixed params. |
| Flat fixed call | `Pair = 10,20; Add(x,y)=x+y; Add(Pair...)` | Explicit supply succeeds, result `30`. |
| Flat fixed no auto-spread | `Pair = 10,20; Add(x,y)=x+y; Add(Pair)` | Failure; one argument expression. |
| Flat variadic call | `Values=10,20; Count(args...)=args.count; Count(Values)` | `2`; segment assigned to `args...` supplies emitted items after allocation. |
| Variadic with suffix | `Sum(values..., last)=values.sum+last; Sum(Values,7)` | `37`; `Values` assigned to variadic slot, `7` to suffix. |
| Variadic suffix missing | `Sum(values..., last)=values.sum+last; Sum(Values)` | Failure; `Values` does not pre-expand to satisfy suffix. |
| Explicit supply before suffix binding | `Sum(values..., last)=values.sum+last; Sum(Values...)` | `30`; explicit supply can satisfy suffix. |
| Grouped variadic pattern | `CountGroup((values...))=values.count; CountGroup((1,2,3))` | `3`; grouped pattern consumes one top-level slot. |
| Grouped variadic boundary | `CountGroup((values...))=values.count; CountGroup(1,2,3)` | Failure; grouped pattern is not top-level flat variadic. |
| Sequence supply before/after comma | `F(a,b,c)=a+b+c; F(1 ; 2, 3...)` | Canonical `F((1 ; 2 ; 3)...)`, result `6`. |
| Slot-local supply | `X(a..., b)` | Two argument slots; no over-flattening because no output join trigger. |
| Sequence supply after output join | `A ; B... ; C` | `((A ; B)...) ; C`; later `C` stays outside supply. |
| Same-line adjacency | `1 2` | Equivalent to `1 ; 2`, never comma or multiplication. |
| Newline adjacency | `1` newline `2` | Equivalent to `1 ; 2` in output contexts. |
| Call delimiter same-line | `F (1, 2)` | Same call as `F(1, 2)` if `F` is callable. |
| Call delimiter newline boundary | `F` newline `(1, 2)` | Output join `F ; (1, 2)`, not a call. |
| Brace callback same-line | `values.map { n * 2 }` | Same callback call as `values.map{n * 2}`. |
| Brace newline boundary | `values.map` newline `{ n * 2 }` | Not callback continuation; separate output/join shape or diagnostics. |
| Indexing same-line | `Pair : 0` | Indexing if same physical line. |
| Indexing newline boundary | `Pair` newline `:0` | Parse diagnostic; does not become `Pair:0`. |
| Open declaration grammar | `open A, B` | One open declaration with comma-separated targets. |
| Open newline target missing | `open` newline `A` | Missing-target diagnostic; `A` remains separate row/statement. |
| Open semicolon invalid | `open A ; B` | Diagnostic: open lists use comma, not semicolon. |
| Open supply invalid | `open A...` | Targeted diagnostic: sequence supply not valid in open targets. |

## 13. Open design questions

These are open or risky only in the sense of future design pressure; they are not implementation recommendations in this report.

1. Should `OutputJoin` be a first-class AST node?
   - Current answer from implementation: it already is first-class in C# and Lean. The real future question is whether source-level output-origin/provenance should survive evaluation/display. Grounding: #75/#122 require output join semantics; `Ast.cs` and `lean/KatLang.lean` already model it.

2. Should normal result display distinguish output-join from comma structure?
   - #122 explicitly says result-window display is presentation only. Current `ToDisplayString()` and tests show `1, 2`, `1 ; 2`, and `1 2` can all display as rows. This is useful for report readability but can confuse semantic discussions.

3. Is `1, 2 ; 3` intuitive enough if it displays like `1, 2, 3`?
   - #122 decided comma has higher priority and semicolon concatenates top-level streams without grouping. Current display tests assert flat rows. The risk is user intuition, not implementation mismatch.

4. Should `F(1 2)` be allowed everywhere as implicit `F(1 ; 2)`?
   - #123 says yes where adjacency applies; generator guidance still recommends explicit `;` or commas for readability. Risk: users may intend two arguments, but the current constitution says comma is never implicit and parser must not inspect arity.

5. Are there contexts where implicit adjacency should not apply?
   - Current exclusions are definition bodies across physical newlines, `open` target lists, declaration starters, same-line postfix continuations, and physical-newline call/index/grace/operator continuation. #123 documents these boundaries. Any future exception must be evaluated against definition-boundary safety.

6. Does `...` applying to accumulated output chain create surprising behavior with comma?
   - #123 explicitly accepts the surprising cases: `F(1 ; 2, 3...)` and `F(1, 2 ; 3...)` both become `F((1 ; 2 ; 3)...)`. Parser tests cover this. Risk: users may expect `3...` to remain slot-local despite the join trigger.

7. Are any docs still reflecting #104's old `...`-replaces-`;` model?
   - Inspected current `AGENTS.md`, `KatLang.ebnf`, `tutorial.md`, `.github/agents/katlang-generator.agent.md`, and `experimental/prompts/katlang-generator.txt` reflect #123/#124. No stale #104 guidance found in the scan. Historical issue text remains obsolete by design.

8. Are any tests still based on older binary/right-operand `...` semantics?
   - Current parser/evaluator tests assert the opposite: `A...B` is `OutputJoin(SequenceSupply(A), B)`, `F(X...Y)` is one joined argument, and `F(X..., Y)` is two slots. No stale binary-supply tests found in inspected target areas.

9. Are generator prompts and tutorials consistent with #123/#124?
   - Yes in inspected files. They state semicolon/adjacency output join, comma never implicit, postfix unary `...`, no right operand, and dot-call receiver as one leading argument segment.

10. Should there be a public canonical/debug AST display?
    - Current tests inspect AST programmatically, and `DemoApp` has an internal pretty-printer, but public `ToDisplayString()` is intentionally presentation-oriented. A debug display could help future design reviews without changing semantics.

11. Should exact `.content` plus flat-variadic spelling have dedicated tests?
    - The surrounding behavior is covered, but this report did not find a direct `Count(Pair.content)` test. If `.content` with flat variadic allocation becomes a design hinge, add a focused test before changing anything.

## 14. Final synthesized core principles

Candidate design constitution for future KatLang prompts:

1. Comma creates structural slots. It separates output slots and call arguments; it is never implicit.
2. Semicolon joins outputs. It concatenates emitted top-level output streams in the current output chain and does not create an explicit group.
3. Adjacency means semicolon, never comma. Same-line adjacency and newline adjacency are implicit output join where allowed; parsing must not depend on callable arity.
4. Parentheses preserve explicit grouping. Grouped values remain visible grouped items unless an explicit projection/supply rule opens them.
5. Fixed calls preserve argument expression boundaries. Ordinary multi-output expressions do not auto-spread into fixed parameters.
6. Postfix `...` explicitly supplies output sequences. It is postfix-only in source and unary internally; it has no right operand.
7. `...` supplies the output chain accumulated to its left at the point where it appears. Later output joins continue after the spread.
8. Comma binds tighter than output join, and mixed comma/output/supply lists normalize through one canonical output-chain rule. Slot-local comma structure remains intact when no output join trigger exists.
9. Flat variadic captures may consume top-level emitted items only after argument segments are allocated to prefix, variadic, and suffix slots. Explicit `expr...` supplies before binding and can therefore satisfy prefix/suffix slots.
10. Variadic provenance forwarding is narrow. Only bindings created by actual variadic captures may forward stream provenance, and only into compatible flat top-level variadic captures.
11. Dot-call is canonical call syntax with receiver as the first argument segment. `receiver.Property(args...)` means `Property(receiver, args...)`, not `Property(receiver..., args...)`.
12. Sequence/variadic builtins may have explicit receiver-expansion metadata, but ordinary user-defined lexical dot-call must preserve the receiver segment boundary.
13. Display formatting must not redefine semantic grouping. Result rows are presentation; explicit parentheses are semantic grouping.
14. The parser must not reinterpret syntax using callable arity, inferred types, or runtime values. Syntax boundaries are source rules, not semantic guesses.
15. `open` is a declaration/import directive with its own comma-list grammar. It does not use output join, adjacency, or sequence supply syntax.
16. Lean and C# must stay aligned for observable operator, binding, and display-adjacent semantics. Any future change in these areas requires Lean review and focused C#/Lean regression coverage.

## Verification performed

- Read and synthesized the requested issues, using web/API content where available and applying the requested truth ranking.
- Inspected current C# parser/evaluator/AST/display/binding files, Lean model/tests, C# tests, tutorial, grammar, generator prompts, and AGENTS guidance.
- Ran focused existing tests: `dotnet test .\KatLang.slnx --filter "FullyQualifiedName~KatLang.Tests.SequenceSupplyTests|FullyQualifiedName~KatLang.Tests.KatLangEngineTests" -p:UseSharedCompilation=false`. Result: 176 tests passed, 0 failed.
