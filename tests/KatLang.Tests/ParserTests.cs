namespace KatLang.Tests;

public class ParserTests
{
    private static List<Expr> OutputJoinLeaves(Expr expr)
    {
        var leaves = new List<Expr>();
        AddOutputJoinLeaves(expr, leaves);
        return leaves;
    }

    private static void AddOutputJoinLeaves(Expr expr, List<Expr> leaves)
    {
        if (expr is Expr.OutputJoin(var left, var right))
        {
            AddOutputJoinLeaves(left, leaves);
            AddOutputJoinLeaves(right, leaves);
            return;
        }

        leaves.Add(expr);
    }

    [Fact]
    public void Parse_EmptySource_ReturnsEmptyAlgorithm()
    {
        var result = Parser.ParseSyntax("");

        Assert.False(result.HasErrors);
        Assert.Empty(result.Root.Properties);
        Assert.Empty(result.Root.Output);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsEmptyAlgorithm()
    {
        var result = Parser.ParseSyntax("   \n\t  ");

        Assert.False(result.HasErrors);
        Assert.Empty(result.Root.Properties);
        Assert.Empty(result.Root.Output);
    }

    [Fact]
    public void Parse_SingleNumber_ReturnsNumExpr()
    {
        var result = Parser.ParseSyntax("42");

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Output);
        Assert.IsType<Expr.Num>(result.Root.Output[0]);
        Assert.Equal(42, ((Expr.Num)result.Root.Output[0]).Value);
    }

    [Fact]
    public void Parse_NegativeNumber_ReturnsUnaryExpr()
    {
        var result = Parser.ParseSyntax("-5");

        Assert.False(result.HasErrors);
        var unary = Assert.IsType<Expr.Unary>(result.Root.Output[0]);
        Assert.Equal(UnaryOp.Minus, unary.Op);
        Assert.Equal(5, ((Expr.Num)unary.Operand).Value);
    }

    [Fact]
    public void Parse_DoubleNegative_ReturnsNestedUnary()
    {
        var result = Parser.ParseSyntax("--5");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Unary>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.Unary>(outer.Operand);
        Assert.Equal(5, ((Expr.Num)inner.Operand).Value);
    }

    [Fact]
    public void Parse_Identifier_ReturnsResolveExpr()
    {
        var result = Parser.ParseSyntax("foo");

        Assert.False(result.HasErrors);
        var resolve = Assert.IsType<Expr.Resolve>(result.Root.Output[0]);
        Assert.Equal("foo", resolve.Name);
    }

