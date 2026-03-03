#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace UnityCtl.Protocol;

/// <summary>
/// Reflection-based C# expression evaluator that works on IL2CPP and Mono.
/// Supports static/instance member access, method calls, and member chaining.
/// No Roslyn dependency - evaluates expressions via System.Reflection at runtime.
/// </summary>
public static class ExpressionEvaluator
{
    // Default namespaces to search when resolving type names
    private static readonly string[] DefaultNamespaces =
    {
        "UnityEngine",
        "UnityEngine.SceneManagement",
        "System",
        "System.Collections.Generic",
    };

    public static EvalResult Evaluate(string expression, string[]? additionalNamespaces = null)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return EvalResult.Fail("Expression cannot be empty.");

        try
        {
            var namespaces = DefaultNamespaces;
            if (additionalNamespaces != null && additionalNamespaces.Length > 0)
                namespaces = DefaultNamespaces.Concat(additionalNamespaces).Distinct().ToArray();

            var tokens = Tokenize(expression);
            var parser = new Parser(tokens);
            var ast = parser.ParseExpression();

            if (parser.Position < tokens.Count)
                return EvalResult.Fail($"Unexpected token after expression: '{tokens[parser.Position].Value}'");

            var result = EvaluateNode(ast, namespaces);
            return EvalResult.Ok(result);
        }
        catch (EvalException ex)
        {
            return EvalResult.Fail(ex.Message);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            return EvalResult.Fail($"Runtime error: {inner.Message}");
        }
        catch (Exception ex)
        {
            return EvalResult.Fail($"Evaluation error: {ex.Message}");
        }
    }

    #region Tokenizer

    internal enum TokenKind
    {
        Identifier,
        Dot,
        LParen,
        RParen,
        Comma,
        StringLiteral,
        IntLiteral,
        FloatLiteral,
        DoubleLiteral,
        BoolLiteral,
        NullLiteral,
        CharLiteral,
        Minus,
        Typeof,
        LAngle,
        RAngle,
    }

    internal class Token
    {
        public TokenKind Kind { get; }
        public string Value { get; }

        public Token(TokenKind kind, string value)
        {
            Kind = kind;
            Value = value;
        }

        public override string ToString() => $"{Kind}:{Value}";
    }

    internal static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            switch (c)
            {
                case '.': tokens.Add(new Token(TokenKind.Dot, ".")); i++; continue;
                case '(': tokens.Add(new Token(TokenKind.LParen, "(")); i++; continue;
                case ')': tokens.Add(new Token(TokenKind.RParen, ")")); i++; continue;
                case ',': tokens.Add(new Token(TokenKind.Comma, ",")); i++; continue;
                case '<': tokens.Add(new Token(TokenKind.LAngle, "<")); i++; continue;
                case '>': tokens.Add(new Token(TokenKind.RAngle, ">")); i++; continue;
                case '-': tokens.Add(new Token(TokenKind.Minus, "-")); i++; continue;
            }

            // String literal
            if (c == '"')
            {
                i++; // skip opening quote
                var start = i;
                var sb = new System.Text.StringBuilder();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        i++;
                        switch (input[i])
                        {
                            case 'n': sb.Append('\n'); break;
                            case 't': sb.Append('\t'); break;
                            case 'r': sb.Append('\r'); break;
                            case '\\': sb.Append('\\'); break;
                            case '"': sb.Append('"'); break;
                            default: sb.Append('\\'); sb.Append(input[i]); break;
                        }
                    }
                    else
                    {
                        sb.Append(input[i]);
                    }
                    i++;
                }
                if (i >= input.Length)
                    throw new EvalException("Unterminated string literal");
                i++; // skip closing quote
                tokens.Add(new Token(TokenKind.StringLiteral, sb.ToString()));
                continue;
            }

            // Char literal
            if (c == '\'')
            {
                i++; // skip opening quote
                if (i >= input.Length)
                    throw new EvalException("Unterminated char literal");
                char ch;
                if (input[i] == '\\' && i + 1 < input.Length)
                {
                    i++;
                    ch = input[i] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '\'' => '\'',
                        _ => input[i]
                    };
                }
                else
                {
                    ch = input[i];
                }
                i++;
                if (i >= input.Length || input[i] != '\'')
                    throw new EvalException("Unterminated char literal");
                i++; // skip closing quote
                tokens.Add(new Token(TokenKind.CharLiteral, ch.ToString()));
                continue;
            }

            // Number literal
            if (char.IsDigit(c))
            {
                var start = i;
                bool hasDot = false;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.'))
                {
                    if (input[i] == '.')
                    {
                        if (hasDot) break;
                        // Look ahead to make sure it's not a member access
                        if (i + 1 < input.Length && char.IsDigit(input[i + 1]))
                            hasDot = true;
                        else
                            break;
                    }
                    i++;
                }

                // Check for type suffix
                if (i < input.Length && (input[i] == 'f' || input[i] == 'F'))
                {
                    i++;
                    tokens.Add(new Token(TokenKind.FloatLiteral, input.Substring(start, i - start)));
                }
                else if (i < input.Length && (input[i] == 'd' || input[i] == 'D'))
                {
                    i++;
                    tokens.Add(new Token(TokenKind.DoubleLiteral, input.Substring(start, i - start)));
                }
                else if (i < input.Length && (input[i] == 'L' || input[i] == 'l'))
                {
                    i++;
                    tokens.Add(new Token(TokenKind.IntLiteral, input.Substring(start, i - start)));
                }
                else if (hasDot)
                {
                    tokens.Add(new Token(TokenKind.DoubleLiteral, input.Substring(start, i - start)));
                }
                else
                {
                    tokens.Add(new Token(TokenKind.IntLiteral, input.Substring(start, i - start)));
                }
                continue;
            }

            // Identifier or keyword
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_'))
                    i++;

                var word = input.Substring(start, i - start);

                switch (word)
                {
                    case "true":
                        tokens.Add(new Token(TokenKind.BoolLiteral, "true"));
                        break;
                    case "false":
                        tokens.Add(new Token(TokenKind.BoolLiteral, "false"));
                        break;
                    case "null":
                        tokens.Add(new Token(TokenKind.NullLiteral, "null"));
                        break;
                    case "typeof":
                        tokens.Add(new Token(TokenKind.Typeof, "typeof"));
                        break;
                    default:
                        tokens.Add(new Token(TokenKind.Identifier, word));
                        break;
                }
                continue;
            }

            throw new EvalException($"Unexpected character: '{c}'");
        }

        return tokens;
    }

    #endregion

    #region Parser

    internal abstract class AstNode { }

    internal class IdentifierNode : AstNode
    {
        public string Name { get; }
        public IdentifierNode(string name) { Name = name; }
    }

    internal class MemberAccessNode : AstNode
    {
        public AstNode Target { get; }
        public string Member { get; }
        public MemberAccessNode(AstNode target, string member) { Target = target; Member = member; }
    }

    internal class MethodCallNode : AstNode
    {
        public AstNode Target { get; }
        public string Method { get; }
        public AstNode[] Arguments { get; }
        public string[]? GenericTypeNames { get; }
        public MethodCallNode(AstNode target, string method, AstNode[] arguments, string[]? genericTypeNames = null)
        {
            Target = target;
            Method = method;
            Arguments = arguments;
            GenericTypeNames = genericTypeNames;
        }
    }

    internal class LiteralNode : AstNode
    {
        public object? Value { get; }
        public LiteralNode(object? value) { Value = value; }
    }

    internal class TypeofNode : AstNode
    {
        public string TypeName { get; }
        public TypeofNode(string typeName) { TypeName = typeName; }
    }

    internal class NegationNode : AstNode
    {
        public AstNode Operand { get; }
        public NegationNode(AstNode operand) { Operand = operand; }
    }

    internal class Parser
    {
        private readonly List<Token> _tokens;
        public int Position { get; private set; }

        public Parser(List<Token> tokens)
        {
            _tokens = tokens;
            Position = 0;
        }

        private Token? Peek() => Position < _tokens.Count ? _tokens[Position] : null;
        private Token Consume()
        {
            if (Position >= _tokens.Count)
                throw new EvalException("Unexpected end of expression");
            return _tokens[Position++];
        }
        private Token Expect(TokenKind kind)
        {
            var token = Consume();
            if (token.Kind != kind)
                throw new EvalException($"Expected {kind}, got {token.Kind} ('{token.Value}')");
            return token;
        }

        public AstNode ParseExpression()
        {
            var node = ParsePrimary();
            return ParseMemberChain(node);
        }

        private AstNode ParsePrimary()
        {
            var token = Peek();
            if (token == null)
                throw new EvalException("Unexpected end of expression");

            switch (token.Kind)
            {
                case TokenKind.Minus:
                    Consume();
                    var operand = ParsePrimary();
                    return new NegationNode(operand);

                case TokenKind.StringLiteral:
                    Consume();
                    return new LiteralNode(token.Value);

                case TokenKind.CharLiteral:
                    Consume();
                    return new LiteralNode(token.Value[0]);

                case TokenKind.IntLiteral:
                    Consume();
                    var intStr = token.Value.TrimEnd('L', 'l');
                    if (long.TryParse(intStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
                    {
                        if (longVal >= int.MinValue && longVal <= int.MaxValue && !token.Value.EndsWith("L") && !token.Value.EndsWith("l"))
                            return new LiteralNode((int)longVal);
                        return new LiteralNode(longVal);
                    }
                    throw new EvalException($"Invalid integer: {token.Value}");

                case TokenKind.FloatLiteral:
                    Consume();
                    var floatStr = token.Value.TrimEnd('f', 'F');
                    if (float.TryParse(floatStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatVal))
                        return new LiteralNode(floatVal);
                    throw new EvalException($"Invalid float: {token.Value}");

                case TokenKind.DoubleLiteral:
                    Consume();
                    var doubleStr = token.Value.TrimEnd('d', 'D');
                    if (double.TryParse(doubleStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleVal))
                        return new LiteralNode(doubleVal);
                    throw new EvalException($"Invalid double: {token.Value}");

                case TokenKind.BoolLiteral:
                    Consume();
                    return new LiteralNode(token.Value == "true");

                case TokenKind.NullLiteral:
                    Consume();
                    return new LiteralNode(null);

                case TokenKind.Typeof:
                    Consume();
                    Expect(TokenKind.LParen);
                    var typeName = ParseQualifiedName();
                    Expect(TokenKind.RParen);
                    return new TypeofNode(typeName);

                case TokenKind.Identifier:
                    Consume();
                    return new IdentifierNode(token.Value);

                case TokenKind.LParen:
                    Consume();
                    var inner = ParseExpression();
                    Expect(TokenKind.RParen);
                    return inner;

                default:
                    throw new EvalException($"Unexpected token: {token.Kind} ('{token.Value}')");
            }
        }

        private AstNode ParseMemberChain(AstNode node)
        {
            while (Peek()?.Kind == TokenKind.Dot)
            {
                Consume(); // consume dot
                var memberToken = Expect(TokenKind.Identifier);

                // Check for generic type arguments: .Method<Type>(...)
                string[]? genericTypeNames = null;
                if (Peek()?.Kind == TokenKind.LAngle)
                {
                    genericTypeNames = ParseGenericTypeArgs();
                }

                // Check if this is a method call
                if (Peek()?.Kind == TokenKind.LParen)
                {
                    var args = ParseArgumentList();
                    node = new MethodCallNode(node, memberToken.Value, args, genericTypeNames);
                }
                else
                {
                    node = new MemberAccessNode(node, memberToken.Value);
                }
            }
            return node;
        }

        private string[] ParseGenericTypeArgs()
        {
            Expect(TokenKind.LAngle);
            var types = new List<string>();
            types.Add(ParseQualifiedName());
            while (Peek()?.Kind == TokenKind.Comma)
            {
                Consume();
                types.Add(ParseQualifiedName());
            }
            Expect(TokenKind.RAngle);
            return types.ToArray();
        }

        private string ParseQualifiedName()
        {
            var name = Expect(TokenKind.Identifier).Value;
            while (Peek()?.Kind == TokenKind.Dot)
            {
                Consume();
                name += "." + Expect(TokenKind.Identifier).Value;
            }
            return name;
        }

        private AstNode[] ParseArgumentList()
        {
            Expect(TokenKind.LParen);
            var args = new List<AstNode>();

            if (Peek()?.Kind != TokenKind.RParen)
            {
                args.Add(ParseExpression());
                while (Peek()?.Kind == TokenKind.Comma)
                {
                    Consume();
                    args.Add(ParseExpression());
                }
            }

            Expect(TokenKind.RParen);
            return args.ToArray();
        }
    }

    #endregion

    #region Evaluator

    private static object? EvaluateNode(AstNode node, string[] namespaces)
    {
        switch (node)
        {
            case LiteralNode literal:
                return literal.Value;

            case NegationNode negation:
                var val = EvaluateNode(negation.Operand, namespaces);
                if (val is int intV) return -intV;
                if (val is long longV) return -longV;
                if (val is float floatV) return -floatV;
                if (val is double doubleV) return -doubleV;
                throw new EvalException($"Cannot negate value of type {val?.GetType().Name ?? "null"}");

            case TypeofNode typeofNode:
                var resolvedType = ResolveType(typeofNode.TypeName, namespaces);
                if (resolvedType == null)
                    throw new EvalException($"Type not found: '{typeofNode.TypeName}'");
                return resolvedType;

            case IdentifierNode ident:
                // Try to resolve as a type first
                var type = ResolveType(ident.Name, namespaces);
                if (type != null)
                    return new TypeReference(type);
                throw new EvalException($"Unknown identifier: '{ident.Name}'. Could not resolve as a type. " +
                    "Available namespaces: " + string.Join(", ", namespaces));

            case MemberAccessNode member:
                return EvaluateMemberAccess(member, namespaces);

            case MethodCallNode method:
                return EvaluateMethodCall(method, namespaces);

            default:
                throw new EvalException($"Unsupported expression node: {node.GetType().Name}");
        }
    }

    private static object? EvaluateMemberAccess(MemberAccessNode node, string[] namespaces)
    {
        var target = EvaluateNode(node.Target, namespaces);

        // If target is a TypeReference, access static member
        if (target is TypeReference typeRef)
        {
            // First check if "Type.Member" could be a nested type or a type in a namespace
            var nestedType = typeRef.Type.GetNestedType(node.Member, BindingFlags.Public | BindingFlags.NonPublic);
            if (nestedType != null)
                return new TypeReference(nestedType);

            // Try to resolve as Type.SubType (e.g., "Application.platform" could be a member,
            // but "RuntimePlatform.Android" is an enum value)
            var prop = typeRef.Type.GetProperty(node.Member, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (prop != null)
                return prop.GetValue(null);

            var field = typeRef.Type.GetField(node.Member, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field != null)
                return field.GetValue(null);

            throw new EvalException($"Static member '{node.Member}' not found on type '{typeRef.Type.FullName}'");
        }

        if (target == null)
            throw new EvalException($"Cannot access member '{node.Member}' on null");

        // Instance member access
        var targetType = target.GetType();

        var instanceProp = targetType.GetProperty(node.Member, BindingFlags.Public | BindingFlags.Instance);
        if (instanceProp != null)
            return instanceProp.GetValue(target);

        var instanceField = targetType.GetField(node.Member, BindingFlags.Public | BindingFlags.Instance);
        if (instanceField != null)
            return instanceField.GetValue(target);

        throw new EvalException($"Member '{node.Member}' not found on type '{targetType.FullName}'");
    }

    private static object? EvaluateMethodCall(MethodCallNode node, string[] namespaces)
    {
        var target = EvaluateNode(node.Target, namespaces);
        var argValues = node.Arguments.Select(a => EvaluateNode(a, namespaces)).ToArray();
        // Unwrap any TypeReference args to their actual values (but not for typeof results which should stay as Type)
        var invokeArgs = argValues.Select(a => a is TypeReference tr ? tr.Type : a).ToArray();
        var argTypes = invokeArgs.Select(a => a?.GetType()).ToArray();

        bool isStatic = target is TypeReference;
        var searchType = isStatic ? ((TypeReference)target).Type : target.GetType();
        var bindingFlags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.FlattenHierarchy;

        // Resolve generic type arguments if present
        Type[]? genericTypeArgs = null;
        if (node.GenericTypeNames != null)
        {
            genericTypeArgs = new Type[node.GenericTypeNames.Length];
            for (int i = 0; i < node.GenericTypeNames.Length; i++)
            {
                var resolved = ResolveType(node.GenericTypeNames[i], namespaces);
                if (resolved == null)
                    throw new EvalException($"Generic type not found: '{node.GenericTypeNames[i]}'");
                genericTypeArgs[i] = resolved;
            }
        }

        // Find matching method
        var methods = searchType.GetMethods(bindingFlags)
            .Where(m => m.Name == node.Method)
            .Where(m => m.GetParameters().Length == invokeArgs.Length)
            .ToArray();

        if (genericTypeArgs != null)
        {
            methods = methods
                .Where(m => m.IsGenericMethodDefinition && m.GetGenericArguments().Length == genericTypeArgs.Length)
                .ToArray();
        }
        else
        {
            // Prefer non-generic methods when no generic args specified
            var nonGeneric = methods.Where(m => !m.IsGenericMethodDefinition).ToArray();
            if (nonGeneric.Length > 0)
                methods = nonGeneric;
        }

        if (methods.Length == 0)
            throw new EvalException(
                $"Method '{node.Method}' with {invokeArgs.Length} argument(s) not found on type '{searchType.FullName}' " +
                $"(searched {(isStatic ? "static" : "instance")} methods)");

        // Try to find best match by parameter types
        MethodInfo? bestMatch = null;
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            bool matches = true;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (invokeArgs[i] == null)
                {
                    if (parameters[i].ParameterType.IsValueType)
                        matches = false;
                }
                else if (!parameters[i].ParameterType.IsAssignableFrom(invokeArgs[i]!.GetType()))
                {
                    // Try implicit conversion for numeric types
                    if (!TryConvertArg(invokeArgs[i]!, parameters[i].ParameterType, out _))
                        matches = false;
                }
            }
            if (matches)
            {
                bestMatch = method;
                break;
            }
        }

        bestMatch ??= methods[0]; // Fall back to first match

        // Apply generic type arguments
        if (genericTypeArgs != null && bestMatch.IsGenericMethodDefinition)
            bestMatch = bestMatch.MakeGenericMethod(genericTypeArgs);

        // Convert arguments to match parameter types
        var finalArgs = new object?[invokeArgs.Length];
        var methodParams = bestMatch.GetParameters();
        for (int i = 0; i < invokeArgs.Length; i++)
        {
            if (invokeArgs[i] != null && !methodParams[i].ParameterType.IsAssignableFrom(invokeArgs[i]!.GetType()))
            {
                if (TryConvertArg(invokeArgs[i]!, methodParams[i].ParameterType, out var converted))
                    finalArgs[i] = converted;
                else
                    finalArgs[i] = invokeArgs[i];
            }
            else
            {
                finalArgs[i] = invokeArgs[i];
            }
        }

        return bestMatch.Invoke(isStatic ? null : target, finalArgs);
    }

    private static bool TryConvertArg(object value, Type targetType, out object? converted)
    {
        converted = null;
        try
        {
            if (targetType == typeof(float) && value is int intV) { converted = (float)intV; return true; }
            if (targetType == typeof(float) && value is double dblV) { converted = (float)dblV; return true; }
            if (targetType == typeof(double) && value is int intV2) { converted = (double)intV2; return true; }
            if (targetType == typeof(double) && value is float fltV) { converted = (double)fltV; return true; }
            if (targetType == typeof(long) && value is int intV3) { converted = (long)intV3; return true; }
            if (targetType == typeof(string) && value != null) { converted = value.ToString(); return true; }
            return false;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Type Resolution

    /// <summary>
    /// Resolves a type name by searching loaded assemblies across configured namespaces.
    /// </summary>
    internal static Type? ResolveType(string name, string[] namespaces)
    {
        // Try fully-qualified name first
        var type = Type.GetType(name);
        if (type != null) return type;

        // Search loaded assemblies
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            // Try exact name
            type = assembly.GetType(name);
            if (type != null) return type;

            // Try with each namespace prefix
            foreach (var ns in namespaces)
            {
                type = assembly.GetType($"{ns}.{name}");
                if (type != null) return type;
            }
        }

        return null;
    }

    #endregion

    /// <summary>
    /// Wrapper to distinguish a Type object used as a reflection target
    /// (e.g., Screen in Screen.width) from a Type value (e.g., typeof(Screen)).
    /// </summary>
    internal class TypeReference
    {
        public Type Type { get; }
        public TypeReference(Type type) { Type = type; }
        public override string ToString() => Type.FullName ?? Type.Name;
    }
}

public class EvalResult
{
    public bool Success { get; private set; }
    public object? Value { get; private set; }
    public string? Error { get; private set; }
    public string? ResultType { get; private set; }

    public static EvalResult Ok(object? value)
    {
        // Unwrap TypeReference to actual Type for return
        if (value is ExpressionEvaluator.TypeReference typeRef)
            value = typeRef.Type;

        string? resultType = null;
        if (value != null)
            resultType = value.GetType().FullName ?? value.GetType().Name;

        return new EvalResult { Success = true, Value = value, ResultType = resultType };
    }

    public static EvalResult Fail(string error)
    {
        return new EvalResult { Success = false, Error = error };
    }

    public string? SerializeValue()
    {
        if (!Success || Value == null)
            return null;

        try
        {
            return JsonConvert.SerializeObject(Value, Formatting.Indented);
        }
        catch
        {
            return Value.ToString();
        }
    }
}

public class EvalException : Exception
{
    public EvalException(string message) : base(message) { }
}
