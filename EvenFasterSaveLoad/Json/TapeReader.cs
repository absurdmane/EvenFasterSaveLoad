using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using FullSerializer;
using FullSerializer.Internal;
using UnityEngine;

namespace EvenFasterSaveLoad.Json
{
    /// <summary>
    /// Rebuilds objects from a <see cref="JsonTape"/> following fsSerializer.TryDeserialize step by step
    /// (references, $type, abstract remapping, instance reuse, $id registration, converter, callbacks).
    ///
    /// Every node is checked before anything is created for it. If the data isn't in a shape we reproduce
    /// exactly, that node is turned into fsData and given to vanilla, which also produces vanilla's warnings.
    /// So results and the success/warning outcome match vanilla; only the fast path skips the fsData tree.
    /// </summary>
    internal sealed class TapeReader
    {
        private readonly JsonTape tape = new JsonTape();
        private readonly List<string> messages = new List<string>();
        private fsSerializer fs;
        private fsCyclicReferenceManager references;

        public object Read(Type type, string json, out fsResult result)
        {
            if (!fsGlobalConfig.IsCaseSensitive || fsGlobalConfig.InternalFieldPrefix != "$")
                throw new FallbackException("non-default fsGlobalConfig");

            fs = Models.Serializer;
            references = fs._references;
            messages.Clear();
            try
            {
                tape.Parse(json);
                references.Enter();
                try
                {
                    object value = null;
                    bool ok = Value(0, Models.Get(type), ref value);
                    result = ok ? fsResult.Success : fsResult.Fail("deserialization failed");
                    if (ok && messages.Count > 0)
                        result._messages = new List<string>(messages);
                    else if (!ok)
                        result._messages.InsertRange(0, messages);
                    return value;
                }
                finally
                {
                    references.Exit();
                }
            }
            finally
            {
                tape.Release();
                messages.Clear();
            }
        }

        // ---- vanilla delegation -------------------------------------------------------------------------

        private bool Vanilla(int node, Type storage, Type overrideConverter, ref object result)
        {
            fsResult r = fs.TryDeserialize(ToData(node), storage, overrideConverter, ref result);
            messages.AddRange(r.RawMessages);
            return r.Succeeded;
        }

        private fsData ToData(int node)
        {
            ref Entry e = ref tape[node];
            switch (e.Token)
            {
                case Token.Null: return new fsData();
                case Token.True: return new fsData(true);
                case Token.False: return new fsData(false);
                case Token.Integer: return new fsData(tape.Integer(node));
                case Token.Real: return new fsData(tape.Real(node));
                case Token.String: return new fsData(tape.GetString(node));
                case Token.Array:
                {
                    var list = new List<fsData>((int)e.Payload);
                    for (int i = node + 1; i < e.End; i = tape[i].End)
                        list.Add(ToData(i));
                    return new fsData(list);
                }
                default:
                {
                    var dict = new Dictionary<string, fsData>(StringComparer.Ordinal);
                    for (int k = node + 1; k < e.End; k = tape[k + 1].End)
                        dict.Add(tape.GetString(k), ToData(k + 1));
                    return new fsData(dict);
                }
            }
        }

        // ---- fsSerializer.TryDeserialize --------------------------------------------------------------

        private struct Meta
        {
            public int Ref, Id, Type, Content, Version;
        }

        private Meta ReadMeta(int node)
        {
            var meta = new Meta { Ref = -1, Id = -1, Type = -1, Content = -1, Version = -1 };
            ref Entry e = ref tape[node];
            if ((e.Flags & JsonTape.HasDollar) == 0)
                return meta;
            for (int k = node + 1; k < e.End; k = tape[k + 1].End)
            {
                if (tape.StringEquals(k, "$ref")) meta.Ref = k + 1;
                else if (tape.StringEquals(k, "$id")) meta.Id = k + 1;
                else if (tape.StringEquals(k, "$type")) meta.Type = k + 1;
                else if (tape.StringEquals(k, "$content")) meta.Content = k + 1;
                else if (tape.StringEquals(k, "$version")) meta.Version = k + 1;
            }
            return meta;
        }

        /// <summary>fsSerializer.ConvertLegacyData rewrites these shapes; leave them to vanilla.</summary>
        private bool IsLegacyShape(int node)
        {
            long count = tape[node].Payload;
            if (count == 1)
                return tape.Member(node, "ReferenceId") >= 0;
            if (count == 2)
                return tape.Member(node, "Data") >= 0 && (tape.Member(node, "Type") >= 0 || tape.Member(node, "SourceId") >= 0);
            return false;
        }

