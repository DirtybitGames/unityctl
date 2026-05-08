using System;
using System.Threading.Tasks;
using UnityCtl.Editor;
using Xunit;

namespace UnityCtl.Tests.Unit.UnityPackage;

public class AsyncUnwrapTests
{
    [Fact]
    public async Task NonTaskValue_ReturnedAsIs()
    {
        var result = await AsyncUnwrap.UnwrapAsync(42, declaredReturnType: typeof(object), maxDepth: 4);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task NullValue_ReturnedAsNull()
    {
        var result = await AsyncUnwrap.UnwrapAsync(null, declaredReturnType: typeof(object), maxDepth: 4);
        Assert.Null(result);
    }

    [Fact]
    public async Task TaskOfInt_FromResult_Unwraps()
    {
        var task = Task.FromResult(99);
        var result = await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task<int>), maxDepth: 4);
        Assert.Equal(99, result);
    }

    [Fact]
    public async Task TaskOfString_RealAwait_Unwraps()
    {
        async Task<string> Inner() { await Task.Delay(5); return "ok"; }
        var task = Inner();
        var result = await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task<string>), maxDepth: 4);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task NonGenericTask_DeclaredVoid_ReturnsNull()
    {
        // `async Task Main()` declares Task; runtime is Task<VoidTaskResult>.
        // Declared-type hint at depth 0 makes the void detection robust without
        // depending on the internal sentinel name.
        async Task Inner() { await Task.Delay(5); }
        Task task = Inner();
        var result = await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task), maxDepth: 4);
        Assert.Null(result);
    }

    [Fact]
    public async Task NonGenericTask_NoDeclaredType_VoidTaskResultFallback()
    {
        // No declared-type hint — fall back to the runtime VoidTaskResult check
        // so the helper still does the right thing if a caller forgets to plumb
        // the declared type through.
        async Task Inner() { await Task.Delay(5); }
        Task task = Inner();
        var result = await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: null, maxDepth: 4);
        Assert.Null(result);
    }

    [Fact]
    public async Task NestedTaskOfTaskOfInt_UnwrapsToInner()
    {
        // Agent writes `return Task.FromResult(42);` inside an async Main —
        // the return is Task<object> wrapping Task<int>.
        var task = Task.FromResult<object>(Task.FromResult(42));
        var result = await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task<object>), maxDepth: 4);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task DeeplyNestedTaskAtDepthCap_UnwrapsAllLayers()
    {
        // Four wrap layers — within the cap of 4.
        var task = Task.FromResult<object>(
            Task.FromResult<object>(
                Task.FromResult<object>(
                    Task.FromResult(7))));
        var result = await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task<object>), maxDepth: 4);
        Assert.Equal(7, result);
    }

    [Fact]
    public async Task DeeplyNestedTaskBeyondCap_ThrowsClearError()
    {
        // Five wrap layers — exceeds the cap. Must throw, not silently leave
        // a Task in the result (which would re-introduce the wedge bug if
        // Newtonsoft serialised it reflectively).
        var task = Task.FromResult<object>(
            Task.FromResult<object>(
                Task.FromResult<object>(
                    Task.FromResult<object>(
                        Task.FromResult(7)))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task<object>), maxDepth: 4));
        Assert.Contains("4", ex.Message);
    }

    [Fact]
    public async Task FaultedTask_PropagatesInnerException()
    {
        async Task<int> Boom() { await Task.Delay(5); throw new InvalidOperationException("boom"); }
        var task = Boom();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await AsyncUnwrap.UnwrapAsync(task, declaredReturnType: typeof(Task<int>), maxDepth: 4));
        Assert.Equal("boom", ex.Message);
    }
}
