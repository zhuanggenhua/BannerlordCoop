using GameInterface.CoopSessionData.Save.Data;
using GameInterface.Surrogates;
using ProtoBuf;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace GameInterface.Tests.Services.CampaignAuthority.Battles;

public sealed class BattlePreparationSessionSerializationTests
{
    private static PropertyInfo StateProperty()
    {
        var property=typeof(CoopSession).GetProperty("BattlePreparations");
        Assert.NotNull(property);
        return property;
    }

    [Fact]
    public void FiniteBattleStateUsesNewTagWithoutChangingExistingSessionTags()
    {
        var property=StateProperty();
        Assert.Equal(19,property.GetCustomAttribute<ProtoMemberAttribute>().Tag);
        Assert.NotNull(typeof(ICoopSession).GetProperty(property.Name));
        var tags=typeof(CoopSession).GetProperties().Select(value=>value.GetCustomAttribute<ProtoMemberAttribute>())
            .Where(value=>value!=null).Select(value=>value.Tag).OrderBy(value=>value).ToArray();
        Assert.Equal(Enumerable.Range(1,19),tags);
    }

    [Fact]
    public void LegacyJsonMissingBattleMemberCreatesIndependentEmptyInstances()
    {
        var property=StateProperty();var options=new JsonSerializerOptions {IncludeFields=true};
        var json=JsonSerializer.Serialize(CoopSession.Empty,options);
        using var document=JsonDocument.Parse(json);
        var legacy="{"+string.Join(",",document.RootElement.EnumerateObject().Where(value=>value.Name!=property.Name)
            .Select(value=>JsonSerializer.Serialize(value.Name)+":"+value.Value.GetRawText()))+"}";
        var first=JsonSerializer.Deserialize<CoopSession>(legacy,options);
        var second=JsonSerializer.Deserialize<CoopSession>(legacy,options);
        Assert.NotNull(property.GetValue(first));Assert.NotNull(property.GetValue(second));
        Assert.NotSame(property.GetValue(first),property.GetValue(second));
    }

    [Fact]
    public void ExistingProtobufSessionSerializationCarriesIndependentBattleState()
    {
        var property=StateProperty();new SurrogateCollection();
        var original=CoopSession.Empty;var restored=Serializer.DeepClone(original);
        Assert.NotNull(property.GetValue(restored));Assert.NotSame(property.GetValue(original),property.GetValue(restored));
    }
}
