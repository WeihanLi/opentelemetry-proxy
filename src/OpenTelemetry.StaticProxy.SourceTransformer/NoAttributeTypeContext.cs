﻿namespace OpenTelemetry.StaticProxy;

internal sealed class NoAttributeTypeContext(
    MethodSyntaxContexts methods,
    Dictionary<string, MemberType> propertyOrField)
    : ITypeContext
{
    public MethodSyntaxContexts Methods { get; } = methods;

    public Dictionary<string, MemberType> PropertyOrField { get; } = propertyOrField;

    public HashSet<string> Tags { get; } = [];

    public ITypeContext ToImplicateActivitySource(string typeFullName)
    {
        var context = new ImplicateActivitySourceContext(typeFullName, Methods, PropertyOrField);

        context.Tags.UnionWith(Tags);

        return context;
    }
}
