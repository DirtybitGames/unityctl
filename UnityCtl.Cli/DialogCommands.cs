using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using UnityCtl.Protocol;

namespace UnityCtl.Cli;

public static class DialogCommands
{
    public static Command CreateCommand()
    {
        var dialogCommand = new Command("dialog", "Detect and dismiss Unity Editor popup dialogs");

        // dialog list
        var listCommand = new Command("list", "List detected popup dialogs");
        listCommand.SetHandler((InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var json = ContextHelper.GetJson(context);

            var dialogs = FindDialogs(context, projectPath);
            if (dialogs == null) return; // error already printed

            if (json)
            {
                var infos = dialogs.Select(d => new DialogInfo
                {
                    Title = d.Title,
                    Buttons = d.Buttons.Select(b => b.Text).ToArray()
                }).ToArray();

                Console.WriteLine(JsonHelper.Serialize(infos));
            }
            else
            {
                if (dialogs.Count == 0)
                {
                    Console.WriteLine("No popup dialogs detected.");
                }
                else
                {
                    Console.WriteLine($"Detected {dialogs.Count} dialog(s):");
                    Console.WriteLine();
                    foreach (var dialog in dialogs)
                    {
                        Console.Write($"  \"{dialog.Title}\"");
                        if (dialog.Buttons.Count > 0)
                        {
                            var buttonLabels = dialog.Buttons.Select(b => $"[{b.Text}]");
                            Console.Write($" {string.Join(" ", buttonLabels)}");
                        }
                        Console.WriteLine();
                    }
                }
            }
        });

        // dialog dismiss
        var dismissCommand = new Command("dismiss", "Dismiss a popup dialog by clicking a button");

        var buttonOption = new Option<string?>(
            "--button",
            "Button text to click (case-insensitive). If not specified, clicks the first button.");

        dismissCommand.AddOption(buttonOption);
        dismissCommand.SetHandler((InvocationContext context) =>
        {
            var projectPath = ContextHelper.GetProjectPath(context);
            var json = ContextHelper.GetJson(context);
            var buttonText = context.ParseResult.GetValueForOption(buttonOption);

            var dialogs = FindDialogs(context, projectPath);
            if (dialogs == null) return;

            if (dialogs.Count == 0)
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new { success = false, error = "No popup dialogs detected" }));
                }
                else
                {
                    Console.Error.WriteLine("No popup dialogs detected.");
                }
                context.ExitCode = 1;
                return;
            }

            var dialog = dialogs[0];
            if (dialog.Buttons.Count == 0)
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new { success = false, error = "Dialog has no buttons" }));
                }
                else
                {
                    Console.Error.WriteLine($"Dialog \"{dialog.Title}\" has no detectable buttons.");
                }
                context.ExitCode = 1;
                return;
            }

            // Resolve which button to click
            var targetButton = buttonText ?? dialog.Buttons[0].Text;

            var clicked = DialogDetector.ClickButton(dialog, targetButton);
            if (clicked)
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new
                    {
                        success = true,
                        dialog = dialog.Title,
                        button = targetButton
                    }));
                }
                else
                {
                    Console.WriteLine($"Clicked \"{targetButton}\" on \"{dialog.Title}\"");
                }
            }
            else
            {
                if (json)
                {
                    Console.WriteLine(JsonHelper.Serialize(new
                    {
                        success = false,
                        error = $"Button \"{targetButton}\" not found",
                        availableButtons = dialog.Buttons.Select(b => b.Text).ToArray()
                    }));
                }
                else
                {
                    Console.Error.WriteLine($"Button \"{targetButton}\" not found on \"{dialog.Title}\".");
                    Console.Error.Write("  Available: ");
                    Console.Error.WriteLine(string.Join(", ", dialog.Buttons.Select(b => b.Text)));
                }
                context.ExitCode = 1;
            }
        });

        dialogCommand.AddCommand(listCommand);
        dialogCommand.AddCommand(dismissCommand);
        return dialogCommand;
    }

    private static List<DetectedDialog>? FindDialogs(InvocationContext context, string? projectPath)
    {
        var projectRoot = projectPath != null
            ? System.IO.Path.GetFullPath(projectPath)
            : ProjectLocator.FindProjectRoot();

        if (projectRoot == null)
        {
            Console.Error.WriteLine("Error: Not in a Unity project.");
            Console.Error.WriteLine("  Use --project to specify project root");
            context.ExitCode = 1;
            return null;
        }

        var unityProcess = EditorCommands.FindUnityProcessForProject(projectRoot);
        if (unityProcess == null)
        {
            Console.Error.WriteLine("Error: No Unity Editor found running for this project.");
            context.ExitCode = 1;
            return null;
        }

        return DialogDetector.DetectDialogs(unityProcess.Id);
    }
}
