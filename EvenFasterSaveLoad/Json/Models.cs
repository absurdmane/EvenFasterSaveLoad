using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using FullSerializer;
using FullSerializer.Internal;
using UnityEngine;

namespace EvenFasterSaveLoad.Json
{
    /// <summary>Which FullSerializer converter handles a type, as far as we care.</summary>
    internal enum Kind : byte
    {
        Other, // anything we don't reproduce: always goes through vanilla
        Bool,
        Integer, // sbyte, byte, short, ushort, int, uint, long
        UInt64,
        Single,
        Double,
        Decimal,
        String,
        Char,
        Enum,
        Guid,
        TypeName,
        Date,
        Array,
        Collection,
        Dictionary,
        Pair,
        Nullable,
        Object,
        Direct,
    }

    /// <summary>
    /// Everything the fast reader and writer need to know about one type, derived from the
    /// same fsSerializer instance the game uses (so converters, processors and config all match).
    /// Immutable once published by <see cref="Models"/>; safe to read from worker threads.
    /// </summary>
    internal sealed class TypeModel
    {
        public readonly Type Type;
        public Kind Kind;
        public fsBaseConverter Converter;

        /// <summary>Why the fast writer can't produce this type; null if it can.</summary>
        public string WriteBlocker;

        /// <summary>Why the fast reader leaves this type to vanilla; null if it can read it.</summary>
        public string ReadBlocker;

        /// <summary>converter.RequestCycleSupport: instances get $id/$ref.</summary>
        public bool Tracked;

        /// <summary>converter.RequestInheritanceSupport, used when this type is a storage type: subtypes get $type.</summary>
        public bool Polymorphic;

        /// <summary>Has fsSerializationCallbackReceiverProcessor (ISerializationCallbackReceiver).</summary>
        public bool Callbacks;

        /// <summary>Writing it may call into Unity (property getters, native-backed converters).</summary>
        public bool MainThreadOnly;
        public string MainThreadReason;

        public byte[] TypeNameJson;

        // Object
        public Member[] WriteMembers;

        /// <summary>
        /// Indices into <see cref="WriteMembers"/> in output order. Differs from declaration order only when two
        /// members share a JSON name (a subclass redeclaring a base field): FullSerializer stores both into one
        /// dictionary key, so the name keeps its first position but carries the last member's value.
        /// </summary>
        public int[] EmitOrder;

        /// <summary>Members that share a JSON name with a later one; only supported when they are leaves.</summary>
        public Member[] Shadowed;
        private int shadowState; // 0 unknown, 1 ok, 2 unsupported
        public Member[] ReadMembers;
        public Func<object> Construct;

        // Array, Collection, Nullable: element/underlying type. Dictionary/Pair: key + value.
        public Type ElementType;
        public Type KeyType;
        public Type ValueType;
        public PropertyInfo PairKey;
        public PropertyInfo PairValue;

        // Collection reading
        public Action<object, object> Add;
        public MethodInfo GetItem;
        public MethodInfo SetItem;
        public MethodInfo Clear;
        public MethodInfo Count;

        public EnumNames Enum;

        private int leafState; // 0 unknown, 1 computing, 2 leaf, 3 not leaf

        public TypeModel(Type type)
        {
            Type = type;
        }

        /// <summary>
        /// True if no value stored under this (storage) type can be tracked, have callbacks or need the
        /// main thread, so the census pass can skip it entirely.
        /// </summary>
        public bool IsLeaf
        {
            get
            {
                if (leafState >= 2)
                    return leafState == 2;
                if (leafState == 1)
                    return false; // recursive struct graph: be conservative
                leafState = 1;
                bool leaf = ComputeLeaf();
                leafState = leaf ? 2 : 3;
                return leaf;
            }
        }

        /// <summary>
        /// Shadowed members are written by FullSerializer but then overwritten, so their output vanishes while
        /// their place in the encounter order stays. That only matters if they can contain tracked objects.
        /// </summary>
        public bool ShadowingSupported
        {
            get
            {
                if (shadowState == 0)
                {
                    bool ok = true;
                    foreach (int i in EmitOrder)
                        ok &= WriteMembers[i].Storage.IsLeaf || !IsReplacement(i);
                    foreach (Member m in Shadowed)
                        ok &= m.Storage.IsLeaf;
                    shadowState = ok ? 1 : 2;
                }
                return shadowState == 1;
            }
        }

