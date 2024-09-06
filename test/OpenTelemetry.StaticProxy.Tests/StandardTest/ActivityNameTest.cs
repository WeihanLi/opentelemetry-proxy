﻿namespace OpenTelemetry.StaticProxy.Tests.StandardTest;

public class ActivityNameTest
{
    [Fact]
    public async Task ActivityNameNoName()
    {
        var test = new ProxyRewriterTest("ActivityNameTestClass1");

        var results = await test.VisitAsync().ConfigureAwait(false);

        var typeMethods = Assert.Single(results);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(typeMethods.Context),
            "ActivityNameTestClass1", 3);

        var methods = typeMethods.MethodContexts.Values.ToArray();

        Assert.Equal(3, methods.Length);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[0])),
            "ActivityNameTestClass1.TestMethod1", 3);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[1])),
            "ActivityNameTestClass1.TestMethod2", 2);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[2])),
            "Test", 4);
    }

    [Fact]
    public async Task ActivityNameHaveName()
    {
        var test = new ProxyRewriterTest("ActivityNameTestClass2");

        var results = await test.VisitAsync().ConfigureAwait(false);

        var typeMethods = Assert.Single(results);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(typeMethods.Context),
            "TestClass", 1);

        var methods = typeMethods.MethodContexts.Values.ToArray();

        Assert.Equal(3, methods.Length);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[0])),
            "TestClass.TestMethod1", 1);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[1])),
            "TestClass.TestMethod2", 2);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[2])),
            "Test", 4);
    }

    [Fact]
    public async Task TypeHaveNoActivityName()
    {
        var test = new ProxyRewriterTest("ActivityNameTestClass3");

        var results = await test.VisitAsync().ConfigureAwait(false);

        var typeMethods = Assert.Single(results);

        Assert.IsAssignableFrom<NoAttributeTypeContext>(typeMethods.Context);

        var methods = typeMethods.MethodContexts.Values.ToArray();

        Assert.Equal(2, methods.Length);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[0])),
            "ActivityNameTestClass3.TestMethod2", 2);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[1])),
            "Test", 4);
    }

    [Fact]
    public async Task TypeHaveActivityNameAndActivitySource()
    {
        var test = new ProxyRewriterTest("ActivityNameTestClass4");

        var results = await test.VisitAsync().ConfigureAwait(false);

        var typeMethods = Assert.Single(results);

        Assert.IsAssignableFrom<ActivitySourceContext>(typeMethods.Context);

        var methods = typeMethods.MethodContexts.Values.ToArray();

        Assert.Equal(2, methods.Length);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[0])),
            "ActivityNameTestClass4.TestMethod2", 2);

        AssertActivityNameContext(Assert.IsAssignableFrom<ActivityNameContext>(Assert.IsAssignableFrom<ActivityNameContext>(methods[1])),
            "Test", 4);
    }

    private static void AssertActivityNameContext(ActivityNameContext context, string activityName, int maxUsableTimes)
    {
        Assert.Equal(activityName, context.ActivityName);
        Assert.Equal(maxUsableTimes, context.MaxUsableTimes);
    }
}