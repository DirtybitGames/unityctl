#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
        static List<MetadataReference>? s_References;
        static readonly object s_ReferencesLock = new object();
        static bool s_InvalidationHooked;

        static bool IsReferenceable(Assembly asm) =>
            !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location);

        static List<MetadataReference> GetReferences()
        {
            var cached = s_References;
            if (cached != null) return cached;

            lock (s_ReferencesLock)
            {
                cached = s_References;
                if (cached != null) return cached;

                if (!s_InvalidationHooked)
                {
                    // Ignore in-memory assemblies (e.g., our own Assembly.Load(byte[]) output) —
                    // otherwise every script execution would invalidate the cache it just built.
                    AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
                    {
                        if (IsReferenceable(args.LoadedAssembly))
                            s_References = null;
                    };
                    s_InvalidationHooked = true;
                }

                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var refs = new List<MetadataReference>(assemblies.Length);
                foreach (var asm in assemblies)
                {
                    if (!IsReferenceable(asm)) continue;
                    refs.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                s_References = refs;
                return refs;
            }
        }

        /// <summary>
        /// Async-aware script entry. If <c>Main</c> returns a <c>Task</c> or
        /// <c>Task&lt;T&gt;</c>, this awaits it (unwrapping the value) before
        /// serialising the result. Other return types are passed through.
        ///
        /// The await happens on the calling thread's SynchronizationContext —
        /// callers that want the main thread freed during the await should
        /// invoke this from an async context (a fire-and-forget Task that
        /// the Pump tick can release).
        /// </summary>
        public static async Task<ScriptExecuteResult> ExecuteAsync(string code, string className, string methodName, string[]? scriptArgs = null)
        {
            var result = Execute(code, className, methodName, scriptArgs);

            // Successful sync invoke that produced a Task → await and unwrap.
            // Loop in case the unwrapped value is itself a Task (e.g. an agent
            // wrote `return Task.FromResult(42);` inside an async Main, which
            // boxes the inner Task<int> through the outer Task<object>). Cap
            // at a small depth so a pathological nested return can't spin.
            if (result.Success && result.RawReturn is Task)
            {
                try
                {
                    object? value = result.RawReturn;
                    for (var depth = 0; depth < 4 && value is Task t; depth++)
                    {
                        await t;
                        // `async Task Main()` actually returns the runtime
                        // type Task<VoidTaskResult> — IsGenericType is true,
                        // but the inner value is a sentinel struct we must
                        // treat as void. Walk up to find a Task<T> ancestor;
                        // if T is VoidTaskResult, the user wanted void.
                        Type? generic = t.GetType();
                        while (generic != null &&
                               !(generic.IsGenericType && generic.GetGenericTypeDefinition() == typeof(Task<>)))
                            generic = generic.BaseType;

                        if (generic != null)
                        {
                            var arg = generic.GetGenericArguments()[0];
                            value = arg.FullName == "System.Threading.Tasks.VoidTaskResult"
                                ? null
                                : generic.GetProperty("Result")!.GetValue(t);
                        }
                        else
                        {
                            value = null;
                        }
                    }
                    result.RawReturn = value;
                    result.Result = SerializeReturn(value);
                }
                catch (Exception ex)
                {
                    var inner = ex is AggregateException agg && agg.InnerException != null
                        ? agg.InnerException
                        : ex;
                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = $"Runtime error: {inner.Message}\n{inner.StackTrace}"
                    };
                }
            }

            return result;
        }

        static string? SerializeReturn(object? value)
        {
            if (value == null) return null;
            try { return JsonConvert.SerializeObject(value, Formatting.Indented); }
            catch { return value.ToString(); }
        }

        public static ScriptExecuteResult Execute(string code, string className, string methodName, string[]? scriptArgs = null)
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

            var profile = Environment.GetEnvironmentVariable("UNITYCTL_SCRIPT_PROFILE") == "1";
            var refSw = profile ? Stopwatch.StartNew() : null;
            var compileSw = profile ? new Stopwatch() : null;

            try
            {
                // Parse the code
                var syntaxTree = CSharpSyntaxTree.ParseText(code);

                // Get references from all loaded assemblies (cached across calls; invalidated on AssemblyLoad).
                var references = GetReferences();

                if (refSw != null) refSw.Stop();
                compileSw?.Start();

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

                if (compileSw != null)
                {
                    compileSw.Stop();
                    UnityEngine.Debug.Log($"[unityctl.script] refs={refSw!.ElapsedMilliseconds}ms compile={compileSw.ElapsedMilliseconds}ms");
                }

                if (!emitResult.Success)
                {
                    var diagnostics = emitResult.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error)
                        .Select(d => d.ToString())
                        .ToArray();

                    var hints = TypeResolver.BuildHints(diagnostics);

                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = "Compilation failed.",
                        Diagnostics = diagnostics,
                        Hints = hints.Length > 0 ? hints : null
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

                // Find the method - try with string[] parameter first, then parameterless
                var methodWithArgs = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(string[]) }, null);
                var methodNoArgs = type.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, Type.EmptyTypes, null);

                MethodInfo? method;
                object?[]? invokeArgs;

                if (methodWithArgs != null)
                {
                    method = methodWithArgs;
                    invokeArgs = new object?[] { scriptArgs ?? Array.Empty<string>() };
                }
                else if (methodNoArgs != null)
                {
                    method = methodNoArgs;
                    invokeArgs = null;
                }
                else
                {
                    return new ScriptExecuteResult
                    {
                        Success = false,
                        Error = $"Static method '{methodName}' not found in class '{className}'. Expected '{methodName}()' or '{methodName}(string[] args)'."
                    };
                }

                // Invoke the method
                object? result;
                try
                {
                    result = method.Invoke(null, invokeArgs);
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

                // Serialize the result. RawReturn is preserved so ExecuteAsync
                // can detect a Task return and await/unwrap it before
                // serialisation. Non-Task returns are serialised eagerly here
                // so the existing sync API is unchanged.
                return new ScriptExecuteResult
                {
                    Success = true,
                    RawReturn = result,
                    Result = result is Task ? null : SerializeReturn(result)
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
        public string[]? Hints { get; set; }

        /// <summary>
        /// Raw object returned by the user's Main method, before JSON
        /// serialisation. Used by <see cref="ScriptExecutor.ExecuteAsync"/> to
        /// detect a <c>Task</c>/<c>Task&lt;T&gt;</c> return so it can await
        /// and unwrap. Not transmitted over the wire.
        /// </summary>
        [JsonIgnore]
        public object? RawReturn { get; set; }
    }
}
