namespace LiveViewEngine.Core.UnitTests;

public class LiveViewEngineOptionsTests
{
    [Fact]
    public void DefaultOptions_SortingAndFilteringEnabled()
    {
        var options = new LiveViewEngineOptions();

        Assert.True(options.SortingEnabled);
        Assert.True(options.FilteringEnabled);
    }

    [Fact]
    public void RequireExplicitCapabilities_True_DisablesSortingAndFilteringByDefault_WithoutAnyDISetup()
    {
        // This must hold purely from constructing LiveViewEngineOptions directly - no
        // ServiceCollectionExtensions.AddLiveViewEngineCore/DI involvement at all. Capability
        // enforcement must not depend on which construction path a host uses.
        var options = new LiveViewEngineOptions { RequireExplicitCapabilities = true };

        Assert.False(options.SortingEnabled);
        Assert.False(options.FilteringEnabled);
    }

    [Fact]
    public void RequireExplicitCapabilities_True_ExplicitlySettingEnabledTrue_StillWins()
    {
        var options = new LiveViewEngineOptions { RequireExplicitCapabilities = true };

        options.SortingEnabled = true;
        options.FilteringEnabled = true;

        Assert.True(options.SortingEnabled);
        Assert.True(options.FilteringEnabled);
    }

    [Fact]
    public void RequireExplicitCapabilities_False_ExplicitlySettingEnabledFalse_StillHonored()
    {
        var options = new LiveViewEngineOptions { RequireExplicitCapabilities = false };

        options.SortingEnabled = false;
        options.FilteringEnabled = false;

        Assert.False(options.SortingEnabled);
        Assert.False(options.FilteringEnabled);
    }
}