    [Fact]
    public void Parse_EmptyBuiltin_AcceptsCanonicalEmptyOutputName()
    {
        var result = Parser.ParseSyntax("empty");

        Assert.False(result.HasErrors);
        var resolve = Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Output));
        Assert.Equal(BuiltinRegistry.EmptyBuiltinName, resolve.Name);
    }

    [Fact]
    public void Parse_EmptyBuiltin_AcceptsParenAndBraceBodies()
    {
        var paren = Parser.ParseSyntax("(empty)");
        Assert.False(paren.HasErrors);
        var parenResolve = Assert.IsType<Expr.Resolve>(Assert.Single(paren.Root.Output));
        Assert.Equal(BuiltinRegistry.EmptyBuiltinName, parenResolve.Name);

        var brace = Parser.ParseSyntax("{empty}");
        Assert.False(brace.HasErrors);
        var braceBlock = Assert.IsType<Expr.Block>(Assert.Single(brace.Root.Output));
        var braceResolve = Assert.IsType<Expr.Resolve>(Assert.Single(braceBlock.Algorithm.Output));
        Assert.Equal(BuiltinRegistry.EmptyBuiltinName, braceResolve.Name);
    }

    [Fact]
    public void Parse_EmptyParenAndBrace_DoNotParseAsEmptyBuiltin()
    {
        var paren = Parser.ParseSyntax("()");
        Assert.False(paren.HasErrors);
        var parenBlock = Assert.IsType<Expr.Block>(Assert.Single(paren.Root.Output));
        Assert.Empty(parenBlock.Algorithm.Output);

        var brace = Parser.ParseSyntax("{}");
        Assert.False(brace.HasErrors);
        var braceBlock = Assert.IsType<Expr.Block>(Assert.Single(brace.Root.Output));
        Assert.Empty(braceBlock.Algorithm.Output);
    }

    [Fact]
    public void Parse_EmptyBuiltin_CannotBeRedefined()
    {
        var property = Parser.ParseSyntax("empty = 1\nempty");
        Assert.True(property.HasErrors);
        Assert.Contains(property.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot be redefined"));
        Assert.Empty(property.Root.Properties);
        var propertyOutput = Assert.IsType<Expr.Resolve>(Assert.Single(property.Root.Output));
        Assert.Equal(BuiltinRegistry.EmptyBuiltinName, propertyOutput.Name);

        var clause = Parser.ParseSyntax("empty(x) = x\nempty");
        Assert.True(clause.HasErrors);
        Assert.Contains(clause.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot be redefined"));
        Assert.Empty(clause.Root.Properties);
        var clauseOutput = Assert.IsType<Expr.Resolve>(Assert.Single(clause.Root.Output));
        Assert.Equal(BuiltinRegistry.EmptyBuiltinName, clauseOutput.Name);
    }

    [Fact]
    public void Parse_EmptyBuiltin_CannotBeUsedAsClauseBinder()
    {
        var result = Parser.ParseSyntax("F(empty) = empty\nF(0)");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot be used as a parameter or pattern binder"));
    }

    [Fact]
    public void Parse_Self_NowParsesAsResolve()
    {
        var result = Parser.ParseSyntax("self");

        Assert.False(result.HasErrors);
        var resolve = Assert.IsType<Expr.Resolve>(result.Root.Output[0]);
        Assert.Equal("self", resolve.Name);
    }

    [Fact]
    public void Parse_Addition_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 + 2");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
        Assert.Equal(1, ((Expr.Num)binary.Left).Value);
        Assert.Equal(2, ((Expr.Num)binary.Right).Value);
    }

    [Fact]
    public void Parse_Subtraction_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("5 - 3");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Sub, binary.Op);
    }

    [Fact]
    public void Parse_Multiplication_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("4 * 3");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Mul, binary.Op);
    }

    [Fact]
    public void Parse_LessThan_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 < 2");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Lt, binary.Op);
    }

    [Fact]
    public void Parse_GreaterThan_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("2 > 1");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Gt, binary.Op);
    }

    [Fact]
    public void Parse_OperatorPrecedence_MultiplicationBeforeAddition()
    {
        var result = Parser.ParseSyntax("1 + 2 * 3");

        Assert.False(result.HasErrors);
        var add = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.Equal(1, ((Expr.Num)add.Left).Value);
        var mul = Assert.IsType<Expr.Binary>(add.Right);
        Assert.Equal(BinaryOp.Mul, mul.Op);
    }

    [Fact]
    public void Parse_OperatorPrecedence_ComparisonAfterArithmetic()
    {
        var result = Parser.ParseSyntax("1 + 2 < 4");

        Assert.False(result.HasErrors);
        var cmp = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Lt, cmp.Op);
        var add = Assert.IsType<Expr.Binary>(cmp.Left);
        Assert.Equal(BinaryOp.Add, add.Op);
    }

    [Fact]
    public void Parse_LeftAssociativity_Addition()
    {
        var result = Parser.ParseSyntax("1 - 2 - 3");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Sub, outer.Op);
        Assert.Equal(3, ((Expr.Num)outer.Right).Value);
        var inner = Assert.IsType<Expr.Binary>(outer.Left);
        Assert.Equal(BinaryOp.Sub, inner.Op);
    }

    [Fact]
    public void Parse_Parentheses_OverridePrecedence()
    {
        var result = Parser.ParseSyntax("(1 + 2) * 3");

        Assert.False(result.HasErrors);
        var mul = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Mul, mul.Op);
        var add = Assert.IsType<Expr.Binary>(mul.Left);
        Assert.Equal(BinaryOp.Add, add.Op);
    }

    [Fact]
    public void Parse_CommaList_ReturnsMultipleOutputs()
    {
        var result = Parser.ParseSyntax("1, 2, 3");

        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Root.Output.Count);
    }

    [Fact]
    public void Parse_Property_ReturnsSingleProperty()
    {
        var result = Parser.ParseSyntax("X = 5");

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.Equal("X", result.Root.Properties[0].Name);
        Assert.Single(result.Root.Properties[0].Value.Output);
        Assert.Empty(result.Root.Output);
    }

    [Fact]
    public void Parse_PropertyWithOutput_BothPresent()
    {
        var source = """
            X = 5
            X
            """;
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.Single(result.Root.Output);
        var resolve = Assert.IsType<Expr.Resolve>(result.Root.Output[0]);
        Assert.Equal("X", resolve.Name);
    }

    [Fact]
    public void Parse_UnaryOutputAfterBraceProperty_StaysAtRootLevel()
    {
        var source = """
            A = {
                X = 1
            }
            -A
            """;

        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("A", property.Name);
        Assert.Empty(property.Value.Output);

        var unary = Assert.IsType<Expr.Unary>(Assert.Single(result.Root.Output));
        Assert.Equal(UnaryOp.Minus, unary.Op);
        var operand = Assert.IsType<Expr.Resolve>(unary.Operand);
        Assert.Equal("A", operand.Name);
    }

    [Fact]
    public void Parse_MultipleProperties_AllParsed()
    {
        var source = """
            A = 1
            B = 2
            C = 3
            """;
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Root.Properties.Count);
        Assert.Equal("A", result.Root.Properties[0].Name);
        Assert.Equal("B", result.Root.Properties[1].Name);
        Assert.Equal("C", result.Root.Properties[2].Name);
        Assert.Empty(result.Root.Output);
    }

    [Fact]
    public void Parse_Index_ReturnsIndexExpr()
    {
        var result = Parser.ParseSyntax("X:0");

        Assert.False(result.HasErrors);
        var index = Assert.IsType<Expr.Index>(result.Root.Output[0]);
        var target = Assert.IsType<Expr.Resolve>(index.Target);
        Assert.Equal("X", target.Name);
        Assert.Equal(0, ((Expr.Num)index.Selector).Value);
    }

    [Fact]
    public void Parse_DotAccess_ReturnsDotCallExpr()
    {
        var result = Parser.ParseSyntax("X.count");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("count", dotCall.Name);
        var target = Assert.IsType<Expr.Resolve>(dotCall.Target);
        Assert.Equal("X", target.Name);
    }

    [Fact]
    public void Parse_Call_ReturnsCallExpr()
    {
        var result = Parser.ParseSyntax("F(1, 2)");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        var func = Assert.IsType<Expr.Resolve>(call.Function);
        Assert.Equal("F", func.Name);
        Assert.Equal(2, call.Args.Output.Count);
    }

    [Fact]
    public void Parse_CallWithBraces_WrapsInBlock()
    {
        // F{x + 1} desugars to F({x + 1}) — the brace content becomes an
        // Expr.Block inside a non-parametrized outer args algorithm.
        var result = Parser.ParseSyntax("F{x + 1}");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.False(call.Args.IsParametrized);
        var block = Assert.IsType<Expr.Block>(call.Args.Output[0]);
        Assert.True(block.Algorithm.IsParametrized);
    }

    [Fact]
    public void Parse_CallWithParens_NotParametrized()
    {
        var result = Parser.ParseSyntax("F(1)");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.False(call.Args.IsParametrized);
    }

    [Fact]
    public void Parse_DotCall_WithArgs_ReturnsDotCallWithArgs()
    {
        var result = Parser.ParseSyntax("X.Method(1)");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("Method", dotCall.Name);
        Assert.IsType<Expr.Resolve>(dotCall.Target);
        Assert.NotNull(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_TrailingBlockWithSpace_AttachesAsDotCallArgs()
    {
        var result = Parser.ParseSyntax("range(0, 5).filter { n > 2 }.count");

        Assert.False(result.HasErrors);
        var countCall = Assert.IsType<Expr.DotCall>(Assert.Single(result.Root.Output));
        Assert.Equal("count", countCall.Name);

        var filterCall = Assert.IsType<Expr.DotCall>(countCall.Target);
        Assert.Equal("filter", filterCall.Name);
        Assert.NotNull(filterCall.Args);
        var predicateBlock = Assert.IsType<Expr.Block>(Assert.Single(filterCall.Args!.Output));
        Assert.True(predicateBlock.Algorithm.IsParametrized);
    }

    [Fact]
    public void Parse_DotCall_ReceiverIsLeftSide()
    {
        // Lean: A.B = dotCall(resolve("A"), "B", none) — receiver is left of dot
        var result = Parser.ParseSyntax("A.B");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("B", dotCall.Name);
        var target = Assert.IsType<Expr.Resolve>(dotCall.Target);
        Assert.Equal("A", target.Name);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_WithArgs_ReceiverIsLeftSide()
    {
        // Lean: A.B(args) = dotCall(resolve("A"), "B", some args)
        var result = Parser.ParseSyntax("A.B(1, 2)");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("B", dotCall.Name);
        var target = Assert.IsType<Expr.Resolve>(dotCall.Target);
        Assert.Equal("A", target.Name);
        Assert.NotNull(dotCall.Args);
        Assert.Equal(2, dotCall.Args!.Output.Count);
    }

    [Fact]
    public void Parse_DotCall_NumericLiteralReceiver()
    {
        // 5.Square → DotCall(Num(5), "Square", null)
        // Lexer: 5 is integer token (dot not consumed as decimal since 'S' is not a digit)
        var result = Parser.ParseSyntax("5.Square");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("Square", dotCall.Name);
        var target = Assert.IsType<Expr.Num>(dotCall.Target);
        Assert.Equal(5, target.Value);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_NumericLiteralReceiver_WithArgs()
    {
        // 5.Add(3) → DotCall(Num(5), "Add", args([Num(3)]))
        var result = Parser.ParseSyntax("5.Add(3)");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("Add", dotCall.Name);
        Assert.IsType<Expr.Num>(dotCall.Target);
        Assert.NotNull(dotCall.Args);
        Assert.Single(dotCall.Args!.Output);
    }

    [Fact]
    public void Parse_DotCall_ParenExprReceiver()
    {
        // (2 + 3).Square → DotCall(Binary(Add, Num(2), Num(3)), "Square", null)
        var result = Parser.ParseSyntax("(2 + 3).Square");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("Square", dotCall.Name);
        Assert.IsType<Expr.Binary>(dotCall.Target);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_DecimalLiteralReceiver()
    {
        // 5.0.Square → DotCall(Num(5.0), "Square", null)
        // Lexer: 5.0 is decimal token, then dot, then identifier
        var result = Parser.ParseSyntax("5.0.Square");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("Square", dotCall.Name);
        var target = Assert.IsType<Expr.Num>(dotCall.Target);
        Assert.Equal(5.0m, target.Value);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_Block_ReturnsBlockExpr()
    {
        var result = Parser.ParseSyntax("{1}");

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        Assert.True(block.Algorithm.IsParametrized);
    }

    [Fact]
    public void Parse_GroupingParens_UnwrapsExpression()
    {
        var result = Parser.ParseSyntax("(1)");

        Assert.False(result.HasErrors);
        var num = Assert.IsType<Expr.Num>(result.Root.Output[0]);
        Assert.Equal(1, num.Value);
    }

    [Fact]
    public void Parse_ParenthesizedReference_PreservesBlockLayer()
    {
        var result = Parser.ParseSyntax("(Inner)");

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.Resolve>(Assert.Single(block.Algorithm.Output));
        Assert.Equal("Inner", inner.Name);
    }

    [Fact]
    public void Parse_DoubleParenthesizedReference_PreservesNestedBlockLayer()
    {
        var result = Parser.ParseSyntax("((Inner))");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.Block>(Assert.Single(outer.Algorithm.Output));
        var reference = Assert.IsType<Expr.Resolve>(Assert.Single(inner.Algorithm.Output));
        Assert.Equal("Inner", reference.Name);
    }

    [Fact]
    public void Parse_Ellipsis_ReturnsSequenceSupplyExpr()
    {
        var result = Parser.ParseSyntax("A...B");

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(result.Root.Output[0]);
        Assert.IsType<Expr.Resolve>(sequenceSupply.Left);
        Assert.IsType<Expr.Resolve>(sequenceSupply.Right);
    }

    [Theory]
    [InlineData("A...B")]
    [InlineData("A ...B")]
    public void Parse_EllipsisTightRightOperand_ReturnsSequenceSupplyPair(string source)
    {
        // The expression-side right operand must immediately follow '...'.
        // Whitespace before '...' stays insignificant.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
    }

    [Theory]
    [InlineData("A... B")]
    [InlineData("A ... B")]
    [InlineData("A...\nB")]
    public void Parse_EllipsisSpacedRightExpression_IsPostfixSupplyThenAdjacency(string source)
    {
        // Whitespace or a newline after '...' ends the supply: the supply is
        // postfix (A...empty) and the following expression joins as an
        // implicit ';', so these parse as (A...) ; B.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(outputJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_PostfixEllipsis_DesugarsToEmptySequenceSupplyRightOperand()
    {
        var result = Parser.ParseSyntax("A...");

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(result.Root.Output[0]);
        Assert.IsType<Expr.Resolve>(sequenceSupply.Left);
        var empty = Assert.IsType<Expr.Resolve>(sequenceSupply.Right);
        Assert.Equal("empty", empty.Name);
    }

    [Fact]
    public void Parse_LineEndingPostfixEllipsis_JoinsSeparateBodyContributions()
    {
        var result = Parser.ParseSyntax(
            """
            A = range(1, 3)

            A...
            A
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(outputJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_LineEndingPostfixEllipsisWithExplicitComma_KeepsNextLineSeparate()
    {
        var result = Parser.ParseSyntax(
            """
            A = range(1, 3)

            A...,
            A
            """);

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Output.Count);

        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(result.Root.Output[0]);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(result.Root.Output[1]).Name);
    }

    [Fact]
    public void Parse_OrdinaryCompleteExpressionsAcrossNewlines_ImplicitlyJoinOutput()
    {
        var result = Parser.ParseSyntax(
            """
            A = range(1, 3)

            A
            A
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_LeadingEllipsisContinuation_IsParseError()
    {
        var result = Parser.ParseSyntax(
            """
            A = range(1, 3)

            A
            ...A
            """);

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Parse_LineEndingPostfixEllipsisWithTrailingComment_JoinsSeparateBodyContributions()
    {
        var result = Parser.ParseSyntax(
            """
            A = range(1, 3)

            A... // no longer continues sequence supply on the next line
            A
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(outputJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Theory]
    [InlineData("A = range(1, 3)\n\nA...A\nA")]
    [InlineData("A = range(1, 3)\n\nA...A ; A")]
    public void Parse_NewlineAfterSequenceSupply_MatchesExplicitSemicolonShape(string source)
    {
        // Newline adjacency is an implicit ';' with exactly the explicit
        // semicolon precedence, so the next line joins into the supply's
        // right operand the same way `A...A ; A` parses as `A...(A ; A)`.
        // The '...' operator itself still never continues across lines.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Right);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_CallEndingAfterInnerPostfixEllipsis_DoesNotContinueSequenceSupply()
    {
        var result = Parser.ParseSyntax(
            """
            F(x...)
            y
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));

        var call = Assert.IsType<Expr.Call>(outputJoin.Left);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(call.Args.Output));
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_CallEndingAfterInnerPostfixEllipsisWithTrailingComment_DoesNotContinueSequenceSupply()
    {
        var result = Parser.ParseSyntax(
            """
            F(x...) // the physical line ends with ')' before the comment
            y
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));

        var call = Assert.IsType<Expr.Call>(outputJoin.Left);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(call.Args.Output));
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_ParenthesizedPostfixEllipsis_DoesNotContinueSequenceSupply()
    {
        var result = Parser.ParseSyntax(
            """
            (x...)
            y
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));

        var block = Assert.IsType<Expr.Block>(outputJoin.Left);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(block.Algorithm.Output));
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_ParenthesizedPostfixEllipsisWithTrailingComment_DoesNotContinueSequenceSupply()
    {
        var result = Parser.ParseSyntax(
            """
            (x...) // the physical line ends with ')' before the comment
            y
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));

        var block = Assert.IsType<Expr.Block>(outputJoin.Left);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(block.Algorithm.Output));
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_UnparenthesizedSequenceSupply_RemainsBareSequenceSupply()
    {
        var result = Parser.ParseSyntax("A...B");

        Assert.False(result.HasErrors);
        Assert.IsType<Expr.SequenceSupply>(result.Root.Output[0]);
    }

    [Fact]
    public void Parse_ParenthesizedSequenceSupply_ReturnsBlockExpr()
    {
        var result = Parser.ParseSyntax("(A...B)");

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        Assert.False(block.Algorithm.IsParametrized);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(block.Algorithm.Output));
        Assert.IsType<Expr.Resolve>(sequenceSupply.Left);
        Assert.IsType<Expr.Resolve>(sequenceSupply.Right);
    }

    [Fact]
    public void Parse_DoubleParenthesizedSequenceSupply_PreservesOuterBlockLayer()
    {
        var result = Parser.ParseSyntax("((A...B))");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.Block>(Assert.Single(outer.Algorithm.Output));
        Assert.IsType<Expr.SequenceSupply>(Assert.Single(inner.Algorithm.Output));
    }

    [Fact]
    public void Parse_ScalarParentheses_RemainTransparent()
    {
        var scalar = Parser.ParseSyntax("(3)");
        Assert.False(scalar.HasErrors);
        var num = Assert.IsType<Expr.Num>(scalar.Root.Output[0]);
        Assert.Equal(3, num.Value);

        var arithmetic = Parser.ParseSyntax("((1 + 2))");
        Assert.False(arithmetic.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(arithmetic.Root.Output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
    }

    [Fact]
    public void Parse_CommaGroup_BehaviorUnchanged()
    {
        var result = Parser.ParseSyntax("(1, 2)");

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        Assert.Equal(2, block.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_NestedCommaGroup_BehaviorUnchanged()
    {
        var result = Parser.ParseSyntax("((1, 2))");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Block>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.Block>(Assert.Single(outer.Algorithm.Output));
        Assert.Equal(2, inner.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_Ellipsis_ChainedSequenceSupply()
    {
        // 1 + 2...3 + 4...5 + 6 -> SequenceSupply(SequenceSupply(1+2, 3+4), 5+6) left-assoc
        var result = Parser.ParseSyntax("1 + 2...3 + 4...5 + 6");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Output); // one sequence supply item in output
        var outer = Assert.IsType<Expr.SequenceSupply>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.SequenceSupply>(outer.Left);
        Assert.IsType<Expr.Binary>(inner.Left);
        Assert.IsType<Expr.Binary>(inner.Right);
        Assert.IsType<Expr.Binary>(outer.Right);
    }

    [Fact]
    public void Parse_CommaAndEllipsis_CorrectStructure()
    {
        // 1, 2...3 -> output list has two items: [Num(1), SequenceSupply(Num(2), Num(3))]
        var result = Parser.ParseSyntax("1, 2...3");
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Output.Count);
        Assert.IsType<Expr.Num>(result.Root.Output[0]);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(result.Root.Output[1]);
    }

    [Fact]
    public void Parse_PropertyDetectionWithEllipsis()
    {
        // A = 1...2 B = 3 -> two properties, A has output [SequenceSupply(1, 2)]
        var result = Parser.ParseSyntax("A = 1...2 B = 3");
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Properties.Count);
    }

    [Fact]
    public void Parse_Semicolon_ReturnsOutputJoinExpr()
    {
        var result = Parser.ParseSyntax("1; 2");

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.IsType<Expr.Num>(outputJoin.Left);
        Assert.IsType<Expr.Num>(outputJoin.Right);
    }

    [Fact]
    public void Parse_SemicolonAcrossNewline_ReturnsOutputJoinExpr()
    {
        var result = Parser.ParseSyntax(
            """
            A ;
            B
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_CommaHasHigherPriorityThanSemicolon_JoinsCommaItemsFirst()
    {
        var result = Parser.ParseSyntax("1, 2 ; 3");

        Assert.False(result.HasErrors);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var leftJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal(1, Assert.IsType<Expr.Num>(leftJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(leftJoin.Right).Value);
        Assert.Equal(3, Assert.IsType<Expr.Num>(outerJoin.Right).Value);
    }

    [Fact]
    public void Parse_NewlineCommaContribution_JoinsTopLevelOutputsWithoutSyntheticBlock()
    {
        var result = Parser.ParseSyntax(
            """
            1, 2
            3
            """);

        Assert.False(result.HasErrors);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var leftJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal(1, Assert.IsType<Expr.Num>(leftJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(leftJoin.Right).Value);
        Assert.Equal(3, Assert.IsType<Expr.Num>(outerJoin.Right).Value);
        Assert.DoesNotContain(OutputJoinLeaves(outerJoin), static expr => expr is Expr.Block);
    }

    [Fact]
    public void Parse_NewlineBodyContributions_ReturnOutputJoinExpr()
    {
        var result = Parser.ParseSyntax(
            """
            1
            2
            3
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var leaves = OutputJoinLeaves(outputJoin);
        Assert.Equal([1m, 2m, 3m], leaves.Select(static expr => Assert.IsType<Expr.Num>(expr).Value));
    }

    [Fact]
    public void Parse_BraceBodyNewlineContributions_ReturnOutputJoinExpr()
    {
        var result = Parser.ParseSyntax(
            """
            {
                1
                2
                3
            }
            """);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(block.Algorithm.Output));
        var leaves = OutputJoinLeaves(outputJoin);
        Assert.Equal([1m, 2m, 3m], leaves.Select(static expr => Assert.IsType<Expr.Num>(expr).Value));
    }

    [Fact]
    public void Parse_BraceBodyCommaThenNewline_JoinsTopLevelOutputsWithoutSyntheticBlock()
    {
        var result = Parser.ParseSyntax(
            """
            {
                1, 2
                3
            }
            """);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(block.Algorithm.Output));
        var leaves = OutputJoinLeaves(outputJoin);
        Assert.Equal([1m, 2m, 3m], leaves.Select(static expr => Assert.IsType<Expr.Num>(expr).Value));
        Assert.DoesNotContain(leaves, static expr => expr is Expr.Block);
    }

    [Fact]
    public void Parse_BraceBodyExplicitSemicolonAcrossNewline_ReturnsOutputJoinExpr()
    {
        var result = Parser.ParseSyntax(
            """
            {
                1 ;
                2
            }
            """);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(block.Algorithm.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_ArithmeticCommaNewline_JoinsTopLevelOutputsWithoutSyntheticBlock()
    {
        var result = Parser.ParseSyntax(
            """
            1 + 2, 2 + 3
            3 + 4
            """);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var leaves = OutputJoinLeaves(outputJoin);
        Assert.Equal(3, leaves.Count);
        Assert.All(leaves, static expr => Assert.IsType<Expr.Binary>(expr));
    }

    [Theory]
    [InlineData("1 2")]
    [InlineData("1 ; 2")]
    [InlineData("Output = 1 2")]
    [InlineData("Output = 1 ; 2")]
    public void Parse_SameLineAdjacentExpressions_ParseAsImplicitSemicolon(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Theory]
    [InlineData("{ 1 2 }")]
    [InlineData("{ 1 ; 2 }")]
    [InlineData("{\n1 2\n}")]
    public void Parse_BraceBodySameLineAdjacency_ParsesAsImplicitSemicolon(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(block.Algorithm.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Theory]
    [InlineData("1 2 3")]
    [InlineData("1\n2\n3")]
    [InlineData("1 ; 2 ; 3")]
    public void Parse_AdjacencyNewlineAndExplicitSemicolon_AreEquivalent(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var leaves = OutputJoinLeaves(outputJoin);
        Assert.Equal([1m, 2m, 3m], leaves.Select(static expr => Assert.IsType<Expr.Num>(expr).Value));
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outputJoin.Left);
        Assert.Equal(1, Assert.IsType<Expr.Num>(innerJoin.Left).Value);
    }

    [Theory]
    [InlineData("1, 2 3")]
    [InlineData("1, 2 ; 3")]
    public void Parse_AdjacencyAfterComma_HasExplicitSemicolonPrecedence(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var leaves = OutputJoinLeaves(outputJoin);
        Assert.Equal([1m, 2m, 3m], leaves.Select(static expr => Assert.IsType<Expr.Num>(expr).Value));
    }

    [Theory]
    [InlineData("A B...")]
    [InlineData("A\nB...")]
    [InlineData("A ; B...")]
    public void Parse_AdjacencyBeforePostfixSequenceSupply_MatchesExplicitSemicolonPrecedence(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
    }

    [Theory]
    [InlineData("A B C...")]
    [InlineData("A\nB\nC...")]
    [InlineData("A ; B ; C...")]
    public void Parse_MultipleAdjacencyBeforePostfixSequenceSupply_SuppliesWholeJoin(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("A ; (B...)")]
    [InlineData("A ;\n(B...)")]
    public void Parse_ExplicitlyGroupedPostfixSequenceSupply_AppliesOnlyToGroupedOperand(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        var grouped = Assert.IsType<Expr.Block>(outputJoin.Right);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(grouped.Algorithm.Output));
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
    }

    [Theory]
    [InlineData("A, B ; C...")]
    [InlineData("A, B C...")]
    [InlineData("A, B\nC...")]
    public void Parse_CommaContributionBeforeJoinedPostfixSequenceSupply_StaysInsideSupplyLeft(string source)
    {
        // Comma binds tighter than semicolon and postfix '...' binds looser
        // than the accumulated output chain, so the comma contribution must
        // not be dropped or split: all three forms are (A ; B ; C)...,
        // never A, ((B ; C)...).
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("A ; B, C...")]
    [InlineData("A B, C...")]
    [InlineData("A\nB, C...")]
    public void Parse_JoinContributionBeforeCommaSlotPostfixSequenceSupply_AbsorbsWholeChain(string source)
    {
        // The mirror of the comma-first case: a sequence supply encountered
        // after an earlier semicolon/output contribution absorbs the full
        // accumulated chain into its left operand. `A ; B, C...` and
        // `A, B ; C...` are symmetric — both are (A ; B ; C)..., never
        // (A ; B) ; (C...) with the earlier chain stranded outside.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Fact]
    public void Parse_DefinitionSeparatedPostfixSequenceSupplyContribution_AbsorbsPriorOutputChain()
    {
        // The contribution boundary is an implicit ';' even when a definition
        // sits between the lines, and postfix '...' binds looser than ';',
        // so this matches `A, B ; C...` = (A ; B ; C)....
        var result = Parser.ParseSyntax("A, B\nP = 1\nC...");

        Assert.False(result.HasErrors);
        Assert.Equal("P", Assert.Single(result.Root.Properties).Name);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Fact]
    public void Parse_DefinitionSeparatedCommaSlotSupplyContribution_AbsorbsPriorOutputChain()
    {
        // The mirror across the definition boundary: the supply sits in a
        // later comma slot of the new contribution, not at its start. The
        // boundary is an implicit ';', so this matches `A ; B, C...` =
        // (A ; B ; C)... — never (A ; B) ; (C...) with the earlier chain
        // stranded outside the supply.
        var result = Parser.ParseSyntax("A\nP = 1\nB, C...");

        Assert.False(result.HasErrors);
        Assert.Equal("P", Assert.Single(result.Root.Properties).Name);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Fact]
    public void Parse_CommaSlotPostfixSequenceSupplyWithoutJoin_KeepsCommaStructure()
    {
        // Without a semicolon/adjacency join in the list, comma slots stay
        // structural and the supply stays local to its own slot.
        var result = Parser.ParseSyntax("A, B...");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Output.Count);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(result.Root.Output[0]).Name);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(result.Root.Output[1]);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
    }

    [Theory]
    [InlineData("A B... C")]
    [InlineData("A\nB...\nC")]
    [InlineData("A ; B... ; C")]
    public void Parse_MiddlePostfixSequenceSupply_AppliesToLeftChainAndLaterOutputContinues(string source)
    {
        // Postfix '...' applies to the output chain accumulated to its left
        // at the point where it appears; later output joins continue after
        // the spread. All three forms parse as ((A ; B)...) ; C — never as
        // (A ; B ; C)... reaching forward across the later join.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(outputJoin.Left);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("(A B...)")]
    [InlineData("(A\nB...)")]
    [InlineData("(A ; B...)")]
    public void Parse_ParenthesizedAdjacencyBeforePostfixSequenceSupply_IsOneGroupedSupply(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(block.Algorithm.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
    }

    [Theory]
    [InlineData("F(A B...)")]
    [InlineData("F(A\nB...)")]
    [InlineData("F(A ; B...)")]
    public void Parse_CallArgumentAdjacencyBeforePostfixSequenceSupply_IsOneJoinedSupplyArgument(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        var argument = Assert.IsType<Expr.SequenceSupply>(Assert.Single(call.Args.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(argument.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(argument.Right).Name);
    }

    [Fact]
    public void Parse_CallArgumentCommaBeforePostfixSequenceSupply_RemainsTwoArguments()
    {
        var result = Parser.ParseSyntax("F(A, B...)");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        Assert.Equal(2, call.Args.Output.Count);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(call.Args.Output[0]).Name);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(call.Args.Output[1]);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
    }

    [Theory]
    [InlineData("(1 2)")]
    [InlineData("(1 ; 2)")]
    [InlineData("(1\n2)")]
    public void Parse_ParenthesizedAdjacency_IsOneGroupedJoinedValue(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(block.Algorithm.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Theory]
    [InlineData("F(1 2)")]
    [InlineData("F (1 2)")]
    [InlineData("F(1 ; 2)")]
    [InlineData("F (1 ; 2)")]
    public void Parse_CallArgumentAdjacency_IsOneJoinedArgument(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        var argument = Assert.IsType<Expr.OutputJoin>(Assert.Single(call.Args.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(argument.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(argument.Right).Value);
    }

    [Theory]
    [InlineData("F(1, 2)")]
    [InlineData("F (1, 2)")]
    public void Parse_DirectCallWhitespaceBeforeParen_IsCallContinuation(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(call.Function).Name);
        Assert.Equal(2, call.Args.Output.Count);
    }

    [Theory]
    [InlineData("F{1}")]
    [InlineData("F {1}")]
    public void Parse_DirectCallWhitespaceBeforeBrace_IsCallContinuation(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(call.Function).Name);
        var argument = Assert.IsType<Expr.Block>(Assert.Single(call.Args.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(Assert.Single(argument.Algorithm.Output)).Value);
    }

    [Theory]
    [InlineData("A.B(1)")]
    [InlineData("A.B (1)")]
    public void Parse_DotCallWhitespaceBeforeParen_IsCallContinuation(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(Assert.Single(result.Root.Output));
        Assert.Equal("B", dotCall.Name);
        Assert.NotNull(dotCall.Args);
        Assert.Equal(1, Assert.IsType<Expr.Num>(Assert.Single(dotCall.Args!.Output)).Value);
    }

    [Theory]
    [InlineData("A.B{1}")]
    [InlineData("A.B {1}")]
    public void Parse_DotCallWhitespaceBeforeBrace_IsCallContinuation(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(Assert.Single(result.Root.Output));
        Assert.Equal("B", dotCall.Name);
        Assert.NotNull(dotCall.Args);
        var argument = Assert.IsType<Expr.Block>(Assert.Single(dotCall.Args!.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(Assert.Single(argument.Algorithm.Output)).Value);
    }

    [Fact]
    public void Parse_ExplicitSemicolonBeforeParen_RemainsOutputJoinNotCall()
    {
        var result = Parser.ParseSyntax("F ; (1)");

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_CommaBeforeParen_RemainsCommaStructureNotCall()
    {
        var result = Parser.ParseSyntax("F, (1)");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Output.Count);
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(result.Root.Output[0]).Name);
        Assert.Equal(1, Assert.IsType<Expr.Num>(result.Root.Output[1]).Value);
    }

    [Fact]
    public void Parse_NewlineBeforeCallDelimiter_IsOutputJoinNotCall()
    {
        // A physical newline never continues a closed expression into a
        // call: `Add` newline `(1, 2)` is the output join `Add ; (1, 2)`,
        // exactly like the explicit form. Multiline calls must open the
        // delimiter before the newline.
        var result = Parser.ParseSyntax("Add\n(1, 2)");

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("Add", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        var group = Assert.IsType<Expr.Block>(outputJoin.Right);
        Assert.Equal(2, group.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_NewlineBeforeDotCallDelimiter_IsOutputJoinNotCall()
    {
        // Same newline boundary for dot calls: `A.B` newline `(1)` is the
        // bare dot call joined with `(1)`, never the dot call `A.B(1)`.
        var result = Parser.ParseSyntax("A.B\n(1)");

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var dotCall = Assert.IsType<Expr.DotCall>(outputJoin.Left);
        Assert.Equal("B", dotCall.Name);
        Assert.Null(dotCall.Args);
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Theory]
    [InlineData("Pair = 1, 2\nP = Pair:0")]
    [InlineData("Pair = 1, 2\nP = Pair :0")]
    [InlineData("Pair = 1, 2\nP = Pair : 0")]
    public void Parse_SameLineIndexing_RemainsPostfixIndex(string source)
    {
        // Same-line whitespace around ':' is insignificant; the index stays
        // a postfix continuation of the expression before it.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = result.Root.Properties[1];
        Assert.Equal("P", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        var index = Assert.IsType<Expr.Index>(Assert.Single(body.Output));
        Assert.Equal("Pair", Assert.IsType<Expr.Resolve>(index.Target).Name);
    }

    [Fact]
    public void Parse_ColonLedLineAfterDefinitionBody_IsRejectedNotBodyContinuation()
    {
        // A physical newline never continues a closed expression into
        // postfix indexing, mirroring the call-delimiter rule: `P = Pair`
        // newline `:0` must not silently define `P = Pair:0`. P's body stays
        // the bare resolve and the ':'-led line is rejected with a targeted
        // diagnostic.
        var result = Parser.ParseSyntax("Pair = 1, 2\nP = Pair\n:0\nP");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                "Indexing is postfix and must follow the indexed expression on the same physical line",
                StringComparison.Ordinal));
        var property = result.Root.Properties[1];
        Assert.Equal("P", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        Assert.Equal("Pair", Assert.IsType<Expr.Resolve>(Assert.Single(body.Output)).Name);
    }

    [Theory]
    [InlineData("Pair = 1, 2\nPair\n:0")]
    [InlineData("Pair = 1, 2\nPair // comment\n:0")]
    public void Parse_ColonLedLineAfterOutputRow_IsRejectedNotPostfixContinuation(string source)
    {
        // Same boundary in root output: `Pair` newline `:0` is not the index
        // `Pair:0`; the ':'-led row reports the targeted diagnostic. A
        // trailing comment is invisible and must not change that.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                "Indexing is postfix and must follow the indexed expression on the same physical line",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            result.Root.Output,
            static expr => expr is Expr.Index);
    }

    [Theory]
    [InlineData("A ; B")]
    [InlineData("A B")]
    [InlineData("A\nB")]
    public void Parse_OutputJoinSpellings_ProduceIdenticalCanonicalShape(string source)
    {
        // Explicit ';', same-line adjacency, and newline adjacency are three
        // spellings of the same output join and share one parser code path.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Theory]
    [InlineData("A\n-1")]
    [InlineData("A // comment\n-1")]
    public void Parse_MinusLedLineAfterOutputRow_IsAdjacencyRowNotSubtraction(string source)
    {
        // A binary operator never continues a closed expression across a
        // physical newline, and a trailing comment must not change that:
        // both forms are the output join `A ; -1`, never `A - 1`.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.IsType<Expr.Unary>(outputJoin.Right);
        Assert.DoesNotContain(result.Root.Output, static expr => expr is Expr.Binary);
    }

    [Theory]
    [InlineData("P = A\n-1")]
    [InlineData("P = A // comment\n-1")]
    public void Parse_MinusLedLineAfterDefinitionBody_IsOutputRowNotBodySubtraction(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(body.Output)).Name);
        Assert.IsType<Expr.Unary>(Assert.Single(result.Root.Output));
    }

    [Theory]
    [InlineData("F(A\n-1)")]
    [InlineData("F(A // comment\n-1)")]
    public void Parse_MinusLedLineInCallArguments_JoinsAsOneArgument(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        var argument = Assert.IsType<Expr.OutputJoin>(Assert.Single(call.Args.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(argument.Left).Name);
        Assert.IsType<Expr.Unary>(argument.Right);
    }

    [Theory]
    [InlineData("F\n(1)")]
    [InlineData("F // comment\n(1)")]
    public void Parse_CommentBeforeParenLedLine_DoesNotEnableCallContinuation(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_SameLinePostfixGrace_BindsToPrecedingIdentifier()
    {
        // Same-line '~' after an identifier is postfix grace on that
        // identifier; the adjacent expression joins after it.
        var result = Parser.ParseSyntax("A~B");

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var grace = Assert.IsType<Expr.Grace>(outputJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(grace.Inner).Name);
        Assert.Equal(1, grace.Weight);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Theory]
    [InlineData("A\n~B")]
    [InlineData("A // comment\n~B")]
    public void Parse_TildeLedLine_IsPrefixGraceRowNotPostfixContinuation(string source)
    {
        // A physical newline never continues a closed expression into
        // postfix grace: the '~'-led line is its own prefix-grace row, and a
        // trailing comment must not change that.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        var grace = Assert.IsType<Expr.Grace>(outputJoin.Right);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(grace.Inner).Name);
        Assert.Equal(-1, grace.Weight);
    }

    [Theory]
    [InlineData("Output = A\n~B")]
    [InlineData("Output = A // comment\n~B")]
    public void Parse_TildeLedLineAfterExplicitOutput_ReportsMixingNotPostfixGrace(string source)
    {
        // The newline ends the `Output =` body before the '~', so the body
        // stays the bare resolve `A` and the '~'-led line is an implicit
        // output row — which cannot mix with explicit output.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot use both"));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
    }

    [Theory]
    [InlineData("A...B ; C")]
    [InlineData("A...B\nC")]
    [InlineData("A...B\nP = 9\nC")]
    public void Parse_TightRightSupplyLaterOutput_AppendsIntoRightOperand(string source)
    {
        // A tight-right supply's right operand keeps absorbing later output
        // joins: explicit ';', newline adjacency, and a definition-separated
        // contribution all canonicalize as A...(B ; C) — never (A...B) ; C.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var supply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(supply.Left).Name);
        var right = Assert.IsType<Expr.OutputJoin>(supply.Right);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(right.Left).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(right.Right).Name);
    }

    [Theory]
    [InlineData("A...B...C ; D")]
    [InlineData("A...B...C\nD")]
    [InlineData("A...B...C\nP = 9\nD")]
    public void Parse_ChainedTightRightSupplyLaterOutput_AppendsIntoOutermostRightOperand(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        var inner = Assert.IsType<Expr.SequenceSupply>(outer.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(inner.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(inner.Right).Name);
        var right = Assert.IsType<Expr.OutputJoin>(outer.Right);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(right.Left).Name);
        Assert.Equal("D", Assert.IsType<Expr.Resolve>(right.Right).Name);
    }

    [Theory]
    [InlineData("A... ; C")]
    [InlineData("A...\nC")]
    [InlineData("A...\nP = 9\nC")]
    public void Parse_SyntheticPostfixSupplyLaterOutput_ContinuesAfterSpread(string source)
    {
        // A SYNTHETIC postfix supply (`A...`) lets later output continue
        // after the spread in every spelling: explicit ';', newline
        // adjacency, and definition-separated rows all give (A...) ; C.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var supply = Assert.IsType<Expr.SequenceSupply>(outputJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(supply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(supply.Right).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Theory]
    [InlineData("A...empty ; C")]
    [InlineData("A...empty\nC")]
    [InlineData("A...empty\nP = 9\nC")]
    public void Parse_ExplicitEmptyRightOperandLaterOutput_AppendsIntoRightOperand(string source)
    {
        // User-written `A...empty` is a TIGHT-RIGHT supply, not synthetic
        // postfix: its right operand keeps absorbing later output joins, so
        // every spelling gives A...(empty ; C) — never (A...empty) ; C.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var supply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(supply.Left).Name);
        var right = Assert.IsType<Expr.OutputJoin>(supply.Right);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(right.Left).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(right.Right).Name);
    }

    [Theory]
    [InlineData("A...B ; C...D")]
    [InlineData("A...B\nC...D")]
    [InlineData("A...B\nP = 9\nC...D")]
    public void Parse_TightSupplyThenTightSupplyContribution_MatchesInlineShape(string source)
    {
        // A later supply contribution appends its own left operand through
        // the accumulated chain first (applying the tight-right rule), then
        // wraps the result: every spelling gives (A...(B ; C))...D.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("D", Assert.IsType<Expr.Resolve>(outer.Right).Name);
        var inner = Assert.IsType<Expr.SequenceSupply>(outer.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(inner.Left).Name);
        var innerRight = Assert.IsType<Expr.OutputJoin>(inner.Right);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerRight.Left).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(innerRight.Right).Name);
    }

    [Theory]
    [InlineData("A...B ; C, D")]
    [InlineData("A...B, C ; D")]
    public void Parse_JoinInsideSupplyRightOperand_TriggersCommaNormalization(string source)
    {
        // The comma-normalization trigger detects ungrouped output joins
        // inside EITHER supply operand, so both spellings flatten into one
        // canonical supply A...(B ; C ; D) — never a stray second comma
        // slot like A...(B ; C), D.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var supply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(supply.Left).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(supply.Right);
        Assert.Equal("D", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("A...B ; C, D...")]
    [InlineData("A...B, C ; D...")]
    public void Parse_JoinInsideSupplyRightOperandThenPostfixSupply_AbsorbsWholeChain(string source)
    {
        // The trailing postfix supply absorbs the whole accumulated chain —
        // including the tight supply whose right operand grew: both
        // spellings are (A...(B ; C ; D))....
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(outer.Right).Name);
        var inner = Assert.IsType<Expr.SequenceSupply>(outer.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(inner.Left).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(inner.Right);
        Assert.Equal("D", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("A...B ; C, D...E")]
    [InlineData("A...B, C ; D...E")]
    public void Parse_JoinInsideSupplyRightOperandThenTightSupply_AbsorbsWholeChain(string source)
    {
        // Same with a tight-right trailing supply: both spellings are
        // (A...(B ; C ; D))...E.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("E", Assert.IsType<Expr.Resolve>(outer.Right).Name);
        var inner = Assert.IsType<Expr.SequenceSupply>(outer.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(inner.Left).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(inner.Right);
        Assert.Equal("D", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("P\n= 1\nP")]
    [InlineData("P // comment\n= 1\nP")]
    public void Parse_CommentBeforeEqualsLine_StillParsesPropertyDefinition(string source)
    {
        // Declaration lookahead skips comments: a trailing comment before
        // the '='-led line must not turn the definition into an output row.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        Assert.Equal("P", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Output)).Name);
    }

    [Theory]
    [InlineData("public P\n= 1\nP")]
    [InlineData("public P // comment\n= 1\nP")]
    public void Parse_CommentInPublicPropertyHeader_StillParsesPublicDefinition(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        Assert.True(property.IsPublic);
    }

    [Theory]
    [InlineData("Output\n= 1")]
    [InlineData("Output // comment\n= 1")]
    public void Parse_CommentInExplicitOutputHeader_StillParsesExplicitOutput(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Empty(result.Root.Properties);
        Assert.Equal(1, Assert.IsType<Expr.Num>(Assert.Single(result.Root.Output)).Value);
    }

    [Theory]
    [InlineData("F(x) = x\nF(4)")]
    [InlineData("F // comment\n(x) // comment\n= x\nF(4)")]
    public void Parse_CommentedClauseHeader_ParsesIdentically(string source)
    {
        // Clause-header lookahead scans through the shared significant-token
        // API, so comments between the header tokens never change what
        // parses as a clause definition.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(call.Function).Name);
    }

    [Theory]
    [InlineData("public F(x) = x\nF(4)")]
    [InlineData("public F // comment\n(x) // comment\n= x\nF(4)")]
    public void Parse_CommentedPublicClauseHeader_ParsesIdentically(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        Assert.True(property.IsPublic);
    }

    [Theory]
    [InlineData("A = (public X = 1)\npublic open A")]
    [InlineData("A = (public X = 1)\npublic // comment\nopen A")]
    public void Parse_CommentedPublicOpen_ReportsSameDiagnostic(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("'public' cannot be applied to open declarations"));
    }

    [Fact]
    public void ParserSource_SequenceSupplyConstruction_OnlyInsideCreateSequenceSupply()
    {
        // Architecture regression: every SequenceSupply the parser builds
        // must go through CreateSequenceSupply so parser-local synthetic-
        // postfix metadata is never dropped (the old open-target
        // normalization raw-rebuilt supplies and lost it).
        var source = ReadParserSource();
        var constructionCount = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"new Expr\.SequenceSupply").Count;
        Assert.Equal(1, constructionCount);
    }

    [Fact]
    public void ParserSource_OpenTargetListParsing_DoesNotUseOutputPrecedenceParsing()
    {
        // Architecture regression: `open` has a dedicated comma-list parser.
        // The open-target parsing region must never invoke the generic
        // output-precedence machinery (output joins, adjacency, sequence
        // supply) — open atoms are plain expressions plus the explicit
        // post-atom supply rejection.
        var source = ReadParserSource();
        var start = source.IndexOf("private List<Expr> ParseOpenTargetList", StringComparison.Ordinal);
        var end = source.IndexOf("private static Expr CreateLoadOpenTarget", StringComparison.Ordinal);
        Assert.True(
            start >= 0 && end > start,
            "Expected the ParseOpenTargetList .. CreateLoadOpenTarget region in Parser.cs.");

        var openRegion = source[start..end];
        Assert.DoesNotContain("ParseOutputOperatorExpression", openRegion, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseOutputLineExprs", openRegion, StringComparison.Ordinal);
    }

    private static string ReadParserSource()
    {
        string? parserPath = null;
        for (var current = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            current is not null;
            current = current.Parent)
        {
            var candidate = System.IO.Path.Combine(current.FullName, "src", "KatLang", "Parser.cs");
            if (System.IO.File.Exists(candidate))
            {
                parserPath = candidate;
                break;
            }
        }

        Assert.NotNull(parserPath);
        return System.IO.File.ReadAllText(parserPath!);
    }

    [Theory]
    [InlineData("~P\n= 1\nP")]
    [InlineData("~P // comment\n= 1\nP")]
    public void Parse_CommentInGracePrefixedDefinition_ReportsSameGraceDiagnostic(string source)
    {
        // The invalid-grace property diagnostic fires identically with or
        // without a comment in the declaration header.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Grace operator cannot be applied to property names"));
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
    }

    [Theory]
    [InlineData("(A\n-1)")]
    [InlineData("(A // comment\n-1)")]
    public void Parse_MinusLedLineInGroup_JoinsAsGroupedRows(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var group = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(group.Algorithm.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.IsType<Expr.Unary>(outputJoin.Right);
    }

    [Theory]
    [InlineData("A\n...")]
    [InlineData("A // comment\n...")]
    public void Parse_EllipsisLedLine_IsRejectedIdentically(string source)
    {
        // The '...' token is line-bound; a '...'-led line is rejected the
        // same way with or without a trailing comment above it.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unexpected token"));
    }

    [Theory]
    [InlineData("A ; B ; C...")]
    [InlineData("A B C...")]
    [InlineData("A\nB\nC...")]
    public void Parse_TrailingPostfixSupplyAfterJoinChain_WrapsWholeChain(string source)
    {
        // Trailing postfix '...' supplies the whole accumulated output
        // chain: all three spellings are (A ; B ; C)....
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var supply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(supply.Right).Name);
        var outerJoin = Assert.IsType<Expr.OutputJoin>(supply.Left);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(outerJoin.Right).Name);
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outerJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(innerJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(innerJoin.Right).Name);
    }

    [Theory]
    [InlineData("F\n{1}")]
    [InlineData("A.B\n{1}")]
    public void Parse_NewlineBeforeBraceDelimiter_IsOutputJoinNotBraceCall(string source)
    {
        // The newline boundary applies to brace delimiters too: a '{'-led
        // line is its own block row, never callback arguments for the
        // callable expression on the previous line.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.True(outputJoin.Left is Expr.Resolve or Expr.DotCall);
        if (outputJoin.Left is Expr.DotCall dotCall)
            Assert.Null(dotCall.Args);
        var block = Assert.IsType<Expr.Block>(outputJoin.Right);
        Assert.Equal(1, Assert.IsType<Expr.Num>(Assert.Single(block.Algorithm.Output)).Value);
    }

    [Theory]
    [InlineData("Add ; (1, 2)")]
    [InlineData("Add ;\n(1, 2)")]
    public void Parse_ExplicitSemicolonBeforeCallDelimiter_ForcesOutputJoinAcrossNewline(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal("Add", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        var group = Assert.IsType<Expr.Block>(outputJoin.Right);
        Assert.Equal(2, group.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_ParenLedLineAfterDefinitionBody_IsOutputRowNotBodyCall()
    {
        // The newline call boundary applies inside definition bodies too: a
        // '('-led line after a body that ends in a callable expression is a
        // following output row, never call arguments appended to that body.
        var result = Parser.ParseSyntax("P = F\n(1, 2)");

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(Assert.Single(body.Output)).Name);
        var row = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        Assert.Equal(2, row.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_ParenLedLineAfterDefinitionBody_DoesNotCreateSelfRecursiveCall()
    {
        // Regression: `A = Identity` newline `(A)` once parsed as
        // `A = Identity(A)`, making A recursively depend on itself and
        // blowing up property evaluation. The newline ends the body, so A's
        // body stays the bare resolve `Identity` and `(A)` is a report row.
        var result = Parser.ParseSyntax("Identity = x\n\nA = Identity\n(A)\n\nA");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Properties.Count);
        var property = result.Root.Properties[1];
        Assert.Equal("A", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        Assert.Equal("Identity", Assert.IsType<Expr.Resolve>(Assert.Single(body.Output)).Name);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var row = Assert.IsType<Expr.Block>(outputJoin.Left);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(row.Algorithm.Output)).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_SameLineCallDelimiterInDefinitionBody_RemainsCall()
    {
        // Control: with the delimiter on the same physical line, the body is
        // the call. Newline is the only boundary; same-line whitespace
        // continues the call.
        var result = Parser.ParseSyntax("Identity = x\nA = Identity (A)");

        Assert.False(result.HasErrors);
        var property = result.Root.Properties[1];
        Assert.Equal("A", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        var call = Assert.IsType<Expr.Call>(Assert.Single(body.Output));
        Assert.Equal("Identity", Assert.IsType<Expr.Resolve>(call.Function).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(call.Args.Output)).Name);
    }

    [Theory]
    [InlineData("Identity = x\nA = Identity(A)")]
    [InlineData("Identity = x\nA = Identity(\n  A\n)")]
    public void Parse_OpenedCallDelimiterSpansLines_RemainsCall(string source)
    {
        // An already-open argument list spans physical lines normally: only
        // the delimiter itself must be opened before the newline.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = result.Root.Properties[1];
        Assert.Equal("A", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        var call = Assert.IsType<Expr.Call>(Assert.Single(body.Output));
        Assert.Equal("Identity", Assert.IsType<Expr.Resolve>(call.Function).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(call.Args.Output)).Name);
    }

    [Theory]
    [InlineData("1\n2, 3")]
    [InlineData("1 ; 2, 3")]
    public void Parse_NewlineJoinAndExplicitSemicolon_ProduceIdenticalAstShape(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        var innerJoin = Assert.IsType<Expr.OutputJoin>(outputJoin.Left);
        Assert.Equal(1, Assert.IsType<Expr.Num>(innerJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(innerJoin.Right).Value);
        Assert.Equal(3, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_CallArgumentAdjacency_DoesNotBecomeTwoArguments()
    {
        var adjacency = Parser.ParseSyntax("F(1 2)");
        var commaSeparated = Parser.ParseSyntax("F(1, 2)");

        Assert.False(adjacency.HasErrors);
        Assert.False(commaSeparated.HasErrors);
        var adjacencyCall = Assert.IsType<Expr.Call>(Assert.Single(adjacency.Root.Output));
        Assert.Single(adjacencyCall.Args.Output);
        var commaCall = Assert.IsType<Expr.Call>(Assert.Single(commaSeparated.Root.Output));
        Assert.Equal(2, commaCall.Args.Output.Count);
        Assert.DoesNotContain(commaCall.Args.Output, static expr => expr is Expr.OutputJoin);
    }

    [Theory]
    [InlineData("F(1, 2 3)")]
    [InlineData("F(1, 2 ; 3)")]
    public void Parse_CallArgumentMixedCommaAndAdjacency_NormalizesLikeExplicitSemicolon(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        var argument = Assert.IsType<Expr.OutputJoin>(Assert.Single(call.Args.Output));
        var leaves = OutputJoinLeaves(argument);
        Assert.Equal([1m, 2m, 3m], leaves.Select(static expr => Assert.IsType<Expr.Num>(expr).Value));
    }

    [Fact]
    public void Parse_IfArityUsesSyntacticArguments_AdjacencyDoesNotSatisfyArity()
    {
        var threeArguments = Parser.ParseSyntax("if(1, 2, 3)");
        Assert.False(threeArguments.HasErrors);

        var joinedSecondArgument = Parser.ParseSyntax("if(1, 2 3)");
        Assert.True(joinedSecondArgument.HasErrors);
        Assert.Contains(
            joinedSecondArgument.Diagnostics,
            static diagnostic => diagnostic.Message.Contains("expects 3 arguments"));
    }

    [Theory]
    [InlineData("P = 1 2", "P")]
    [InlineData("P = 1 ; 2", "P")]
    public void Parse_PropertyBodySameLineAdjacency_JoinsIntoBody(string source, string propertyName)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal(propertyName, property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(body.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(2, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_ClauseBodySameLineAdjacency_JoinsIntoBody()
    {
        var result = Parser.ParseSyntax("F(x) = x y");

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(body.Output));
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_AdjacencyDoesNotConsumePropertyDefinitionOnSameLine()
    {
        var result = Parser.ParseSyntax("1 P = 3");

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        var output = Assert.Single(result.Root.Output);
        Assert.Equal(1, Assert.IsType<Expr.Num>(output).Value);
    }

    [Fact]
    public void Parse_PublicPropertyDefinitionAfterOutputLine_KeepsDeclarationBoundary()
    {
        var result = Parser.ParseSyntax("1\npublic P = 2");

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        Assert.True(property.IsPublic);
        var output = Assert.Single(result.Root.Output);
        Assert.Equal(1, Assert.IsType<Expr.Num>(output).Value);
    }

    [Fact]
    public void Parse_ExplicitOutputDefinitionAfterPropertyBody_KeepsDeclarationBoundary()
    {
        var result = Parser.ParseSyntax("P = 1\nOutput = 2");

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        var output = Assert.Single(result.Root.Output);
        Assert.Equal(2, Assert.IsType<Expr.Num>(output).Value);
    }

    [Fact]
    public void Parse_ClauseDefinitionAfterOutputLine_KeepsDeclarationBoundary()
    {
        var result = Parser.ParseSyntax("1\nF(x) = x + 1");

        Assert.False(result.HasErrors);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        var output = Assert.Single(result.Root.Output);
        Assert.Equal(1, Assert.IsType<Expr.Num>(output).Value);
    }

    [Fact]
    public void Parse_OpenAfterOutputLine_KeepsDeclarationBoundaryAndPlacementDiagnostic()
    {
        // 'open' on a later line is still an open declaration, never an
        // adjacent expression; its placement rule still applies.
        var result = Parser.ParseSyntax("1\nopen A");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("'open' declaration must appear before"));
        Assert.Single(result.Root.Opens);
    }

    [Theory]
    [InlineData("(F) (1)")]
    [InlineData("(F)\n(1)")]
    [InlineData("(1 + 2)(3)")]
    [InlineData("(1 + 2) (3)")]
    [InlineData("(1 + 2)\n(3)")]
    public void Parse_GroupedArbitraryExpressionBeforeParen_IsAdjacencyNotCall(string source)
    {
        // Grouped values and arithmetic results are not callable targets, so
        // a following '(' joins as adjacency and never becomes a call.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.False(outputJoin.Left is Expr.Call);
        Assert.False(outputJoin.Right is Expr.Call);
    }

    [Fact]
    public void Parse_LeadingSemicolonOnNextLineAfterDefinitionBody_JoinsIntoThatBody()
    {
        // Explicit ';' takes its right-hand expression from the next line and
        // equally continues a definition body across the line boundary, so a
        // leading ';' does not separate a definition from a following output
        // row. Use `Output = ...` or restructure instead.
        var result = Parser.ParseSyntax("P = F\n; (1)");

        Assert.False(result.HasErrors);
        Assert.Empty(result.Root.Output);
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("P", property.Name);
        var body = Assert.IsType<Algorithm.User>(property.Value);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(body.Output));
        Assert.Equal("F", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Theory]
    [InlineData("2(3)")]
    [InlineData("2 (3)")]
    [InlineData("2\n(3)")]
    public void Parse_NumberBeforeParenthesizedExpression_IsAdjacencyNotMultiplicationOrCall(string source)
    {
        // Numbers are not callable targets, so the relaxed call-whitespace
        // rule does not apply; the parenthesized expression joins as
        // adjacency and never becomes multiplication or a call.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal(2, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(3, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_AdjacencyDoesNotSplitIdentifiersOrNumbers()
    {
        var identifier = Parser.ParseSyntax("ab");
        var resolve = Assert.IsType<Expr.Resolve>(Assert.Single(identifier.Root.Output));
        Assert.Equal("ab", resolve.Name);

        var number = Parser.ParseSyntax("12");
        Assert.False(number.HasErrors);
        var num = Assert.IsType<Expr.Num>(Assert.Single(number.Root.Output));
        Assert.Equal(12, num.Value);
    }

    [Fact]
    public void Parse_BinaryOperatorContinuation_IsNotAdjacency()
    {
        var result = Parser.ParseSyntax("1 - 2");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(Assert.Single(result.Root.Output));
        Assert.Equal(BinaryOp.Sub, binary.Op);
    }

    [Fact]
    public void Parse_SequenceSupplyAfterOutputJoin_HasLowerPrecedenceThanSemicolon()
    {
        var result = Parser.ParseSyntax("X(a ; b...)");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(Assert.Single(call.Args.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(sequenceSupply.Left);
        Assert.Equal("a", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("b", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
    }

    [Fact]
    public void Parse_SequenceSupplyWithCommaInCall_KeepsCommaStructural()
    {
        var result = Parser.ParseSyntax("X(a..., b)");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        Assert.Equal(2, call.Args.Output.Count);
        var sequenceSupply = Assert.IsType<Expr.SequenceSupply>(call.Args.Output[0]);
        Assert.Equal("a", Assert.IsType<Expr.Resolve>(sequenceSupply.Left).Name);
        Assert.Equal("empty", Assert.IsType<Expr.Resolve>(sequenceSupply.Right).Name);
        Assert.Equal("b", Assert.IsType<Expr.Resolve>(call.Args.Output[1]).Name);
    }

    [Fact]
    public void Parse_NewlineInsideExplicitGroup_JoinsAsImplicitSemicolon()
    {
        var result = Parser.ParseSyntax(
            """
            (A
            B)
            """);

        Assert.False(result.HasErrors);
        var block = Assert.IsType<Expr.Block>(Assert.Single(result.Root.Output));
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(block.Algorithm.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(outputJoin.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(outputJoin.Right).Name);
    }

    [Fact]
    public void Parse_NewlineInsideCallArgs_JoinsAsOneArgumentNotArgumentSeparation()
    {
        var result = Parser.ParseSyntax(
            """
            F(
                A
                B
            )
            """);

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(Assert.Single(result.Root.Output));
        var argument = Assert.IsType<Expr.OutputJoin>(Assert.Single(call.Args.Output));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(argument.Left).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(argument.Right).Name);
    }

    [Fact]
    public void Parse_ArithmeticGroupingUnchanged()
    {
        // 1 + (2 * 3) → Binary with paren grouping
        var result = Parser.ParseSyntax("1 + (2 * 3)");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
    }

    [Fact]
    public void Parse_ChainedIndex_LeftAssociative()
    {
        var result = Parser.ParseSyntax("X:0:1");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Index>(result.Root.Output[0]);
        var inner = Assert.IsType<Expr.Index>(outer.Target);
        Assert.IsType<Expr.Resolve>(inner.Target);
    }

    [Fact]
    public void Parse_ChainedDotCall_LeftAssociative()
    {
        var result = Parser.ParseSyntax("X.A.B");

        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("B", outer.Name);
        var inner = Assert.IsType<Expr.DotCall>(outer.Target);
        Assert.Equal("A", inner.Name);
    }

    [Fact]
    public void Parse_BinaryMinusWithNegative_ParsesCorrectly()
    {
        var result = Parser.ParseSyntax("5 - -3");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Sub, binary.Op);
        var unary = Assert.IsType<Expr.Unary>(binary.Right);
        Assert.Equal(UnaryOp.Minus, unary.Op);
    }

    [Fact]
    public void Parse_Comment_IsIgnored()
    {
        // Comments are semantically invisible: a same-line comment changes
        // nothing, and a trailing operator continues its expression onto the
        // next line with or without a comment in between.
        foreach (var source in new[] { "1 + 2 // comment", "1 +\n2", "1 + // comment\n2" })
        {
            var result = Parser.ParseSyntax(source);

            Assert.False(result.HasErrors);
            var binary = Assert.IsType<Expr.Binary>(Assert.Single(result.Root.Output));
            Assert.Equal(BinaryOp.Add, binary.Op);
        }
    }

    [Theory]
    [InlineData("1\n+ 2")]
    [InlineData("1 // comment\n+ 2")]
    public void Parse_CommentDoesNotEnableBinaryContinuationAcrossNewline(string source)
    {
        // A binary operator never continues a closed expression across a
        // physical newline, and a skipped comment must not relax that
        // boundary: both spellings reject the '+'-led line identically.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Unexpected token"));
    }

    [Fact]
    public void Parse_DotCall_InlineParenMultiOutputReceiver_IsBlock()
    {
        var result = Parser.ParseSyntax("(1, 2, 3).order");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        var target = Assert.IsType<Expr.Block>(dotCall.Target);
        Assert.Equal(3, target.Algorithm.Output.Count);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_DoubleParenGroupedReceiver_PreservesOuterBlockLayer()
    {
        var result = Parser.ParseSyntax("((1, 2, 3)).count");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        var outer = Assert.IsType<Expr.Block>(dotCall.Target);
        Assert.Single(outer.Algorithm.Output);

        var inner = Assert.IsType<Expr.Block>(outer.Algorithm.Output[0]);
        Assert.Equal(3, inner.Algorithm.Output.Count);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_ParenWrappedBraceReceiver_RemainsScopingOnly()
    {
        var result = Parser.ParseSyntax("({1, 2, 3}).order");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        var target = Assert.IsType<Expr.Block>(dotCall.Target);
        Assert.True(target.Algorithm.IsParametrized);
        Assert.Equal(3, target.Algorithm.Output.Count);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_DotCall_InlineBraceReceiver_IsBlock()
    {
        var result = Parser.ParseSyntax("{1, 2, 3}.order");

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        var target = Assert.IsType<Expr.Block>(dotCall.Target);
        Assert.Equal(3, target.Algorithm.Output.Count);
        Assert.Null(dotCall.Args);
    }

    [Fact]
    public void Parse_RootAlgorithm_IsParametrized()
    {
        var result = Parser.ParseSyntax("x + 1");
        Assert.True(result.Root.IsParametrized);
    }

    [Fact]
    public void Parse_PropertyBody_IsParametrized()
    {
        var result = Parser.ParseSyntax("X = x + 1");
        Assert.True(result.Root.Properties[0].Value.IsParametrized);
    }

    [Fact]
    public void Parse_PropertyBody_GroupedTuple_PreservedAsSingleValue()
    {
        var result = Parser.ParseSyntax("Pair = (1, 2)");

        Assert.False(result.HasErrors);
        var pair = result.Root.Properties[0].Value;
        Assert.Single(pair.Output);
        var block = Assert.IsType<Expr.Block>(pair.Output[0]);
        Assert.Equal(2, block.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_UnexpectedToken_ReportsError()
    {
        var result = Parser.ParseSyntax("1 + + 2");
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Parse_MissingCloseParen_ReportsError()
    {
        var result = Parser.ParseSyntax("(1 + 2");
        Assert.True(result.HasErrors);
    }

    // â"€â"€ New operators â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public void Parse_Division_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("10 / 3");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Div, binary.Op);
    }

    [Fact]
    public void Parse_IntegerDivision_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("10 div 3");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.IDiv, binary.Op);
    }

    [Fact]
    public void Parse_Modulo_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("10 mod 3");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Mod, binary.Op);
    }

    [Fact]
    public void Parse_Power_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("2 ^ 3");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Pow, binary.Op);
    }

    [Fact]
    public void Parse_Power_RightAssociative()
    {
        var result = Parser.ParseSyntax("2 ^ 3 ^ 4");
        Assert.False(result.HasErrors);
        var outer = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Pow, outer.Op);
        Assert.Equal(2, ((Expr.Num)outer.Left).Value);
        var inner = Assert.IsType<Expr.Binary>(outer.Right);
        Assert.Equal(BinaryOp.Pow, inner.Op);
        Assert.Equal(3, ((Expr.Num)inner.Left).Value);
        Assert.Equal(4, ((Expr.Num)inner.Right).Value);
    }

    [Fact]
    public void Parse_LessEqual_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 <= 2");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Le, binary.Op);
    }

    [Fact]
    public void Parse_GreaterEqual_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("2 >= 1");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Ge, binary.Op);
    }

    [Fact]
    public void Parse_EqualEqual_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 == 1");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Eq, binary.Op);
    }

    [Fact]
    public void Parse_NotEqual_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 != 2");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Ne, binary.Op);
    }

    [Fact]
    public void Parse_And_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 and 0");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.And, binary.Op);
    }

    [Fact]
    public void Parse_Or_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 or 0");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Or, binary.Op);
    }

    [Fact]
    public void Parse_Xor_ReturnsBinaryExpr()
    {
        var result = Parser.ParseSyntax("1 xor 0");
        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Xor, binary.Op);
    }

    [Fact]
    public void Parse_Not_ReturnsUnaryExpr()
    {
        var result = Parser.ParseSyntax("not 1");
        Assert.False(result.HasErrors);
        var unary = Assert.IsType<Expr.Unary>(result.Root.Output[0]);
        Assert.Equal(UnaryOp.Not, unary.Op);
    }

    [Fact]
    public void Parse_Precedence_PowerBeforeMultiplication()
    {
        // 2 * 3 ^ 4 = 2 * (3 ^ 4)
        var result = Parser.ParseSyntax("2 * 3 ^ 4");
        Assert.False(result.HasErrors);
        var mul = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Mul, mul.Op);
        var pow = Assert.IsType<Expr.Binary>(mul.Right);
        Assert.Equal(BinaryOp.Pow, pow.Op);
    }

    [Fact]
    public void Parse_Precedence_DivModSameAsMul()
    {
        // 12 / 3 mod 2 = (12 / 3) mod 2
        var result = Parser.ParseSyntax("12 / 3 mod 2");
        Assert.False(result.HasErrors);
        var mod = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Mod, mod.Op);
        var div = Assert.IsType<Expr.Binary>(mod.Left);
        Assert.Equal(BinaryOp.Div, div.Op);
    }

    [Fact]
    public void Parse_Precedence_ComparisonBeforeLogical()
    {
        // 1 < 2 and 3 > 1 = (1 < 2) and (3 > 1)
        var result = Parser.ParseSyntax("1 < 2 and 3 > 1");
        Assert.False(result.HasErrors);
        var andExpr = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.And, andExpr.Op);
        var lt = Assert.IsType<Expr.Binary>(andExpr.Left);
        Assert.Equal(BinaryOp.Lt, lt.Op);
        var gt = Assert.IsType<Expr.Binary>(andExpr.Right);
        Assert.Equal(BinaryOp.Gt, gt.Op);
    }

    [Fact]
    public void Parse_Precedence_AndBeforeOr()
    {
        // 1 or 2 and 3 = 1 or (2 and 3)
        var result = Parser.ParseSyntax("1 or 2 and 3");
        Assert.False(result.HasErrors);
        var orExpr = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Or, orExpr.Op);
        Assert.Equal(1, ((Expr.Num)orExpr.Left).Value);
        var andExpr = Assert.IsType<Expr.Binary>(orExpr.Right);
        Assert.Equal(BinaryOp.And, andExpr.Op);
    }

    [Fact]
    public void Parse_Precedence_EqualityBeforeComparison()
    {
        // Note: equality (==) at prec 4, comparison (<) at prec 5
        // So 1 == 2 < 3 = 1 == (2 < 3)
        var result = Parser.ParseSyntax("1 == 2 < 3");
        Assert.False(result.HasErrors);
        var eq = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Eq, eq.Op);
        var lt = Assert.IsType<Expr.Binary>(eq.Right);
        Assert.Equal(BinaryOp.Lt, lt.Op);
    }

    [Fact]
    public void Parse_CommentDoesNotConflictWithSlash()
    {
        // // is comment, / is division
        var result = Parser.ParseSyntax("10 / 2 // this is a comment");
        Assert.False(result.HasErrors);
        var div = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Div, div.Op);
    }

    [Fact]
    public void Parse_PropertyAssignmentNotConfusedWithEqualEqual()
    {
        // X = 5 should be property, not X == 5
        var result = Parser.ParseSyntax("X = 5\nX");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.Equal("X", result.Root.Properties[0].Name);
    }

    // â"€â"€ Grace operator tests â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public void Parse_PrefixGrace_ProducesGraceNode()
    {
        var result = Parser.ParseSyntax("~x + 1");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        var grace = Assert.IsType<Expr.Grace>(binary.Left);
        Assert.Equal(-1, grace.Weight);
        var resolve = Assert.IsType<Expr.Resolve>(grace.Inner);
        Assert.Equal("x", resolve.Name);
    }

    [Fact]
    public void Parse_PostfixGrace_ProducesGraceNode()
    {
        var result = Parser.ParseSyntax("x~ + 1");

        Assert.False(result.HasErrors);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        var grace = Assert.IsType<Expr.Grace>(binary.Left);
        Assert.Equal(1, grace.Weight);
        var resolve = Assert.IsType<Expr.Resolve>(grace.Inner);
        Assert.Equal("x", resolve.Name);
    }

    [Fact]
    public void Parse_PostfixGrace_CanBeDirectCallee()
    {
        var result = Parser.ParseSyntax("predicate~(x)");

        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        var grace = Assert.IsType<Expr.Grace>(call.Function);
        Assert.Equal(1, grace.Weight);
        var resolve = Assert.IsType<Expr.Resolve>(grace.Inner);
        Assert.Equal("predicate", resolve.Name);
        var arg = Assert.IsType<Expr.Resolve>(call.Args.Output[0]);
        Assert.Equal("x", arg.Name);
    }

    [Fact]
    public void Parse_DoublePrefixGrace_WeightMinusTwo()
    {
        var result = Parser.ParseSyntax("~~x");

        Assert.False(result.HasErrors);
        var grace = Assert.IsType<Expr.Grace>(result.Root.Output[0]);
        Assert.Equal(-2, grace.Weight);
    }

    [Fact]
    public void Parse_DoublePostfixGrace_WeightPlusTwo()
    {
        var result = Parser.ParseSyntax("x~~");

        Assert.False(result.HasErrors);
        var grace = Assert.IsType<Expr.Grace>(result.Root.Output[0]);
        Assert.Equal(2, grace.Weight);
    }

    [Fact]
    public void Parse_PrefixAndPostfixCancel_NoGraceNode()
    {
        // ~x~ has weight -1 + 1 = 0, so no Grace wrapper
        var result = Parser.ParseSyntax("~x~");

        Assert.False(result.HasErrors);
        var resolve = Assert.IsType<Expr.Resolve>(result.Root.Output[0]);
        Assert.Equal("x", resolve.Name);
    }

    [Fact]
    public void Parse_GraceOnNonIdentifier_ReportsError()
    {
        var result = Parser.ParseSyntax("~42");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Expected identifier after '~'"));
    }

    [Fact]
    public void Parse_GraceOnPropertyName_ReportsError()
    {
        var result = Parser.ParseSyntax("~X = 5\nX");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace operator cannot be applied to property names"));
    }

    // â"€â"€ Public property parsing â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [Fact]
    public void Parse_PublicProperty_SetsIsPublic()
    {
        var result = Parser.ParseSyntax("public X = 5\nX");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.Equal("X", result.Root.Properties[0].Name);
        Assert.True(result.Root.Properties[0].IsPublic);
    }

    [Fact]
    public void Parse_PrivateProperty_DefaultIsNotPublic()
    {
        var result = Parser.ParseSyntax("X = 5\nX");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.False(result.Root.Properties[0].IsPublic);
    }

    [Fact]
    public void Parse_MixedVisibility_BothParsed()
    {
        var result = Parser.ParseSyntax("public A = 1\nB = 2\nA + B");
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Properties.Count);
        Assert.True(result.Root.Properties[0].IsPublic);
        Assert.False(result.Root.Properties[1].IsPublic);
    }

    [Fact]
    public void Parse_PublicOpen_ReportsError()
    {
        var result = Parser.ParseSyntax("public open Math\nPi");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("'public' cannot be applied to open"));
    }

    [Fact]
    public void Parse_GraceOnPublicProperty_ReportsError()
    {
        var result = Parser.ParseSyntax("~public X = 5\nX");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace operator cannot be applied to property names"));
    }

    // -- Open declaration tests -----------------------------------------------

    [Fact]
    public void Parse_Open_UnbracketedCommaList_TwoOpens()
    {
        // open Lib2, Lib3 -> two open entries
        var result = Parser.ParseSyntax("open Lib2, Lib3\nLib2 = (public Val2 = 20)\nLib3 = (public Val3 = 30)\nVal3");
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Opens.Count);
        Assert.IsType<Expr.Resolve>(result.Root.Opens[0]);
        Assert.Equal("Lib2", ((Expr.Resolve)result.Root.Opens[0]).Name);
        Assert.IsType<Expr.Resolve>(result.Root.Opens[1]);
        Assert.Equal("Lib3", ((Expr.Resolve)result.Root.Opens[1]).Name);
    }

    [Fact]
    public void Parse_Open_SingleItem_OneOpen()
    {
        // open Lib2 -> one open entry
        var result = Parser.ParseSyntax("open Lib2\nLib2 = (public Val2 = 20)\nVal2");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        Assert.IsType<Expr.Resolve>(result.Root.Opens[0]);
        Assert.Equal("Lib2", ((Expr.Resolve)result.Root.Opens[0]).Name);
    }

    [Fact]
    public void Parse_Open_CallInOpenList_BadOpenForm()
    {
        // open F(1,2), Lib3 -> Call is not a valid open form; should report error.
        // The comma inside F(1,2) must NOT split the list.
        var result = Parser.ParseSyntax("open F(1,2), Lib3\nF = (X = 1)\nLib3 = (Y = 2)\nY");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Invalid open form") && d.Message.Contains("call"));
    }

    // -- Open DotCall normalization tests -------------------------------------

    [Fact]
    public void Parse_Open_DotPath_NormalizesToDotCall()
    {
        // open Lib.Sub -> parser produces DotCall(Resolve("Lib"), "Sub", null)
        var result = Parser.ParseSyntax("open Lib.Sub\nLib = (public Sub = (public X = 1))\nX");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Opens[0]);
        Assert.Equal("Sub", dotCall.Name);
        Assert.Null(dotCall.Args);
        Assert.IsType<Expr.Resolve>(dotCall.Target);
    }

    [Fact]
    public void Parse_Open_DotCallWithArgs_ReportsError()
    {
        // open Lib.Sub() -> DotCall with args -> rejected as invalid open form
        var result = Parser.ParseSyntax("open Lib.Sub()\nLib = (public Sub = (public X = 1))\nX");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("not allowed in open"));
    }

    [Fact]
    public void Parse_Open_NestedDotPath_NormalizesToNestedDotCall()
    {
        // open A.B.C -> DotCall(DotCall(Resolve("A"), "B", null), "C", null)
        var result = Parser.ParseSyntax("open A.B.C\nA = (public B = (public C = (public X = 1)))\nX");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        var outer = Assert.IsType<Expr.DotCall>(result.Root.Opens[0]);
        Assert.Equal("C", outer.Name);
        Assert.Null(outer.Args);
        var inner = Assert.IsType<Expr.DotCall>(outer.Target);
        Assert.Equal("B", inner.Name);
        Assert.Null(inner.Args);
        Assert.IsType<Expr.Resolve>(inner.Target);
    }

    // -- Open declaration: new syntax tests -----------------------------------

    [Fact]
    public void Parse_Open_ByIdentifier()
    {
        var result = Parser.ParseSyntax("open A\n1");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        var resolve = Assert.IsType<Expr.Resolve>(result.Root.Opens[0]);
        Assert.Equal("A", resolve.Name);
    }

    [Fact]
    public void Parse_Open_ByDottedPath()
    {
        var result = Parser.ParseSyntax("open Lib.Sub\n1");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Opens[0]);
        Assert.Equal("Sub", dotCall.Name);
        Assert.Null(dotCall.Args);
        Assert.IsType<Expr.Resolve>(dotCall.Target);
    }

    [Fact]
    public void Parse_Open_ByLoadCall()
    {
        var source = "open load('https://katlang.org/algorithm.kat')\n1";
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        var call = Assert.IsType<Expr.Call>(result.Root.Opens[0]);
        var fn = Assert.IsType<Expr.Resolve>(call.Function);
        Assert.Equal("load", fn.Name);
        Assert.NotNull(fn.Span);
    }

    [Fact]
    public void Parse_Open_StringLiteralSugar_DesugarsToLoad()
    {
        var source = "open 'https://katlang.org/algorithm.kat'\n1";
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        var call = Assert.IsType<Expr.Call>(result.Root.Opens[0]);
        var fn = Assert.IsType<Expr.Resolve>(call.Function);
        Assert.Equal("load", fn.Name);
        Assert.Null(fn.Span);
        Assert.Single(call.Args.Output);
        var strLit = Assert.IsType<Expr.StringLiteral>(call.Args.Output[0]);
        Assert.Equal("https://katlang.org/algorithm.kat", strLit.Value);
    }

    [Fact]
    public void Parse_Open_MultipleTargets()
    {
        var source = "open A, 'https://katlang.org/algorithm.kat', Lib.Sub\n1";
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Root.Opens.Count);
        Assert.IsType<Expr.Resolve>(result.Root.Opens[0]);
        Assert.IsType<Expr.Call>(result.Root.Opens[1]);
        Assert.IsType<Expr.DotCall>(result.Root.Opens[2]);
    }

    [Fact]
    public void Parse_Open_RepeatedDeclaration_ReportsError()
    {
        var result = Parser.ParseSyntax("open A\nopen B\n1");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Only one") && d.Message.Contains("open"));
    }

    [Fact]
    public void Parse_Open_InExpressionPosition_ReportsError()
    {
        var result = Parser.ParseSyntax("1 + open A");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("declaration") && d.Message.Contains("expression"));
    }

    [Fact]
    public void Parse_Open_InvalidTarget_NumericExpression_ReportsError()
    {
        var result = Parser.ParseSyntax("open 1 + 2\n3");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Invalid open form"));
    }

    [Theory]
    [InlineData("open A, B\n3", 2)]
    [InlineData("open A, B, C\n3", 3)]
    public void Parse_Open_CommaList_ParsesIndividualTargets(string source, int expectedTargets)
    {
        // `open` is a declaration with one comma-separated target list; the
        // targets are individual Lean-compatible forms — no OutputJoin and
        // no SequenceSupply node ever lands in the opens list.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Equal(expectedTargets, result.Root.Opens.Count);
        Assert.All(result.Root.Opens, static open => Assert.IsType<Expr.Resolve>(open));
        Assert.Equal(3, Assert.IsType<Expr.Num>(Assert.Single(result.Root.Output)).Value);
    }

    [Fact]
    public void Parse_Open_SingleQuotedStringAndNameTargets_DesugarAndStayInOneList()
    {
        // `open 'url', A`: the single-quoted string desugars through the
        // load sugar and `A` stays a second open target — not an output row.
        var result = Parser.ParseSyntax("open 'https://katlang.org/lib.kat', A");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Opens.Count);
        var load = Assert.IsType<Expr.Call>(result.Root.Opens[0]);
        Assert.Equal("load", Assert.IsType<Expr.Resolve>(load.Function).Name);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(result.Root.Opens[1]).Name);
        Assert.Empty(result.Root.Output);
    }

    [Fact]
    public void Parse_Open_SemicolonSeparator_ReportsCommaDiagnosticNotTwoTargets()
    {
        // ';' is not an open-target separator: `open` is a declaration, not
        // an output expression.
        var result = Parser.ParseSyntax("open A ; B\n3");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Open target lists use ',' separators, not ';'"));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
    }

    [Fact]
    public void Parse_Open_SameLineAdjacency_ReportsMissingCommaNotTwoTargets()
    {
        var result = Parser.ParseSyntax("open A B\n3");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Expected ',' between open targets"));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
    }

    [Theory]
    [InlineData("open A, B, C\n1")]
    [InlineData("open A,\nB,\nC\n1")]
    [InlineData("open A\n, B\n, C\n1")]
    public void Parse_Open_CommaContinuation_SpansLinesLikeGeneralCommaContinuation(string source)
    {
        // Comma keeps its normal explicit line-continuation behavior in
        // open target lists: trailing `open A,` newline `B` and leading
        // `open A` newline `, B` both continue the list.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Root.Opens.Count);
        Assert.All(result.Root.Opens, static open => Assert.IsType<Expr.Resolve>(open));
        Assert.Equal(1, Assert.IsType<Expr.Num>(Assert.Single(result.Root.Output)).Value);
    }

    [Theory]
    [InlineData("open A.B\n1")]
    [InlineData("open A\n.B\n1")]
    [InlineData("open A // comment\n.B\n1")]
    public void Parse_Open_LeadingDotContinuation_ContinuesDottedTarget(string source)
    {
        // A leading '.' is the whitelisted dotted-path continuation, so a
        // dotted open target may span lines; comments stay invisible.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(Assert.Single(result.Root.Opens));
        Assert.Equal("B", dotCall.Name);
        Assert.Null(dotCall.Args);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(dotCall.Target).Name);
    }

    [Fact]
    public void Parse_Open_DotContinuationThenCommaTarget_OpensBoth()
    {
        var result = Parser.ParseSyntax("open A\n.B,\nC\n1");

        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Opens.Count);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Opens[0]);
        Assert.Equal("B", dotCall.Name);
        Assert.Equal("C", Assert.IsType<Expr.Resolve>(result.Root.Opens[1]).Name);
    }

    [Theory]
    [InlineData("open\nA")]
    [InlineData("open // comment\nA")]
    public void Parse_Open_FirstTargetOnLaterLine_ReportsMissingTargetAndKeepsNextRow(string source)
    {
        // The first target must begin on the same physical line as `open`;
        // a newline right after `open` (comments invisible) is a missing
        // target, and `A` stays the next output row.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Expected an open target after 'open' on the same physical line"));
        Assert.Empty(result.Root.Opens);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Output)).Name);
    }

    [Fact]
    public void Parse_Open_DanglingCommaAtEnd_ReportsMissingTarget()
    {
        var result = Parser.ParseSyntax("open A,");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Expected an open target after ','"));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
    }

    [Fact]
    public void Parse_Open_DanglingCommaBeforeDefinitionLine_KeepsDefinition()
    {
        // Recovery after a dangling comma leaves a following definition
        // line intact: P stays a property, never an open target.
        var result = Parser.ParseSyntax("open A,\nP = 1\nP");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Expected an open target after ','"));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
        Assert.Equal("P", Assert.Single(result.Root.Properties).Name);
    }

    [Theory]
    [InlineData("open 'url'...")]
    [InlineData("open 'url'...A")]
    [InlineData("open A, 'url'...")]
    public void Parse_Open_SequenceSupplyOnStringTarget_ReportsSupplyDiagnostic(string source)
    {
        // String atoms go through the same post-atom supply detection as
        // every other atom kind — never just a generic missing-comma
        // diagnostic.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Sequence supply '...' is not valid in open targets"));
        Assert.DoesNotContain(result.Root.Opens, static open => open is Expr.SequenceSupply);
    }

    [Fact]
    public void Parse_Open_CrossLineSemicolon_DoesNotContinueOpenList()
    {
        // Unlike `Output = 1` newline `; 2` (explicit output-body
        // continuation), a ';'-led line after an open declaration is not an
        // open continuation: the declaration ended at the newline.
        var result = Parser.ParseSyntax("open A\n; B");

        Assert.True(result.HasErrors);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
    }

    [Fact]
    public void Parse_Open_DuplicateOpenDeclaration_RemainsInvalid()
    {
        // One `open` declaration per algorithm: a second `open` keeps the
        // existing diagnostic and never becomes multi-open syntax.
        var result = Parser.ParseSyntax("open A\nopen B");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Only one 'open' declaration is allowed per algorithm"));
    }

    [Fact]
    public void Parse_Open_NewlineEndsTargetList()
    {
        // Open target lists are line-bounded: a plain physical newline ends
        // the list, so `open Math` newline `Math.Pi` stays an open plus a
        // report row — the second line is never a second open target.
        var result = Parser.ParseSyntax("open A\nB");

        Assert.False(result.HasErrors);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
        Assert.Equal("B", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Output)).Name);
    }

    [Theory]
    [InlineData("open A...")]
    [InlineData("open A...B")]
    public void Parse_Open_SequenceSupplyTarget_ReportsTargetedDiagnostic(string source)
    {
        // '...' is the sequence supply operator, not an open-target
        // separator: the parser rejects it immediately with a targeted
        // diagnostic instead of passing a SequenceSupply to open resolution.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Sequence supply '...' is not valid in open targets"));
        Assert.DoesNotContain(result.Root.Opens, static open => open is Expr.SequenceSupply);
    }

    [Fact]
    public void Parse_Open_SequenceSupplyTarget_ReportsSourcePositionedSpan()
    {
        var result = Parser.ParseSyntax("open A...B");

        Assert.True(result.HasErrors);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            d => d.Message.Contains("Sequence supply '...' is not valid in open targets"));
        Assert.Equal(1, diagnostic.Span.StartLineNumber);
        Assert.Equal(6, diagnostic.Span.StartColumn);
        Assert.Equal(1, diagnostic.Span.EndLineNumber);
        Assert.Equal(10, diagnostic.Span.EndColumn);
    }

    [Fact]
    public void Parse_Open_SequenceSupplyInCommaList_ReportsDiagnosticAndKeepsValidTargets()
    {
        // Valid comma-separated targets before the invalid supply do not
        // hide the error, and the rejected supply never lands in the opens
        // list.
        var result = Parser.ParseSyntax("open A, B...");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Sequence supply '...' is not valid in open targets"));
        Assert.DoesNotContain(result.Root.Opens, static open => open is Expr.SequenceSupply);
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
    }

    [Fact]
    public void Parse_Open_SemicolonThenSupplyExpression_ReportsCommaDiagnosticAndKeepsSupplyAsOutputRow()
    {
        // The ';' separator mistake is reported on the open declaration; the
        // rest of the line is ordinary output (where sequence supply is
        // legal), never a second open target.
        var result = Parser.ParseSyntax("open A ; B...C");

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("Open target lists use ',' separators, not ';'"));
        Assert.Equal("A", Assert.IsType<Expr.Resolve>(Assert.Single(result.Root.Opens)).Name);
        Assert.IsType<Expr.SequenceSupply>(Assert.Single(result.Root.Output));
    }

    [Fact]
    public void Parse_Open_StringLiteralDoesNotSurviveElaboration()
    {
        var source = "open 'https://katlang.org/test.kat'\n1";
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Opens);
        Assert.IsNotType<Expr.StringLiteral>(result.Root.Opens[0]);
        Assert.IsType<Expr.Call>(result.Root.Opens[0]);
    }

    [Fact]
    public void Parse_Open_AfterProperty_ReportsError()
    {
        var result = Parser.ParseSyntax("X = 1\nopen Math\n2");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("must appear before"));
    }

    [Fact]
    public void Parse_Open_AfterOutput_ReportsError()
    {
        var result = Parser.ParseSyntax("1\nopen Math\n2");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("must appear before"));
    }

    // ── Explicit output syntax ──────────────────────────────────────────────

    [Fact]
    public void Parse_ExplicitOutput_BasicForm()
    {
        var result = Parser.ParseSyntax("A = 6\nOutput = A");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.Equal("A", result.Root.Properties[0].Name);
        Assert.Single(result.Root.Output);
        Assert.IsType<Expr.Resolve>(result.Root.Output[0]);
        Assert.Equal("A", ((Expr.Resolve)result.Root.Output[0]).Name);
    }

    [Fact]
    public void Parse_ExplicitOutput_NotAProperty()
    {
        // Output = expr must NOT create a property named "Output"
        var result = Parser.ParseSyntax("Output = 42");
        Assert.False(result.HasErrors);
        Assert.Empty(result.Root.Properties);
        Assert.Single(result.Root.Output);
        Assert.IsType<Expr.Num>(result.Root.Output[0]);
    }

    [Fact]
    public void Parse_ExplicitOutput_Expression()
    {
        var result = Parser.ParseSyntax("A = 1\nOutput = A + 1");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        Assert.Single(result.Root.Output);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
    }

    [Fact]
    public void Parse_ExplicitOutput_InMiddleOfProperties()
    {
        var result = Parser.ParseSyntax("A = 1\nOutput = A + 1\nB = 2");
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Properties.Count);
        Assert.Equal("A", result.Root.Properties[0].Name);
        Assert.Equal("B", result.Root.Properties[1].Name);
        Assert.Single(result.Root.Output);
        var binary = Assert.IsType<Expr.Binary>(result.Root.Output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
    }

    [Fact]
    public void Parse_ExplicitOutput_MultipleValues()
    {
        var result = Parser.ParseSyntax("Output = 1, 2, 3");
        Assert.False(result.HasErrors);
        Assert.Equal(3, result.Root.Output.Count);
    }

    [Fact]
    public void Parse_ExplicitOutput_DuplicateReportsError()
    {
        var result = Parser.ParseSyntax("Output = 1\nOutput = 2");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("output may be defined only once"));
    }

    [Theory]
    [InlineData("A = 1\nOutput = A\nA")]
    [InlineData("Output = 1\n3")]
    public void Parse_ExplicitThenImplicitOutput_ReportsError(string source)
    {
        // Explicit and implicit output cannot mix in either direction. The
        // `Output = ...` body is line-bounded like every definition body, so
        // a following row is an implicit output row, and it reports the same
        // diagnostic as `Output = ...` after an implicit row. Write
        // `Output = A ; B` (or continue the body with an explicit ';') when
        // joined explicit output is intended.
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot use both"));
    }

    [Fact]
    public void Parse_ExplicitThenImplicitOutput_InNestedBraceAlgorithm_ReportsError()
    {
        var result = Parser.ParseSyntax("F = {Output = 1\n3}\nF");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot use both"));
    }

    [Theory]
    [InlineData("Output = 1 ; 3")]
    [InlineData("Output = 1\n; 3")]
    public void Parse_ExplicitOutput_ExplicitSemicolonBodyJoin_RemainsValid(string source)
    {
        // Joined explicit output stays explicit: a same-line ';' or a
        // leading ';' that continues the definition body across the line
        // boundary both keep the join inside `Output = ...`.
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var outputJoin = Assert.IsType<Expr.OutputJoin>(Assert.Single(result.Root.Output));
        Assert.Equal(1, Assert.IsType<Expr.Num>(outputJoin.Left).Value);
        Assert.Equal(3, Assert.IsType<Expr.Num>(outputJoin.Right).Value);
    }

    [Fact]
    public void Parse_ImplicitThenExplicitOutput_ReportsError()
    {
        var result = Parser.ParseSyntax("A = 1\nA\nOutput = A");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Cannot use both"));
    }

    [Fact]
    public void Parse_PublicOutput_ReportsError()
    {
        var result = Parser.ParseSyntax("public Output = 42");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("'public' cannot be applied to output"));
    }

    [Fact]
    public void Parse_PublicOutputClauseDefinition_ReportsError()
    {
        var result = Parser.ParseSyntax("public Output(x) = x + 1");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("'public' cannot be applied to output"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output cannot declare explicit parameters"));
    }

    [Fact]
    public void Parse_ExplicitOutput_ImplicitOutputSameAST()
    {
        // Both forms should produce equivalent Output lists
        var implicit_ = Parser.ParseSyntax("A = 6\nA");
        var explicit_ = Parser.ParseSyntax("A = 6\nOutput = A");

        Assert.False(implicit_.HasErrors);
        Assert.False(explicit_.HasErrors);

        Assert.Single(implicit_.Root.Output);
        Assert.Single(explicit_.Root.Output);

        // Both should be Resolve("A")
        var implicitOut = Assert.IsType<Expr.Resolve>(implicit_.Root.Output[0]);
        var explicitOut = Assert.IsType<Expr.Resolve>(explicit_.Root.Output[0]);
        Assert.Equal(implicitOut.Name, explicitOut.Name);
    }

    [Fact]
    public void Parse_ExplicitOutput_InsideBlock()
    {
        var result = Parser.ParseSyntax("X = {A = 1\nOutput = A + 1\nB = 2}");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var block = result.Root.Properties[0].Value;
        Assert.Equal(2, block.Properties.Count);
        Assert.Single(block.Output);
    }

    [Fact]
    public void Parse_OutputClauseDefinition_ReportsError()
    {
        var result = Parser.ParseSyntax("Output(x) = x + 1");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output cannot declare explicit parameters"));
    }

    [Fact]
    public void Parse_OutputClauseGroup_ReportsConditionalError()
    {
        var result = Parser.ParseSyntax("Output(0) = 0\nOutput(x) = x");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output cannot declare explicit parameters"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output cannot be a conditional or multi-branch definition"));
    }

    [Fact]
    public void Parse_ParametrizedAlgorithm_WithoutOutput_ReportsError()
    {
        var result = Parser.ParseSyntax(
            """
            Algo(x, y) = {
              Prop = 7
            }
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("declares explicit parameters") &&
            d.Message.Contains("does not define an output"));
    }

    [Fact]
    public void Parse_ParametrizedAlgorithm_WithOnlyHelperProperties_ReportsError()
    {
        var result = Parser.ParseSyntax(
            """
            Algo(x) = {
              A = x + 1
              B = 2
            }
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d =>
            d.Message.Contains("declares explicit parameters") &&
            d.Message.Contains("does not define an output"));
    }

    [Fact]
    public void Parse_ParametrizedAlgorithm_WithExplicitOutputInBody()
    {
        var result = Parser.ParseSyntax("Algo(x) = { Output = x + 1 }");

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var user = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Equal(["x"], user.Params);
        Assert.Single(user.Output);
        var binary = Assert.IsType<Expr.Binary>(user.Output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
    }

    private static void AssertVariadicParameters(
        string source,
        string[] expectedNames,
        ParameterKind[] expectedKinds,
        string[]? expectedPatternDisplay = null)
    {
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var property = Assert.Single(result.Root.Properties);
        var user = Assert.IsType<Algorithm.User>(property.Value);
        Assert.Equal(expectedNames, user.Params);
        Assert.Equal(expectedNames, user.Parameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(expectedKinds, user.Parameters.Select(parameter => parameter.Kind).ToArray());
        Assert.Equal(expectedNames, user.ExplicitParameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(expectedKinds, user.ExplicitParameters.Select(parameter => parameter.Kind).ToArray());
        if (expectedPatternDisplay is not null)
            Assert.Equal(expectedPatternDisplay, user.ParameterPatterns.Select(parameter => parameter.DisplayName).ToArray());
    }

    [Fact]
    public void Parse_VariadicExplicitParameter_ParsesNameAndKind()
        => AssertVariadicParameters(
            "Group(list...) = list",
            ["list"],
            [ParameterKind.Variadic]);

    [Fact]
    public void Parse_NormalThenVariadicExplicitParameter_ParsesNameAndKind()
        => AssertVariadicParameters(
            "Group(a, rest...) = rest",
            ["a", "rest"],
            [ParameterKind.Normal, ParameterKind.Variadic]);

    [Fact]
    public void Parse_VariadicThenSuffixExplicitParameter_ParsesNameAndKind()
        => AssertVariadicParameters(
            "Scale(values..., factor) = values",
            ["values", "factor"],
            [ParameterKind.Variadic, ParameterKind.Normal]);

    [Fact]
    public void Parse_GroupedVariadicExplicitParameter_ParsesFixedSlotKind()
        => AssertVariadicParameters(
            "Group((list...)) = list",
            ["list"],
            [ParameterKind.Variadic],
            ["(list...)"]);

    [Fact]
    public void Parse_GroupedVariadicWithSuffixExplicitParameters_ParsesFixedSlotKind()
        => AssertVariadicParameters(
            "Group((history...), previous, next) = history",
            ["history", "previous", "next"],
            [ParameterKind.Variadic, ParameterKind.Normal, ParameterKind.Normal],
            ["(history...)", "previous", "next"]);

    [Fact]
    public void Parse_HeadTailGroupedExplicitParameter_ParsesRecursivePattern()
        => AssertVariadicParameters(
            "Group((head, tail...)) = head, tail",
            ["head", "tail"],
            [ParameterKind.Normal, ParameterKind.Variadic],
            ["(head, tail...)"]);

    [Fact]
    public void Parse_FirstMiddleLastGroupedExplicitParameter_ParsesRecursivePattern()
        => AssertVariadicParameters(
            "Group((first, middle..., last)) = first, middle, last",
            ["first", "middle", "last"],
            [ParameterKind.Normal, ParameterKind.Variadic, ParameterKind.Normal],
            ["(first, middle..., last)"]);

    [Fact]
    public void Parse_NestedGroupedExplicitParameter_ParsesRecursivePattern()
        => AssertVariadicParameters(
            "Group(((history..., pre2), pre1)) = history, pre2, pre1",
            ["history", "pre2", "pre1"],
            [ParameterKind.Variadic, ParameterKind.Normal, ParameterKind.Normal],
            ["((history..., pre2), pre1)"]);

    [Fact]
    public void Parse_PrefixVariadicSuffixExplicitParameter_ParsesNameAndKind()
        => AssertVariadicParameters(
            "Surround(prefix, values..., suffix) = values",
            ["prefix", "values", "suffix"],
            [ParameterKind.Normal, ParameterKind.Variadic, ParameterKind.Normal]);

    [Fact]
    public void Parse_SeparateVariadicCapturesAtDifferentPatternLevels_Parses()
        => AssertVariadicParameters(
            "Nested((inner...), outer...) = inner.count, outer.count",
            ["inner", "outer"],
            [ParameterKind.Variadic, ParameterKind.Variadic],
            ["(inner...)", "outer..."]);

    [Fact]
    public void Parse_MultipleVariadicExplicitParameters_ReportsError()
    {
        var result = Parser.ParseSyntax("Bad(a..., b...) = b");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Only one variadic parameter is allowed per pattern level."));
    }

    [Fact]
    public void Parse_RepeatedVariadicAndNormalName_ReportsUnsupportedError()
    {
        var result = Parser.ParseSyntax("Bad(xs..., xs) = xs");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Repeated parameter names cannot include variadic captures."));
    }

    [Fact]
    public void Parse_RepeatedVariadicNameAtSameLevel_RemainsRejected()
    {
        var result = Parser.ParseSyntax("Bad(xs..., xs...) = xs");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Only one variadic parameter is allowed per pattern level."));
    }

    [Fact]
    public void Parse_VariadicExplicitParameterWithGrace_ReportsError()
    {
        var result = Parser.ParseSyntax("Bad(a~...) = a");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("Variadic parameters cannot use `~` reordering."));
    }

    [Fact]
    public void Parse_ContainerWithParametrizedChildProperty_RemainsValid()
    {
        var result = Parser.ParseSyntax("Algo = { Prop(x, y) = 7 }");

        Assert.False(result.HasErrors);
        var algo = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Empty(algo.Params);
        var prop = Assert.Single(algo.Properties);
        var child = Assert.IsType<Algorithm.User>(prop.Value);
        Assert.Equal(["x", "y"], child.Params);
        Assert.Single(child.Output);
    }

    [Fact]
    public void Parse_ImplicitOuterOutputOwnership_MarksNestedPropertyLocalOnly()
    {
        var result = Parser.Parse(
            """
            Algo = {
              Prop = x + 1
              x
            }
            """);

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var algo = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Equal(["x"], algo.Params);

        var prop = Assert.Single(algo.Properties);
        Assert.Equal(PropertyExposure.LocalOnlyCapturedAncestorParameters, prop.Exposure);

        var propBody = Assert.IsType<Algorithm.User>(prop.Value);
        Assert.Empty(propBody.Params);
    }

    [Fact]
    public void Parse_ExplicitAndImplicitOuterOutputOwnership_AreEquivalent()
    {
        var implicitResult = Parser.Parse(
            """
            Algo = {
              Prop = x + 1
              x
            }
            """);
        var explicitResult = Parser.Parse(
            """
            Algo(x) = {
              Prop = x + 1
              x
            }
            """);

        Assert.False(implicitResult.HasErrors, string.Join(Environment.NewLine, implicitResult.Diagnostics.Select(d => d.Message)));
        Assert.False(explicitResult.HasErrors, string.Join(Environment.NewLine, explicitResult.Diagnostics.Select(d => d.Message)));

        var implicitAlgo = Assert.IsType<Algorithm.User>(implicitResult.Root.Properties[0].Value);
        var explicitAlgo = Assert.IsType<Algorithm.User>(explicitResult.Root.Properties[0].Value);
        Assert.Equal(["x"], implicitAlgo.Params);
        Assert.Equal(["x"], explicitAlgo.Params);

        var implicitProp = Assert.Single(implicitAlgo.Properties);
        var explicitProp = Assert.Single(explicitAlgo.Properties);
        Assert.Equal(PropertyExposure.LocalOnlyCapturedAncestorParameters, implicitProp.Exposure);
        Assert.Equal(PropertyExposure.LocalOnlyCapturedAncestorParameters, explicitProp.Exposure);
        Assert.Empty(Assert.IsType<Algorithm.User>(implicitProp.Value).Params);
        Assert.Empty(Assert.IsType<Algorithm.User>(explicitProp.Value).Params);
    }

        [Fact]
        public void Parse_NestedLocalPropertyDependencyOnCapturedSibling_PropagatesLocalOnlyExposure()
        {
                var result = Parser.Parse(
                        """
                        Algo(x) = {
                            Captured = x + 1
                            Wrapper = {
                                Inner = Captured
                                Inner
                            }
                            x
                        }
                        """);

                Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
                var algo = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);

                var captured = Assert.Single(algo.Properties, property => property.Name == "Captured");
                var wrapper = Assert.Single(algo.Properties, property => property.Name == "Wrapper");

                Assert.Equal(PropertyExposure.LocalOnlyCapturedAncestorParameters, captured.Exposure);
                Assert.Equal(PropertyExposure.LocalOnlyCapturedAncestorParameters, wrapper.Exposure);

                var wrapperBody = Assert.IsType<Algorithm.User>(wrapper.Value);
                var inner = Assert.Single(wrapperBody.Properties);
                Assert.Equal("Inner", inner.Name);
                Assert.Equal(PropertyExposure.LocalOnlyCapturedAncestorParameters, inner.Exposure);
        }

    [Fact]
    public void Parse_NestedPropertyOwnsParameter_WhenOuterOutputDoesNotUseIt()
    {
        var result = Parser.Parse(
            """
            Algo = {
              Prop = x + 1
              7
            }
            """);

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        var algo = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Empty(algo.Params);

        var prop = Assert.Single(algo.Properties);
        Assert.Equal(PropertyExposure.Exported, prop.Exposure);
        Assert.Equal(["x"], Assert.IsType<Algorithm.User>(prop.Value).Params);
    }

    [Fact]
    public void Parse_PlainContainerAlgorithm_RemainsValid()
    {
        var result = Parser.ParseSyntax("Algo = { Prop = 7 }");

        Assert.False(result.HasErrors);
        var algo = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Empty(algo.Params);
        Assert.Empty(algo.Output);
        Assert.Single(algo.Properties);
    }

    [Fact]
    public void Parse_OutputPropertyAccess_ReportsError()
    {
        var result = Parser.ParseSyntax(
            """
            Algo(x) = {
              Output = x + 1
            }
            Algo.Output(6)
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output is the designated result of an algorithm"));
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Instead of `Algo.Output(6)`, write `Algo(6)`"));
    }

    [Fact]
    public void Parse_NestedOutputPropertyAccess_ReportsError()
    {
        var result = Parser.ParseSyntax(
            """
            Outer = {
              Inner(x) = {
                Output = x + 10
              }
            }
            Outer.Inner.Output(6)
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output is the designated result of an algorithm"));
    }

    [Fact]
    public void Parse_BareOutputPropertyAccess_ReportsError()
    {
        var result = Parser.ParseSyntax(
            """
            Algo = {
              Output = 5
            }
            Algo.Output
            """);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Output is the designated result of an algorithm"));
    }

    // -- Double-parens: ordinary grouping unless preserving a grouped receiver block ---

    [Fact]
    public void Parse_ParenSubExpr_FirstCallArg_ParsesNormally()
    {
        // f((a + b) mod 2, c) must parse without error now that
        // double-parens detection is removed
        var result = Parser.ParseSyntax("F((a + b) mod 2, c)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(2, call.Args.Output.Count);
        // First arg should be binary mod expression
        var modExpr = Assert.IsType<Expr.Binary>(call.Args.Output[0]);
        Assert.Equal(BinaryOp.Mod, modExpr.Op);
    }

    [Fact]
    public void Parse_If_ParenSubExpr_FirstArg_ParsesNormally()
    {
        var result = Parser.ParseSyntax("if((a + b) mod 2 == 0, 1, 0)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(3, call.Args.Output.Count);
    }

    [Fact]
    public void Parse_DoubleParens_RemainsOrdinaryGrouping()
    {
        // Scalar/group-free cases still collapse to ordinary nested grouping.
        var result = Parser.ParseSyntax("X = ((1 + 2))");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var output = result.Root.Properties[0].Value.Output;
        Assert.Single(output);
        var binary = Assert.IsType<Expr.Binary>(output[0]);
        Assert.Equal(BinaryOp.Add, binary.Op);
    }

    // -- Direct-call argument boundaries for while/repeat ---

    [Fact]
    public void Parse_While_DirectCall_MultiInit_PreservesArgs()
    {
        var result = Parser.ParseSyntax("while(Step, x, 0)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(3, call.Args.Output.Count);
        Assert.IsType<Expr.Resolve>(call.Args.Output[0]);
        Assert.IsType<Expr.Resolve>(call.Args.Output[1]);
        Assert.IsType<Expr.Num>(call.Args.Output[2]);
    }

    [Fact]
    public void Parse_While_DirectCall_TwoArgs_NoLowering()
    {
        // while(Step, init) stays with 2 args, no lowering
        var result = Parser.ParseSyntax("while(Step, init)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(2, call.Args.Output.Count);
        Assert.IsType<Expr.Resolve>(call.Args.Output[1]);
    }

    [Fact]
    public void Parse_Repeat_DirectCall_MultiInit_PreservesArgs()
    {
        var result = Parser.ParseSyntax("repeat(Step, n, x, 0)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(4, call.Args.Output.Count);
        Assert.IsType<Expr.Resolve>(call.Args.Output[0]);
        Assert.IsType<Expr.Resolve>(call.Args.Output[1]);
        Assert.IsType<Expr.Resolve>(call.Args.Output[2]);
        Assert.IsType<Expr.Num>(call.Args.Output[3]);
    }

    [Fact]
    public void Parse_Repeat_DirectCall_ThreeArgs_NoLowering()
    {
        // repeat(Step, n, init) stays with 3 args, no lowering
        var result = Parser.ParseSyntax("repeat(Step, n, init)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(3, call.Args.Output.Count);
        Assert.IsType<Expr.Resolve>(call.Args.Output[2]);
    }

    [Fact]
    public void Parse_First_DirectCall_MultiResult_PreservesOrdinaryArgs()
    {
        // first(x, y, z) should stay as three ordinary call arguments.
        var result = Parser.ParseSyntax("first(x, y, z)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(3, call.Args.Output.Count);
        Assert.All(call.Args.Output, expression => Assert.IsType<Expr.Resolve>(expression));
    }

    [Fact]
    public void Parse_Last_DirectCall_MultiResult_PreservesOrdinaryArgs()
    {
        // last(x, y, z) should stay as three ordinary call arguments.
        var result = Parser.ParseSyntax("last(x, y, z)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(3, call.Args.Output.Count);
        Assert.All(call.Args.Output, expression => Assert.IsType<Expr.Resolve>(expression));
    }

    [Fact]
    public void Parse_Take_DirectCall_PreservesSuffixCountOrder()
    {
        var result = Parser.ParseSyntax("take(x, y, z, n)");
        Assert.False(result.HasErrors);

        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(4, call.Args.Output.Count);
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(call.Args.Output[0]).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(call.Args.Output[1]).Name);
        Assert.Equal("z", Assert.IsType<Expr.Resolve>(call.Args.Output[2]).Name);
        Assert.Equal("n", Assert.IsType<Expr.Resolve>(call.Args.Output[3]).Name);
    }

    [Fact]
    public void Parse_Skip_DirectCall_PreservesSuffixCountOrder()
    {
        var result = Parser.ParseSyntax("skip(x, y, z, n)");
        Assert.False(result.HasErrors);

        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        Assert.Equal(4, call.Args.Output.Count);
        Assert.Equal("x", Assert.IsType<Expr.Resolve>(call.Args.Output[0]).Name);
        Assert.Equal("y", Assert.IsType<Expr.Resolve>(call.Args.Output[1]).Name);
        Assert.Equal("z", Assert.IsType<Expr.Resolve>(call.Args.Output[2]).Name);
        Assert.Equal("n", Assert.IsType<Expr.Resolve>(call.Args.Output[3]).Name);
    }

    [Fact]
    public void Parse_DotCall_Take_NoLowering_InParser()
    {
        var result = Parser.ParseSyntax("values.take(n)");
        Assert.False(result.HasErrors);

        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("take", dotCall.Name);
        Assert.NotNull(dotCall.Args);
        Assert.Single(dotCall.Args!.Output);
        Assert.Equal("n", Assert.IsType<Expr.Resolve>(dotCall.Args.Output[0]).Name);
    }

    [Fact]
    public void Parse_DotCall_While_NoLowering_InParser()
    {
        // Step.while(x, 0) keeps both explicit init arguments in the parser.
        var result = Parser.ParseSyntax("Step.while(x, 0)");
        Assert.False(result.HasErrors);
        var dotCall = Assert.IsType<Expr.DotCall>(result.Root.Output[0]);
        Assert.Equal("while", dotCall.Name);
        Assert.NotNull(dotCall.Args);
        Assert.Equal(2, dotCall.Args!.Output.Count);
    }

    // ── if arity validation ─────────────────────────────────────────────────

    [Fact]
    public void Parse_If_TwoArgs_ReportsBuiltinArityError()
    {
        var result = Parser.ParseSyntax("if(1, 2)");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Builtin 'if' expects 3 arguments: condition, whenTrue, whenFalse."));
    }

    [Fact]
    public void Parse_If_ThreeArgs_RemainsIf()
    {
        var result = Parser.ParseSyntax("if(1, 2, 3)");
        Assert.False(result.HasErrors);
        var call = Assert.IsType<Expr.Call>(result.Root.Output[0]);
        var resolve = Assert.IsType<Expr.Resolve>(call.Function);
        Assert.Equal("if", resolve.Name);
        Assert.Equal(3, call.Args.Output.Count);
    }

    [Fact]
    public void Parse_If_TwoArgs_InsideExpression_ReportsBuiltinArityError()
    {
        var result = Parser.ParseSyntax("10 * if(7 < 6, 1)");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Builtin 'if' expects 3 arguments: condition, whenTrue, whenFalse."));
    }

    [Fact]
    public void Parse_If_ZeroArgs_ReportsError()
    {
        var result = Parser.ParseSyntax("if()");
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Parse_If_OneArg_ReportsError()
    {
        var result = Parser.ParseSyntax("if(1)");
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Parse_If_FourArgs_ReportsError()
    {
        var result = Parser.ParseSyntax("if(1, 2, 3, 4)");
        Assert.True(result.HasErrors);
    }

    // ── Clause definition classification ────────────────────────────────────

    [Fact]
    public void Parse_Clause_FlatMultiBinderSingleBranch_ElaboratesToOrdinaryAlgorithm()
    {
        var result = Parser.ParseSyntax("K(a, b) = a");
        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var prop = result.Root.Properties[0];
        Assert.Equal("K", prop.Name);
        var user = Assert.IsType<Algorithm.User>(prop.Value);
        Assert.Equal(["a", "b"], user.Params);
        Assert.Single(user.Output);
    }

    [Fact]
    public void Parse_Clause_SingleBinder_ElaboratesToOrdinaryAlgorithm()
    {
        var result = Parser.ParseSyntax("Id(x) = x");

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var user = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Equal(["x"], user.Params);
        Assert.Single(user.Output);
    }

    [Fact]
    public void Parse_Clause_GroupedPattern_ElaboratesToOrdinaryParameterPattern()
    {
        var result = Parser.ParseSyntax("Stats(x, (acc, counter)) = (x + acc, counter + 1)");

        Assert.False(result.HasErrors);
        var user = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Equal(["x", "acc", "counter"], user.Params);
        Assert.Equal(["x", "(acc, counter)"], user.ParameterPatterns.Select(pattern => pattern.DisplayName).ToArray());
    }

    [Fact]
    public void Parse_ClauseGroup_DoubleParenGroupedPattern_PreservesOuterSingletonGroup()
    {
        var source = """
            MarkGroupedRange((a, b, c)) = 1
            MarkGroupedRange(x) = 0
            """;
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Equal(2, cond.Branches.Count);

        var outerGroup = Assert.IsType<Pattern.Group>(cond.Branches[0].Pattern);
        Assert.Single(outerGroup.Items);
        var innerGroup = Assert.IsType<Pattern.Group>(outerGroup.Items[0]);
        Assert.Equal(3, innerGroup.Items.Count);
        Assert.IsType<Pattern.Bind>(cond.Branches[1].Pattern);
    }

    [Fact]
    public void Parse_Clause_LiteralPattern_RemainsConditionalAlgorithm()
    {
        var result = Parser.ParseSyntax("F(1) = 100");

        Assert.False(result.HasErrors);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Single(cond.Branches);
        Assert.IsType<Pattern.LitInt>(cond.Branches[0].Pattern);
    }

    [Fact]
    public void Parse_ClauseGroup_LiteralThenPlainBinder_RemainsConditionalAlgorithm()
    {
        var source = """
            F(0) = 0
            F(x) = 1
            """;
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        Assert.Single(result.Root.Properties);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Equal(2, cond.Branches.Count);
        Assert.IsType<Pattern.LitInt>(cond.Branches[0].Pattern);
        Assert.IsType<Pattern.Bind>(cond.Branches[1].Pattern);
    }

    [Fact]
    public void Parse_Conditional_VariadicBranchPattern_ReportsError()
    {
        var source = """
            F(0) = 0
            F(values...) = values.count
            """;
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("Variadic parameters are only supported in ordinary explicit parameter lists") &&
            d.Message.Contains("F"));
        Assert.Equal(2, diag.Span.StartLineNumber);
    }

    [Fact]
    public void Parse_Conditional_MultipleBranches()
    {
        var source = """
            F(1) = 100
            F((x)) = 0
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Equal(2, cond.Branches.Count);
    }

    [Fact]
    public void Parse_Clause_RepeatedBinder_ElaboratesToOrdinaryEqualityPattern()
    {
        var result = Parser.ParseSyntax("F(a, a) = a");

        Assert.False(result.HasErrors);
        var user = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Equal(["a", "a"], user.Params);
    }

    [Fact]
    public void Parse_Conditional_MixedWithNormalProperty_ReportsError()
    {
        var source = """
            F = 1
            F((x)) = x
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("already defined"));
    }

    [Fact]
    public void Parse_Conditional_NegativeLiteralPattern()
    {
        var result = Parser.ParseSyntax("F(-1) = 100");
        Assert.False(result.HasErrors);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        var pat = cond.Branches[0].Pattern;
        // Single element pattern: outer parens consumed by algorithm parser,
        // ParsePattern returns the atom directly (no group wrapper)
        var lit = Assert.IsType<Pattern.LitInt>(pat);
        Assert.Equal(-1m, lit.Value);
    }

    [Fact]
    public void Parse_Conditional_NestedGroupPattern()
    {
        var result = Parser.ParseSyntax("F(a, (b, c)) = a");
        Assert.False(result.HasErrors);
        var user = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        Assert.Equal(["a", "b", "c"], user.Params);
        Assert.Equal(["a", "(b, c)"], user.ParameterPatterns.Select(pattern => pattern.DisplayName).ToArray());
    }

    // ── Grace rejection in clause-head patterns ─────────────────────────────

    [Fact]
    public void Parse_ClauseHead_PrefixGraceInPattern_ReportsError()
    {
        var result = Parser.ParseSyntax("F(~a, b) = a");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in clause-head patterns"));
    }

    [Fact]
    public void Parse_ClauseHead_PostfixGraceInPattern_ReportsError()
    {
        var result = Parser.ParseSyntax("F(a~, b) = a");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in clause-head patterns"));
    }

    [Fact]
    public void Parse_ClauseHead_GraceInNestedPattern_ReportsError()
    {
        var result = Parser.ParseSyntax("F(a, (~b, c)) = a");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in clause-head patterns"));
    }

    // ── Grace rejection in conditional branch bodies ────────────────────────

    [Fact]
    public void Parse_Conditional_PrefixGraceInBody_ReportsError()
    {
        var result = Parser.ParseSyntax("F(1, x) = ~x");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in conditional branch bodies"));
    }

    [Fact]
    public void Parse_Conditional_PostfixGraceInBody_ReportsError()
    {
        var result = Parser.ParseSyntax("F(1, x) = x~");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in conditional branch bodies"));
    }

    [Fact]
    public void Parse_Conditional_GraceInNestedBodyExpr_ReportsError()
    {
        var result = Parser.ParseSyntax("F(1, x) = 1 * ~x");
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in conditional branch bodies"));
    }

    [Fact]
    public void Parse_Conditional_GraceInBody_ErrorSpanPointsToGraceLine()
    {
        var source = """
            F(1, qty) = qty
            F(2, qty) = ~qty
            F(3, qty) = qty
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d => d.Message.Contains("Grace is not allowed in conditional branch bodies"));
        Assert.Equal(2, diag.Span.StartLineNumber);
    }

    // ── Uniform top-level pattern arity validation ──────────────────────────

    [Fact]
    public void Parse_Conditional_SameArity_NestedStructureDiffers_Valid()
    {
        // Both branches have top-level arity 2; nested structure differs
        var source = """
            Else(1, (a, b)) = a
            Else(2, x) = x
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Equal(2, cond.Branches.Count);
    }

    [Fact]
    public void Parse_Conditional_SameArity_FlatBranches_Valid()
    {
        // Both branches have top-level arity 3
        var source = """
            F(1, a, b) = a
            F(2, a, b) = b
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        var cond = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Equal(2, cond.Branches.Count);
    }

    [Fact]
    public void Parse_Conditional_SingleBranch_AlwaysValid()
    {
        // Single branch: no arity conflict possible
        var result = Parser.ParseSyntax("K((x)) = x");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_Conditional_DifferentArity_ReportsError()
    {
        // First branch arity 2, second branch arity 3
        var source = """
            Expense(1, qty) = qty
            Expense(2, a, qty) = a * qty
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("same top-level pattern arity") &&
            d.Message.Contains("Expense"));
        // Error span should point to the second branch (line 2)
        Assert.Equal(2, diag.Span.StartLineNumber);
    }

    [Fact]
    public void Parse_Conditional_Arity1vs2_ReportsError()
    {
        // First branch arity 1 (grouped singleton), second branch arity 2 (group)
        var source = """
            F((x)) = 1
            F(a, (b)) = a
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("same top-level pattern arity") &&
            d.Message.Contains("Expected 1") &&
            d.Message.Contains("arity 2"));
        // Error span should point to the second branch (line 2)
        Assert.Equal(2, diag.Span.StartLineNumber);
    }

    [Fact]
    public void Parse_Conditional_ThreeBranches_ThirdMismatches_ReportsError()
    {
        // First two branches arity 2, third branch arity 3
        var source = """
            G(1, x) = x
            G(2, x) = x + 1
            G(3, x, y) = x + y
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("same top-level pattern arity") &&
            d.Message.Contains("G"));
        // Error span should point to the third branch (line 3)
        Assert.Equal(3, diag.Span.StartLineNumber);
    }

    // ── Uniform top-level output arity validation ─────────────────────────

    [Fact]
    public void Parse_Conditional_SameOutputArity1_Valid()
    {
        // Both branches return top-level output arity 1 — valid
        var source = """
            Expense(1, qty) = qty * 2
            Expense(2, qty) = qty * 3
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_Conditional_SameOutputArity2_Valid()
    {
        // Both branches return top-level output arity 2 — valid
        var source = """
            F(1, x) = x, x + 1
            F(2, x) = 0, x
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_Conditional_SameOutputArity_NestedDiffers_Valid()
    {
        // Both branches return top-level output arity 2;
        // nested internal output structure differs — valid
        var source = """
            G(1, x) = x, (x + 1, x + 2)
            G(2, x) = x, x * 2
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_Conditional_SingleBranch_OutputArity_AlwaysValid()
    {
        // Single branch: no output arity conflict possible
        var result = Parser.ParseSyntax("F((x)) = x, x + 1");
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_OrdinaryGroupedParameterBody_PreservedAsSingleValue()
    {
        var source = """
            Stats(x, (acc, counter)) = (x + acc, counter + 1)
            """;
        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var user = Assert.IsType<Algorithm.User>(result.Root.Properties[0].Value);
        var body = user;
        Assert.Single(body.Output);
        var block = Assert.IsType<Expr.Block>(body.Output[0]);
        Assert.Equal(2, block.Algorithm.Output.Count);
    }

    [Fact]
    public void Parse_Conditional_DifferentOutputArity_ReportsError()
    {
        // First branch output arity 2, second branch output arity 1
        var source = """
            Expense(1, qty) = qty * 2, 2
            Expense(2, qty) = qty * 3
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("same top-level output arity") &&
            d.Message.Contains("Expense"));
        // Error span should point to the second branch (line 2)
        Assert.Equal(2, diag.Span.StartLineNumber);
    }

    [Fact]
    public void Parse_Conditional_OutputArity1vs2_ReportsError()
    {
        // First branch output arity 1, second branch output arity 2
        var source = """
            F(1, x) = x
            F(2, x) = x, x + 1
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("same top-level output arity") &&
            d.Message.Contains("Expected 1") &&
            d.Message.Contains("output arity 2"));
        // Error span should point to the second branch (line 2)
        Assert.Equal(2, diag.Span.StartLineNumber);
    }

    [Fact]
    public void Parse_Conditional_ThreeBranches_ThirdOutputMismatches_ReportsError()
    {
        // First two branches output arity 1, third branch output arity 2
        var source = """
            G(1, x) = x
            G(2, x) = x + 1
            G(3, x) = x, x + 1
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d =>
            d.Message.Contains("same top-level output arity") &&
            d.Message.Contains("G"));
        // Error span should point to the third branch (line 3)
        Assert.Equal(3, diag.Span.StartLineNumber);
    }

    // ── Old `when` syntax no longer recognized ─────────────────────────────

    [Fact]
    public void Parse_Conditional_WhenSyntax_NotRecognized()
    {
        // Old `when` syntax no longer exists. `when` is now a regular identifier.
        // `F when (1) = 100` parses as: F (output), then when(1)=100 (conditional branch named "when").
        // This is semantically different from the old meaning but NOT a parse error.
        var source = """
            F when (1) = 100
            F when ((x)) = 0
            """;
        var result = Parser.ParseSyntax(source);
        // F is output, when(1)=100 and when(x)=0 are branches of conditional "when"
        Assert.False(result.HasErrors);
        // The old name-based conditional algorithm "F" does NOT exist
        Assert.DoesNotContain(result.Root.Properties, p => p.Name == "F");
        // Instead, "when" is the conditional algorithm name
        Assert.Contains(result.Root.Properties, p => p.Name == "when");
    }

    [Fact]
    public void Parse_Conditional_WhenSyntax_SingleBranch_NotRecognized()
    {
        // K when ((a, b)) = a → parses as K (output), when((a,b))=a (conditional branch named "when")
        var result = Parser.ParseSyntax("K when ((a, b)) = a");
        Assert.False(result.HasErrors);
        Assert.Contains(result.Root.Properties, p => p.Name == "when");
        Assert.Single(result.Root.Output); // K is output
    }

    // ── Disambiguation and edge cases ──────────────────────────────────────

    [Fact]
    public void Parse_Conditional_DefinitionVsCall_Disambiguated()
    {
        // First two lines are definitions (followed by =), last line is a call (no =)
        var source = """
            F(1) = 100
            F((x)) = 0
            F(1)
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        // One property (conditional) + one output expression (the call)
        Assert.Single(result.Root.Properties);
        Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Single(result.Root.Output);
    }

    [Fact]
    public void Parse_Conditional_CallInBodyRemainsCall()
    {
        // F(x) in the body of G is a call, not a branch definition
        var source = """
            F(1) = 100
            F((x)) = 0
            G = F(1)
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        Assert.Equal(2, result.Root.Properties.Count);
        // G is a regular property (added first), F is conditional (added after loop)
        var gProp = result.Root.Properties.Single(p => p.Name == "G");
        var fProp = result.Root.Properties.Single(p => p.Name == "F");
        Assert.IsType<Algorithm.Conditional>(fProp.Value);
        // G's body should be a User algorithm, not Conditional
        Assert.IsType<Algorithm.User>(gProp.Value);
    }

    [Fact]
    public void Parse_PublicClause_SetsIsPublicOnOrdinarySingleClause()
    {
        var result = Parser.ParseSyntax("public F(x) = x");

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        Assert.True(property.IsPublic);
        var user = Assert.IsType<Algorithm.User>(property.Value);
        Assert.Equal(["x"], user.Params);
    }

    [Fact]
    public void Parse_PublicClause_SetsIsPublicOnSingleBranchConditional()
    {
        var result = Parser.ParseSyntax("public F(0) = 1");

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        Assert.True(property.IsPublic);
        Assert.IsType<Algorithm.Conditional>(property.Value);
    }

    [Fact]
    public void Parse_PublicClause_MarksWholeClauseFamilyPublic()
    {
        var source = """
            public F(0) = 0
            public F(x) = 1
            """;

        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var property = Assert.Single(result.Root.Properties);
        Assert.Equal("F", property.Name);
        Assert.True(property.IsPublic);
        var conditional = Assert.IsType<Algorithm.Conditional>(property.Value);
        Assert.Equal(2, conditional.Branches.Count);
        Assert.Equal(2, property.DeclarationSpans.Count);
    }

    [Theory]
    [InlineData("F(0) = 0\npublic F(x) = 1")]
    [InlineData("public F(0) = 0\nF(x) = 1")]
    public void Parse_PublicClause_MixedVisibilityInClauseFamilyReportsError(string source)
    {
        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("All clauses of 'F' must use the same public modifier"));
    }

    // ── Property redefinition detection ────────────────────────────────────────

    [Fact]
    public void Parse_DuplicateProperty_ReportsError()
    {
        var source = """
            A = 5
            A = 6
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Property 'A' is already defined"));
    }

    [Fact]
    public void Parse_DuplicateProperty_WithImplicitParams_ReportsError()
    {
        var source = """
            B = x + 1
            B = x + 2
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Property 'B' is already defined"));
    }

    [Fact]
    public void Parse_DuplicatePublicProperty_ReportsError()
    {
        var source = """
            public A = 5
            public A = 6
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Property 'A' is already defined"));
    }

    [Fact]
    public void Parse_DuplicateProperty_MixedVisibility_ReportsError()
    {
        var source = """
            A = 5
            public A = 6
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Property 'A' is already defined"));
    }

    [Fact]
    public void Parse_DuplicateProperty_PublicThenPrivate_ReportsError()
    {
        var source = """
            public A = 5
            A = 6
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Message.Contains("Property 'A' is already defined"));
    }

    [Fact]
    public void Parse_DuplicateConditionalBranchPattern_LitInt_ReportsError()
    {
        var source = """
            F(1) = 100
            F(1) = 200
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d => d.Message.Contains("Duplicate branch pattern"));
        Assert.Equal(2, diag.Span.StartLineNumber);
        Assert.Equal(1, diag.Span.StartColumn);
        Assert.Equal(2, diag.Span.EndLineNumber);
    }

    [Fact]
    public void Parse_DuplicateConditionalBranchPattern_Bind_ReportsError()
    {
        var source = """
            F((x)) = x + 1
            F((x)) = x + 2
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d => d.Message.Contains("Duplicate branch pattern"));
        Assert.Equal(2, diag.Span.StartLineNumber);
        Assert.Equal(1, diag.Span.StartColumn);
        Assert.Equal(2, diag.Span.EndLineNumber);
    }

    [Fact]
    public void Parse_Conditional_RepeatedBinderConstraintAndFallback_AreDistinct()
    {
        var source = """
            Equal(x, x) = 1
            Equal(x, y) = 0
            """;

        var result = Parser.ParseSyntax(source);

        Assert.False(result.HasErrors);
        var conditional = Assert.IsType<Algorithm.Conditional>(result.Root.Properties[0].Value);
        Assert.Equal(2, conditional.Branches.Count);
    }

    [Fact]
    public void Parse_DuplicateConditionalRepeatedBinderPattern_UsesAlphaEquivalence()
    {
        var source = """
            Equal(x, x) = 1
            Equal(a, a) = 0
            """;

        var result = Parser.ParseSyntax(source);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Duplicate branch pattern"));
    }

    [Fact]
    public void Parse_DuplicateConditionalBranchPattern_WithFinalCall_SpanPointsToDuplicateBranch()
    {
        var source = """
            F(1) = 10
            F(1) = 20
            F(1)
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
        var diag = Assert.Single(result.Diagnostics, d => d.Message.Contains("Duplicate branch pattern"));
        Assert.Equal(2, diag.Span.StartLineNumber);
        Assert.Equal(1, diag.Span.StartColumn);
        Assert.Equal(2, diag.Span.EndLineNumber);
    }

    [Fact]
    public void Parse_ConditionalBranchPattern_DifferentLiterals_IsValid()
    {
        var source = """
            F(1) = 100
            F(2) = 200
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_ConditionalBranchPattern_LitAndBind_IsValid()
    {
        var source = """
            F(1) = 100
            F((x)) = 0
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Parse_DistinctProperties_NoError()
    {
        var source = """
            A = 5
            B = 6
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
    }

    // ── String literal pattern tests ────────────────────────────────────────

    [Fact]
    public void Parse_StringLiteralPattern_InConditionalBranch()
    {
        var source = """
            Price('apples') = 0.80
            Price('tomatoes') = 1.20
            """;
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        var cond = Assert.IsType<Algorithm.Conditional>(
            result.Root.Properties.Single(p => p.Name == "Price").Value);
        Assert.Equal(2, cond.Branches.Count);
        Assert.IsType<Pattern.LitString>(cond.Branches[0].Pattern);
        Assert.Equal("apples", ((Pattern.LitString)cond.Branches[0].Pattern).Value);
    }

    [Fact]
    public void Parse_StringLiteralExpression_Standalone()
    {
        var source = "'hello'";
        var result = Parser.ParseSyntax(source);
        Assert.False(result.HasErrors);
        var output = result.Root.Output;
        Assert.Single(output);
        Assert.IsType<Expr.StringLiteral>(output[0]);
        Assert.Equal("hello", ((Expr.StringLiteral)output[0]).Value);
    }

    [Fact]
    public void Parse_UnterminatedString_ProducesError()
    {
        var source = "'hello";
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void Parse_DuplicateStringPatterns_ProducesError()
    {
        var source = """
            F('a') = 1
            F('a') = 2
            """;
        var result = Parser.ParseSyntax(source);
        Assert.True(result.HasErrors);
    }
}
