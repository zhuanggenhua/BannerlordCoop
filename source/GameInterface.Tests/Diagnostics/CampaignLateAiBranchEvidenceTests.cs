using Autofac;
using Common;
using GameInterface.Services.CampaignAuthority;
using GameInterface.Services.Entity;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.CampaignSystem;
using Xunit;

namespace GameInterface.Tests.Diagnostics;

[Collection(ModInformationRoleCollection.Name)]
public sealed class CampaignLateAiBranchEvidenceTests
{
    private static Action<Campaign> afterEntry;
    private static Action beforePartiesSkip;
    private static bool resetDt;
    private static Exception thinkError;
    private static bool SkipPartiesThink(Campaign __instance)
    {
        beforePartiesSkip?.Invoke();
        if(resetDt)__instance._dt=0;
        if(thinkError!=null)throw thinkError;
        return false;
    }
    private static void BeforeNative(Campaign __instance)
    {
        var action=afterEntry;afterEntry=null;action?.Invoke(__instance);
    }

    [Theory]
    [InlineData(0f,10,true,"observerLateAiNoOp",0)]
    [InlineData(0f,10,false,"unexpected",0)]
    [InlineData(0.1f,10,true,"unexpected",1)]
    [InlineData(0f,1,true,"unexpected",1)]
    public void ActualNativeBranchIsObservedRatherThanInferredFromBoundaryDelta(float dt,int count,bool coverage,string classification,int attempts)
    {
        var snapshot=ObserveActual(dt,count,coverage);
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal(classification,(string)group["Classification"]);
        Assert.Equal(coverage?1:0,(int?)group["LastExit"]["LateAiCoverageVersion"]);
        Assert.Equal(attempts,(int?)group["LastExit"]["LateAiPartiesThinkAttempts"]);
        Assert.Equal(1,(long)snapshot["Counts"]["Campaign.LateAITickAux.completed"]);
    }

    [Fact]
    public void TransientPositiveDeltaWithBothBoundariesZeroStillRecordsActualAttemptAndRejectsEmpty()
    {
        var snapshot=ObserveActual(0,10,true,after:c=>{c._dt=0.1f;resetDt=true;});
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal(0,(int)group["FirstEntry"]["DtBits"]);Assert.Equal(0,(int)group["LastExit"]["DtBits"]);
        Assert.Equal(1,(int)group["LastExit"]["LateAiPartiesThinkAttempts"]);
        Assert.Equal("unexpected",(string)group["Classification"]);
    }

    [Fact]
    public void EarlierFalsePrefixStillRecordsTransientBranchAttemptOnActualHarmonyDependency()
    {
        int? attemptsBeforeSkip=null;
        var snapshot=ObserveActual(0,10,true,after:c=>{c._dt=0.1f;resetDt=true;},earlySkip:true,beforeSkip:()=>
        {
            var scope=AccessTools.Field(typeof(CampaignSimulationEvidencePatches),"currentLateAi").GetValue(null);
            Assert.NotNull(scope);attemptsBeforeSkip=(int)AccessTools.Field(scope.GetType(),"Attempts").GetValue(scope);
        });
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal(0,attemptsBeforeSkip);
        Assert.Equal(0,(int)group["FirstEntry"]["DtBits"]);Assert.Equal(0,(int)group["LastExit"]["DtBits"]);
        Assert.Equal(1,(int)group["LastExit"]["LateAiPartiesThinkAttempts"]);
        Assert.Equal("unexpected",(string)group["Classification"]);
        Assert.Equal(1,(long)snapshot["Counts"]["Campaign.LateAITickAux.completed"]);
    }

    [Fact]
    public void WriterProgressRetainsDifferentEntryExitAttemptProofWithoutChangingRoleQualification()
    {
        var snapshot=ObserveActual(0.1f,10,true,setup:(c,id)=>Assert.True(id.TryApply(new AuthorityLease("room","world","a",5),"a")));
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal("writerProgress",(string)group["Classification"]);
        Assert.Equal(0,(int)group["FirstEntry"]["LateAiPartiesThinkAttempts"]);
        Assert.Equal(1,(int)group["FirstExit"]["LateAiPartiesThinkAttempts"]);
    }

    [Fact]
    public void NestedActualCallsRestoreParentAndFirstLastProofsRemainComplete()
    {
        var snapshot=ObserveActual(0,10,true,after:c=>AccessTools.Method(typeof(Campaign),"LateAITickAux").Invoke(c,Array.Empty<object>()));
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal("observerLateAiNoOp",(string)group["Classification"]);Assert.Equal(2,(long)group["Count"]);
        foreach(string key in new[]{"FirstEntry","FirstExit","LastEntry","LastExit"})
        {Assert.True((bool)group[key]["LateAiScopeMatched"]);Assert.Equal(0,(int)group[key]["LateAiPartiesThinkAttempts"]);}
    }

