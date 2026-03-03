using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnityCtl.Bridge;

/// <summary>
/// Host-side C# compiler using Roslyn. Compiles C# code into an assembly
/// that can be shipped to a device running Mono for execution via Assembly.Load.
///
/// Reference assemblies are gathered from the Unity project's Library/ScriptAssemblies
/// and the Unity Editor's managed assemblies.
/// </summary>
public static class ScriptCompiler
{
    public static CompilationResult Compile(string code, string projectPath)
    {
        if (string.IsNullOrEmpty(code))
            return CompilationResult.Failure("Code cannot be empty", Array.Empty<string>());

        try
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            // Gather reference assemblies from the Unity project
            var references = GatherReferences(projectPath);
            if (references.Length == 0)
            {
                return CompilationResult.Failure(
                    "No reference assemblies found. Ensure Unity Editor has been opened at least once " +
                    "(Library/ScriptAssemblies must exist).",
                    Array.Empty<string>());
            }

            var compilation = CSharpCompilation.Create(
                assemblyName: "DeviceScript_" + Guid.NewGuid().ToString("N"),
                syntaxTrees: new[] { syntaxTree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            if (!emitResult.Success)
            {
                var diagnostics = emitResult.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString())
                    .ToArray();

                return CompilationResult.Failure("Compilation failed", diagnostics);
            }

            ms.Seek(0, SeekOrigin.Begin);
            return CompilationResult.Ok(ms.ToArray());
        }
        catch (Exception ex)
        {
            return CompilationResult.Failure($"Compiler error: {ex.Message}", Array.Empty<string>());
        }
    }

    private static MetadataReference[] GatherReferences(string projectPath)
    {
        var references = new List<MetadataReference>();

        // Unity project's compiled assemblies
        var scriptAssemblies = Path.Combine(projectPath, "Library", "ScriptAssemblies");
        if (Directory.Exists(scriptAssemblies))
        {
            foreach (var dll in Directory.GetFiles(scriptAssemblies, "*.dll"))
            {
                try { references.Add(MetadataReference.CreateFromFile(dll)); }
                catch { /* Skip unreadable assemblies */ }
            }
        }

        // Try to find Unity's managed assemblies via the project's Unity version
        var editorManagedPath = FindUnityManagedAssembliesPath(projectPath);
        if (editorManagedPath != null && Directory.Exists(editorManagedPath))
        {
            foreach (var dll in Directory.GetFiles(editorManagedPath, "*.dll"))
            {
                try { references.Add(MetadataReference.CreateFromFile(dll)); }
                catch { /* Skip unreadable assemblies */ }
            }
        }

        // Also include netstandard reference from the runtime
        var netstandardPath = Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
            "netstandard.dll");
        if (File.Exists(netstandardPath))
        {
            references.Add(MetadataReference.CreateFromFile(netstandardPath));
        }

        // Core runtime assemblies
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        foreach (var coreLib in new[] { "System.Runtime.dll", "mscorlib.dll", "System.dll", "System.Core.dll" })
        {
            var path = Path.Combine(runtimeDir, coreLib);
            if (File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        return references.ToArray();
    }

    private static string? FindUnityManagedAssembliesPath(string projectPath)
    {
        // Read Unity version from ProjectSettings/ProjectVersion.txt
        var versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile)) return null;

        var content = File.ReadAllText(versionFile);
        // Format: m_EditorVersion: 2022.3.10f1
        var match = System.Text.RegularExpressions.Regex.Match(content, @"m_EditorVersion:\s*(.+)");
        if (!match.Success) return null;

        var version = match.Groups[1].Value.Trim();

        // Common Unity installation paths
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            candidates.Add($@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Data\Managed\UnityEngine");
            candidates.Add($@"C:\Program Files\Unity\Hub\Editor\{version}\Editor\Data\Managed");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add($"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/Managed/UnityEngine");
            candidates.Add($"/Applications/Unity/Hub/Editor/{version}/Unity.app/Contents/Managed");
        }
        else // Linux
        {
            candidates.Add($"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Unity/Hub/Editor/{version}/Editor/Data/Managed/UnityEngine");
            candidates.Add($"{Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}/Unity/Hub/Editor/{version}/Editor/Data/Managed");
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }
}

public class CompilationResult
{
    public bool Success { get; private set; }
    public byte[]? AssemblyBytes { get; private set; }
    public string? Error { get; private set; }
    public string[]? Diagnostics { get; private set; }

    public static CompilationResult Ok(byte[] bytes) => new() { Success = true, AssemblyBytes = bytes };
    public static CompilationResult Failure(string error, string[] diagnostics) =>
        new() { Success = false, Error = error, Diagnostics = diagnostics };
}
