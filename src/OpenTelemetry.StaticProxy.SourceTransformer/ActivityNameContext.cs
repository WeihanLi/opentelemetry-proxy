﻿namespace OpenTelemetry.StaticProxy;

internal abstract class ActivityNameContext(string activityName)
{
    public string ActivityName { get; set; } = activityName;

    public int MaxUsableTimes { get; set; } = 1;
}

internal sealed class MethodActivityNameContext(string activityName)
    : ActivityNameContext(activityName), IMethodTagContext
{
    public HashSet<string> UnknownTag { get; } = [];

    public Dictionary<string, ActivityTagSource> InTags { get; } = [];

    public bool IsStatic { get; set; }
}

internal sealed class TypeActivityNameContext(
    string activityName,
    MethodSyntaxContexts methods,
    Dictionary<string, MemberType> propertyOrField)
    : ActivityNameContext(activityName), ITypeContext
{
    public MethodSyntaxContexts Methods { get; } = methods;

    public Dictionary<string, MemberType> PropertyOrField { get; } = propertyOrField;

    public HashSet<string> Tags { get; } = [];
}
