using GameInterface.Services.Entity;
using System;
using Xunit;

namespace GameInterface.Tests.Services.Entity;

public class ControllerIdProviderTests
{
    [Theory]
    [InlineData("\"C:\\Games Folder\\Bannerlord.exe\" /listenhost")]
    [InlineData("Bannerlord.exe /client")]
    [InlineData("")]
    public void NoOverrideUsesRealPlatformIdentity(string args)
    {
        var provider = new ControllerIdProvider(() => args, () => "76561198000000001");
        provider.SetControllerFromProgramArgs();
        Assert.Equal("76561198000000001", provider.ControllerId);
    }

    [Theory]
    [InlineData("Bannerlord.exe /platformId LocalA", "LocalA")]
    [InlineData("Bannerlord.exe /PLATFORMID \"testclient1\" /client", "testclient1")]
    public void ExplicitTestIdentityRemainsAvailable(string args, string expected)
    {
        var provider = new ControllerIdProvider(() => args, () => throw new Exception("Platform must not be used"));
        provider.SetControllerFromProgramArgs();
        Assert.Equal(expected, provider.ControllerId);
    }

    [Theory]
    [InlineData("Bannerlord.exe /platformId")]
    [InlineData("Bannerlord.exe /platformId /client")]
    [InlineData("Bannerlord.exe /platformId \"\"")]
    [InlineData("Bannerlord.exe /platformId \"C:\\Games\\Bannerlord.exe\"")]
    [InlineData("Bannerlord.exe /platformId A /platformid B")]
    public void InvalidExplicitIdentityFailsWithoutDefaultOrPathIdentity(string args)
    {
        var provider = new ControllerIdProvider(() => args, () => "steam-id");
        Assert.Contains("/platformId", Assert.Throws<InvalidOperationException>(provider.SetControllerFromProgramArgs).Message);
        Assert.Null(provider.ControllerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingSteamIdentityRefusesAmbiguousPlayerRegistration(string? id)
    {
        var provider = new ControllerIdProvider(() => "Bannerlord.exe /client", () => id!);
        Assert.Contains("Steam", Assert.Throws<InvalidOperationException>(provider.SetControllerFromProgramArgs).Message);
        Assert.Null(provider.ControllerId);
    }
}
