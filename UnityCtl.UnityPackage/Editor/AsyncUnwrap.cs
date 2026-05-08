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
        // Compiler-generated builder for `async Task` methods returns a
        // runtime type of `Task<VoidTaskResult>` — that internal type isn't
        // public, so we match by full name.
        private const string VoidTaskResultFullName = "System.Threading.Tasks.VoidTaskResult";

        /// <summary>
        /// If <paramref name="value"/> is a <c>Task</c> or <c>Task&lt;T&gt;</c>,
        /// awaits and returns the inner value. Repeats for nested Tasks
        /// (e.g. <c>return Task.FromResult(x);</c> inside an async Main, which
        /// boxes a <c>Task&lt;T&gt;</c> through the outer <c>Task&lt;object&gt;</c>).
        ///
        /// <paramref name="declaredReturnType"/> distinguishes <c>async Task</c>
        /// (declared <c>Task</c>, runtime <c>Task&lt;VoidTaskResult&gt;</c>)
        /// from <c>async Task&lt;T&gt;</c> at the outermost layer reliably,
        /// without depending on the internal sentinel type.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the unwrap depth exceeds <paramref name="maxDepth"/>.
        /// Without this guard, returning a still-Task value would silently
        /// hand it to JSON serialisation, which reflects over the Task's
        /// public properties (including <c>.Result</c>) and re-introduces the
        /// pre-async-by-default wedge bug if the inner Task is pending and
        /// needs the main thread.
        /// </exception>
        public static async Task<object?> UnwrapAsync(
            object? value, Type? declaredReturnType, int maxDepth = 4)
        {
            if (value is not Task outer) return value;

            await outer;
            value = ReadResult(outer, declaredReturnType);

            // Inner layers are rare (only nested Task.FromResult chains), so
            // we always use the runtime-type walk past the outermost. depth
            // starts at 1 because the outer was just consumed.
            for (var depth = 1; value is Task inner; depth++)
            {
                if (depth >= maxDepth)
                {
                    throw new InvalidOperationException(
                        $"Task unwrap depth exceeded {maxDepth} — return Task chain too deeply nested. " +
                        $"Did you mean to `await` an inner Task before returning?");
                }
                await inner;
                value = ReadResult(inner, declaredReturnType: null);
            }

            return value;
        }

        // Extract the value from a Task. At the outermost layer the user's
        // declared return type is the most reliable signal for void; inner
        // layers fall back to walking the runtime type chain.
        private static object? ReadResult(Task task, Type? declaredReturnType)
        {
            if (declaredReturnType == typeof(Task))
                return null;

            Type? generic = null;
            if (declaredReturnType is { IsGenericType: true } d
                && d.GetGenericTypeDefinition() == typeof(Task<>))
            {
                generic = d;
            }
            else
            {
                generic = task.GetType();
                while (generic != null
                       && !(generic.IsGenericType && generic.GetGenericTypeDefinition() == typeof(Task<>)))
                    generic = generic.BaseType;
            }

            if (generic == null) return null;

            var arg = generic.GetGenericArguments()[0];
            if (arg.FullName == VoidTaskResultFullName) return null;

            return generic.GetProperty("Result")!.GetValue(task);
        }
    }
}
