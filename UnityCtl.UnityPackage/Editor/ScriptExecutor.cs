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
        /// <c>Task&lt;T&gt;</c>, this awaits and unwraps before serialising.
        /// All result serialisation lives here so the wire shape of
        /// <see cref="ScriptExecuteResult.Result"/> is built in one place.
        ///
        /// The await captures Unity's main-thread <see cref="System.Threading.SynchronizationContext"/>,
        /// so the continuation (and serialisation) resume on the main thread —
        /// safe for Unity-type JSON output. Callers that want the main thread
        /// freed during the await invoke this from a fire-and-forget Task so
        /// the Pump tick releases the main thread immediately.
        /// </summary>
        public static async Task<ScriptExecuteResult> ExecuteAsync(string code, string className, string methodName, string[]? scriptArgs = null)
        {
            var (result, raw, declared) = Compile(code, className, methodName, scriptArgs);
            if (!result.Success) return result;

            try
            {
                var value = await AsyncUnwrap.UnwrapAsync(raw, declared);
                result.Result = SerializeReturn(value);
                return result;
            }
            catch (Exception ex)
            {
                // `await` already rethrows the first inner exception from a
                // faulted Task, so we don't need an AggregateException unwrap.
                return new ScriptExecuteResult
                {
                    Success = false,
                    Error = $"Runtime error: {ex.Message}\n{ex.StackTrace}"
                };
            }
        }

        static string? SerializeReturn(object? value)
        {
            if (value == null) return null;
            try { return JsonConvert.SerializeObject(value, Formatting.Indented); }
            catch { return value.ToString(); }
        }

        // Compile + invoke the user's Main. Returns the success/error
        // shell along with the raw return object and the method's
        // declared return type — both needed by ExecuteAsync to decide
        // whether to await/unwrap. Private so callers can't accidentally
        // bypass the unwrap step.
        static (ScriptExecuteResult Result, object? Raw, Type? Declared) Compile(
            string code, string className, string methodName, string[]? scriptArgs = null)
        {
            if (string.IsNullOrEmpty(code))
                return (Failure("Code cannot be null or empty."), null, null);
            if (string.IsNullOrEmpty(className))
                return (Failure("Class name cannot be null or empty."), null, null);
            if (string.IsNullOrEmpty(methodName))
                return (Failure("Method name cannot be null or empty."), null, null);

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

                    return (new ScriptExecuteResult
                    {
                        Success = false,
                        Error = "Compilation failed.",
                        Diagnostics = diagnostics,
                        Hints = hints.Length > 0 ? hints : null
                    }, null, null);
                }

                // Load the assembly
                ms.Seek(0, SeekOrigin.Begin);
                var assembly = Assembly.Load(ms.ToArray());

                // Find the type
                var type = assembly.GetType(className);
                if (type == null)
                    return (Failure($"Class '{className}' not found in compiled assembly."), null, null);

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
                    return (Failure(
                        $"Static method '{methodName}' not found in class '{className}'. " +
                        $"Expected '{methodName}()' or '{methodName}(string[] args)'."), null, null);
                }

                object? raw;
                try
                {
                    raw = method.Invoke(null, invokeArgs);
                }
                catch (TargetInvocationException ex)
                {
                    var inner = ex.InnerException ?? ex;
                    return (Failure($"Runtime error: {inner.Message}\n{inner.StackTrace}"), null, null);
                }

                return (new ScriptExecuteResult { Success = true }, raw, method.ReturnType);
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null
                    ? $"\nInner: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}"
                    : "";
                var inner2Msg = ex.InnerException?.InnerException != null
                    ? $"\nInner2: {ex.InnerException.InnerException.Message}"
                    : "";
                return (Failure($"Unexpected error: {ex.Message}{innerMsg}{inner2Msg}\n{ex.StackTrace}"), null, null);
            }
        }

        static ScriptExecuteResult Failure(string error) =>
            new ScriptExecuteResult { Success = false, Error = error };
    }

    public class ScriptExecuteResult
    {
        public bool Success { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        public string[]? Diagnostics { get; set; }
        public string[]? Hints { get; set; }
    }
}
