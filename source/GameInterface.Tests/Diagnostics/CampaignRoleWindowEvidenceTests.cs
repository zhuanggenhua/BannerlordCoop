using Autofac;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Diagnostics;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignRoleWindowEvidenceTests
{
    private static readonly Type BoundaryType = typeof(CampaignCycleDiagnostics).Assembly.GetType("GameInterface.Services.CampaignAuthority.CampaignNativeBoundary");
    private static object Boundary(string node = "a", long epoch = 3, bool writer = true)
    {
        Assert.NotNull(BoundaryType);
        var value = Activator.CreateInstance(BoundaryType);
        Set(value,"Node",node);Set(value,"Lease",new AuthorityLease("room","world",writer ? node : "other",epoch));
        Set(value,"LeaseKnown",true);Set(value,"LocalWriter",writer);Set(value,"CampaignId","campaign");
        Set(value,"CampaignIdentity",17);Set(value,"CurrentTickCount",10);Set(value,"WorldTicks",100L);
        Set(value,"GameStarted",true);
        return value;
    }
    private static void Set(object value,string field,object content) => BoundaryType.GetField(field).SetValue(value,content);
    private static void Observe(CampaignCycleDiagnostics collector,string method,object entry,object exit,bool ran=true,Exception error=null)
    {
        var observe=AccessTools.Method(typeof(CampaignCycleDiagnostics),"NativeTick");Assert.NotNull(observe);
        observe.Invoke(collector,new[]{method,entry,exit,(object)ran,error});
    }
    private static JObject Snapshot(CampaignCycleDiagnostics collector) => JObject.FromObject(collector.SimulationEvidenceSnapshot());

    [Theory]
    [InlineData("Campaign.Tick",1)]
    [InlineData("Campaign.RealTick",0)]
    [InlineData("Campaign.LateAITickAux",0)]
    public void ExactConfirmedWriterZeroDeltaIsPartitionedWithoutDroppingTotals(string method,int increment)
    {
        var collector=new CampaignCycleDiagnostics();var entry=Boundary();var exit=Boundary();Set(exit,"CurrentTickCount",10+increment);
        Observe(collector,method,entry,exit);
        var snapshot=Snapshot(collector);Assert.Equal(1,(long)snapshot["Counts"][method+".completed"]);
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal("writerBookkeeping",(string)group["Classification"]);Assert.Equal(1,(long)group["Count"]);
        Assert.Equal(10,(int)group["FirstEntry"]["CurrentTickCount"]);
        Assert.Equal(10+increment,(int)group["LastExit"]["CurrentTickCount"]);
    }

    [Theory]
    [InlineData("observer")][InlineData("frozen")][InlineData("unknown")][InlineData("epoch")]
    [InlineData("allowed")][InlineData("restricted")][InlineData("phase")][InlineData("bootstrap")]
    [InlineData("clock")][InlineData("dt")][InlineData("nan")][InlineData("counter")][InlineData("campaign")]
    [InlineData("not-started")][InlineData("thread")][InlineData("paused")]
    public void EachUnsafeBoundaryRemainsUnexpected(string invalid)
    {
        var collector=new CampaignCycleDiagnostics();var entry=Boundary();var exit=Boundary();Set(exit,"CurrentTickCount",11);
        if(invalid=="observer") {entry=Boundary(writer:false);exit=Boundary(writer:false);Set(exit,"CurrentTickCount",11);}
        if(invalid=="frozen")Set(exit,"Frozen",true);
        if(invalid=="unknown")Set(entry,"LeaseKnown",false);
        if(invalid=="epoch")Set(exit,"Lease",new AuthorityLease("room","world","a",5));
        if(invalid=="allowed")Set(entry,"AllowedThread",true);
        if(invalid=="restricted")Set(entry,"Restricted",true);
        if(invalid=="phase")Set(entry,"Phase",CampaignExecutionPhase.Replay);
        if(invalid=="bootstrap") {Set(entry,"CurrentTickCount",1);Set(exit,"CurrentTickCount",2);}
        if(invalid=="clock")Set(exit,"WorldTicks",101L);
        if(invalid=="dt")Set(exit,"Dt",0.01f);
        if(invalid=="nan")Set(entry,"Dt",float.NaN);
        if(invalid=="counter")Set(exit,"CurrentTickCount",12);
        if(invalid=="campaign")Set(exit,"CampaignIdentity",99);
        if(invalid=="not-started") {Set(entry,"GameStarted",false);Set(exit,"GameStarted",false);}
        if(invalid=="thread")Set(exit,"ThreadId",2);
        if(invalid=="paused") {var paused=new AuthorityLease("room","world","a",3,true);Set(entry,"Lease",paused);Set(exit,"Lease",paused);}
        Observe(collector,"Campaign.Tick",entry,exit);
        Assert.Equal("unexpected",(string)Assert.Single((JArray)Snapshot(collector)["RoleGroups"])["Classification"]);
    }

    [Fact]
    public void ProgressIsSeparateFromZeroDeltaAndExceptionsOrSkippedOriginalNeverQualify()
    {
        var collector=new CampaignCycleDiagnostics();var entry=Boundary();var exit=Boundary();Set(exit,"CurrentTickCount",11);
        Set(entry,"Dt",0.1f);Set(exit,"Dt",0.1f);Observe(collector,"Campaign.Tick",entry,exit);
        Observe(collector,"Campaign.Tick",entry,exit,error:new InvalidOperationException("original"));
        Observe(collector,"Campaign.Tick",entry,exit,ran:false);
        Observe(collector,"Campaign.Tick",entry,exit,ran:false,error:new InvalidOperationException("before original"));
        var snapshot=Snapshot(collector);var groups=(JArray)snapshot["RoleGroups"];
        Assert.Contains(groups,row=>(string)row["Classification"]=="writerProgress" && (string)row["Outcome"]=="completed");
        Assert.Contains(groups,row=>(string)row["Classification"]=="unexpected" && (string)row["Outcome"]=="failed");
        Assert.Contains(groups,row=>(string)row["Classification"]=="skipped");
        Assert.Equal(1,(long)snapshot["Counts"]["Campaign.Tick.failed"]);
        Assert.Equal(1,(long)snapshot["Counts"]["Campaign.Tick.beforeOriginal.failed"]);
    }

    [Fact]
    public void ActualNativeLateAiZeroDeltaCapturesOriginalBoundaryAndConfirmedIdentity()
    {
        var previous=Campaign.Current;var campaign=(Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        campaign.MapTimeTracker=new MapTimeTracker(new CampaignTime(100));campaign.CurrentTickCount=10;Campaign.Current=campaign;
        campaign.GameStarted=true;
        AccessTools.PropertySetter(typeof(Campaign),"UniqueGameId").Invoke(campaign,new object[]{"campaign"});
        var identity=new SessionIdentity();Assert.True(identity.TryApply(new AuthorityLease("room","world","a",3),"a"));
        var controller=new ControllerIdProvider();controller.SetControllerId("a");
        var builder=new ContainerBuilder();builder.RegisterInstance(identity).As<ISessionIdentity>();builder.RegisterInstance(controller).As<IControllerIdProvider>();
        builder.RegisterInstance(new CampaignAuthorityContext(identity,controller)).As<ICampaignAuthorityContext>();
        using var scope=builder.Build();ContainerProvider.SetContainer(scope);
        var harmony=new Harmony("coop.tests.role-window.native-late-ai");var original=AccessTools.Method(typeof(Campaign),"LateAITickAux");
        try
        {
            harmony.Patch(original,prefix:new HarmonyMethod(typeof(CampaignSimulationEvidencePatches),"Prefix"),finalizer:new HarmonyMethod(typeof(CampaignSimulationEvidencePatches),"Finalizer"));
            original.Invoke(campaign,Array.Empty<object>());
            var group=Assert.Single((JArray)Snapshot(CampaignCycleDispatchPatches.For(campaign))["RoleGroups"]);
            Assert.Equal("writerBookkeeping",(string)group["Classification"]);Assert.Equal("a",(string)group["FirstEntry"]["Node"]);
            Assert.Equal(100,(long)group["LastExit"]["WorldTicks"]);
        }
        finally { harmony.UnpatchAll(harmony.Id);ContainerProvider.Clear();Campaign.Current=previous; }
    }

    [Fact]
    public void GroupOverflowIsExplicitAndSnapshotsDoNotShareMutableCountsOrGroups()
    {
        var collector=new CampaignCycleDiagnostics(1);var entry=Boundary();var exit=Boundary();Set(exit,"CurrentTickCount",11);
        Observe(collector,"Campaign.Tick",entry,exit);var before=Snapshot(collector);
        Observe(collector,"Campaign.RealTick",entry,entry);var after=Snapshot(collector);
        Assert.True((bool)after["RoleOverflowed"]);Assert.Single((JArray)after["RoleGroups"]);
        Assert.Equal(1,(long)before["Counts"]["Campaign.Tick.completed"]);
        Assert.Equal(0,(long)before["Counts"]["Campaign.RealTick.completed"]);
    }

    [Fact]
    public void ConcurrentObservationAndOneSnapshotAlwaysPreserveWholePartitions()
    {
        var collector=new CampaignCycleDiagnostics();
        Parallel.For(0,1000,i=>
        {
            var entry=Boundary();var exit=Boundary();Set(exit,"CurrentTickCount",11);
            Observe(collector,"Campaign.Tick",entry,exit);
            var snapshot=Snapshot(collector);
            Assert.Equal((long)snapshot["Counts"]["Campaign.Tick.completed"],
                ((JArray)snapshot["RoleGroups"]).Sum(row=>(long)row["Count"]));
        });
        var final=Snapshot(collector);Assert.Equal(1000,(long)final["Counts"]["Campaign.Tick.completed"]);
        Assert.Single((JArray)final["RoleGroups"]);
    }

    [Fact]
    public void AiGroupRetainsIdentityAndOriginalUnsafeCountersAlongsideNativeBoundary()
    {
        var collector=new CampaignCycleDiagnostics();var native=Boundary(writer:false);
        var ai=new CampaignAiTickEvidence { Classification="observerLocalNavigation",BaseClassification="observerLocalNavigation",
            PartyRole="localPlayer",PartyId="player-party",ObjectController="a",MainParty=true,AiIdentity=37,UnexpectedWorld=false };
        // The actual registry classification is covered by CampaignSimulationEvidenceTests; this checks its atomic wire retention.
        AccessTools.Method(typeof(CampaignCycleDiagnostics),"AiTick").Invoke(collector,new object[]{ai,ai.Copy(),true,null,native,native});
        var snapshot=Snapshot(collector);var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal("observerLocalNavigation",(string)group["Classification"]);
        Assert.Equal("player-party",(string)group["FirstEntry"]["AiPartyId"]);
        Assert.Equal(37,(int)group["LastExit"]["AiIdentity"]);
        Assert.False((bool)group["AiUnexpectedWorld"]);
        Assert.Equal(1,(long)snapshot["Counts"]["MobilePartyAi.Tick.observerLocalNavigation.completed"]);
        var skipped=new CampaignAiTickEvidence();
        AccessTools.Method(typeof(CampaignCycleDiagnostics),"AiTick").Invoke(collector,new object[]{skipped,skipped.Copy(),false,null,native,native});
        snapshot=Snapshot(collector);var suppressed=((JArray)snapshot["RoleGroups"]).Single(row=>(string)row["Outcome"]=="skipped");
        Assert.False((bool)suppressed["AiUnexpectedWorld"]);
        Assert.Equal(0,(long)snapshot["Counts"]["MobilePartyAi.Tick.unexpectedWorld.skipped"]);
    }

    [Fact]
    public void ActualCampaignChangeIsUnsafeAndOriginalCollectorRetainsTheCall()
    {
        var previous=Campaign.Current;var campaign=(Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        campaign.MapTimeTracker=new MapTimeTracker(new CampaignTime(100));Campaign.Current=campaign;
        try
        {
            var entry=CampaignSimulationEvidencePatches.CaptureNative(campaign);
            Campaign.Current=(Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
            var exit=CampaignSimulationEvidencePatches.CaptureNative(campaign);
            Assert.Contains("no longer current",exit.DiagnosticError);
            var collector=CampaignCycleDispatchPatches.For(campaign);collector.NativeTick("Campaign.RealTick",entry,exit,true,null);
            var snapshot=Snapshot(collector);Assert.Equal("unexpected",(string)Assert.Single((JArray)snapshot["RoleGroups"])["Classification"]);
            Assert.DoesNotContain("ActualCampaign",snapshot.ToString());
            Assert.Empty((JArray)Snapshot(CampaignCycleDispatchPatches.For(Campaign.Current))["RoleGroups"]);
        }
        finally {Campaign.Current=previous;}
    }
}
