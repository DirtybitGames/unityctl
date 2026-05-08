#nullable enable
using System;
using System.Threading.Tasks;

namespace UnityCtl.Editor
{
    /// <summary>
    /// Unwrap a <see cref="Task"/> or <see cref="Task{T}"/> return value into
    /// the inner result. Pure .NET, no Unity references — linked into the
    /// test project so the unwrap rules can be tested without spinning up an
    /// editor.
    /// </summary>
    public static class AsyncUnwrap
    {
        // Internal sentinel from the compiler-generated builder for
        // `async Task` methods — the runtime type is actually
        // `Task<VoidTaskResult>`. Used as a fallback when the caller does not
        // provide a declared return type.
        private const string VoidTaskResultFullName = "System.Threading.Tasks.VoidTaskResult";

        /// <summary>
        /// If <paramref name="value"/> is a <c>Task</c> or <c>Task&lt;T&gt;</c>,
        /// awaits and returns the inner value. Repeats for nested Tasks
        /// (e.g. an agent who returns <c>Task.FromResult(x)</c> from inside an
        /// async method, boxing a <c>Task&lt;T&gt;</c> through the outer
        /// <c>Task&lt;object&gt;</c>).
        ///
        /// <paramref name="declaredReturnType"/> is the user method's declared
        /// return type — used at depth 0 to distinguish <c>async Task</c>
        /// (declared <c>Task</c>, runtime <c>Task&lt;VoidTaskResult&gt;</c>)
        /// from <c>async Task&lt;T&gt;</c> reliably, without depending on the
        /// internal <c>VoidTaskResult</c> type name.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the unwrap depth exceeds <paramref name="maxDepth"/>.
        /// Without this guard, returning a still-Task value would silently
        /// hand it to JSON serialisation, which reflects over the Task's
        /// public properties (including <c>.Result</c>) and re-introduces the
        /// pre-async-by-default wedge bug if the inner Task is pending and
        /// needs the main thread.
        /// </exception>
        public static async Task<object?> UnwrapAsync(object? value, Type? declaredReturnType, int maxDepth)
        {
            for (var depth = 0; value is Task t; depth++)
            {
                if (depth >= maxDepth)
                {
                    throw new InvalidOperationException(
                        $"Task unwrap depth exceeded {maxDepth} — return Task chain too deeply nested. " +
                        $"Did you mean to `await` an inner Task before returning?");
                }

                await t;

                // Pick the Task<T> we read .Result from. At depth 0 the user's
                // declared return type is the most reliable hint — declared
                // `Task` (non-generic) means void, even though the runtime
                // type is Task<VoidTaskResult>.
                Type? generic = null;
                if (depth == 0 && declaredReturnType != null)
                {
                    if (declaredReturnType == typeof(Task))
                        return null;
                    if (declaredReturnType.IsGenericType
                        && declaredReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                        generic = declaredReturnType;
                }

                // Inner layers (or no declared-type hint) — walk the runtime
                // type chain to find a Task<T> ancestor.
                if (generic == null)
                {
                    generic = t.GetType();
                    while (generic != null
                           && !(generic.IsGenericType && generic.GetGenericTypeDefinition() == typeof(Task<>)))
                        generic = generic.BaseType;
                }

                if (generic == null)
                {
                    // Non-generic Task with no declared-type hint — void.
                    return null;
                }

                // Compiler-generated `async Task` builder produces the
                // VoidTaskResult sentinel; treat as void if we landed here
                // without a declared-type hint to do it earlier.
                var arg = generic.GetGenericArguments()[0];
                if (arg.FullName == VoidTaskResultFullName)
                    return null;

                value = generic.GetProperty("Result")!.GetValue(t);
            }

            return value;
        }
    }
}
