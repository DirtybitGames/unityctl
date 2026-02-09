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

    /// <summary>
    /// Wait for a condition to become true, polling at 10ms intervals.
    /// Throws TimeoutException if the condition is not met within timeout.
    /// Use this instead of Task.Delay + Assert.True to avoid flaky tests.
    /// </summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new Xunit.Sdk.XunitException(
                    message ?? "Condition was not met within timeout");
            await Task.Delay(10);
        }
    }
}
