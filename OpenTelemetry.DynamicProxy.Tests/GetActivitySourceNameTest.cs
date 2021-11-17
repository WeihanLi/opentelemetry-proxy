namespace OpenTelemetry.DynamicProxy.Tests;

public class GetActivitySourceNameTest
{
    [Fact]
    public void Missing_ActivitySourceAttribute() =>
        Assert.Equal(typeof(GetActivitySourceNameTest).FullName, ActivitySourceAttribute.GetActivitySourceName(typeof(GetActivitySourceNameTest)));

    [Fact]
    public void Empty_ActivitySourceAttribute_Name() =>
        Assert.Equal(typeof(ITestInterface1).FullName, ActivitySourceAttribute.GetActivitySourceName(typeof(ITestInterface1)));

    [Fact]
    public void ActivitySourceAttribute_Name() =>
        Assert.Equal("TestActivitySource1", ActivitySourceAttribute.GetActivitySourceName(typeof(TestClass1)));
}
