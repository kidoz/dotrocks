using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Xunit;

namespace DotRocks.PackageTests;

public sealed class PublicApiBaselineTests
{
    private static readonly ApiAssembly[] ApiAssemblies =
    [
        new("DotRocks.Data", "net10.0"),
        new("DotRocks.EntityFrameworkCore", "net10.0"),
        new("DotRocks.EntityFrameworkCore.Design", "net10.0"),
        new("DotRocks.Analyzers", "netstandard2.0"),
        new("DotRocks.Analyzers.CodeFixes", "netstandard2.0"),
    ];

    [Theory]
    [MemberData(nameof(PublicApiAssemblies))]
    public void PublicApi_MatchesBaseline(string assemblyName, string targetFramework)
    {
        string root = FindRepositoryRoot();
        string assemblyPath = Path.Combine(
            root,
            "src",
            assemblyName,
            "bin",
            GetAssemblyConfiguration(),
            targetFramework,
            assemblyName + ".dll"
        );
        string baselinePath = Path.Combine(
            root,
            "tests",
            "DotRocks.PackageTests",
            "PublicApi",
            assemblyName + ".txt"
        );
        string actual = GeneratePublicApi(assemblyPath);

        if (
            string.Equals(
                Environment.GetEnvironmentVariable("DOTROCKS_UPDATE_PUBLIC_API"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, actual);
        }

        Assert.True(File.Exists(baselinePath), "Missing public API baseline: " + baselinePath);
        string expected = File.ReadAllText(baselinePath);
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    public static TheoryData<string, string> PublicApiAssemblies() =>
        new(ApiAssemblies.Select(assembly => (assembly.AssemblyName, assembly.TargetFramework)));

    private static string GeneratePublicApi(string assemblyPath)
    {
        Assert.True(File.Exists(assemblyPath), "Assembly not found: " + assemblyPath);
        var context = new SnapshotAssemblyLoadContext(assemblyPath);
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);
            var builder = new StringBuilder();
            builder.AppendLine("# " + assembly.GetName().Name);
            builder.AppendLine();

            foreach (
                Type type in assembly
                    .GetExportedTypes()
                    .OrderBy(type => type.FullName, StringComparer.Ordinal)
            )
            {
                if (type.IsSpecialName)
                {
                    continue;
                }

                builder.AppendLine(GetTypeSignature(type));

                foreach (ConstructorInfo constructor in GetDeclaredConstructors(type))
                {
                    builder.AppendLine("  " + GetConstructorSignature(type, constructor));
                }

                foreach (EventInfo eventInfo in GetDeclaredEvents(type))
                {
                    builder.AppendLine("  " + GetEventSignature(eventInfo));
                }

                foreach (FieldInfo field in GetDeclaredFields(type))
                {
                    builder.AppendLine("  " + GetFieldSignature(field));
                }

                foreach (PropertyInfo property in GetDeclaredProperties(type))
                {
                    builder.AppendLine("  " + GetPropertySignature(property));
                }

                foreach (MethodInfo method in GetDeclaredMethods(type))
                {
                    builder.AppendLine("  " + GetMethodSignature(method));
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }
        finally
        {
            context.Unload();
        }
    }

    private static IEnumerable<ConstructorInfo> GetDeclaredConstructors(Type type) =>
        type.GetConstructors(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly
            )
            .Where(IsVisible)
            .OrderBy(
                constructor => GetConstructorSignature(type, constructor),
                StringComparer.Ordinal
            );

    private static IEnumerable<EventInfo> GetDeclaredEvents(Type type) =>
        type.GetEvents(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Where(eventInfo => IsVisible(eventInfo.AddMethod) || IsVisible(eventInfo.RemoveMethod))
            .OrderBy(eventInfo => eventInfo.Name, StringComparer.Ordinal);

    private static IEnumerable<FieldInfo> GetDeclaredFields(Type type) =>
        type.GetFields(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Where(field => IsVisible(field) && !field.IsSpecialName)
            .OrderBy(field => field.Name, StringComparer.Ordinal);

    private static IEnumerable<PropertyInfo> GetDeclaredProperties(Type type) =>
        type.GetProperties(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Where(property => IsVisible(property.GetMethod) || IsVisible(property.SetMethod))
            .OrderBy(property => property.Name, StringComparer.Ordinal);

    private static IEnumerable<MethodInfo> GetDeclaredMethods(Type type) =>
        type.GetMethods(
                BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly
            )
            .Where(method => IsVisible(method) && !method.IsSpecialName)
            .OrderBy(method => GetMethodSignature(method), StringComparer.Ordinal);

    private static string GetTypeSignature(Type type)
    {
        var parts = new List<string>();
        parts.Add(type.IsNested ? GetNestedVisibility(type) : GetTopLevelVisibility(type));
        if (type.IsAbstract && type.IsSealed)
        {
            parts.Add("static");
        }
        else if (type.IsAbstract && !type.IsInterface)
        {
            parts.Add("abstract");
        }
        else if (type.IsSealed && type.IsClass)
        {
            parts.Add("sealed");
        }

        parts.Add(GetTypeKind(type));
        parts.Add(GetTypeName(type));

        Type? baseType = type.BaseType;
        if (
            baseType is not null
            && baseType != typeof(object)
            && !type.IsEnum
            && !type.IsValueType
            && !type.IsInterface
        )
        {
            parts.Add(": " + FormatTypeName(baseType));
        }

        Type[] interfaces = type.GetInterfaces()
            .Where(candidate =>
                type.BaseType is null || !type.BaseType.GetInterfaces().Contains(candidate)
            )
            .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal)
            .ToArray();
        if (interfaces.Length > 0)
        {
            string prefix = baseType is not null && baseType != typeof(object) ? ", " : ": ";
            parts.Add(prefix + string.Join(", ", interfaces.Select(FormatTypeName)));
        }

        return string.Join(" ", parts).Replace(" : ", " ", StringComparison.Ordinal);
    }

    private static string GetConstructorSignature(
        Type declaringType,
        ConstructorInfo constructor
    ) =>
        $"{GetVisibility(constructor)} {GetTypeName(declaringType)}({FormatParameters(constructor.GetParameters())})";

    private static string GetEventSignature(EventInfo eventInfo) =>
        $"{GetVisibility(eventInfo.AddMethod ?? eventInfo.RemoveMethod)} event {FormatTypeName(eventInfo.EventHandlerType!)} {eventInfo.Name}";

    private static string GetFieldSignature(FieldInfo field)
    {
        string modifiers = field.IsStatic ? " static" : string.Empty;
        return $"{GetVisibility(field)}{modifiers} field {FormatTypeName(field.FieldType)} {field.Name}";
    }

    private static string GetPropertySignature(PropertyInfo property)
    {
        MethodInfo? accessor = property.GetMethod ?? property.SetMethod;
        string modifiers = accessor?.IsStatic == true ? " static" : string.Empty;
        string accessors =
            (
                property.GetMethod is not null && IsVisible(property.GetMethod)
                    ? "get;"
                    : string.Empty
            )
            + (
                property.SetMethod is not null && IsVisible(property.SetMethod)
                    ? " set;"
                    : string.Empty
            );
        return $"{GetVisibility(accessor)}{modifiers} property {FormatTypeName(property.PropertyType)} {property.Name} {{ {accessors.Trim()} }}";
    }

    private static string GetMethodSignature(MethodInfo method)
    {
        string modifiers =
            method.IsStatic ? " static"
            : method.IsAbstract ? " abstract"
            : method.IsVirtual ? " virtual"
            : string.Empty;
        return $"{GetVisibility(method)}{modifiers} method {FormatTypeName(method.ReturnType)} {GetMethodName(method)}({FormatParameters(method.GetParameters())})";
    }

    private static string GetMethodName(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? method.Name
                + "<"
                + string.Join(", ", method.GetGenericArguments().Select(argument => argument.Name))
                + ">"
            : method.Name;

    private static string FormatParameters(ParameterInfo[] parameters) =>
        string.Join(
            ", ",
            parameters.Select(parameter =>
                (
                    parameter.IsOut ? "out "
                    : parameter.ParameterType.IsByRef ? "ref "
                    : string.Empty
                )
                + FormatTypeName(
                    parameter.ParameterType.IsByRef
                        ? parameter.ParameterType.GetElementType()!
                        : parameter.ParameterType
                )
                + " "
                + parameter.Name
            )
        );

    private static string FormatTypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsArray)
        {
            return FormatTypeName(type.GetElementType()!) + "[]";
        }

        Type nullableUnderlyingType = Nullable.GetUnderlyingType(type)!;
        if (nullableUnderlyingType is not null)
        {
            return FormatTypeName(nullableUnderlyingType) + "?";
        }

        if (type.IsGenericType)
        {
            string name = (type.FullName ?? type.Name).Replace('+', '.');
            int tick = name.IndexOf('`', StringComparison.Ordinal);
            if (tick >= 0)
            {
                name = name[..tick];
            }

            return name
                + "<"
                + string.Join(", ", type.GetGenericArguments().Select(FormatTypeName))
                + ">";
        }

        return (type.FullName ?? type.Name).Replace("+", ".", StringComparison.Ordinal);
    }

    private static string GetTypeName(Type type)
    {
        string name = type.FullName ?? type.Name;
        name = name.Replace("+", ".", StringComparison.Ordinal);
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name =
                name[..tick]
                + "<"
                + string.Join(", ", type.GetGenericArguments().Select(argument => argument.Name))
                + ">";
        }

        return name;
    }

    private static string GetTypeKind(Type type)
    {
        if (type.IsInterface)
        {
            return "interface";
        }

        if (type.IsEnum)
        {
            return "enum";
        }

        return type.IsValueType ? "struct" : "class";
    }

    private static bool IsVisible(MethodBase? method) =>
        method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

    private static bool IsVisible(FieldInfo field) =>
        field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;

    private static string GetVisibility(MethodBase? method)
    {
        if (method?.IsPublic == true)
        {
            return "public";
        }

        return "protected";
    }

    private static string GetVisibility(FieldInfo field) => field.IsPublic ? "public" : "protected";

    private static string GetTopLevelVisibility(Type type) => type.IsPublic ? "public" : "internal";

    private static string GetNestedVisibility(Type type) =>
        type.IsNestedPublic ? "public" : "protected";

    private static string NormalizeLineEndings(string value) =>
        // Normalize CRLF and trailing blank lines so an editor that trims the final newline does
        // not cause a spurious baseline mismatch.
        value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n') + "\n";

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotRocks.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DotRocks.slnx.");
    }

    private static string GetAssemblyConfiguration() =>
        typeof(PublicApiBaselineTests)
            .Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration
        ?? "Debug";

    private sealed record ApiAssembly(string AssemblyName, string TargetFramework);

    private sealed class SnapshotAssemblyLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public SnapshotAssemblyLoadContext(string assemblyPath)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(assemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
        }
    }
}
