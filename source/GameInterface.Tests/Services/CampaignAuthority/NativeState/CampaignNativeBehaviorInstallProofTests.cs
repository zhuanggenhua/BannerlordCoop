using Common;
using Common.Util;
using GameInterface.Services.CampaignAuthority.NativeState;
using GameInterface.Services.Kingdoms;
using HarmonyLib;
using Moq;
using ProtoBuf;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using Xunit;
using NativeObjects = GameInterface.Services.ObjectManager.ObjectManager;

namespace GameInterface.Tests.Services.CampaignAuthority.NativeState;

[Collection(ModInformationRoleCollection.Name)]
public class CampaignNativeBehaviorInstallProofTests
{
    [Fact]
    public void ProductionReadbackProvesRealInstalledDomainsAndExactNativeNaNBits()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Service.TryCapture(out var captured));
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, captured);
        stream.Position = 0;
        var checkpoint = Serializer.Deserialize<CampaignNativeBehaviorCheckpoint>(stream);
        fixture.Aging._gameStartDay = 999;
        fixture.Aging._extraLivesContainer.Clear();
        fixture.Caravans._priceDictionary.Clear();
        fixture.Proposals._kingdomDecisionsList.Clear();
        Assert.False(fixture.Service.IsInstalled(checkpoint));
        Assert.True(fixture.Service.TryPrepare(checkpoint, out var install));

        install();

        Assert.True(fixture.Service.IsInstalled(checkpoint));
        Assert.Equal(3, fixture.Aging._extraLivesContainer[fixture.Hero]);
        Assert.Equal(unchecked((int)0xffc00000), BitConverter.SingleToInt32Bits(
            fixture.Caravans._priceDictionary[fixture.Category].AverageBuySellPriceIndex));
        Assert.Same(fixture.Clan, Assert.Single(fixture.Proposals._kingdomDecisionsList).ProposerClan);
    }

    [Theory]
    [InlineData("aging-day")]
    [InlineData("extra-lives")]
    [InlineData("young-heroes")]
    [InlineData("visit")]
    [InlineData("looted")]
    [InlineData("distance")]
    [InlineData("pack")]
    [InlineData("average")]
    [InlineData("land-price")]
    [InlineData("coastal-price")]
    [InlineData("proposal")]
    public void ProductionReadbackRejectsChangedFiniteDomainWithoutRepairingIt(string field)
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Service.TryCapture(out var checkpoint));
        Assert.True(fixture.Service.IsInstalled(checkpoint));
        switch (field)
        {
            case "aging-day": fixture.Aging._gameStartDay++; break;
            case "extra-lives": fixture.Aging._extraLivesContainer[fixture.Hero]++; break;
            case "young-heroes": fixture.Aging._heroesYoungerThanHeroComesOfAge[fixture.Hero]++; break;
            case "visit": fixture.Caravans._caravanLastHomeTownVisitTime[fixture.Party] = new CampaignTime(77); break;
            case "looted": fixture.Caravans._lootedCaravans[fixture.Party] = new CampaignTime(77); break;
            case "distance": fixture.Caravans._navalCaravanVeryFarCache = 55; break;
            case "pack": fixture.Caravans._packAnimalCategoryIndex = -1; break;
            case "average": fixture.Caravans._averageValuesCached[fixture.Category] = 55; break;
            case "land-price": fixture.Caravans._priceDictionary[fixture.Category] = new(55, 12); break;
            case "coastal-price": fixture.Caravans._coastalPriceDictionary[fixture.Category] = new(55, 12); break;
            case "proposal": fixture.Proposals._kingdomDecisionsList[0].TriggerTime = new CampaignTime(77); break;
        }

        Assert.False(fixture.Service.IsInstalled(checkpoint));
        Assert.Contains("differs", fixture.Service.LastRejectedReason);
        Assert.True(fixture.Service.TryCapture(out var stillChanged));
        Assert.False(fixture.Service.IsInstalled(checkpoint));
        Assert.True(fixture.Service.IsInstalled(stillChanged));
    }

    [Fact]
    public void RegistryRemovalMakesRecaptureFailInsteadOfProvingAStalePreparedReference()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Service.TryCapture(out var checkpoint));
        Assert.True(fixture.Objects.Remove(fixture.Hero));

        Assert.False(fixture.Service.IsInstalled(checkpoint));

        Assert.Contains("registry", fixture.Service.LastRejectedReason);
        Assert.Equal(3, fixture.Aging._extraLivesContainer[fixture.Hero]);
    }

    [Fact]
    public void LocalCaravanRumorsAndInteractionHistoryDoNotInvalidateWorldInstallProof()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Service.TryCapture(out var checkpoint));
        fixture.Caravans._tradeRumorTakenCaravans[fixture.Party] = new CampaignTime(999);
        fixture.Caravans._interactedCaravans[fixture.Party] = CaravansCampaignBehavior.PlayerInteraction.Friendly;

        Assert.True(fixture.Service.IsInstalled(checkpoint));
        Assert.Equal(999, fixture.Caravans._tradeRumorTakenCaravans[fixture.Party].NumTicks);
    }

    [Fact]
    public void MissingCampaignCannotProveInstallation()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Service.TryCapture(out var checkpoint));
        Campaign.Current = null;
        Assert.False(fixture.Service.IsInstalled(checkpoint));
        Assert.Equal("capture.campaign=null", fixture.Service.LastRejectedReason);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Campaign previous = Campaign.Current;
        internal readonly NativeObjects Objects = new(Mock.Of<ILogger>());
        internal readonly Hero Hero = ObjectHelper.SkipConstructor<Hero>();
        internal readonly MobileParty Party = ObjectHelper.SkipConstructor<MobileParty>();
        internal readonly ItemCategory Category = ObjectHelper.SkipConstructor<ItemCategory>();
        internal readonly Clan Clan = ObjectHelper.SkipConstructor<Clan>();
        internal readonly Kingdom Kingdom = ObjectHelper.SkipConstructor<Kingdom>();
        internal readonly Kingdom Target = ObjectHelper.SkipConstructor<Kingdom>();
        internal readonly AgingCampaignBehavior Aging = new();
        internal readonly CaravansCampaignBehavior Caravans = new();
        internal readonly KingdomDecisionProposalBehavior Proposals = new();
        internal readonly CampaignNativeBehaviorCheckpointService Service;

        internal Fixture()
        {
            Assert.True(Objects.AddExisting("Hero_native", Hero, 7));
            Assert.True(Objects.AddExisting("MobileParty_native", Party, 10));
            Assert.True(Objects.AddExisting("ItemCategory_pack", Category, 20));
            Assert.True(Objects.AddExisting("clan", Clan, 30));
            Assert.True(Objects.AddExisting("kingdom", Kingdom, 31));
            Assert.True(Objects.AddExisting("target", Target, 32));
            Aging._gameStartDay = 17;
            Aging._extraLivesContainer[Hero] = 3;
            Aging._heroesYoungerThanHeroComesOfAge[Hero] = 13;
            Caravans._caravanLastHomeTownVisitTime[Party] = new CampaignTime(100);
            Caravans._lootedCaravans[Party] = new CampaignTime(200);
            Caravans._packAnimalCategoryIndex = 0;
            Caravans._averageValuesCached[Category] = 3;
            Caravans._priceDictionary[Category] = new(BitConverter.Int32BitsToSingle(unchecked((int)0xffc00000)), 1000);
            Caravans._coastalPriceDictionary[Category] = new(4, 2);
            var decision = ObjectHelper.SkipConstructor<DeclareWarDecision>();
            decision.ProposerClan = Clan;
            decision._kingdom = Kingdom;
            AccessTools.Field(typeof(DeclareWarDecision), nameof(DeclareWarDecision.FactionToDeclareWarOn)).SetValue(decision, Target);
            decision.TriggerTime = new CampaignTime(333);
            Proposals._kingdomDecisionsList.Add(decision);
            var manager = new Mock<ICampaignBehaviorManager>();
            manager.Setup(value => value.GetBehavior<AgingCampaignBehavior>()).Returns(Aging);
            manager.Setup(value => value.GetBehavior<CaravansCampaignBehavior>()).Returns(Caravans);
            manager.Setup(value => value.GetBehavior<KingdomDecisionProposalBehavior>()).Returns(Proposals);
            var campaign = ObjectHelper.SkipConstructor<Campaign>();
            campaign._campaignBehaviorManager = manager.Object;
            campaign.AllItemCategories = new MBReadOnlyList<ItemCategory>(new List<ItemCategory> { Category });
            Campaign.Current = campaign;
            Service = new CampaignNativeBehaviorCheckpointService(Objects, new KingdomDecisionDataConverter(Objects));
        }

        public void Dispose() => Campaign.Current = previous;
    }
}