    [Fact]
    public void AnotherCampaignPartiesThinkAttemptInvalidatesTheActualCallScope()
    {
        var snapshot=ObserveActual(0,10,true,after:c=>AccessTools.Method(typeof(Campaign),"PartiesThink").Invoke(
            (Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign)),new object[]{0f}));
        var group=Assert.Single((JArray)snapshot["RoleGroups"]);
        Assert.Equal(1,(int)group["LastExit"]["LateAiPartiesThinkAttempts"]);
        Assert.False((bool)group["LastExit"]["LateAiScopeMatched"]);
        Assert.Equal("unexpected",(string)group["Classification"]);
    }

    [Fact]
    public void OriginalBranchExceptionRetainsSameInstanceAndRestoresScopeForNextInvocation()
    {
        var error=new InvalidOperationException("original party think failure");
        var snapshot=ObserveActual(0.1f,10,true,error:error,invokeAgain:true);
        var groups=(JArray)snapshot["RoleGroups"];
        Assert.Contains(groups,g=>(string)g["Outcome"]=="failed"&&(string)g["Classification"]=="unexpected"&&(int)g["LastExit"]["LateAiPartiesThinkAttempts"]==1);
        Assert.Contains(groups,g=>(string)g["Classification"]=="observerLateAiNoOp"&&(bool)g["LastExit"]["LateAiScopeMatched"]);
        Assert.Equal(1,(long)snapshot["Counts"]["Campaign.LateAITickAux.failed"]);
        Assert.Equal(1,(long)snapshot["Counts"]["Campaign.LateAITickAux.completed"]);
    }

    [Theory]
    [InlineData("unknown")][InlineData("frozen")][InlineData("replay")][InlineData("bootstrap")]
    [InlineData("clock")][InlineData("counter")][InlineData("epoch")][InlineData("campaign")][InlineData("empty-unpaused-writer")]
    public void ActualEmptyCallCannotExcuseUnsafeIdentityOrPhase(string invalid)
    {
        IDisposable phase=null;
        try
        {
            var snapshot=ObserveActual(0,10,true,setup:(c,identity)=>
            {
                if(invalid=="frozen")
                {
                    var lease=new AuthorityLease("room","world","b",5);
                    Assert.True(identity.TryApply(lease,"a"));Assert.True(identity.TryFreezeLocalAuthority(lease,19));
                }
                if(invalid=="replay")phase=CampaignExecutionScope.Enter(CampaignExecutionPhase.Replay);
                if(invalid=="bootstrap")c.GameStarted=false;
                if(invalid=="empty-unpaused-writer")Assert.True(identity.TryApply(new AuthorityLease("room","world","",5),"a"));
            },after:c=>
            {
                if(invalid=="clock")c.MapTimeTracker=new MapTimeTracker(new CampaignTime(101));
                if(invalid=="counter")c.CurrentTickCount++;
                if(invalid=="epoch")
                {
                    Assert.True(ContainerProvider.TryResolve<ISessionIdentity>(out var identity));
                    Assert.True(identity.TryApply(new AuthorityLease("room","paused-next","",5,true),"a"));
                }
                if(invalid=="campaign")Campaign.Current=(Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
            },confirmed:invalid!="unknown");
            Assert.Equal("unexpected",(string)Assert.Single((JArray)snapshot["RoleGroups"])["Classification"]);
        }
        finally {phase?.Dispose();}
    }

    private static JObject ObserveActual(float dt,int count,bool coverage,Action<Campaign> after=null,
        Action<Campaign,SessionIdentity> setup=null,Exception error=null,bool invokeAgain=false,bool confirmed=true,bool earlySkip=false,Action beforeSkip=null)
    {
        var previous=Campaign.Current;
        var campaign=(Campaign)RuntimeHelpers.GetUninitializedObject(typeof(Campaign));
        campaign.MapTimeTracker=new MapTimeTracker(new CampaignTime(100));campaign.CurrentTickCount=count;campaign.GameStarted=true;campaign._dt=dt;
        AccessTools.PropertySetter(typeof(Campaign),"UniqueGameId").Invoke(campaign,new object[]{"campaign"});Campaign.Current=campaign;
        var identity=new SessionIdentity();if(confirmed)Assert.True(identity.TryApply(new AuthorityLease("room","paused","",4,true),"a"));
        var controller=new ControllerIdProvider();controller.SetControllerId("a");var builder=new ContainerBuilder();
        builder.RegisterInstance(identity).As<ISessionIdentity>();builder.RegisterInstance(controller).As<IControllerIdProvider>();
        builder.RegisterInstance(new CampaignAuthorityContext(identity,controller)).As<ICampaignAuthorityContext>();
        using var container=builder.Build();ContainerProvider.SetContainer(container);
        var harmony=new Harmony("coop.tests.late-ai.branch");var late=AccessTools.Method(typeof(Campaign),"LateAITickAux");var think=AccessTools.Method(typeof(Campaign),"PartiesThink");
        try
        {
            setup?.Invoke(campaign,identity);afterEntry=after;thinkError=error;beforePartiesSkip=beforeSkip;
            if(coverage)harmony.Patch(think,prefix:new HarmonyMethod(typeof(CampaignSimulationEvidencePatches),"Prefix"),finalizer:new HarmonyMethod(typeof(CampaignSimulationEvidencePatches),"Finalizer"));
            harmony.Patch(think,prefix:new HarmonyMethod(typeof(CampaignLateAiBranchEvidenceTests),nameof(SkipPartiesThink)) { priority=earlySkip?Priority.First+100:Priority.Normal });
            harmony.Patch(late,prefix:new HarmonyMethod(typeof(CampaignSimulationEvidencePatches),"Prefix"),finalizer:new HarmonyMethod(typeof(CampaignSimulationEvidencePatches),"Finalizer"));
            harmony.Patch(late,prefix:new HarmonyMethod(typeof(CampaignLateAiBranchEvidenceTests),nameof(BeforeNative)));
            if(error==null)late.Invoke(campaign,Array.Empty<object>());
            else Assert.Same(error,Assert.Throws<TargetInvocationException>(()=>late.Invoke(campaign,Array.Empty<object>())).InnerException);
            if(invokeAgain){thinkError=null;campaign._dt=0;late.Invoke(campaign,Array.Empty<object>());}
            return JObject.FromObject(CampaignCycleDispatchPatches.For(campaign).SimulationEvidenceSnapshot());
        }
        finally {harmony.UnpatchAll(harmony.Id);afterEntry=null;beforePartiesSkip=null;resetDt=false;thinkError=null;ContainerProvider.Clear();Campaign.Current=previous;}
    }
}