        private bool Value(int node, TypeModel storageModel, ref object result)
        {
            ref Entry e = ref tape[node];
            Type storage = storageModel.Type;

            if (e.Token == Token.Null)
            {
                if (storageModel.ReadBlocker != null && !storage.IsAbstract && !storage.IsInterface)
                    return Vanilla(node, storage, null, ref result); // custom processors see the null
                result = null;
                return true;
            }

            Meta meta = default;
            meta.Ref = meta.Id = meta.Type = meta.Content = meta.Version = -1;
            if (e.Token == Token.Object)
            {
                if (IsLegacyShape(node))
                    return Vanilla(node, storage, null, ref result);
                meta = ReadMeta(node);
                if (meta.Version >= 0)
                    return Vanilla(node, storage, null, ref result);
                if (meta.Ref >= 0)
                    return Reference(node, meta.Ref, storage, ref result);
            }

            // $type
            Type target = storage;
            if (meta.Type >= 0)
            {
                if (tape[meta.Type].Token != Token.String)
                    return Vanilla(node, storage, null, ref result);
                Type named = fsTypeCache.GetType(tape.GetString(meta.Type));
                if (named == null || !storage.IsAssignableFrom(named))
                    return Vanilla(node, storage, null, ref result);
                target = named;
            }
            fs.RemapAbstractStorageTypeToDefaultType(ref target);

            TypeModel m = target == storage ? storageModel : Models.Get(target);
            if (m.ReadBlocker != null)
                return Vanilla(node, storage, null, ref result);

            int content = meta.Content >= 0 ? meta.Content : node;
            if (meta.Id >= 0 && (tape[meta.Id].Token != Token.String || !NeedsInstance(m.Kind)))
                return Vanilla(node, storage, null, ref result);
            if (!Accepts(m, content))
                return Vanilla(node, storage, null, ref result);

            if (m.Kind == Kind.Nullable)
            {
                // fsNullableConverter deserializes the same data again as the underlying type.
                object inner = null;
                bool ok = Value(content, Models.Get(m.ElementType), ref inner);
                result = inner;
                return ok;
            }

            if (NeedsInstance(m.Kind) && (result == null || result.GetType() != target))
                result = m.Construct();
            if (meta.Id >= 0)
                references.AddReferenceWithId(int.Parse(tape.GetString(meta.Id)), result);

            bool succeeded = Convert(m, content, ref result);
            if (succeeded && m.Callbacks && result != null)
                ((ISerializationCallbackReceiver)result).OnAfterDeserialize();
            return succeeded;
        }

        private bool Reference(int node, int refNode, Type storage, ref object result)
        {
            if (tape[refNode].Token != Token.String)
                return Vanilla(node, storage, null, ref result);
            object target = references.GetReferenceObject(int.Parse(tape.GetString(refNode)));
            TypeModel m = Models.Get(target.GetType());
            if (m.ReadBlocker != null)
                return Vanilla(node, storage, null, ref result);
            result = target;
            if (m.Callbacks)
                ((ISerializationCallbackReceiver)result).OnAfterDeserialize();
            return true;
        }

        private static bool NeedsInstance(Kind kind) =>
            kind == Kind.Object || kind == Kind.Collection || kind == Kind.Dictionary;

        // ---- shape checks (before anything is created) ------------------------------------------------

        private bool Accepts(TypeModel m, int node)
        {
            Token t = tape[node].Token;
            switch (m.Kind)
            {
                case Kind.Bool:
                    return t == Token.True || t == Token.False;
                case Kind.Integer:
                case Kind.UInt64:
                case Kind.Single:
                case Kind.Double:
                case Kind.Decimal:
                    return t == Token.Integer || t == Token.Real;
                case Kind.String:
                    return t == Token.String;
                case Kind.Enum:
                    return t == Token.Integer || (t == Token.String && m.Enum.TryParse(tape.GetString(node), out _));
                case Kind.Guid:
                    return t == Token.String;
                case Kind.TypeName:
                    return t == Token.String && fsTypeCache.GetType(tape.GetString(node)) != null;
                case Kind.Date:
                    return t == Token.String && TryParseDate(m.Type, tape.GetString(node), out _);
                case Kind.Array:
                    return t == Token.Array;
                case Kind.Collection:
                    return t == Token.Array && m.Add != null;
                case Kind.Pair:
                    return t == Token.Object && tape.Member(node, "Key") >= 0 && tape.Member(node, "Value") >= 0;
                case Kind.Object:
                    return t == Token.Object;
                case Kind.Nullable:
                    return true;
                case Kind.Dictionary:
                    return AcceptsDictionary(m, node, t);
                default:
                    return false;
            }
        }

