using System;
using System.Linq.Expressions;
using System.Reflection;
using FullSerializer.Internal;

namespace EvenFasterSaveLoad.Json
{
    /// <summary>
    /// Compiled member access with reflection fallback. If the runtime ever rejects generated code
    /// (Mono can, for some access patterns) <see cref="Disable"/> switches everything to reflection.
    /// </summary>
    internal static class Accessors
    {
        public static volatile bool Disabled;

        public static void Disable()
        {
            Disabled = true;
            Models.Reset();
        }

        public static bool IsCodegenFailure(Exception e) =>
            e is InvalidProgramException || e is FieldAccessException || e is MethodAccessException ||
            e is TypeLoadException || e is System.Security.VerificationException;

        public static Func<object, object> Getter(fsMetaProperty property)
        {
            MemberInfo member = property._memberInfo;
            return Compile<Func<object, object>>(p =>
                       Expression.Convert(Expression.MakeMemberAccess(Owner(p[0], member.DeclaringType), member), typeof(object)),
                   CanRead(member))
                   ?? property.Read;
        }

        public static Action<object, object> Setter(fsMetaProperty property)
        {
            MemberInfo member = property._memberInfo;
            // Writing through an unboxed struct is not portable across expression compilers; reflection
            // (FieldInfo.SetValue on the box) mutates the box in place, which is what FullSerializer relies on.
            bool compile = !member.DeclaringType.IsValueType && CanWrite(member);
            return Compile<Action<object, object>>(p =>
                       Expression.Assign(Expression.MakeMemberAccess(Owner(p[0], member.DeclaringType), member),
                           Unbox(p[1], property.StorageType)),
                   compile)
                   ?? property.Write;
        }

        /// <summary>Writes a primitive member straight into the sink, without boxing. Null if not applicable.</summary>
        public static Action<object, ByteSink> PrimitiveEmitter(fsMetaProperty property)
        {
            MemberInfo member = property._memberInfo;
            Type type = property.StorageType;
            string method;
            Type parameter;
            if (type == typeof(bool)) { method = nameof(ByteSink.Bool); parameter = typeof(bool); }
            else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(sbyte) ||
                     type == typeof(byte) || type == typeof(ushort) || type == typeof(uint)) { method = nameof(ByteSink.Integer); parameter = typeof(long); }
            else if (type == typeof(float)) { method = nameof(ByteSink.Single); parameter = typeof(float); }
            else if (type == typeof(double)) { method = nameof(ByteSink.Double); parameter = typeof(double); }
            else if (type == typeof(string)) { method = nameof(ByteSink.StringOrNull); parameter = typeof(string); }
            else return null;

            MethodInfo write = typeof(ByteSink).GetMethod(method, new[] { parameter });
            return Compile<Action<object, ByteSink>>(p =>
            {
                Expression value = Expression.MakeMemberAccess(Owner(p[0], member.DeclaringType), member);
                if (value.Type != parameter)
                    value = Expression.Convert(value, parameter);
                return Expression.Call(p[1], write, value);
            }, CanRead(member));
        }

        public static Func<object> Constructor(Type type)
        {
            ConstructorInfo ctor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (ctor == null || type.IsAbstract)
                return null;
            return Compile<Func<object>>(_ => Expression.Convert(Expression.New(ctor), typeof(object)), true);
        }

        public static Action<object, object> Method1(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            bool compile = !method.IsStatic && !method.ContainsGenericParameters && parameters.Length == 1 && !method.DeclaringType.IsValueType;
            return Compile<Action<object, object>>(p =>
                       Expression.Call(Owner(p[0], method.DeclaringType), method, Unbox(p[1], parameters[0].ParameterType)),
                   compile)
                   ?? ((target, value) => method.Invoke(target, new[] { value }));
        }

        private static bool CanRead(MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo f:
                    return !f.IsStatic;
                case PropertyInfo p:
                    MethodInfo get = p.GetGetMethod(true);
                    return get != null && !get.IsStatic && p.GetIndexParameters().Length == 0;
                default:
                    return false;
            }
        }

        private static bool CanWrite(MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo f:
                    return !f.IsStatic && !f.IsInitOnly && !f.IsLiteral;
                case PropertyInfo p:
                    MethodInfo set = p.GetSetMethod(true);
                    return set != null && !set.IsStatic && p.GetIndexParameters().Length == 0;
                default:
                    return false;
            }
        }

        private static Expression Owner(Expression instance, Type type) =>
            type.IsValueType ? Expression.Unbox(instance, type) : (Expression)Expression.Convert(instance, type);

        // null -> default(T) for value types, like FieldInfo.SetValue does.
        private static Expression Unbox(Expression value, Type type) =>
            type.IsValueType
                ? Expression.Condition(Expression.Equal(value, Expression.Constant(null)), Expression.Default(type), Expression.Convert(value, type))
                : (Expression)Expression.Convert(value, type);

        private static T Compile<T>(Func<ParameterExpression[], Expression> body, bool allowed) where T : Delegate
        {
            if (!allowed || Disabled)
                return null;
            try
            {
                ParameterInfo[] signature = typeof(T).GetMethod("Invoke").GetParameters();
                ParameterExpression[] parameters = Array.ConvertAll(signature, p => Expression.Parameter(p.ParameterType, p.Name));
                return Expression.Lambda<T>(body(parameters), parameters).Compile();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogDebug($"Accessors: falling back to reflection: {e.Message}");
                return null;
            }
        }
    }
}
