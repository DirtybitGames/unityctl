using UnityEngine;

public class Script
{
    public static object Main(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "World";
        var loud = System.Array.Exists(args, a => a == "--loud");
        var greeting = $"Hello, {name}!";
        if (loud) greeting = greeting.ToUpper();
        Debug.Log(greeting);
        return greeting;
    }
}
