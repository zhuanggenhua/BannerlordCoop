using Common.Logging;
using Serilog;
using System;
using System.Linq;
using System.Text.RegularExpressions;
using TaleWorlds.Engine;
using TaleWorlds.PlatformService;

namespace GameInterface.Services.Entity;

public interface IControllerIdProvider
{
    string ControllerId { get; }
    void SetControllerId(string controllerId);
    void SetControllerAsPlatformId();
    void SetControllerFromProgramArgs();
}

public class ControllerIdProvider : IControllerIdProvider
{
    private static readonly ILogger Logger = LogManager.GetLogger<ControllerIdProvider>();

    public string ControllerId { get; private set; }

    private readonly Func<string> commandLine;
    private readonly Func<string> platformId;

    public ControllerIdProvider() : this(Utilities.GetFullCommandLineString, () => PlatformServices.UserId) { }

    internal ControllerIdProvider(Func<string> commandLine, Func<string> platformId)
    {
        this.commandLine = commandLine;
        this.platformId = platformId;
    }

    public void SetControllerFromProgramArgs()
    {
        var args = Regex.Matches(commandLine() ?? string.Empty, "\"[^\"]*\"|[^\\s]+")
            .Cast<Match>().Select(match => match.Value.Trim('"')).ToArray();
        var indices = Enumerable.Range(0, args.Length)
            .Where(index => string.Equals(args[index], "/platformId", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (indices.Length == 0)
        {
            SetControllerAsPlatformId();
            return;
        }
        var index = indices[0] + 1;
        if (indices.Length != 1 || index >= args.Length || !Regex.IsMatch(args[index], "^[A-Za-z0-9_.-]+$"))
            throw new InvalidOperationException("启动参数 /platformId 无效：请移除测试身份参数，使用真实 Steam 身份。");
        ControllerId = args[index];
    }

    public void SetControllerAsPlatformId()
    {
        string controllerId = platformId();

        if (string.IsNullOrWhiteSpace(controllerId))
        {
            Logger.Error("无法获取 Steam 玩家身份，请先启动 Steam 并登录后重试。");
            throw new InvalidOperationException("无法获取 Steam 玩家身份，请先启动 Steam 并登录后重试。");
        }

        ControllerId = controllerId;
    }

    public void SetControllerId(string controllerId)
    {
        ControllerId = controllerId;
    }

}
