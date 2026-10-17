using Common.Util;
using GameInterface.Services.GameDebug.Interfaces;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;
using Xunit;

namespace GameInterface.Tests.Services.Players;

public class ListenAuthorityCharacterCreationTests
{
    [Theory]
    [InlineData("帝国")]
    [InlineData("Empire")]
    public void DebugCreationSelectsEmpireIndependentlyOfDisplayLanguage(string localizedName)
    {
        var empire = ObjectHelper.SkipConstructor<CultureObject>();
        empire.StringId = "empire";
        empire.Name = new TextObject(localizedName);
        var other = ObjectHelper.SkipConstructor<CultureObject>();
        other.StringId = "vlandia";
        other.Name = new TextObject("Vlandia");
        Assert.Same(empire, DebugCharacterCreationInterface.SelectDebugCulture(new[] { other, empire }));
    }
}
