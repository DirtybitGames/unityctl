#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Newtonsoft.Json;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Executes arbitrary C# code using Roslyn runtime compilation.
    /// The code must define a class with a static method to execute.
    /// </summary>
    public static class ScriptExecutor
    {
        public static ScriptExecuteResult Execute(string code, string className, string methodName)
        {
            if (string.IsNullOrEmpty(code))
            {
                return new ScriptExecuteResult
                {
                    Success = false,
                    Error = "Code cannot be null or empty."
                };
            }

            if (string.IsNullOrEmpty(className))
            {
                return new ScriptExecuteResult
                {
                    Success = false,
                    Error = "Class name cannot be null or empty."
                };
            }

            if (string.IsNullOrEmpty(methodName))
            {
                return new ScriptExecuteResult
                {
                    Success = false,
                    Error = "Method name cannot be null or empty."
                };
            }

            try
            {
                // Parse the code
                var syntaxTree = CSharpSyntaxTree.ParseText(code);

                // Get references from all loaded assemblies
                var references = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic)
                    .Where(a => !string.IsNullOrEmpty(a.Location))
                    .Select(a => MetadataReference.CreateFromFile(a.Location))
                    .ToArray();

                // Create compilation
                var compilation = CSharpCompilation.Create(
                    assemblyName: "DynamicScript_" + Guid.NewGuid().ToString("N"),
                    syntaxTrees: new[] { syntaxTree },
                    references: references,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                // Emit to memory
                using var ms = new MemoryStream();
                var emitResult = compilation.Emit(ms);

                if (!emitResult.Success)
                {
                    var diagnostics = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString())
                        .ToArray();

                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = "Compilation failed.",
                        Diagnostics = diagnostics
                    };
                }

                // Load the assembly
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                // Find the type
                var type = assembly.GetType(className);
                if (type == null)
                {
                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = $"Class '{className}' not found in compiled assembly."
                    };
                }

                // Find the method
                var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null)
                {
                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = $"Static method '{methodName}' not found in class '{className}'."
                    };
                }

                // Invoke the method
                object? result;
                try
                {
                    result = method.Invoke(null, null);
                }
                catch (TargetInvocationException ex)
                {
                    var inner = ex.InnerException ?? ex;
                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = $"Runtime error: {inner.Message}\n{inner.StackTrace}"
                    };
                }

                // Serialize the result
                string? resultString = null;
                if (result != null)
                {
                    try
                    {
                        resultString = JsonConvert.SerializeObject(result, Formatting.Indented);
                    }
                    catch
                    {
                        resultString = result.ToString();
                    }
                }

                return new ScriptExecuteResult
                {
                    Success = true,
                    Result = resultString
                };
            }
            catch (Exception ex)
            {
                // Unwrap inner exceptions to get the real error
                var innerMsg = ex.InnerException != null
                    ? $"\nInner: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}"
                    : "";
                var inner2Msg = ex.InnerException?.InnerException != null
                    ? $"\nInner2: {ex.InnerException.InnerException.Message}"
                    : "";
                return new ScriptExecuteResult
                {
                    Success = false,
                    Error = $"Unexpected error: {ex.Message}{innerMsg}{inner2Msg}\n{ex.StackTrace}"
                };
            }
        }
    }

    public class ScriptExecuteResult
    {
        public bool Success { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        public string[]? Diagnostics { get; set; }
    }
}
