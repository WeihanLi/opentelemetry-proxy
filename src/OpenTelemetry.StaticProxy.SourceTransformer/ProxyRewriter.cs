﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenTelemetry.StaticProxy.SourceTransformer;

internal sealed class ProxyRewriter(
    ProxyRewriterContext proxyRewriterContext,
    Dictionary<TypeDeclarationSyntax, ITypeContext> types,
    Dictionary<MethodDeclarationSyntax, IMethodContext> methods)
    : CSharpSyntaxRewriter
{
    private bool _addAssemblyAttribute;

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var syntaxNode = base.VisitCompilationUnit(node);

        return _addAssemblyAttribute && syntaxNode is CompilationUnitSyntax compilationUnit
            ? compilationUnit.AddAttributeLists(SyntaxFactory.AttributeList(
                    SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(
                        SyntaxFactory.ParseName("OpenTelemetry.Proxy.ProxyHasGeneratedAttribute"))))
                .WithTarget(SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Identifier("assembly"))))
            : syntaxNode;
    }

    #region VisitTypeDeclarationSyntax

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) =>
        VisitTypeDeclarationSyntax(node, base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) =>
        VisitTypeDeclarationSyntax(node, base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) =>
        VisitTypeDeclarationSyntax(node, base.VisitRecordDeclaration(node));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
        VisitTypeDeclarationSyntax(node, base.VisitInterfaceDeclaration(node));

    private SyntaxNode? VisitTypeDeclarationSyntax(TypeDeclarationSyntax node, SyntaxNode? syntaxNode)
    {
        if (syntaxNode is not TypeDeclarationSyntax type) return syntaxNode;

        var hasMethod = methods.Keys.Select(m => m.GetDeclaringType()).Any(t => t == node);

        if (!types.TryGetValue(node, out var context) ||
            context is not IActivitySourceContext activitySourceContext)
            return AddLineNumber(node, type);

        type = AddActivitySource(type, activitySourceContext.ActivitySourceName);

        if (!proxyRewriterContext.TypeHasAddedAttribute.Add(activitySourceContext.ActivitySourceName))
            return AddLineNumber(node, type);

        type = type.AddAttributeLists(SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(
                SyntaxFactory.ParseName("OpenTelemetry.Proxy.ProxyHasGeneratedAttribute")))));

        if (hasMethod && !proxyRewriterContext.AssemblyHasAddedAttribute)
            proxyRewriterContext.AssemblyHasAddedAttribute = _addAssemblyAttribute = true;

        return AddLineNumber(node, type);
    }

    /// <summary>Add raw start line number and end line number.</summary>
    private static TypeDeclarationSyntax AddLineNumber(TypeDeclarationSyntax node, TypeDeclarationSyntax type)
    {
        type = node.Modifiers.Count > 0
            ? type.WithModifiers(new(type.Modifiers.Skip(1)
                .Prepend(type.Modifiers[0].RestoreLineNumber(node.Modifiers[0].GetLineNumber(node.SyntaxTree)))))
            : type.WithKeyword(type.Keyword.RestoreLineNumber(node.Keyword.GetLineNumber(node.SyntaxTree)));

        return type.WithCloseBraceToken(
            node.CloseBraceToken.RestoreLineNumber(node.CloseBraceToken.GetLineNumber(node.SyntaxTree)));
    }

    private static TypeDeclarationSyntax AddActivitySource(TypeDeclarationSyntax node, string activitySourceName)
    {
        var fieldDeclaration = SyntaxFactory.FieldDeclaration(SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName("System.Diagnostics.ActivitySource"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier("@ActivitySource@").WithWhiteSpace())
                    .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.ObjectCreationExpression(
                            SyntaxFactory.ParseTypeName("System.Diagnostics.ActivitySource").WithLeadingWhiteSpace(),
                            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                    SyntaxKind.StringLiteralExpression,
                                    SyntaxFactory.Literal(activitySourceName))),
                                SyntaxFactory.Argument(SyntaxFactory.ParseExpression(
                                        $"typeof({node.GetTypeName(1)}).Assembly.GetName().Version?.ToString()")
                                    .WithLeadingWhiteSpace())
                            ])), null)
                        .WithLeadingWhiteSpace())))))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingWhiteSpace(),
                SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingWhiteSpace(),
                SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingWhiteSpace()));

        return node.AddMembers(fieldDeclaration.WithNewLine(node.GetIndent() + 4));
    }

    #endregion

    #region VisitMethodDeclaration

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (!methods.TryGetValue(node, out var context) || node.GetDeclaringType() is not { } type) return node;

        MethodDeclarationSyntax? method;
        if (context is ActivityContext activityContext)
        {
            var syntaxNode = base.VisitMethodDeclaration(node);

            method = syntaxNode as MethodDeclarationSyntax;
            if (method == null) return syntaxNode;

            syntaxNode = AddActivity(type, method, activityContext);

            method = syntaxNode as MethodDeclarationSyntax;
            if (method == null) return syntaxNode;
        }
        else
            method = context is MethodActivityNameContext activityNameContext
                ? AddActivityName(type, node, activityNameContext)
                : AddSuppressInstrumentation(node);

        if (method.Body == null) return method;

        ILineNumber line;
        if (node.Body != null) line = node.Body.CloseBraceToken.GetLineNumber(node.SyntaxTree);
        else if (node.ExpressionBody != null) line = node.ExpressionBody.Expression.GetLineNumber();
        else return method;

        return method.WithBody(method.Body.WithCloseBraceToken(method.Body.CloseBraceToken.RestoreLineNumber(line)));
    }

    private static MethodDeclarationSyntax AddSuppressInstrumentation(MethodDeclarationSyntax node)
    {
        var method = AddLineNumber(node, Expression2Return(node, out var indent));
        if (method.Body == null) return method;

        var usingStatement = SyntaxFactory.UsingStatement(SyntaxFactory.Block(method.Body.Statements).WithNewLine())
            .WithExpression(SuppressInstrumentationScope());

        return method.WithBody(SyntaxFactory.Block(usingStatement.WithNewLine(indent)).WithNewLine());
    }

    private static InvocationExpressionSyntax SuppressInstrumentationScope() => SyntaxFactory.InvocationExpression(
        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ParseTypeName("OpenTelemetry.SuppressInstrumentationScope"),
            SyntaxFactory.IdentifierName("Begin")));

    private static MethodDeclarationSyntax AddActivityName(TypeDeclarationSyntax type, MethodDeclarationSyntax node,
        MethodActivityNameContext context)
    {
        var method = AddLineNumber(node, Expression2Return(node, out var indent));
        if (method.Body == null) return method;

        ExpressionSyntax dictionaryCreation;
        if (context.InTags.Count > 0)
        {
            var initializerExpressions = new SyntaxNodeOrToken[context.InTags.Count * 2];

            var index = 0;
            foreach (var tag in context.InTags)
            {
                initializerExpressions[index++] = SyntaxFactory.InitializerExpression(
                    SyntaxKind.ComplexElementInitializerExpression,
                    SyntaxFactory.SeparatedList<ExpressionSyntax>(new SyntaxNodeOrToken[]
                    {
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(tag.Key)),
                        SyntaxFactory.Token(SyntaxKind.CommaToken),
                        GetTagValue(tag.Value, type)
                            .WithLeadingWhiteSpace()
                    }));

                initializerExpressions[index++] = SyntaxFactory.Token(SyntaxKind.CommaToken)
                    .WithTrailingWhiteSpace();
            }

            dictionaryCreation = SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("System.Collections.Generic.Dictionary<string, object?>"))
                .WithNewKeyword(SyntaxFactory.Token(SyntaxKind.NewKeyword).WithWhiteSpace())
                .WithInitializer(SyntaxFactory.InitializerExpression(
                    SyntaxKind.CollectionInitializerExpression,
                    SyntaxFactory.SeparatedList<ExpressionSyntax>(initializerExpressions)));
        }
        else
            dictionaryCreation = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                .WithLeadingWhiteSpace();

        var usingStatement = SyntaxFactory.UsingStatement(SyntaxFactory.Block(method.Body.Statements).WithNewLine())
            .WithExpression(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParseTypeName("OpenTelemetry.Proxy.ActivityName"),
                    SyntaxFactory.IdentifierName("SetName")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(context.ActivityName))),
                    SyntaxFactory.Argument(dictionaryCreation),
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(context.MaxUsableTimes)))
                        .WithLeadingWhiteSpace()
                ])))).WithNewLine(indent);

        return method.WithBody(SyntaxFactory.Block(usingStatement).WithNewLine());
    }

    private static SyntaxNode AddActivity(TypeDeclarationSyntax type,
        MethodDeclarationSyntax node, ActivityContext context)
    {
        var activity = SyntaxFactory.IdentifierName("activity@");

        MethodDeclarationSyntax method;
        int indent;

        if (string.IsNullOrWhiteSpace(context.ReturnValueTag) && context.OutTags.Count < 1 ||
            node.ExpressionBody is { Expression: ThrowExpressionSyntax })
            method = Expression2Return(node, out indent);
        else
        {
            var syntaxNode = new ReturnRewriter(type, context, activity,
                    node.ExpressionBody?.Expression.GetLineNumber())
                .Visit(Expression2Return(node, out indent, false));

            if (syntaxNode is not MethodDeclarationSyntax newMethod) return syntaxNode;

            method = newMethod;
        }

        method = AddLineNumber(node, method);
        if (method.Body == null) return method;

        var activityDeclaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("var"))
                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(activity.Identifier)
                        .WithWhiteSpace()
                        .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.InvocationExpression(
                                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                                        SyntaxFactory.ParseExpression(type.GetTypeName(2))
                                            .WithLeadingWhiteSpace(),
                                        SyntaxFactory.IdentifierName("@ActivitySource@")),
                                    SyntaxFactory.IdentifierName("StartActivity")))
                            .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                                    SyntaxKind.StringLiteralExpression,
                                    SyntaxFactory.Literal(context.ActivityName))),
                                SyntaxFactory.Argument(SyntaxFactory.ParseExpression(context.Kind))
                                    .WithLeadingWhiteSpace()
                            ])))))))).WithNewLine(indent);

        var bodyStatements = new List<StatementSyntax> { activityDeclaration };

        if (context.InTags.Count > 0)
            bodyStatements.Add(SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression, activity.WithTrailingWhiteSpace(),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression).WithLeadingWhiteSpace()),
                    SyntaxFactory.ExpressionStatement(SetTag(activity.WithLeadingWhiteSpace(), context.InTags, type)))
                .WithNewLine(indent));

        var finallyStatements = new List<StatementSyntax>();
        if (context.SuppressInstrumentation)
        {
            var disposable = SyntaxFactory.IdentifierName("disposable@");

            bodyStatements.Add(SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("var"))
                        .WithVariables(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(disposable.Identifier.WithWhiteSpace())
                                .WithInitializer(SyntaxFactory.EqualsValueClause(SuppressInstrumentationScope()
                                    .WithLeadingWhiteSpace())))))
                .WithNewLine(indent));

            finallyStatements.Add(SyntaxFactory.ExpressionStatement(
                SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    disposable,
                    SyntaxFactory.IdentifierName("Dispose")))).WithNewLine(indent));
        }

        var catchClause = SyntaxFactory.CatchClause()
            .WithDeclaration(SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName("Exception"))
                .WithIdentifier(SyntaxFactory.Identifier("ex").WithLeadingWhiteSpace()))
            .WithFilter(SyntaxFactory.CatchFilterClause(SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.ParseExpression("OpenTelemetry.Proxy.ActivityExtensions"),
                        SyntaxFactory.IdentifierName("SetExceptionStatus")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                    SyntaxFactory.Argument(activity),
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("ex")).WithLeadingWhiteSpace()
                ]))).WithLeadingTrivia()).WithLeadingWhiteSpace())
            .WithBlock(SyntaxFactory.Block(SyntaxFactory.ThrowStatement().WithNewLine(indent)).WithNewLine())
            .WithNewLine(indent);

        finallyStatements.Add(SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(
                SyntaxKind.NotEqualsExpression, activity.WithTrailingWhiteSpace(),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression).WithLeadingWhiteSpace()),
            SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                activity.WithLeadingWhiteSpace(), SyntaxFactory.IdentifierName("Dispose"))))).WithNewLine(indent));

        var tryStatement = SyntaxFactory.TryStatement(method.Body,
            SyntaxFactory.SingletonList(catchClause),
            SyntaxFactory.FinallyClause().WithBlock(SyntaxFactory.Block(finallyStatements).WithNewLine())
                .WithNewLine(indent));

        bodyStatements.Add(tryStatement.WithTryKeyword(tryStatement.TryKeyword.WithNewLine(indent)));

        return method.WithBody(SyntaxFactory.Block(bodyStatements).WithNewLine());
    }

    private static ExpressionSyntax SetTag(ExpressionSyntax variable, Dictionary<string, ActivityTagSource> inTags,
        TypeDeclarationSyntax type) =>
        inTags.Aggregate(variable, (current, tag) =>
            SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    current, SyntaxFactory.IdentifierName("SetTag")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(tag.Key))),
                    SyntaxFactory.Argument(GetTagValue(tag.Value, type))
                        .WithLeadingWhiteSpace()
                ]))));

    private static ExpressionSyntax SetTag(ExpressionSyntax variable, Dictionary<string, ActivityTagSource> inTags,
        Dictionary<string, ActivityTagSource> outTags, TypeDeclarationSyntax type) =>
        outTags.Aggregate(variable, (current, tag) =>
            SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    current, SyntaxFactory.IdentifierName("SetTag")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                    SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression,
                        SyntaxFactory.Literal(inTags.ContainsKey(tag.Key) ? tag.Key + "$out" : tag.Key))),
                    SyntaxFactory.Argument(GetTagValue(tag.Value, type))
                        .WithLeadingWhiteSpace()
                ]))));

    private static MethodDeclarationSyntax Expression2Return(MethodDeclarationSyntax method,
        out int indent, bool addLineNumber = true)
    {
        indent = method.GetIndent() + 4;

        if (method.ExpressionBody == null) return method;

        var expression = method.ExpressionBody.Expression;

        StatementSyntax statement = method.IsVoid()
            ? SyntaxFactory.ExpressionStatement(expression)
            : expression is ThrowExpressionSyntax tes
                ? SyntaxFactory.ThrowStatement(tes.Expression.WithLeadingWhiteSpace())
                : SyntaxFactory.ReturnStatement(expression.WithLeadingWhiteSpace());

        return method.WithBody(SyntaxFactory.Block(addLineNumber // Try to keep the same column number
                ? statement.WithNewLine(expression.GetColumnNumber()).RestoreLineNumber(expression.GetLineNumber())
                : statement.WithNewLine(expression is ThrowExpressionSyntax
                    ? expression.GetColumnNumber()
                    : Math.Max(0, expression.GetColumnNumber() - 7))).WithNewLine())
            .WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }

    private static MethodDeclarationSyntax AddLineNumber(MethodDeclarationSyntax node, MethodDeclarationSyntax method)
    {
        if (node.Body == null || node.Body.Statements.Count < 1 ||
            method.Body == null || method.Body.Statements.Count < 1) return method;

        if (!node.Body.Statements[0].HaveLineNumber())
            method = method.WithBody(method.Body.WithStatements(new(method.Body.Statements.Skip(1)
                .Prepend(method.Body.Statements[0].RestoreLineNumber(node.Body.Statements[0].GetLineNumber())))));

        return method.WithBody(method.Body!.WithCloseBraceToken(
            method.Body.CloseBraceToken.RestoreLineNumber(node.Body.CloseBraceToken.GetLineNumber(node.SyntaxTree))));
    }

    private static ExpressionSyntax GetTagValue(ActivityTagSource tag, TypeDeclarationSyntax type) => tag.From switch
    {
        ActivityTagFrom.InstanceFieldOrProperty => SyntaxFactory.ParseExpression($"this.{tag.Name}"),
        ActivityTagFrom.StaticFieldOrProperty => SyntaxFactory.ParseExpression($"{type.GetTypeName(2)}.{tag.Name}"),
        _ => SyntaxFactory.IdentifierName(tag.Name)
    };

    #endregion

    private sealed class ReturnRewriter(
        TypeDeclarationSyntax type,
        ActivityContext context,
        ExpressionSyntax variable,
        ILineNumber? line)
        : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node) => node.Expression switch
        {
            null or ThrowExpressionSyntax => context.OutTags.Count > 0 ? InsertVoid(node) : node,
            LiteralExpressionSyntax or IdentifierNameSyntax => InsertSimple(node, node.Expression),
            _ => InsertComplex(node, node.Expression),
        };

        private BlockSyntax InsertVoid(ReturnStatementSyntax node) => SyntaxFactory.Block(SyntaxFactory.IfStatement(
                    SyntaxFactory.BinaryExpression(SyntaxKind.NotEqualsExpression,
                        variable.WithTrailingWhiteSpace(),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression).WithLeadingWhiteSpace()),
                    SyntaxFactory.ExpressionStatement(SetTag(variable, context.InTags, context.OutTags, type)))
                .RestoreLineNumber(line ?? node.GetLineNumber()),
            node).WithNewLine();

        private BlockSyntax InsertSimple(ReturnStatementSyntax node, ExpressionSyntax returnExpression)
        {
            returnExpression = string.IsNullOrWhiteSpace(context.ReturnValueTag)
                ? SetTag(variable, context.InTags, context.OutTags, type)
                : SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SetTag(variable, context.InTags, context.OutTags, type),
                        SyntaxFactory.IdentifierName("SetTag")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(context.ReturnValueTag!))),
                        SyntaxFactory.Argument(returnExpression)
                    ])));

            var setTagStatement = SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression, variable.WithTrailingWhiteSpace(),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)
                            .WithLeadingWhiteSpace()),
                    SyntaxFactory.ExpressionStatement(returnExpression).WithLeadingWhiteSpace())
                .WithNewLine(node.GetColumnNumber()).RestoreLineNumber(line ?? node.GetLineNumber());

            return SyntaxFactory.Block(setTagStatement, node).WithNewLine();
        }

        private BlockSyntax InsertComplex(ReturnStatementSyntax node, ExpressionSyntax returnExpression)
        {
            var retVariable = SyntaxFactory.IdentifierName("return@");

            var retDeclaration = SyntaxFactory.LocalDeclarationStatement(SyntaxFactory
                    .VariableDeclaration(SyntaxFactory.IdentifierName("var")
                    )
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(SyntaxFactory
                        .VariableDeclarator(retVariable.Identifier.WithWhiteSpace())
                        .WithInitializer(SyntaxFactory.EqualsValueClause(returnExpression.WithLeadingWhiteSpace())))))
                .WithNewLine(Math.Max(0, returnExpression.GetColumnNumber() - 14)) // Try to keep the same column number
                .RestoreLineNumber(line ?? node.GetLineNumber());

            returnExpression = SetTag(variable, context.InTags, context.OutTags, type);
            if (!string.IsNullOrWhiteSpace(context.ReturnValueTag))
                returnExpression = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                            returnExpression, SyntaxFactory.IdentifierName("SetTag")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList([
                        SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(context.ReturnValueTag!))),
                        SyntaxFactory.Argument(retVariable).WithLeadingWhiteSpace()
                    ])));

            var setTagStatement = SyntaxFactory.IfStatement(SyntaxFactory.BinaryExpression(
                        SyntaxKind.NotEqualsExpression,
                        variable.WithTrailingWhiteSpace(),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression).WithLeadingWhiteSpace()),
                    SyntaxFactory.ExpressionStatement(returnExpression).WithLeadingWhiteSpace())
                .WithNewLine(node.GetColumnNumber());

            return SyntaxFactory.Block(retDeclaration, setTagStatement,
                node.WithExpression(retVariable.WithLeadingWhiteSpace())).WithNewLine();
        }
    }
}
