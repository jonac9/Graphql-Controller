﻿using GraphQL.Resolvers;
using GraphQL.Types;
using GraphqlController.Arguments;
using GraphqlController.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GraphqlController.GraphQl
{
    public class DynamicGraphType : ObjectGraphType
    {
        public DynamicGraphType(IGraphQlTypePool graphTypePool, Type type)
        {
            //if (!typeof(GraphNodeType<>).IsAssignableFrom(type))
            //{
            //    throw new ArgumentException("The type is not of type GraphNodeType<>");
            //}

            // get the name
            var nameArgument = type.GetAttribute<TypeNameAttribute>();
            var name = nameArgument == null ? type.Name : nameArgument.Name;

            // set type name
            Name = name;

            // Generate fields -----------------------------------------------
            // start with the properties
            var properties = type
                // Get all properties with getters
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.SetProperty | BindingFlags.GetProperty)
                // ignore the ones that have the ignore attribute
                .Where(x => x.GetAttribute<IgnoreFieldAttribute>() == null);

            foreach (var property in properties)
            {
                var graphType = graphTypePool.GetGraphType(property.PropertyType);
                var descriptionAttr = property.GetAttribute<FieldDescriptionAttribute>();
                var fieldNameAttr = property.GetAttribute<FieldNameAttribute>();
                var isNonNull = property.GetAttribute<NonNullFieldAttribute>() != null;

                // create field
                var field = new FieldType()
                {
                    Name = fieldNameAttr == null ? property.Name : fieldNameAttr.Name,
                    Description = descriptionAttr?.Description,
                    ResolvedType = isNonNull ? new NonNullGraphType(graphType) : graphType,
                    Resolver = new FuncFieldResolver<object>(c => property.GetValue(c.Source))
                };

                AddField(field);
            }

            // work with the methods
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                              .Where(x => x.GetAttribute<IgnoreFieldAttribute>() == null);

            foreach (var method in methods)
            {
                if(method.Name == nameof(GraphNodeType<object>.OnCreateAsync) ||
                   method.IsSpecialName)
                {
                    continue;
                }

                var descriptionAttr = method.GetAttribute<FieldDescriptionAttribute>();
                var fieldNameAttr = method.GetAttribute<FieldNameAttribute>();

                IGraphType graphType;
                IFieldResolver resolver;
                if (method.IsGenericMethod && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                {
                    var awaitableReturnType = method.ReturnType.GetGenericArguments()[0];
                    graphType = graphTypePool.GetGraphType(awaitableReturnType);
                    resolver = new AsyncFieldResolver<object>(c =>
                    {
                        var task = ExecuteResolverFunction(method, c);
                        return task as Task<object>;
                    });
                }
                else
                {
                    graphType = graphTypePool.GetGraphType(method.ReturnType);
                    resolver = new FuncFieldResolver<object>(c => ExecuteResolverFunction(method, c));
                }

                var isNonNull = method.GetAttribute<NonNullFieldAttribute>() != null;

                // create field
                var field = new FieldType()
                {
                    Arguments = GetArguments(graphTypePool, method),
                    Name = fieldNameAttr == null ? method.Name : fieldNameAttr.Name,
                    Description = descriptionAttr?.Description,
                    ResolvedType = isNonNull ? new NonNullGraphType(graphType) : graphType,
                    Resolver = resolver
                };
            }

        }

        public static QueryArguments GetArguments(IGraphQlTypePool graphTypePool, MethodInfo methodInfo)
        {
            var result = new List<QueryArgument>();
            var parameters = methodInfo.GetParameters();
            foreach(var param in parameters)
            {
                if(param.ParameterType == typeof(CancellationToken))
                {
                    continue;
                }

                var argumentNameAttr = param.GetAttribute<ArgumentNameAttribute>();
                var argumentDescriptionAttr = param.GetAttribute<ArgumentDescriptionAttribute>();
                var isNonNullType = param.GetAttribute<NonNullArgumentAttribute>() != null;

                var name = argumentNameAttr == null ? param.Name : argumentNameAttr.Name;
                var description = argumentDescriptionAttr == null ? "" : argumentDescriptionAttr.Description;

                var graphInputType = isNonNullType
                                            ? new NonNullGraphType(graphTypePool.GetInputType(param.ParameterType))
                                            : graphTypePool.GetInputType(param.ParameterType);

                var argument = new QueryArgument(graphInputType) 
                { 
                    Name = name, 
                    Description = description,                    
                    DefaultValue = param.HasDefaultValue ? param.DefaultValue : null
                };

                result.Add(argument);
            }

            return new QueryArguments(result);
        }

        public static object ExecuteResolverFunction(MethodInfo method, ResolveFieldContext c)
        {
            var parameters = method.GetParameters();
            var parameterValues = new List<object>();

            foreach (var param in parameters)
            {
                if (param.ParameterType == typeof(CancellationToken))
                {
                    parameterValues.Add(c.CancellationToken);
                    continue;
                }

                var nameAttr = param.GetAttribute<ArgumentNameAttribute>();

                object value;
                if (c.Arguments.TryGetValue(nameAttr == null ? param.Name : nameAttr.Name, out value))
                {
                    parameterValues.Add(value);
                }
                else
                {
                    if (param.HasDefaultValue)
                    {
                        parameterValues.Add(param.DefaultValue);
                    }
                    else
                    {
                        parameterValues.Add(GetDefault(param.ParameterType));
                    }
                }
            }

            var resultValue = method.Invoke(c.Source, parameterValues.ToArray());

            return resultValue;
        }

        // Get deafult value of type
        public static object GetDefault(Type type)
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }
            return null;
        }
    }
}