        private bool AcceptsDictionary(TypeModel m, int node, Token t)
        {
            ref Entry e = ref tape[node];
            if (t == Token.Array)
            {
                for (int i = node + 1; i < e.End; i = tape[i].End)
                {
                    if (tape[i].Token != Token.Object || tape.Member(i, "Key") < 0 || tape.Member(i, "Value") < 0)
                        return false;
                }
                return true;
            }
            if (t != Token.Object)
                return false;

            // Object form: every non-reserved key must convert from a string the way vanilla would.
            TypeModel key = Models.Get(m.KeyType);
            for (int k = node + 1; k < e.End; k = tape[k + 1].End)
            {
                string name = tape.GetString(k);
                if (fsSerializer.IsReservedKeyword(name))
                    continue;
                if (!TryKey(key, name, out _))
                    return false;
            }
            return true;
        }

        /// <summary>The key fsDictionaryConverter gets from TryDeserialize(new fsData(name), keyType).</summary>
        private static bool TryKey(TypeModel key, string name, out object value)
        {
            value = null;
            switch (key.Kind)
            {
                case Kind.String:
                    value = name;
                    return true;
                case Kind.Enum:
                    if (!key.Enum.TryParse(name, out long bits))
                        return false;
                    value = Enum.ToObject(key.Type, bits);
                    return true;
                case Kind.Date:
                    return TryParseDate(key.Type, name, out value);
                default:
                    return false; // everything else either fails in vanilla or needs its exact error path
            }
        }

        private static bool TryParseDate(Type type, string text, out object value)
        {
            value = null;
            if (type == typeof(DateTime))
            {
                if (!DateTime.TryParse(text, null, DateTimeStyles.RoundtripKind, out DateTime dt))
                    return false;
                value = dt;
                return true;
            }
            if (type == typeof(DateTimeOffset))
            {
                if (!DateTimeOffset.TryParse(text, null, DateTimeStyles.RoundtripKind, out DateTimeOffset dto))
                    return false;
                value = dto;
                return true;
            }
            if (type == typeof(TimeSpan))
            {
                if (!TimeSpan.TryParse(text, out TimeSpan ts))
                    return false;
                value = ts;
                return true;
            }
            return false;
        }

        // ---- converters ---------------------------------------------------------------------------------

        private bool Convert(TypeModel m, int node, ref object result)
        {
            switch (m.Kind)
            {
                case Kind.Bool:
                    result = tape[node].Token == Token.True;
                    return true;
                case Kind.Integer:
                case Kind.UInt64:
                case Kind.Single:
                case Kind.Double:
                case Kind.Decimal:
                    result = tape[node].Token == Token.Real
                        ? System.Convert.ChangeType(tape.Real(node), m.Type)
                        : System.Convert.ChangeType(tape.Integer(node), m.Type);
                    return true;
                case Kind.String:
                    result = tape.GetString(node);
                    return true;
                case Kind.Enum:
                    if (tape[node].Token == Token.Integer)
                    {
                        result = Enum.ToObject(m.Type, (object)(int)tape.Integer(node));
                    }
                    else
                    {
                        m.Enum.TryParse(tape.GetString(node), out long bits);
                        result = Enum.ToObject(m.Type, (object)bits);
                    }
                    return true;
                case Kind.Guid:
                    result = new Guid(tape.GetString(node));
                    return true;
                case Kind.TypeName:
                    result = fsTypeCache.GetType(tape.GetString(node));
                    return true;
                case Kind.Date:
                    TryParseDate(m.Type, tape.GetString(node), out result);
                    return true;
                case Kind.Object:
                    Fields(m, node, result);
                    return true;
                case Kind.Array:
                    result = ArrayItems(m, node);
                    return true;
                case Kind.Collection:
                    Items(m, node, result);
                    return true;
                case Kind.Dictionary:
                    return Entries(m, node, (IDictionary)result);
                case Kind.Pair:
                    result = PairOf(m, node);
                    return true;
                default:
                    throw new FallbackException($"{m.Type}: no reader for {m.Kind}");
            }
        }

