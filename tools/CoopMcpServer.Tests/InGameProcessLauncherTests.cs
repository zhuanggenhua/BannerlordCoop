namespace CoopMcpServer.Tests;

public sealed class InGameProcessLauncherTests
{
    [Fact]
    public void LoopbackProxyChangesOnlyTheClientsStructuredEndpoint()
    {
        var profile = new LaunchProfile { Executable = @"C:\Game Folder\Bannerlord.exe", ListenAuthority = true, ClientProxyPort = 24200 };
        var launcher = new InGameProcessLauncher(new OwnedProcessFactory());
        var client = launcher.CreateStartInfo(profile, "client", "B", "token");
        var host = launcher.CreateStartInfo(profile, "server", "A", "token");
        int flag = client.ArgumentList.IndexOf("/autoconnect");
        Assert.Equal("127.0.0.1:24200", client.ArgumentList[flag + 1]);
        Assert.DoesNotContain("127.0.0.1:24200", host.ArgumentList);
        Assert.Contains("/listenhost", host.ArgumentList);
        Assert.Contains("/cooptestmanualjoin", client.ArgumentList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidProxyPortIsRejectedBeforeProcessConstruction(int port)
    {
        var profile = new LaunchProfile { Executable = @"C:\Game Folder\Bannerlord.exe", ClientProxyPort = port };
        Assert.Throws<ArgumentException>(() => new InGameProcessLauncher(new OwnedProcessFactory())
            .CreateStartInfo(profile, "client", "B", "token"));
    }

    [Fact]
    public void SinglePeerProxyProfileRejectsMultipleClients()
    {
        var profile = new LaunchProfile { ClientProxyPort = 24200 };
        Assert.Throws<ArgumentException>(() => profile.ValidateClientProxy(2));
        profile.ValidateClientProxy(0);
        profile.ValidateClientProxy(1);
    }

    [Theory]
    [InlineData("server", true)]
    [InlineData("client", false)]
    public void ListenProfileSelectsInProcessAuthorityOnlyForHost(string role, bool listen)
    {
        var profile = new LaunchProfile { Executable = @"C:\Game Folder\Bannerlord.exe", ListenAuthority = true };
        var info = new InGameProcessLauncher(new OwnedProcessFactory()).CreateStartInfo(profile, role, "LocalA", "token");
        Assert.Equal(listen, info.ArgumentList.Contains("/listenhost"));
    }

    [Theory]
    [InlineData("server", false)]
    [InlineData("client", true)]
    public void LaunchArgumentsAreStructuredAndClientsDeferJoining(string role, bool deferred)
    {
        var profile = new LaunchProfile { Executable = @"C:\Game Folder\Bannerlord.exe" };
        var info = new InGameProcessLauncher(new OwnedProcessFactory()).CreateStartInfo(profile, role, "testclient1", "run-token");
        Assert.False(info.UseShellExecute);
        Assert.Equal(profile.Executable, info.FileName);
        Assert.Equal(@"C:\Game Folder", info.WorkingDirectory);
        Assert.Contains("/" + role, info.ArgumentList);
        Assert.Contains("/autoconnect", info.ArgumentList);
        Assert.Contains("run-token", info.ArgumentList);
        Assert.Equal(deferred, info.ArgumentList.Contains("/cooptestmanualjoin"));
        Assert.Contains("_MODULES_*Native*SandBoxCore*SandBox*StoryMode*Coop*_MODULES_", info.ArgumentList);
    }

    [Theory]
    [InlineData("server", true)]
    [InlineData("client", false)]
    public void SaveSelectionIsOneArgumentAndOnlyControlsServerStartup(string role, bool selected)
    {
        var info = new InGameProcessLauncher(new OwnedProcessFactory()).CreateStartInfo(
            new LaunchProfile { Executable = @"C:\Game Folder\Bannerlord.exe" }, role, "identity", "token", "Danustica campaign");
        Assert.Equal(selected, info.ArgumentList.Contains("/coopsave"));
        Assert.Equal(selected, info.ArgumentList.Contains("Danustica campaign"));
        Assert.Contains("/autoconnect", info.ArgumentList);
        Assert.DoesNotContain("/coopowner", info.ArgumentList);
    }

    [Fact]
    public void DuplicatePlatformIdsAreRejectedBeforeLaunch()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "Bannerlord.exe");
            File.WriteAllText(path, "fake, never launched");
            var profile = new LaunchProfile { Executable = path, ClientPlatformIds = new[] { "testclient", "TESTCLIENT" } };
            Assert.Throws<ArgumentException>(() => profile.Validate(2));
        }
        finally { Directory.Delete(directory, true); }
    }
}
