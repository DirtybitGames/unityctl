using System;
using System.CommandLine;
using System.CommandLine.Invocation;

namespace UnityCtl.Cli;

/// <summary>
/// Hidden commands that print "did you mean?" hints for common agent misses.
/// These catch natural guesses that map to differently-named commands.
/// </summary>
public static class CommandHints
{
    public static void Register(RootCommand root)
    {
        root.AddCommand(CreateHint("compile", "Did you mean 'unityctl asset refresh'?"));
        root.AddCommand(CreateHint("exec", "Did you mean 'unityctl script eval <expression>'?"));
        root.AddCommand(CreateHint("start", "Did you mean 'unityctl editor run'?"));
    }

    private static Command CreateHint(string name, string message)
    {
        var command = new Command(name) { IsHidden = true };
        command.SetHandler((InvocationContext ctx) =>
        {
            Console.Error.WriteLine(message);
            ctx.ExitCode = 1;
        });
        return command;
    }
}