        private bool IsReplacement(int index)
        {
            foreach (Member m in Shadowed)
            {
                if (m.JsonName == WriteMembers[index].JsonName)
                    return true;
            }
            return false;
        }

        private bool ComputeLeaf()
        {
            if (WriteBlocker != null || Tracked || Callbacks || MainThreadOnly)
                return false;
            switch (Kind)
            {
                case Kind.Bool:
                case Kind.Integer:
                case Kind.UInt64:
                case Kind.Single:
                case Kind.Double:
                case Kind.Decimal:
                case Kind.String:
                case Kind.Char:
                case Kind.Enum:
                case Kind.Guid:
                case Kind.Date:
                case Kind.TypeName:
                    return true;
                case Kind.Nullable:
                    return Models.Get(ElementType).IsLeaf;
                case Kind.Array:
                    return Models.Get(ElementType).IsLeaf;
                case Kind.Pair:
                    return Models.Get(KeyType).IsLeaf && Models.Get(ValueType).IsLeaf;
                case Kind.Object:
                    // Only structs: a class storage type could hold a subclass (and classes are tracked anyway).
                    if (!Type.IsValueType)
                        return false;
                    foreach (Member m in WriteMembers)
                    {
                        if (!m.Storage.IsLeaf)
                            return false;
                    }
                    return true;
                default:
                    return false;
            }
        }
    }

    internal sealed class Member
    {
        public readonly fsMetaProperty Meta;
        public readonly string JsonName;
        public readonly byte[] KeyJson; // "name":
        public readonly bool HasOverrideConverter;

        private Func<object, object> get;
        private Action<object, object> set;
        private Action<object, ByteSink> emit;
        private bool emitResolved;
        private TypeModel storage;

        public Member(fsMetaProperty meta)
        {
            Meta = meta;
            JsonName = meta.JsonName;
            KeyJson = Encoding.UTF8.GetBytes("\"" + meta.JsonName + "\":");
            HasOverrideConverter = meta.OverrideConverterType != null;
        }

        public Type StorageType => Meta.StorageType;

        public TypeModel Storage => storage ?? (storage = Models.Get(Meta.StorageType));

        public Func<object, object> Get => get ?? (get = Accessors.Getter(Meta));

        public Action<object, object> Set => set ?? (set = Accessors.Setter(Meta));

        /// <summary>Typed writer for primitive members (no boxing), or null.</summary>
        public Action<object, ByteSink> PrimitiveEmit
        {
            get
            {
                if (!emitResolved)
                {
                    emit = Accessors.PrimitiveEmitter(Meta);
                    emitResolved = true;
                }
                return emit;
            }
        }

        /// <summary>Plain fields (and compiler-generated property backing) have no side effects on read.</summary>
        public bool IsField => Meta._memberInfo is FieldInfo;
    }

    /// <summary>fsEnumConverter's names, cached per value (thread-safe).</summary>
    internal sealed class EnumNames
    {
        private readonly Type type;
        private readonly bool flags;
        private readonly Dictionary<long, string> names = new Dictionary<long, string>();
        private readonly Dictionary<string, long> values = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> declared;

        public EnumNames(Type type)
        {
            this.type = type;
            flags = fsPortableReflection.GetAttribute<FlagsAttribute>(type) != null;
            declared = new HashSet<string>(System.Enum.GetNames(type), StringComparer.Ordinal);
        }

        public bool Flags => flags;

        /// <summary>The string fsEnumConverter writes; null where vanilla would crash on a null name.</summary>
        public string Name(object value)
        {
            long key = Convert.ToInt64(value);
            lock (names)
            {
                if (names.TryGetValue(key, out string name))
                    return name;
            }
            string computed;
            if (flags)
            {
                // Every declared flag that overlaps the value (zero-valued names never match).
                var sb = new StringBuilder();
                bool first = true;
                foreach (object defined in System.Enum.GetValues(type))
                {
                    if ((key & Convert.ToInt64(defined)) == 0)
                        continue;
                    if (!first)
                        sb.Append(',');
                    first = false;
                    sb.Append(defined);
                }
                computed = sb.ToString();
            }
            else
            {
                computed = System.Enum.GetName(type, value);
            }
            lock (names)
                names[key] = computed;
            return computed;
        }

