using Newtonsoft.Json.Linq;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Helpers;

/// <summary>
/// Custom assertion helpers for UnityCtl test responses.
/// </summary>
public static class AssertExtensions
{
    public static void IsOk(ResponseMessage response)
    {
        Assert.Equal(ResponseStatus.Ok, response.Status);
        Assert.Null(response.Error);
    }

    public static void IsError(ResponseMessage response, string? expectedCode = null)
    {
        Assert.Equal(ResponseStatus.Error, response.Status);
        Assert.NotNull(response.Error);
        if (expectedCode != null)
        {
            Assert.Equal(expectedCode, response.Error.Code);
        }
    }

    public static string? GetResultState(ResponseMessage response)
    {
        return (response.Result as JObject)?["state"]?.ToString();
    }

    public static T GetResultAs<T>(ResponseMessage response)
    {
        var json = JsonHelper.Serialize(response.Result!);
        return JsonHelper.Deserialize<T>(json)!;
    }

    public static JObject GetResultJObject(ResponseMessage response)
    {
        if (response.Result is JObject jObj)
            return jObj;

        // Round-trip through JSON to get a JObject
        var json = JsonHelper.Serialize(response.Result!);
        return JObject.Parse(json);
    }
}
