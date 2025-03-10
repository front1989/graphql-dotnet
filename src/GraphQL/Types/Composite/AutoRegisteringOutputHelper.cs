using System.Linq.Expressions;
using System.Reflection;
using GraphQL.Resolvers;

namespace GraphQL.Types;

/// <summary>
/// Helper methods for <see cref="AutoRegisteringObjectGraphType{TSourceType}"/> and <see cref="AutoRegisteringInterfaceGraphType{TSourceType}"/>.
/// </summary>
internal static class AutoRegisteringOutputHelper
{
    /// <summary>
    /// Configures query arguments and a field resolver for the specified <see cref="FieldType"/>, overwriting
    /// any existing configuration within <see cref="FieldType.Arguments"/>, <see cref="FieldType.Resolver"/>
    /// and <see cref="FieldType.StreamResolver"/>.
    /// <br/><br/>
    /// For fields and properties, no query arguments are added and the field resolver simply pulls the appropriate
    /// member from <see cref="IResolveFieldContext.Source"/>.
    /// <br/><br/>
    /// For methods, method arguments are iterated and processed by
    /// <see cref="AutoRegisteringObjectGraphType{TSourceType}.GetArgumentInformation{TParameterType}(FieldType, ParameterInfo)">GetArgumentInformation</see>, building
    /// a list of query arguments and expressions as necessary. Then a field resolver is built around the method.
    /// </summary>
    public static void BuildFieldType(
        MemberInfo memberInfo,
        FieldType fieldType,
        Func<MemberInfo, LambdaExpression> BuildMemberInstanceExpression,
        Func<Type, Func<FieldType, ParameterInfo, ArgumentInformation>> getTypedArgumentInfoMethod,
        Action<ParameterInfo, QueryArgument> ApplyArgumentAttributes)
    {
        if (memberInfo is PropertyInfo propertyInfo)
        {
            var resolver = new MemberResolver(propertyInfo, BuildMemberInstanceExpression(memberInfo));
            fieldType.Arguments = null;
            fieldType.Resolver = resolver;
            fieldType.StreamResolver = null;
        }
        else if (memberInfo is MethodInfo methodInfo)
        {
            List<LambdaExpression> expressions = new();
            QueryArguments queryArguments = new();
            foreach (var parameterInfo in methodInfo.GetParameters())
            {
                var getArgumentInfoMethod = getTypedArgumentInfoMethod(parameterInfo.ParameterType);
                var argumentInfo = getArgumentInfoMethod(fieldType, parameterInfo);
                var (queryArgument, expression) = argumentInfo.ConstructQueryArgument();
                if (queryArgument != null)
                {
                    ApplyArgumentAttributes(parameterInfo, queryArgument);
                    queryArguments.Add(queryArgument);
                }
                expression ??= AutoRegisteringHelper.GetParameterExpression(
                    parameterInfo.ParameterType,
                    queryArgument ?? throw new InvalidOperationException("Invalid response from ConstructQueryArgument: queryArgument and expression cannot both be null"));
                expressions.Add(expression);
            }
            var memberInstanceExpression = BuildMemberInstanceExpression(methodInfo);
            if (IsObservable(methodInfo.ReturnType))
            {
                var resolver = new SourceStreamMethodResolver(methodInfo, memberInstanceExpression, expressions);
                fieldType.Resolver = resolver;
                fieldType.StreamResolver = resolver;
            }
            else
            {
                var resolver = new MemberResolver(methodInfo, memberInstanceExpression, expressions);
                fieldType.Resolver = resolver;
                fieldType.StreamResolver = null;
            }
            fieldType.Arguments = queryArguments;
        }
        else if (memberInfo is FieldInfo fieldInfo)
        {
            var resolver = new MemberResolver(fieldInfo, BuildMemberInstanceExpression(memberInfo));
            fieldType.Arguments = null;
            fieldType.Resolver = resolver;
            fieldType.StreamResolver = null;
        }
        else if (memberInfo == null)
        {
            throw new ArgumentNullException(nameof(memberInfo));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(memberInfo), "Member must be a field, property or method.");
        }
    }

    /// <summary>
    /// Determines if the type is an <see cref="IObservable{T}"/> or task that returns an <see cref="IObservable{T}"/>.
    /// </summary>
    private static bool IsObservable(Type type)
    {
        if (!type.IsGenericType)
            return false;

        var g = type.GetGenericTypeDefinition();
        if (g == typeof(IObservable<>))
            return true;
        if (g == typeof(Task<>) || g == typeof(ValueTask<>))
            return IsObservable(type.GetGenericArguments()[0]);
        return false;
    }

    /// <summary>
    /// Returns a list of properties, methods or fields that should have fields created for them.
    /// <br/><br/>
    /// Unless overridden, returns a list of public instance readable properties and public instance methods
    /// that do not return <see langword="void"/> or <see cref="Task"/>
    /// including properties and methods declared on inherited classes.
    /// </summary>
    public static IEnumerable<MemberInfo> GetRegisteredMembers<TSourceType>(Expression<Func<TSourceType, object?>>[]? excludedProperties)
    {
        if (typeof(TSourceType).IsInterface)
        {
            var types = typeof(TSourceType).GetInterfaces().Append(typeof(TSourceType));

            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            var properties = AutoRegisteringHelper.ExcludeProperties(
                types.SelectMany(type => type.GetProperties(flags)).Where(x => x.CanRead),
                excludedProperties);
            var methods = types.SelectMany(type => type.GetMethods(flags))
                .Where(x =>
                    !x.ContainsGenericParameters &&     // exclude methods with open generics
                    !x.IsSpecialName &&                 // exclude methods generated for properties
                    x.ReturnType != typeof(void) &&     // exclude methods which do not return a value
                    x.ReturnType != typeof(Task));
            return properties.Concat<MemberInfo>(methods);
        }
        else
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            var properties = AutoRegisteringHelper.ExcludeProperties(
                typeof(TSourceType).GetProperties(flags).Where(x => x.CanRead),
                excludedProperties);
            var methods = typeof(TSourceType).GetMethods(flags)
                .Where(x =>
                    !x.ContainsGenericParameters &&                          // exclude methods with open generics
                    !x.IsSpecialName &&                                      // exclude methods generated for properties
                    x.ReturnType != typeof(void) &&                          // exclude methods which do not return a value
                    x.ReturnType != typeof(Task) &&                          // exclude methods which do not return a value
                    x.GetBaseDefinition().DeclaringType != typeof(object) && // exclude methods inherited from object (e.g. GetHashCode)
                                                                             // exclude methods generated for record types: bool Equals(TSourceType)
                    !(x.Name == "Equals" && !x.IsStatic && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(TSourceType) && x.ReturnType == typeof(bool)) &&
                    x.Name != "<Clone>$");                                   // exclude methods generated for record types
            return properties.Concat<MemberInfo>(methods);
        }
    }

    /// <summary>
    /// Analyzes a method parameter and returns an instance of <see cref="ArgumentInformation"/>
    /// containing information necessary to build a <see cref="QueryArgument"/> and <see cref="IFieldResolver"/>.
    /// Also applies any <see cref="GraphQLAttribute"/> attributes defined on the <see cref="ParameterInfo"/>
    /// to the returned <see cref="ArgumentInformation"/> instance.
    /// </summary>
    public static ArgumentInformation GetArgumentInformation<TSourceType>(TypeInformation typeInformation, FieldType fieldType, ParameterInfo parameterInfo)
    {
        var argumentInfo = new ArgumentInformation(parameterInfo, typeof(TSourceType), fieldType, typeInformation);
        argumentInfo.ApplyAttributes();
        return argumentInfo;
    }

    /// <summary>
    /// Applies <see cref="GraphQLAttribute"/> attributes defined on the supplied <see cref="ParameterInfo"/>
    /// to the specified <see cref="QueryArgument"/>.
    /// Also scans the parameter's owning module and assembly for globally-applied attributes.
    /// </summary>
    public static void ApplyArgumentAttributes(ParameterInfo parameterInfo, QueryArgument queryArgument)
    {
        // Apply derivatives of GraphQLAttribute
        var attributes = parameterInfo.GetGraphQLAttributes();
        foreach (var attr in attributes)
        {
            attr.Modify(queryArgument);
        }
    }
}