        /// <summary>fsReflectedConverter: members in declaration order, existing values reused.</summary>
        private void Fields(TypeModel m, int node, object instance)
        {
            Member[] members = m.ReadMembers;
            ref Entry obj = ref tape[node];
            // JSON members usually come in the same order as the model's: try the next key first.
            int nextKey = node + 1;
            foreach (Member member in members)
            {
                int valueNode = -1;
                if (nextKey < obj.End && tape.StringEquals(nextKey, member.JsonName))
                    valueNode = nextKey + 1;
                else
                    valueNode = tape.Member(node, member.JsonName);
                if (valueNode < 0)
                    continue;
                nextKey = tape[valueNode].End;

                object value = null;
                if (member.Meta.CanRead && !(member.IsField && IsScalar(member.Storage.Kind)))
                    value = member.Get(instance);

                bool ok = member.HasOverrideConverter
                    ? Vanilla(valueNode, member.StorageType, member.Meta.OverrideConverterType, ref value)
                    : Value(valueNode, member.Storage, ref value);
                if (ok)
                    member.Set(instance, value);
            }
        }

        // Converters for these replace the value outright, so reading the old one first is pointless.
        private static bool IsScalar(Kind kind) => kind >= Kind.Bool && kind <= Kind.Date;

        private Array ArrayItems(TypeModel m, int node)
        {
            ref Entry e = ref tape[node];
            var items = new ArrayList((int)e.Payload);
            TypeModel element = Models.Get(m.ElementType);
            for (int i = node + 1; i < e.End; i = tape[i].End)
            {
                object item = null;
                if (Value(i, element, ref item))
                    items.Add(item);
            }
            return items.ToArray(m.ElementType);
        }

        /// <summary>fsIEnumerableConverter: overwrite by index where possible, otherwise clear and add.</summary>
        private void Items(TypeModel m, int node, object collection)
        {
            if (m.SetItem == null)
                m.Clear?.Invoke(collection, null);
            int existing = m.Count != null ? (int)m.Count.Invoke(collection, null) : 0;

            ref Entry e = ref tape[node];
            TypeModel element = Models.Get(m.ElementType);
            int index = 0;
            for (int i = node + 1; i < e.End; i = tape[i].End, index++)
            {
                object item = null;
                if (m.GetItem != null && index < existing)
                    item = m.GetItem.Invoke(collection, new object[] { index });
                if (!Value(i, element, ref item))
                    continue;
                if (m.SetItem != null && index < existing)
                    m.SetItem.Invoke(collection, new[] { index, item });
                else
                    m.Add(collection, item);
            }
        }

        private bool Entries(TypeModel m, int node, IDictionary dictionary)
        {
            ref Entry e = ref tape[node];
            TypeModel keyModel = Models.Get(m.KeyType);
            TypeModel valueModel = Models.Get(m.ValueType);
            if (e.Token == Token.Array)
            {
                for (int i = node + 1; i < e.End; i = tape[i].End)
                {
                    object key = null, value = null;
                    if (!Value(tape.Member(i, "Key"), keyModel, ref key))
                        return false;
                    if (!Value(tape.Member(i, "Value"), valueModel, ref value))
                        return false;
                    AddEntry(dictionary, key, value);
                }
                return true;
            }

            for (int k = node + 1; k < e.End; k = tape[k + 1].End)
            {
                string name = tape.GetString(k);
                if (fsSerializer.IsReservedKeyword(name))
                    continue;
                TryKey(keyModel, name, out object key);
                object value = null;
                if (!Value(k + 1, valueModel, ref value))
                    return false;
                AddEntry(dictionary, key, value);
            }
            return true;
        }

        /// <summary>fsDictionaryConverter.AddItemToDictionary.</summary>
        private static void AddEntry(IDictionary dictionary, object key, object value)
        {
            if (key != null && value != null)
            {
                dictionary[key] = value;
                return;
            }
            Type collection = fsReflectionUtility.GetInterface(dictionary.GetType(), typeof(ICollection<>));
            if (collection == null)
                throw new FallbackException(dictionary.GetType() + " does not extend ICollection"); // vanilla warns
            object pair = Activator.CreateInstance(collection.GetGenericArguments()[0], key, value);
            fsPortableReflection.GetFlattenedMethod(collection, "Add").Invoke(dictionary, new[] { pair });
        }

        private object PairOf(TypeModel m, int node)
        {
            object key = null, value = null;
            Value(tape.Member(node, "Key"), Models.Get(m.KeyType), ref key);
            Value(tape.Member(node, "Value"), Models.Get(m.ValueType), ref value);
            return Activator.CreateInstance(m.Type, key, value);
        }
    }
}
