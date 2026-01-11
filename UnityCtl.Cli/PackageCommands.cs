using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class PackageCommands
{
    private const string PackageName = "com.dirtybit.unityctl";
    private const string GitRepoUrl = "https://github.com/DirtybitGames/unityctl.git";
    private const string PackagePath = "UnityCtl.UnityPackage";

    public static Command CreateCommand()
    {
        var packageCommand = new Command("package", "Unity package management");

        packageCommand.AddCommand(CreateAddCommand());
        packageCommand.AddCommand(CreateRemoveCommand());
        packageCommand.AddCommand(CreateUpdateCommand());
        packageCommand.AddCommand(CreateStatusCommand());

        return packageCommand;
    }

    private static Command CreateAddCommand()
    {
        var addCommand = new Command("add", "Add Unity package to project");

        var methodOption = new Option<string>(
            "--method",
            getDefaultValue: () => "upm",
            "Installation method: upm (git URL) or local (file path)"
        );
        methodOption.AddAlias("-m");

        var versionOption = new Option<string?>(
            "--version",
            "Package version tag (e.g., v0.3.1). Default: current CLI version"
        );
        versionOption.AddAlias("-v");

        var localPathOption = new Option<string?>(
            "--local-path",
            "Path to local UnityCtl.UnityPackage directory (for --method local)"
        );

        addCommand.AddOption(methodOption);
        addCommand.AddOption(versionOption);
        addCommand.AddOption(localPathOption);

        addCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var method = context.ParseResult.GetValueForOption(methodOption)!;
            var version = context.ParseResult.GetValueForOption(versionOption);
            var localPath = context.ParseResult.GetValueForOption(localPathOption);
            var json = ContextHelper.GetJson(context);

            await AddPackageAsync(projectPath, method, version, localPath, json);
        });

        return addCommand;
    }

    private static Command CreateRemoveCommand()
    {
        var removeCommand = new Command("remove", "Remove Unity package from project");

        removeCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var json = ContextHelper.GetJson(context);

            await RemovePackageAsync(projectPath, json);
        });

        return removeCommand;
    }

    private static Command CreateUpdateCommand()
    {
        var updateCommand = new Command("update", "Update Unity package to latest version");

        var versionOption = new Option<string?>(
            "--version",
            "Package version tag (e.g., v0.3.1). Default: current CLI version"
        );
        versionOption.AddAlias("-v");

        updateCommand.AddOption(versionOption);

        updateCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var version = context.ParseResult.GetValueForOption(versionOption);
            var json = ContextHelper.GetJson(context);

            await UpdatePackageAsync(projectPath, version, json);
        });

        return updateCommand;
    }

    private static Command CreateStatusCommand()
    {
        var statusCommand = new Command("status", "Show Unity package installation status");

        statusCommand.SetHandler(async (InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var json = ContextHelper.GetJson(context);

            await StatusPackageAsync(projectPath, json);
        });

        return statusCommand;
    }

    private static string? FindProjectRoot(string? projectPath)
    {
        var root = projectPath ?? ProjectLocator.FindProjectRoot();
        if (root == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project. Use --project to specify project root.");
        }
        return root;
    }

    private static string GetManifestPath(string projectRoot)
    {
        return Path.Combine(projectRoot, "Packages", "manifest.json");
    }

    private static async Task<JsonObject?> ReadManifestAsync(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Error: manifest.json not found at {manifestPath}");
            return null;
        }

        try
        {
            var content = await File.ReadAllTextAsync(manifestPath);
            return JsonNode.Parse(content)?.AsObject();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading manifest.json: {ex.Message}");
            return null;
        }
    }

    private static async Task WriteManifestAsync(string manifestPath, JsonObject manifest)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var content = manifest.ToJsonString(options);
        await File.WriteAllTextAsync(manifestPath, content);
    }

    public static async Task AddPackageAsync(string? projectPath, string method, string? version, string? localPath, bool json)
    {
        var projectRoot = FindProjectRoot(projectPath);
        if (projectRoot == null) return;

        var manifestPath = GetManifestPath(projectRoot);
        var manifest = await ReadManifestAsync(manifestPath);
        if (manifest == null) return;

        var dependencies = manifest["dependencies"]?.AsObject();
        if (dependencies == null)
        {
            dependencies = new JsonObject();
            manifest["dependencies"] = dependencies;
        }

        // Check if already installed
        if (dependencies.ContainsKey(PackageName))
        {
            var existingValue = dependencies[PackageName]?.ToString();
            if (!json)
            {
                Console.WriteLine($"Package {PackageName} is already installed.");
                Console.WriteLine($"Current: {existingValue}");
                Console.WriteLine("Use 'unityctl package update' to update, or 'unityctl package remove' first.");
            }
            else
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = false,
                    error = "already_installed",
                    currentValue = existingValue
                }));
            }
            return;
        }

        string packageValue;
        if (method.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            // Local file reference
            string packageDir;
            if (!string.IsNullOrEmpty(localPath))
            {
                packageDir = Path.GetFullPath(localPath);
            }
            else
            {
                // Try to find UnityCtl.UnityPackage relative to this executable or project
                var possiblePaths = new[]
                {
                    Path.Combine(projectRoot, "..", PackagePath),
                    Path.Combine(projectRoot, "..", "..", PackagePath),
                    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", PackagePath)
                };

                packageDir = possiblePaths.FirstOrDefault(p => Directory.Exists(p)) ?? "";
                if (string.IsNullOrEmpty(packageDir))
                {
                    Console.Error.WriteLine("Error: Could not find UnityCtl.UnityPackage directory.");
                    Console.Error.WriteLine("Use --local-path to specify the path to the UnityCtl.UnityPackage directory.");
                    return;
                }
            }

            // Validate it's the Unity package
            var packageJsonPath = Path.Combine(packageDir, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                Console.Error.WriteLine($"Error: Not a valid Unity package at '{packageDir}'");
                Console.Error.WriteLine("Missing package.json file.");
                return;
            }

            // Calculate relative path from Packages folder to the Unity package
            var packagesDir = Path.GetDirectoryName(manifestPath)!;
            var relativePath = Path.GetRelativePath(packagesDir, Path.GetFullPath(packageDir));
            // Ensure forward slashes for Unity
            relativePath = relativePath.Replace('\\', '/');
            packageValue = $"file:{relativePath}";
        }
        else
        {
            // UPM git URL
            var versionTag = version ?? $"v{VersionInfo.Version}";
            packageValue = $"{GitRepoUrl}?path={PackagePath}#{versionTag}";
        }

        dependencies[PackageName] = packageValue;
        await WriteManifestAsync(manifestPath, manifest);

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                success = true,
                package = PackageName,
                value = packageValue,
                method
            }));
        }
        else
        {
            Console.WriteLine($"Added {PackageName} to Packages/manifest.json");
            Console.WriteLine($"  {packageValue}");
            Console.WriteLine();
            Console.WriteLine("Unity will automatically import the package on next domain reload.");
            Console.WriteLine("Open the Unity project or run 'unityctl asset refresh' if Unity is already open.");
        }
    }

    private static async Task RemovePackageAsync(string? projectPath, bool json)
    {
        var projectRoot = FindProjectRoot(projectPath);
        if (projectRoot == null) return;

        var manifestPath = GetManifestPath(projectRoot);
        var manifest = await ReadManifestAsync(manifestPath);
        if (manifest == null) return;

        var dependencies = manifest["dependencies"]?.AsObject();
        if (dependencies == null || !dependencies.ContainsKey(PackageName))
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new { success = false, error = "not_installed" }));
            }
            else
            {
                Console.Error.WriteLine($"Package {PackageName} is not installed.");
            }
            return;
        }

        dependencies.Remove(PackageName);
        await WriteManifestAsync(manifestPath, manifest);

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new { success = true, package = PackageName }));
        }
        else
        {
            Console.WriteLine($"Removed {PackageName} from Packages/manifest.json");
            Console.WriteLine("Unity will remove the package on next domain reload.");
        }
    }

    public static async Task UpdatePackageAsync(string? projectPath, string? version, bool json)
    {
        var projectRoot = FindProjectRoot(projectPath);
        if (projectRoot == null) return;

        var manifestPath = GetManifestPath(projectRoot);
        var manifest = await ReadManifestAsync(manifestPath);
        if (manifest == null) return;

        var dependencies = manifest["dependencies"]?.AsObject();
        if (dependencies == null || !dependencies.ContainsKey(PackageName))
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new { success = false, error = "not_installed" }));
            }
            else
            {
                Console.Error.WriteLine($"Package {PackageName} is not installed.");
                Console.Error.WriteLine("Run 'unityctl package add' to install it first.");
            }
            return;
        }

        var currentValue = dependencies[PackageName]?.ToString() ?? "";

        // Check if it's a local install
        if (currentValue.StartsWith("file:"))
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = false,
                    error = "local_install",
                    message = "Package is installed locally. Local installs are updated automatically."
                }));
            }
            else
            {
                Console.WriteLine("Package is installed locally (file: reference).");
                Console.WriteLine("Local installs are updated automatically when you rebuild UnityCtl.");
            }
            return;
        }

        // Update git URL version
        var versionTag = version ?? $"v{VersionInfo.Version}";
        var newValue = $"{GitRepoUrl}?path={PackagePath}#{versionTag}";

        if (currentValue == newValue)
        {
            if (json)
            {
                Console.WriteLine(JsonHelper.Serialize(new
                {
                    success = true,
                    message = "already_up_to_date",
                    version = versionTag
                }));
            }
            else
            {
                Console.WriteLine($"Package is already at version {versionTag}");
            }
            return;
        }

        dependencies[PackageName] = newValue;
        await WriteManifestAsync(manifestPath, manifest);

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                success = true,
                package = PackageName,
                previousValue = currentValue,
                newValue,
                version = versionTag
            }));
        }
        else
        {
            Console.WriteLine($"Updated {PackageName} to {versionTag}");
            Console.WriteLine($"  From: {currentValue}");
            Console.WriteLine($"  To:   {newValue}");
            Console.WriteLine();
            Console.WriteLine("Unity will update the package on next domain reload.");
        }
    }

    public static async Task StatusPackageAsync(string? projectPath, bool json)
    {
        var projectRoot = FindProjectRoot(projectPath);
        if (projectRoot == null) return;

        var manifestPath = GetManifestPath(projectRoot);
        var manifest = await ReadManifestAsync(manifestPath);
        if (manifest == null) return;

        var dependencies = manifest["dependencies"]?.AsObject();
        var installed = dependencies?.ContainsKey(PackageName) ?? false;
        var value = installed ? dependencies![PackageName]?.ToString() : null;

        // Parse version from value if present
        string? installedVersion = null;
        string? method = null;

        if (value != null)
        {
            if (value.StartsWith("file:"))
            {
                method = "local";
                installedVersion = "local";
            }
            else if (value.Contains("#"))
            {
                method = "upm";
                installedVersion = value.Split('#').LastOrDefault();
            }
        }

        if (json)
        {
            Console.WriteLine(JsonHelper.Serialize(new
            {
                installed,
                package = PackageName,
                value,
                method,
                version = installedVersion,
                cliVersion = VersionInfo.Version
            }));
        }
        else
        {
            Console.WriteLine($"Package: {PackageName}");
            Console.WriteLine($"Installed: {(installed ? "Yes" : "No")}");

            if (installed)
            {
                Console.WriteLine($"Method: {method}");
                Console.WriteLine($"Value: {value}");

                if (method == "upm" && installedVersion != null)
                {
                    Console.WriteLine($"Package Version: {installedVersion}");
                    Console.WriteLine($"CLI Version: v{VersionInfo.Version}");

                    if (installedVersion != $"v{VersionInfo.Version}")
                    {
                        Console.WriteLine();
                        Console.WriteLine("Version mismatch! Run 'unityctl package update' to sync versions.");
                    }
                }
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Run 'unityctl package add' to install the Unity package.");
            }
        }
    }
}
