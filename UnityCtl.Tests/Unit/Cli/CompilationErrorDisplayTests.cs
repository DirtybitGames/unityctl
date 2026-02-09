using UnityCtl.Cli;
using UnityCtl.Protocol;
using Xunit;

namespace UnityCtl.Tests.Unit.Cli;

public class CompilationErrorDisplayTests
{
    [Fact]
    public void DisplayCompilationErrors_WithErrors_PrintsFileLineColumnMessage()
    {
        var response = MakeErrorResponse(new
        {
            errors = new[] {
                new { file = "Assets/Foo.cs", line = 10, column = 5, message = "CS1002: ; expected" }
            },
            warnings = Array.Empty<object>()
        });

        var output = CaptureStderr(() => ContextHelper.DisplayCompilationErrors(response));

        Assert.Contains("Assets/Foo.cs(10,5): CS1002: ; expected", output);
    }

    [Fact]
    public void DisplayCompilationErrors_WithWarnings_PrintsWithWarningPrefix()
    {
        var response = MakeErrorResponse(new
        {
            errors = Array.Empty<object>(),
            warnings = new[] {
                new { file = "Assets/Bar.cs", line = 3, column = 1, message = "CS0168: unused variable" }
            }
        });

        var output = CaptureStderr(() => ContextHelper.DisplayCompilationErrors(response));

        Assert.Contains("Assets/Bar.cs(3,1): warning: CS0168: unused variable", output);
    }

    [Fact]
    public void DisplayCompilationErrors_MultipleErrors_PrintsAll()
    {
        var response = MakeErrorResponse(new
        {
            errors = new[] {
                new { file = "A.cs", line = 1, column = 1, message = "err1" },
                new { file = "B.cs", line = 2, column = 3, message = "err2" }
            },
            warnings = Array.Empty<object>()
        });

        var output = CaptureStderr(() => ContextHelper.DisplayCompilationErrors(response));

        Assert.Contains("A.cs(1,1): err1", output);
        Assert.Contains("B.cs(2,3): err2", output);
    }

    [Fact]
    public void DisplayCompilationErrors_NoErrorsArray_PrintsNothing()
    {
        var response = MakeErrorResponse(new { state = "SomeError" });

        var output = CaptureStderr(() => ContextHelper.DisplayCompilationErrors(response));

        Assert.Equal("", output);
    }

    [Fact]
    public void DisplayCompilationErrors_NullResult_PrintsNothing()
    {
        var response = new ResponseMessage
        {
            Origin = "bridge",
            RequestId = "test",
            Status = ResponseStatus.Error,
            Result = null,
            Error = new ErrorPayload { Code = "TEST", Message = "test" }
        };

        var output = CaptureStderr(() => ContextHelper.DisplayCompilationErrors(response));

        Assert.Equal("", output);
    }

    private static ResponseMessage MakeErrorResponse(object result) => new()
    {
        Origin = "bridge",
        RequestId = "test",
        Status = ResponseStatus.Error,
        Result = result,
        Error = new ErrorPayload { Code = "COMPILATION_ERROR", Message = "Compilation failed" }
    };

    private static string CaptureStderr(Action action)
    {
        var oldErr = Console.Error;
        using var sw = new StringWriter();
        Console.SetError(sw);
        try { action(); }
        finally { Console.SetError(oldErr); }
        return sw.ToString();
    }
}