        /// <summary>fsEnumConverter.TryDeserialize for string data; false where vanilla fails.</summary>
        public bool TryParse(string text, out long value)
        {
            lock (values)
            {
                if (values.TryGetValue(text, out value))
                    return true;
            }
            value = 0;
            foreach (string part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!declared.Contains(part))
                    return false;
                value |= (long)Convert.ChangeType(System.Enum.Parse(type, part), typeof(long));
            }
            lock (values)
            {
                if (values.Count < 1024)
                    values[text] = value;
            }
            return true;
        }
    }

    /// <summary>Model registry. Models are built under one lock; reads of published models are lock-free.</summary>
    internal static class Models
    {
        private static readonly object Gate = new object();
        private static Dictionary<Type, TypeModel> cache = new Dictionary<Type, TypeModel>();
        private static fsSerializer owner;

        // Direct converters whose TrySerialize only touches plain struct fields.
        private static readonly HashSet<string> PureDirectConverters = new HashSet<string>
        {
            "Rect_DirectConverter", "Bounds_DirectConverter", "LayerMask_DirectConverter", "Keyframe_DirectConverter",
        };

        private static readonly HashSet<string> SupportedDirectConverters = new HashSet<string>
        {
            "Rect_DirectConverter", "Bounds_DirectConverter", "LayerMask_DirectConverter", "Keyframe_DirectConverter",
            "AnimationCurve_DirectConverter", "Gradient_DirectConverter", "RectOffset_DirectConverter",
        };

        public static fsSerializer Serializer => FsHost.Serializer;

        public static void Reset()
        {
            lock (Gate)
                cache = new Dictionary<Type, TypeModel>();
        }

        /// <summary>
        /// Lock-free for known types (the published dictionary is never mutated: copy-on-write), so the
        /// parallel writers don't contend. Building doesn't recurse into Get: member and element models are
        /// resolved lazily on first use.
        /// </summary>
        public static TypeModel Get(Type type)
        {
            Dictionary<Type, TypeModel> published = cache;
            if (published.TryGetValue(type, out TypeModel model) && ReferenceEquals(owner, Serializer))
                return model;

            lock (Gate)
            {
                fsSerializer fs = Serializer;
                if (!ReferenceEquals(fs, owner))
                {
                    // Converter instances belong to one serializer; never mix models across serializers.
                    owner = fs;
                    cache = new Dictionary<Type, TypeModel>();
                }
                if (cache.TryGetValue(type, out model))
                    return model;

                model = new TypeModel(type);
                try
                {
                    Build(model, fs);
                }
                catch (Exception e)
                {
                    model.Kind = Kind.Other;
                    model.WriteBlocker = model.ReadBlocker = $"{type}: {e.GetType().Name}: {e.Message}";
                }
                cache = new Dictionary<Type, TypeModel>(cache) { [type] = model };
            }
            return model;
        }

        private static void Build(TypeModel m, fsSerializer fs)
        {
            Type type = m.Type;
            m.TypeNameJson = Quoted(type.FullName);

            if (type.IsInterface || type.IsAbstract)
            {
                // Only ever a storage type. Values are modeled by their runtime type; reading remaps or uses $type.
                fsBaseConverter storageConverter = fs.GetConverter(type, null);
                m.Converter = storageConverter;
                m.Polymorphic = storageConverter.RequestInheritanceSupport(type);
                m.Tracked = storageConverter.RequestCycleSupport(type);
                m.Kind = Kind.Other;
                m.WriteBlocker = $"{type}: abstract runtime type";
                m.ReadBlocker = $"{type}: abstract storage";
                if (typeof(Type).IsAssignableFrom(type))
                {
                    m.Kind = Kind.TypeName;
                    m.WriteBlocker = m.ReadBlocker = null;
                }
                return;
            }

            fsBaseConverter converter = fs.GetConverter(type, null);
            m.Converter = converter;
            m.Tracked = converter.RequestCycleSupport(type);
            m.Polymorphic = converter.RequestInheritanceSupport(type);

            if (fsPortableReflection.GetAttribute<fsObjectAttribute>(type)?.Processor != null)
                Block(m, "custom fsObject processor");
            foreach (fsObjectProcessor processor in fs.GetProcessors(type))
            {
                if (processor is fsSerializationCallbackReceiverProcessor)
                    m.Callbacks = true;
                else
                    Block(m, "processor " + processor.GetType().Name);
            }
            if (m.Callbacks && type.IsValueType)
                Block(m, "struct with serialization callbacks"); // callbacks would mutate a temporary box
            if (fsVersionManager.GetVersionedType(type).HasValue)
                Block(m, "versioned type");

            Type converterType = converter.GetType();
            if (converterType == typeof(fsPrimitiveConverter))
                BuildPrimitive(m, fs);
            else if (converterType == typeof(fsEnumConverter))
            {
                m.Kind = Kind.Enum;
                m.Enum = new EnumNames(type);
                if (fs.Config.SerializeEnumsAsInteger)
                    Block(m, "SerializeEnumsAsInteger");
            }
            else if (converterType == typeof(fsGuidConverter))
                m.Kind = Kind.Guid;
            else if (converterType == typeof(fsTypeConverter))
                m.Kind = Kind.TypeName;
            else if (converterType == typeof(fsDateConverter))
                m.Kind = Kind.Date;
            else if (converterType == typeof(fsNullableConverter))
            {
                m.Kind = Kind.Nullable;
                m.ElementType = Nullable.GetUnderlyingType(type);
            }
            else if (converterType == typeof(fsArrayConverter))
            {
                m.Kind = Kind.Array;
                m.ElementType = type.GetElementType();
                if (type.GetArrayRank() != 1)
                    Block(m, "multidimensional array");
            }
            else if (converterType == typeof(fsIEnumerableConverter))
                BuildCollection(m);
            else if (converterType == typeof(fsDictionaryConverter))
            {
                m.Kind = Kind.Dictionary;
                fsDictionaryConverter.GetKeyValueTypes(type, out m.KeyType, out m.ValueType);
                m.Construct = ConstructorFor(type, fs);
            }
            else if (converterType == typeof(fsKeyValuePairConverter))
            {
                m.Kind = Kind.Pair;
                Type[] args = type.GetGenericArguments();
                m.KeyType = args[0];
                m.ValueType = args[1];
                m.PairKey = fsPortableReflection.GetDeclaredProperty(type, "Key");
                m.PairValue = fsPortableReflection.GetDeclaredProperty(type, "Value");
            }
            else if (converterType == typeof(fsReflectedConverter))
                BuildObject(m, fs);
            else if (converter is fsDirectConverter && SupportedDirectConverters.Contains(converterType.Name))
            {
                m.Kind = Kind.Direct;
                m.MainThreadOnly = !PureDirectConverters.Contains(converterType.Name);
                m.MainThreadReason = $"{type.Name} uses {converterType.Name}";
                m.ReadBlocker = "direct converter";
            }
            else
            {
                Block(m, "converter " + converterType.Name);
            }
        }

        private static void BuildPrimitive(TypeModel m, fsSerializer fs)
        {
            switch (Type.GetTypeCode(m.Type))
            {
                case TypeCode.Boolean: m.Kind = Kind.Bool; break;
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64: m.Kind = Kind.Integer; break;
                case TypeCode.UInt64: m.Kind = Kind.UInt64; break;
                case TypeCode.Single: m.Kind = Kind.Single; break;
                case TypeCode.Double: m.Kind = Kind.Double; break;
                case TypeCode.Decimal: m.Kind = Kind.Decimal; break;
                case TypeCode.String: m.Kind = Kind.String; break;
                case TypeCode.Char:
                    m.Kind = Kind.Char;
                    m.ReadBlocker = "char"; // vanilla assigns a string to the char; keep its behavior
                    break;
                default:
                    Block(m, "primitive " + m.Type);
                    break;
            }
            if (fs.Config.Serialize64BitIntegerAsString && (m.Type == typeof(long) || m.Type == typeof(ulong)))
                Block(m, "Serialize64BitIntegerAsString");
        }

        private static void BuildCollection(TypeModel m)
        {
            Type type = m.Type;
            m.Kind = Kind.Collection;
            m.ElementType = fsIEnumerableConverter.GetElementType(type);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Stack<>))
                m.WriteBlocker = "Stack<> (written reversed)";

            MethodInfo add = fsIEnumerableConverter.GetAddMethod(type);
            m.Add = add != null ? Accessors.Method1(add) : null;
            m.GetItem = fsPortableReflection.GetFlattenedMethod(type, "get_Item");
            m.SetItem = fsPortableReflection.GetFlattenedMethod(type, "set_Item");
            m.Clear = fsPortableReflection.GetFlattenedMethod(type, "Clear");
            PropertyInfo count = fsPortableReflection.GetFlattenedProperty(type, "Count");
            if (count != null)
            {
                m.Count = count.GetGetMethod();
                if (m.Count == null)
                    m.ReadBlocker = "non-public Count";
            }
            m.Construct = ConstructorFor(type, Serializer);
        }

        private static void BuildObject(TypeModel m, fsSerializer fs)
        {
            m.Kind = Kind.Object;
            fsMetaType meta = fsMetaType.Get(fs.Config, m.Type);
            var write = new List<Member>();
            var read = new List<Member>();
            var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<int>();
            var shadowed = new List<Member>();
            foreach (fsMetaProperty property in meta.Properties)
            {
                var member = new Member(property);
                if (property.CanRead)
                {
                    write.Add(member);
                    if (slotOf.TryGetValue(property.JsonName, out int slot))
                    {
                        shadowed.Add(write[order[slot]]);
                        order[slot] = write.Count - 1; // same position, later value
                    }
                    else
                    {
                        slotOf[property.JsonName] = order.Count;
                        order.Add(write.Count - 1);
                    }
                    if (property.OverrideConverterType != null)
                        m.WriteBlocker = $"{m.Type}.{property.MemberName}: custom converter";
                    if (property._memberInfo is PropertyInfo p && !IsAutoProperty(p))
                    {
                        m.MainThreadOnly = true; // a real getter might touch Unity API
                        m.MainThreadReason = m.MainThreadReason ?? $"{m.Type.Name}.{p.Name} is a computed property";
                    }
                }
                if (property.CanWrite)
                    read.Add(member);
            }
            m.WriteMembers = write.ToArray();
            m.EmitOrder = order.ToArray();
            m.Shadowed = shadowed.ToArray();
            m.ReadMembers = read.ToArray();
            m.Construct = ConstructorFor(m.Type, fs);
        }

        private static bool IsAutoProperty(PropertyInfo property)
        {
            MethodInfo get = property.GetGetMethod(true);
            return get != null && get.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false);
        }

        /// <summary>fsMetaType.CreateInstance, with a compiled fast path for the plain "new T()" case.</summary>
        private static Func<object> ConstructorFor(Type type, fsSerializer fs)
        {
            fsMetaType meta = fsMetaType.Get(fs.Config, type);
            if (!type.IsValueType && !type.IsAbstract && meta.HasDefaultConstructor &&
                !typeof(ScriptableObject).IsAssignableFrom(type) && type != typeof(string) && !type.IsArray)
            {
                Func<object> compiled = Accessors.Constructor(type);
                if (compiled != null)
                    return compiled;
            }
            return meta.CreateInstance;
        }

        private static void Block(TypeModel m, string reason)
        {
            reason = $"{m.Type}: {reason}";
            m.WriteBlocker = m.WriteBlocker ?? reason;
            m.ReadBlocker = m.ReadBlocker ?? reason;
        }

        private static byte[] Quoted(string text)
        {
            var sink = new ByteSink(text.Length * 6 + 2);
            sink.String(text);
            return sink.ToArray();
        }
    }
}
