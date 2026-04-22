using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using UnityCtl.Tests.Helpers;
using Xunit;

namespace UnityCtl.Tests.Integration;

/// <summary>
/// Tests for the script.lookupType RPC and the hint-carrying shape of
/// script.execute responses. Uses the FakeUnityClient to simulate the
/// Unity-side handler; the Unity-side TypeResolver is tested separately
/// as a unit test (see TypeResolverTests).
/// </summary>
public class ScriptLookupTypeTests : IAsyncLifetime
{
    private readonly BridgeTestFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task ScriptLookupType_WithMatches_RoundtripsToClient()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptLookupType, req =>
        {
            var name = req.Args?["name"]?.ToString() ?? "";
            return new ScriptLookupTypeResult
            {
                Query = name,
                Matches = new[]
                {
                    new ScriptLookupTypeMatch
                    {
                        FullName = "Dirtybit.Game.Model.Storage",
                        Namespace = "Dirtybit.Game.Model",
                        Assembly = "Assembly-CSharp",
                        Kind = "class",
                        IsStatic = true
                    }
                },
                Truncated = false
            };
        });

        var args = new Dictionary<string, object?> { ["name"] = "Storage", ["limit"] = 10 };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScriptLookupType, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Storage", result["query"]?.ToString());
        var matches = result["matches"] as JArray;
        Assert.NotNull(matches);
        Assert.Single(matches!);
        Assert.Equal("Dirtybit.Game.Model.Storage", matches![0]["fullName"]?.ToString());
        Assert.Equal("Assembly-CSharp", matches[0]["assembly"]?.ToString());
        Assert.Equal("Dirtybit.Game.Model", matches[0]["namespace"]?.ToString());
        Assert.Equal("class", matches[0]["kind"]?.ToString());
        Assert.True(matches[0]["isStatic"]?.Value<bool>());
    }

    [Fact]
    public async Task ScriptLookupType_EmptyMatches_StillReturnsQueryField()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptLookupType, _ =>
            new ScriptLookupTypeResult
            {
                Query = "Nonexistent",
                Matches = Array.Empty<ScriptLookupTypeMatch>(),
                Truncated = false
            });

        var args = new Dictionary<string, object?> { ["name"] = "Nonexistent", ["limit"] = 10 };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScriptLookupType, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.Equal("Nonexistent", result["query"]?.ToString());
        var matches = result["matches"] as JArray;
        Assert.NotNull(matches);
        Assert.Empty(matches!);
    }

    [Fact]
    public async Task ScriptLookupType_ForwardsNameAndLimitToUnity()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptLookupType, _ =>
            new ScriptLookupTypeResult
            {
                Query = "anything",
                Matches = Array.Empty<ScriptLookupTypeMatch>(),
                Truncated = false
            });

        var args = new Dictionary<string, object?> { ["name"] = "ClanFeedOSA", ["limit"] = 25 };
        await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScriptLookupType, args);

        var received = await _fixture.FakeUnity.WaitForRequestAsync(UnityCtlCommands.ScriptLookupType);
        Assert.Equal("ClanFeedOSA", received.Args?["name"]?.ToString());
        // The bridge deserializes numeric args as long; accept either JToken or boxed long.
        var limitRaw = received.Args?["limit"];
        var limit = limitRaw switch
        {
            Newtonsoft.Json.Linq.JToken jt => jt.ToObject<long>(),
            long l => l,
            int i => (long)i,
            _ => (long?)null
        };
        Assert.Equal(25L, limit);
    }

    [Fact]
    public async Task ScriptExecute_WithHints_CarriesHintsInResponse()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptExecute, _ =>
            new ScriptExecuteResult
            {
                Success = false,
                Error = "Compilation failed.",
                Diagnostics = new[]
                {
                    "(11,31): error CS0103: The name 'Storage' does not exist in the current context"
                },
                Hints = new[]
                {
                    "Hint: 'Storage' is defined as Dirtybit.Game.Model.Storage in assembly Assembly-CSharp — add `-u Dirtybit.Game.Model` or fully qualify."
                }
            });

        var args = new Dictionary<string, object?>
        {
            ["code"] = "whatever",
            ["className"] = "Script",
            ["methodName"] = "Main"
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScriptExecute, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        Assert.False(result["success"]?.Value<bool>());
        var hints = result["hints"] as JArray;
        Assert.NotNull(hints);
        Assert.Single(hints!);
        Assert.Contains("Dirtybit.Game.Model.Storage", hints![0]!.ToString());
    }

    [Fact]
    public async Task ScriptExecute_WithoutHints_HintsAreAbsentOrNull()
    {
        _fixture.FakeUnity.OnCommand(UnityCtlCommands.ScriptExecute, _ =>
            new ScriptExecuteResult
            {
                Success = true,
                Result = "\"ok\""
            });

        var args = new Dictionary<string, object?>
        {
            ["code"] = "whatever",
            ["className"] = "Script",
            ["methodName"] = "Main"
        };
        var response = await _fixture.SendRpcAndParseAsync(UnityCtlCommands.ScriptExecute, args);

        AssertExtensions.IsOk(response);
        var result = AssertExtensions.GetResultJObject(response);
        var hints = result["hints"];
        // Null or missing is acceptable; just make sure an empty array doesn't sneak in
        Assert.True(hints == null || hints.Type == JTokenType.Null);
    }
}
