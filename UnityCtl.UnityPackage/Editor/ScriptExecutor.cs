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

        // Cap on how many layers of nested Task<Task<...>> we will unwrap.
        // 4 covers any realistic agent expression — `return Task.FromResult(x)`
        // inside an async Main is the only non-pathological way to nest, and
        // that's depth 2 (outer async Task<object> wrapping the inner Task<T>).
        // Beyond the cap we throw rather than handing a still-Task value to
        // JSON serialisation, which would re-introduce the wedge bug if
        // Newtonsoft reflected over `.Result` on a pending main-thread-bound
        // Task.
        const int MaxUnwrapDepth = 4;

        /// <summary>
        /// Async-aware script entry. If <c>Main</c> returns a <c>Task</c> or
        /// <c>Task&lt;T&gt;</c>, this awaits and unwraps before serialising.
        /// All result serialisation is centralised here so the rule "the
        /// string in <see cref="ScriptExecuteResult.Result"/> is the JSON
        /// view of whatever the user ultimately returned" holds for every
        /// path.
        ///
        /// The await captures the caller's <see cref="System.Threading.SynchronizationContext"/>,
        /// so when invoked from Unity's main thread the continuation
        /// (and serialisation) resume on the main thread — safe for Unity-type
        /// JSON output. Callers that want the main thread freed during the
        /// await should invoke this from a fire-and-forget Task so the
        /// Pump tick releases the main thread immediately.
        /// </summary>
        public static async Task<ScriptExecuteResult> ExecuteAsync(string code, string className, string methodName, string[]? scriptArgs = null)
        {
            var result = Execute(code, className, methodName, scriptArgs);
            if (!result.Success) return result;

            try
            {
                var value = await AsyncUnwrap.UnwrapAsync(
                    result.RawReturn, result.DeclaredReturnType, MaxUnwrapDepth);
                result.RawReturn = value;
                result.Result = SerializeReturn(value);
                return result;
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

        static string? SerializeReturn(object? value)
        {
            if (value == null) return null;
            try { return JsonConvert.SerializeObject(value, Formatting.Indented); }
            catch { return value.ToString(); }
        }

        // Compile + invoke the user's Main. Internal — callers should use
        // ExecuteAsync, which is the only path that handles Task returns
        // correctly. Kept as a separate method so the (sync) Roslyn work and
        // the (async) Task-unwrap responsibilities live in obviously
        // separate functions.
        static ScriptExecuteResult Execute(string code, string className, string methodName, string[]? scriptArgs = null)
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

                // Stash the raw return + declared return type for ExecuteAsync.
                // The declared type lets the unwrapper distinguish `async Task`
                // (declared Task, runtime Task<VoidTaskResult>) from
                // `async Task<T>` without depending on the internal sentinel
                // type name. ExecuteAsync owns serialisation — keeping it in
                // one place ensures one rule for what the wire result looks
                // like.
                return new ScriptExecuteResult
                {
                    Success = true,
                    RawReturn = result,
                    DeclaredReturnType = method.ReturnType
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

        /// <summary>
        /// Declared return type of the user's Main method. Lets the unwrapper
        /// distinguish <c>async Task</c> (declared <c>Task</c>, runtime
        /// <c>Task&lt;VoidTaskResult&gt;</c>) from <c>async Task&lt;T&gt;</c>.
        /// Not transmitted over the wire.
        /// </summary>
        [JsonIgnore]
        public Type? DeclaredReturnType { get; set; }
    }
}
