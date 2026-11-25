using FileDock.Services;

namespace FileDock.Tests;

public class PublikTests
{
    [Theory]
    [InlineData("3f2b8c1e-9d4a-4b6e-8f1a-2c3d4e5f6a7b", true)]
    [InlineData("3F2B8C1E-9D4A-4B6E-AF1A-2C3D4E5F6A7B", true)]
    [InlineData("3f2b8c1e-9d4a-1b6e-8f1a-2c3d4e5f6a7b", false)] // version 1
    [InlineData("3f2b8c1e-9d4a-4b6e-cf1a-2c3d4e5f6a7b", false)] // wrong variant
    [InlineData("3f2b8c1e9d4a4b6e8f1a2c3d4e5f6a7b", false)]
    [InlineData("", false)]
    public void IsUuidV4(string id, bool expected) => Assert.Equal(expected, Publik.IsUuidV4(id));

    [Fact]
    public void FreshGuidsAreUuidV4() => Assert.True(Publik.IsUuidV4(Guid.NewGuid().ToString()));

    [Fact]
    public void SignInScreenHasOnlyTheSignInButton()
    {
        Publik.Screen screen = Publik.GateScreen("sign_in", null, null);
        Assert.Equal("Sign in with publik", screen.Title);
        Assert.Equal([("sign_in", "Sign in with publik")], screen.Buttons);
    }

    [Fact]
    public void UnreachableScreenOffersRetry()
    {
        Publik.Screen screen = Publik.GateScreen("unreachable", null, null);
        Assert.Equal("publik is not reachable. Check your connection.", screen.Body);
        Assert.Equal([("retry", "Retry")], screen.Buttons);
    }

    [Fact]
    public void ModuleIdentifiesTheListing()
    {
        Assert.Equal("filedock", Publik.APP_SLUG);
        Assert.False(Publik.USES_AI);
    }
}
