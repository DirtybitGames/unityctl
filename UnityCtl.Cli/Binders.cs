using System.CommandLine;
using System.CommandLine.Binding;
using System.Linq;

namespace UnityCtl.Cli;

// Binders for global options
public class ProjectBinder : BinderBase<string?>
{
    protected override string? GetBoundValue(BindingContext bindingContext)
    {
        // Find the --project option by traversing up the command tree
        var option = FindOption(bindingContext, "project");
        if (option != null)
        {
            return bindingContext.ParseResult.GetValueForOption(option) as string;
        }
        return null;
    }

    private static Option? FindOption(BindingContext context, string name)
    {
        // Look for the option in all parent commands
        var current = context.ParseResult.CommandResult;
        while (current != null)
        {
            var option = current.Command.Options.FirstOrDefault(o => o.Name == name);
            if (option != null) return option;
            current = current.Parent as System.CommandLine.Parsing.CommandResult;
        }
        return null;
    }
}

public class AgentIdBinder : BinderBase<string?>
{
    protected override string? GetBoundValue(BindingContext bindingContext)
    {
        var option = FindOption(bindingContext, "agent-id");
        if (option != null)
        {
            return bindingContext.ParseResult.GetValueForOption(option) as string;
        }
        return null;
    }

    private static Option? FindOption(BindingContext context, string name)
    {
        var current = context.ParseResult.CommandResult;
        while (current != null)
        {
            var option = current.Command.Options.FirstOrDefault(o => o.Name == name);
            if (option != null) return option;
            current = current.Parent as System.CommandLine.Parsing.CommandResult;
        }
        return null;
    }
}

public class JsonBinder : BinderBase<bool>
{
    protected override bool GetBoundValue(BindingContext bindingContext)
    {
        var option = FindOption(bindingContext, "json");
        if (option != null)
        {
            var value = bindingContext.ParseResult.GetValueForOption(option);
            if (value is bool boolValue)
            {
                return boolValue;
            }
        }
        return false;
    }

    private static Option? FindOption(BindingContext context, string name)
    {
        var current = context.ParseResult.CommandResult;
        while (current != null)
        {
            var option = current.Command.Options.FirstOrDefault(o => o.Name == name);
            if (option != null) return option;
            current = current.Parent as System.CommandLine.Parsing.CommandResult;
        }
        return null;
    }
}

public class CommandBinder : BinderBase<string>
{
    private readonly string _command;

    public CommandBinder(string command)
    {
        _command = command;
    }

    protected override string GetBoundValue(BindingContext bindingContext)
    {
        return _command;
    }
}
