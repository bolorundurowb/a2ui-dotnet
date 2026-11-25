using System.Globalization;
using System.Reflection;

internal static class PublicApi
{
    private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static void Write(string path, Assembly assembly)
    {
        var signatures = new List<string>();
        var nullability = new NullabilityInfoContext();
        foreach (var type in assembly.GetTypes().Where(IsVisible))
        {
            var interfaces = string.Join(", ", type.GetInterfaces().Select(i => i.ToString()).Order(StringComparer.Ordinal));
            signatures.Add($"T {type} [{type.Attributes}] : {type.BaseType} [{interfaces}]{Constraints(type.GetGenericArguments())}");

            foreach (var method in type.GetMethods(DeclaredMembers).Where(IsVisible))
            {
                signatures.Add($"M {type}.{method.Name} [{method.Attributes}] -> {FormatType(nullability.Create(method.ReturnParameter))}" +
                    $" ({Parameters(method.GetParameters(), nullability)}){Constraints(method.GetGenericArguments())}");
            }

            foreach (var constructor in type.GetConstructors(DeclaredMembers).Where(IsVisible))
            {
                signatures.Add($"C {type} [{constructor.Attributes}] ({Parameters(constructor.GetParameters(), nullability)})");
            }

            foreach (var field in type.GetFields(DeclaredMembers).Where(f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly))
            {
                var constant = field.IsLiteral ? $" = {Constant(field.GetRawConstantValue())}" : string.Empty;
                signatures.Add($"F {type}.{field.Name} [{field.Attributes}] : {FormatType(nullability.Create(field))}{constant}");
            }

            foreach (var property in type.GetProperties(DeclaredMembers).Where(p => p.GetAccessors(true).Any(IsVisible)))
            {
                var index = Parameters(property.GetIndexParameters(), nullability);
                var init = property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true ? " init" : string.Empty;
                signatures.Add($"P {type}.{property.Name} : {FormatType(nullability.Create(property))} ({index}){init}");
            }

            foreach (var @event in type.GetEvents(DeclaredMembers).Where(e => e.AddMethod is { } add && IsVisible(add)))
            {
                signatures.Add($"E {type}.{@event.Name} : {FormatType(nullability.Create(@event))}");
            }
        }

        signatures.Sort(StringComparer.Ordinal);
        File.WriteAllText(path, string.Join('\n', signatures) + "\n");
    }

    private static bool IsVisible(Type type) => type.IsNested
        ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem) && IsVisible(type.DeclaringType!)
        : type.IsPublic;

    private static bool IsVisible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static string Parameters(IEnumerable<ParameterInfo> parameters, NullabilityInfoContext nullability) =>
        string.Join(", ", parameters.Select(parameter =>
        {
            var defaultValue = parameter.HasDefaultValue ? $" = {Constant(parameter.RawDefaultValue)}" : string.Empty;
            var attributes = parameter.GetCustomAttributesData().Any(attribute => attribute.AttributeType == typeof(ParamArrayAttribute))
                ? $"{parameter.Attributes}, ParamArray"
                : parameter.Attributes.ToString();
            return $"{FormatType(nullability.Create(parameter))} {parameter.Name} [{attributes}]{defaultValue}";
        }));

    private static string FormatType(NullabilityInfo info)
    {
        var runtimeType = info.Type.IsByRef ? info.Type.GetElementType()! : info.Type;
        string type;
        if (info.ElementType is { } element)
        {
            var rank = runtimeType.GetArrayRank();
            var dimensions = runtimeType.IsSZArray ? string.Empty : rank == 1 ? "*" : new string(',', rank - 1);
            type = $"{FormatType(element)}[{dimensions}]";
        }
        else if (info.GenericTypeArguments.Length > 0)
        {
            var definition = runtimeType.GetGenericTypeDefinition();
            type = $"{definition}[{string.Join(", ", info.GenericTypeArguments.Select(FormatType))}]";
        }
        else
        {
            type = runtimeType.ToString();
        }

        if (info.ReadState == NullabilityState.Nullable && !runtimeType.IsValueType)
        {
            type += "?";
        }

        return info.Type.IsByRef ? type + "&" : type;
    }

    private static string Constraints(IEnumerable<Type> arguments) => string.Concat(arguments.Where(t => t.IsGenericParameter)
        .Select(t => $" <{t.Name} [{t.GenericParameterAttributes}] : " +
            $"{string.Join(", ", t.GetGenericParameterConstraints().Select(c => c.ToString()).Order(StringComparer.Ordinal))}>"));

    private static string Constant(object? value) => value is string text
        ? System.Text.Json.JsonSerializer.Serialize(text)
        : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
}
