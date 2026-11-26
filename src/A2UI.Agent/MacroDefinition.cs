using System.Text.Json.Nodes;

namespace A2UI.Agent;

/// <summary>The type of a macro parameter. It selects the schema written to a derived catalog and the coercion applied to arguments.</summary>
public enum MacroParameterType
{
    /// <summary>A string. Data bindings and function calls are rejected.</summary>
    String,
    /// <summary>An integer. Booleans and bindings are rejected.</summary>
    Integer,
    /// <summary>A number. Booleans and bindings are rejected.</summary>
    Number,
    /// <summary>A boolean. In string interpolation it is written as <c>true</c> or <c>false</c>.</summary>
    Boolean,
    /// <summary>A reference to one component by ID.</summary>
    Component,
    /// <summary>A static list of component IDs.</summary>
    ComponentList,
    /// <summary>A static list of component IDs, or a template that repeats a component over a collection.</summary>
    ChildList,
    /// <summary>An action. An event name, an event or function call shorthand, or a full action is accepted.</summary>
    Action,
    /// <summary>A string literal, a data binding or a function call.</summary>
    DynamicString,
    /// <summary>A number literal, a data binding or a function call.</summary>
    DynamicNumber,
    /// <summary>A boolean literal, a data binding or a function call.</summary>
    DynamicBoolean,
    /// <summary>A list of strings, a data binding or a function call.</summary>
    DynamicStringList,
    /// <summary>Any value. Bindings and function calls are kept, and every other object is kept as a literal object.</summary>
    DynamicValue,
    /// <summary>A list of check rules.</summary>
    CheckRuleList,
    /// <summary>Accessibility attributes passed to the macro instead of being merged onto the root component.</summary>
    Accessibility
}

/// <summary>Declares one argument of a macro.</summary>
/// <param name="Name">The property name on the macro component.</param>
/// <param name="Type">The type of the argument.</param>
public sealed record MacroParameter(string Name, MacroParameterType Type)
{
    /// <summary>Whether the argument must be supplied when it has no default. The default is <see langword="true"/>.</summary>
    public bool Required { get; init; } = true;

    /// <summary>The value used when the argument is omitted, or <see langword="null"/> when there is none.</summary>
    public JsonNode? DefaultValue { get; init; }

    /// <summary>The permitted values of a <see cref="MacroParameterType.String"/> argument, or <see langword="null"/> for any string.</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
}

/// <summary>The coerced arguments of one macro call, passed to <see cref="MacroBody"/>.</summary>
public sealed class MacroInvocation
{
    internal MacroInvocation(string name, string id, JsonObject arguments) =>
        (Name, Id, Arguments) = (name, id, arguments);

    /// <summary>The macro name.</summary>
    public string Name { get; }

    /// <summary>The component ID of the call. The root component of the expansion receives this ID.</summary>
    public string Id { get; }

    /// <summary>
    /// The coerced arguments by parameter name. Omitted arguments with no default are absent. The body owns this object and may change it.
    /// </summary>
    public JsonObject Arguments { get; }
}

/// <summary>The outcome of a <see cref="MacroBody"/>.</summary>
public sealed class MacroResult
{
    private MacroResult(JsonObject? root, string? error) => (Root, Error) = (root, error);

    internal JsonObject? Root { get; }

    internal string? Error { get; }

    /// <summary>Returns a component tree for the macro.</summary>
    /// <param name="root">
    /// The root component. Each node is an object with a <c>component</c> name. A property that holds a node, or an array of nodes, is
    /// replaced by the generated ID of that node. Use <c>catalogId</c>, <c>accessibility</c>, <c>metadata</c> and <c>checks</c> for the
    /// component envelope members. They are written with their <c>@</c> prefix.
    /// </param>
    /// <returns>A successful result.</returns>
    public static MacroResult Success(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new(root, null);
    }

    /// <summary>Reports that the arguments are not acceptable to the macro.</summary>
    /// <param name="message">A description of the problem.</param>
    /// <returns>A failed result. The expander treats it as an error in the macro call.</returns>
    public static MacroResult Failure(string message)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        return new(null, message);
    }
}

/// <summary>Builds the component tree for a macro call.</summary>
/// <param name="invocation">The coerced arguments.</param>
/// <returns>The component tree, or a failure for arguments the macro rejects.</returns>
public delegate MacroResult MacroBody(MacroInvocation invocation);

/// <summary>A macro that an agent can use as a component. The expander replaces each call with its expansion.</summary>
public sealed class MacroDefinition
{
    private readonly MacroParameter[] _parameters;

    /// <summary>Creates a macro with a code body.</summary>
    /// <param name="name">The component name that calls the macro. It must be a UAX #31 identifier, and must not be <c>Surface</c>.</param>
    /// <param name="parameters">The declared arguments.</param>
    /// <param name="body">Produces the component tree for a call.</param>
    public MacroDefinition(string name, IEnumerable<MacroParameter> parameters, MacroBody body)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(body);
        Name = name;
        _parameters = parameters.ToArray();
        Body = body;
    }

    /// <summary>The component name that calls the macro.</summary>
    public string Name { get; }

    /// <summary>
    /// The catalog that owns the macro, or <see langword="null"/> for the default macro catalog. A macro in a named catalog is expanded only
    /// where the effective catalog of the component is that catalog.
    /// </summary>
    public string? CatalogId { get; init; }

    /// <summary>The A2UI protocol version the macro targets. Only <c>1.0</c> is supported. The default is <c>1.0</c>.</summary>
    public string ProtocolVersion { get; init; } = "1.0";

    /// <summary>A description for the derived catalog, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>The declared arguments in order.</summary>
    public IReadOnlyList<MacroParameter> Parameters => _parameters;

    /// <summary>Produces the component tree for a call.</summary>
    public MacroBody Body { get; }

    /// <summary>Creates a macro whose body is a declarative template.</summary>
    /// <param name="name">The component name that calls the macro.</param>
    /// <param name="parameters">The declared arguments.</param>
    /// <param name="template">
    /// The root node of the expansion. <c>{"$param": "name"}</c> inserts an argument, and omits the property or array item when the argument
    /// is absent. <c>{"$spreadParam": "name"}</c> inserts the items of a list argument into an array. <c>${name}</c> in a string inserts a
    /// primitive argument, and <c>${name.member}</c> reads a member of an object argument. Any other <c>${...}</c> text is left for the
    /// renderer.
    /// </param>
    /// <returns>The macro. The template is copied.</returns>
    public static MacroDefinition FromTemplate(string name, IEnumerable<MacroParameter> parameters, JsonObject template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var declared = parameters.ToArray();
        var copy = (JsonObject)template.DeepClone();
        return new(name, declared, invocation => MacroTemplate.Expand(copy, declared, invocation));
    }
}
