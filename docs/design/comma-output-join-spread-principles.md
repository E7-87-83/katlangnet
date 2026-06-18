# Comma, Grouping, And Spread Principles

This note supersedes the earlier flat stream-composition design notes. Issue #125 changed the model to expression-list slots plus sequence values.

## Current Principles

1. Comma is the global expression-list separator.
2. A bare expression list is consumed by its surrounding syntax: root output consumes it as output slots, call syntax consumes it as argument slots, and `open` consumes its own declaration target list.
3. Parentheses materialize an expression list as one sequence value.
4. Allowed expression adjacency also creates expression-list slots. Newline-separated `1`/`2`/`3` and same-line `1 2 3` behave like `1, 2, 3`.
5. Semicolon is not supported as expression syntax. It is not an alternative separator or sequence constructor.
6. Use parentheses to materialize one sequence value. Therefore `(1, 2, 3)` is one value, while `1, 2, 3` is three surrounding slots.
7. `...` is unary postfix spread. It spreads the evaluated sequence value of its immediate operand into the surrounding structural context and never consumes a right operand.
8. `values...` parameters destructure one sequence-valued argument. Inline comma or adjacency arguments are not implicitly collected into that parameter.
9. Dot-call receiver syntax remains canonical call syntax: `receiver.Property(args...)` means `Property(receiver, args...)`, not `Property(receiver..., args...)`.
10. `open` remains declaration syntax with its dedicated comma-only target grammar; it does not use ordinary expression-list or spread syntax.

## Examples

```katlang
1, 2, 3              // three output slots at root
1 2 3                // also three output slots where adjacency is allowed
(1, 2, 3)            // one sequence value
1, (2, 3)            // two output slots: atom 1 and sequence value (2, 3)
(1, 2), 3            // two output slots: sequence value (1, 2) and atom 3
F(1, 2, 3)           // three call argument slots
F((1, 2, 3))         // one sequence argument
F((1, 2, 3), (4, 5, 6)) // two call arguments, each a sequence value
```

Table-like output uses sequence-value rows:

```katlang
Reports = (7, 6, 4, 2, 1),
          (1, 2, 7, 8, 9),
          (9, 7, 6, 2, 1)
```

Without row parentheses this is one flat expression list. Use one parenthesized value per row when a sequence of row values is intended.
