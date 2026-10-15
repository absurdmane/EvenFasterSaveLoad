using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using FullSerializer;
using UnityEngine;

namespace EvenFasterSaveLoad.Json
{
    /// <summary>
    /// Produces the same bytes as <c>fsJsonPrinter.CompressedJson(fsSerializer.TrySerialize(...))</c>, in two passes.
    ///
    /// FullSerializer numbers every tracked object (classes, collections) in first-encounter order, writes
    /// <c>{"$ref":"n"}</c> for repeat encounters, and adds <c>"$id":"n"</c> to an object's definition only if
    /// a reference to it shows up somewhere. That last bit isn't known until the whole graph has been seen, so:
    ///
    /// 1. Census (main thread): walk the graph in FullSerializer's order, fire OnBeforeSerialize callbacks,
    ///    number tracked objects and remember which ones are met more than once. Leaf values are skipped.
    /// 2. Emit: walk again and write final JSON straight away. Big lists right under the root are cut into
    ///    ranges written by several threads; the census recorded the id counter at each element, which is all
    ///    a range needs to decide definition vs. reference on its own.
    ///
    /// Anything the census and the emitter disagree about (e.g. a getter returning new objects each call)
    /// throws <see cref="FallbackException"/>, and the caller uses vanilla instead.
    /// </summary>
    internal sealed class GraphWriter
    {
        private const int ParallelThreshold = 64;
        private const int MinItemsPerRange = 16;

        private readonly Dictionary<object, int> ids = new Dictionary<object, int>(IdentityComparer.Instance);
        private readonly Dictionary<object, int[]> rangeStarts = new Dictionary<object, int[]>(IdentityComparer.Instance);
        private bool[] repeated = new bool[1024];
        private int idCount;
        private bool needsMainThread;
        private string mainThreadReason;
        private bool parallel;

        // Native-backed direct converters (AnimationCurve, Gradient) must run on the main thread: their output is
        // computed during the census and reused by the emitter, so they don't force single-threaded writing.
        private readonly Dictionary<object, fsData> directData = new Dictionary<object, fsData>(IdentityComparer.Instance);

        public readonly ByteSink Output = new ByteSink(1 << 20);

        /// <summary>Worker count for the emit pass; 1 disables parallel emission.</summary>
        public int Threads = 1;

        /// <summary>Whether the last document was emitted by several threads (for logging).</summary>
        public bool UsedParallel => parallel;

        /// <summary>Why the last document was written by one thread (for logging).</summary>
        public string SingleThreadReason =>
            parallel ? null : Threads <= 1 ? "Threads = 1" : mainThreadReason ?? (rangeStarts.Count == 0 ? "no large list under the root" : null);

        public void Serialize(Type storageType, object root)
        {
            ids.Clear();
            rangeStarts.Clear();
            directData.Clear();
            Array.Clear(repeated, 0, Math.Min(idCount, repeated.Length));
            idCount = 0;
            needsMainThread = false;
            mainThreadReason = null;
            Output.Clear();
            Output.CaptureCulture();

            TypeModel storage = Models.Get(storageType);
            Census(storage, root, isRoot: true, underRoot: false);

            parallel = Threads > 1 && !needsMainThread && rangeStarts.Count > 0;
            var emitter = new Emitter(this, Output, 0);
            emitter.Value(storage, root);
            if (emitter.Next != idCount)
                throw new FallbackException("object graph changed between passes");
        }

        // ---- pass 1 -------------------------------------------------------------------------------------

        private void Census(TypeModel storage, object value, bool isRoot, bool underRoot)
        {
            if (value == null)
                return;
            TypeModel m = value.GetType() == storage.Type ? storage : Models.Get(value.GetType());
            if (m.WriteBlocker != null)
                throw new FallbackException(m.WriteBlocker);
            bool precomputed = m.Kind == Kind.Direct && m.Tracked; // class instances: cache by identity
            if (m.MainThreadOnly && !precomputed && !needsMainThread)
            {
                needsMainThread = true;
                mainThreadReason = m.MainThreadReason ?? m.Type.Name;
            }

            // FullSerializer runs processors on every encounter, references included, before the cycle check.
            if (m.Callbacks)
                ((ISerializationCallbackReceiver)value).OnBeforeSerialize();

            if (m.Tracked)
            {
                if (ids.TryGetValue(value, out int id))
                {
                    repeated[id] = true;
                    return;
                }
                ids.Add(value, idCount);
                if (++idCount > repeated.Length)
                    Array.Resize(ref repeated, repeated.Length * 2);
            }
            if (precomputed && m.MainThreadOnly)
                directData[value] = ConvertDirect(m, value);

            switch (m.Kind)
            {
                case Kind.Object:
                    if (m.Shadowed.Length > 0 && !m.ShadowingSupported)
                        throw new FallbackException($"{m.Type}: members sharing a JSON name hold objects");
                    foreach (Member member in m.WriteMembers)
                    {
                        TypeModel memberStorage = member.Storage;
                        if (!memberStorage.IsLeaf)
                            Census(memberStorage, member.Get(value), isRoot: false, underRoot: isRoot);
                    }
                    break;

                case Kind.Array:
                case Kind.Collection:
                {
                    TypeModel element = Models.Get(m.ElementType);
                    if (element.IsLeaf)
                        break;
                    if (underRoot && value is IList list && list.Count >= ParallelThreshold)
                    {
                        var starts = new int[list.Count + 1];
                        for (int i = 0; i < list.Count; i++)
                        {
                            starts[i] = idCount;
                            Census(element, list[i], false, false);
                        }
                        starts[list.Count] = idCount;
                        rangeStarts[value] = starts;
                    }
                    else
                    {
                        foreach (object item in (IEnumerable)value)
                            Census(element, item, false, false);
                    }
                    break;
                }

                case Kind.Dictionary:
                {
                    TypeModel key = Models.Get(m.KeyType);
                    TypeModel val = Models.Get(m.ValueType);
                    if (key.IsLeaf && val.IsLeaf)
                        break;
                    IDictionaryEnumerator e = ((IDictionary)value).GetEnumerator();
                    while (e.MoveNext())
                    {
                        Census(key, e.Key, false, false);
                        Census(val, e.Value, false, false);
                    }
                    break;
                }

                case Kind.Pair:
                    Census(Models.Get(m.KeyType), m.PairKey.GetValue(value, null), false, false);
                    Census(Models.Get(m.ValueType), m.PairValue.GetValue(value, null), false, false);
                    break;
            }
        }

        // ---- pass 2 -------------------------------------------------------------------------------------

        private sealed class Emitter
        {
            private readonly GraphWriter g;
            private readonly ByteSink s;
            public int Next;

            public Emitter(GraphWriter graph, ByteSink sink, int next)
            {
                g = graph;
                s = sink;
                Next = next;
            }

            public void Value(TypeModel storage, object value)
            {
                if (value == null)
                {
                    s.Null();
                    return;
                }
                TypeModel m = value.GetType() == storage.Type ? storage : Models.Get(value.GetType());
                if (m.WriteBlocker != null)
                    throw new FallbackException(m.WriteBlocker);

                int id = -1;
                if (m.Tracked)
                {
                    if (!g.ids.TryGetValue(value, out id))
                        throw new FallbackException($"{m.Type} instance appeared after the census");
                    if (id < Next)
                    {
                        s.Ascii("{\"$ref\":");
                        s.Id(id);
                        s.Byte((byte)'}');
                        return;
                    }
                    if (id != Next)
                        throw new FallbackException("object graph changed between passes");
                    Next++;
                    if (!g.repeated[id])
                        id = -1;
                }
                bool withType = m.Type != storage.Type && storage.Polymorphic;

                switch (m.Kind)
                {
                    case Kind.Object:
                        s.Byte((byte)'{');
                        Close(Members(m, value) > 0, withType, id, m);
                        return;
                    case Kind.Pair:
                        s.Ascii("{\"Key\":");
                        Value(Models.Get(m.KeyType), m.PairKey.GetValue(value, null));
                        s.Ascii(",\"Value\":");
                        Value(Models.Get(m.ValueType), m.PairValue.GetValue(value, null));
                        Close(true, withType, id, m);
                        return;
                    case Kind.Dictionary:
                        if (TryObjectForm((IDictionary)value, m, out List<string> keys))
                        {
                            s.Byte((byte)'{');
                            Close(DictionaryBody((IDictionary)value, m, keys) > 0, withType, id, m);
                            return;
                        }
                        break;
                    case Kind.Direct:
                    {
                        fsData data;
                        if (!g.directData.TryGetValue(value, out data))
                        {
                            if (m.MainThreadOnly && g.parallel)
                                throw new FallbackException($"{m.Type}: needs the main thread but wasn't precomputed");
                            data = ConvertDirect(m, value);
                        }
                        if (data.IsDictionary)
                        {
                            s.Byte((byte)'{');
                            Close(DataMembers(data) > 0, withType, id, m);
                            return;
                        }
                        Wrapped(withType, id, m, () => Print(data));
                        return;
                    }
                }

                // Not a JSON object: FullSerializer wraps it as {"$content":...} if it needs $type/$id.
                if (!withType && id < 0)
                {
                    Content(m, value);
                    return;
                }
                s.Ascii("{\"$content\":");
                Content(m, value);
                Close(true, withType, id, m);
            }

            private void Wrapped(bool withType, int id, TypeModel m, Action content)
            {
                if (!withType && id < 0)
                {
                    content();
                    return;
                }
                s.Ascii("{\"$content\":");
                content();
                Close(true, withType, id, m);
            }

            /// <summary>Appends $type then $id (FullSerializer's order) and closes the object.</summary>
            private void Close(bool hasMembers, bool withType, int id, TypeModel m)
            {
                if (withType)
                {
                    if (hasMembers)
                        s.Byte((byte)',');
                    s.Ascii("\"$type\":");
                    s.Bytes(m.TypeNameJson);
                    hasMembers = true;
                }
                if (id >= 0)
                {
                    if (hasMembers)
                        s.Byte((byte)',');
                    s.Ascii("\"$id\":");
                    s.Id(id);
                }
                s.Byte((byte)'}');
            }

            private int Members(TypeModel m, object value)
            {
                Member[] members = m.WriteMembers;
                int[] order = m.EmitOrder;
                for (int i = 0; i < order.Length; i++)
                {
                    Member member = members[order[i]];
                    if (i > 0)
                        s.Byte((byte)',');
                    s.Bytes(member.KeyJson);
                    Action<object, ByteSink> primitive = member.PrimitiveEmit;
                    if (primitive != null)
                        primitive(value, s);
                    else
                        Value(member.Storage, member.Get(value));
                }
                return order.Length;
            }

            private void Content(TypeModel m, object value)
            {
                switch (m.Kind)
                {
                    case Kind.Bool:
                        s.Bool((bool)value);
                        break;
                    case Kind.Integer:
                        s.Integer(Convert.ToInt64(value));
                        break;
                    case Kind.UInt64:
                        s.Integer(checked((long)(ulong)value));
                        break;
                    case Kind.Single:
                        s.Single((float)value);
                        break;
                    case Kind.Double:
                        s.Double((double)value);
                        break;
                    case Kind.Decimal:
                        s.Double((double)(decimal)value);
                        break;
                    case Kind.String:
                        s.String((string)value);
                        break;
                    case Kind.Char:
                        s.String(((char)value).ToString());
                        break;
                    case Kind.Enum:
                        s.String(m.Enum.Name(value) ?? throw new FallbackException($"{m.Type}: undefined value {value}"));
                        break;
                    case Kind.Guid:
                        s.String(((Guid)value).ToString());
                        break;
                    case Kind.TypeName:
                        s.String(((Type)value).FullName ?? throw new FallbackException("type without a full name"));
                        break;
                    case Kind.Date:
                        s.String(DateText(value));
                        break;
                    case Kind.Array:
                    case Kind.Collection:
                        Items(m, value);
                        break;
                    case Kind.Dictionary:
                        PairList((IDictionary)value, m);
                        break;
                    default:
                        throw new FallbackException($"{m.Type}: no writer for {m.Kind}");
                }
            }

            private void Items(TypeModel m, object value)
            {
                TypeModel element = Models.Get(m.ElementType);
                if (g.parallel && g.rangeStarts.TryGetValue(value, out int[] starts))
                {
                    ParallelItems((IList)value, element, starts);
                    return;
                }
                s.Byte((byte)'[');
                bool first = true;
                foreach (object item in (IEnumerable)value)
                {
                    if (!first)
                        s.Byte((byte)',');
                    first = false;
                    Value(element, item);
                }
                s.Byte((byte)']');
            }

            private void ParallelItems(IList list, TypeModel element, int[] starts)
            {
                int count = list.Count;
                if (count + 1 != starts.Length || starts[0] != Next)
                    throw new FallbackException("list changed between passes");

                int ranges = Math.Max(1, Math.Min(g.Threads, count / MinItemsPerRange));
                var sinks = new ByteSink[ranges];
                try
                {
                    Parallel.For(0, ranges, new ParallelOptions { MaxDegreeOfParallelism = g.Threads }, r =>
                    {
                        int from = (int)((long)count * r / ranges);
                        int to = (int)((long)count * (r + 1) / ranges);
                        ByteSink sink = SinkPool.Rent(s);
                        sinks[r] = sink;
                        var worker = new Emitter(g, sink, starts[from]);
                        for (int i = from; i < to; i++)
                        {
                            if (i > from)
                                sink.Byte((byte)',');
                            worker.Value(element, list[i]);
                        }
                        if (worker.Next != starts[to])
                            throw new FallbackException("parallel range diverged from the census");
                    });
                }
                catch (AggregateException e)
                {
                    ReturnAll(sinks);
                    ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw();
                }

                s.Byte((byte)'[');
                for (int r = 0; r < ranges; r++)
                {
                    if (r > 0)
                        s.Byte((byte)',');
                    s.Append(sinks[r]);
                }
                s.Byte((byte)']');
                ReturnAll(sinks);
                Next = starts[count];
            }

            private static void ReturnAll(ByteSink[] sinks)
            {
                foreach (ByteSink sink in sinks)
                {
                    if (sink != null)
                        SinkPool.Return(sink);
                }
            }

            /// <summary>
            /// fsDictionaryConverter writes a JSON object if every key serializes to a JSON string,
            /// otherwise a list of {"Key":..,"Value":..}. Later duplicates of a key string would overwrite
            /// earlier ones in place, which we don't reproduce.
            /// </summary>
            private bool TryObjectForm(IDictionary dictionary, TypeModel m, out List<string> keys)
            {
                keys = null;
                TypeModel keyStorage = Models.Get(m.KeyType);
                if (dictionary.Count == 0)
                    return true;
                if (keyStorage.Kind == Kind.String)
                    return true; // keys are already unique strings

                keys = new List<string>(dictionary.Count);
                foreach (object key in dictionary.Keys)
                {
                    TypeModel km = key.GetType() == keyStorage.Type ? keyStorage : Models.Get(key.GetType());
                    if (km.WriteBlocker != null || km.Tracked || (km.Type != keyStorage.Type && keyStorage.Polymorphic))
                        return false;
                    string text;
                    switch (km.Kind)
                    {
                        case Kind.String: text = (string)key; break;
                        case Kind.Char: text = ((char)key).ToString(); break;
                        case Kind.Enum: text = km.Enum.Name(key) ?? throw new FallbackException($"{km.Type}: undefined key {key}"); break;
                        case Kind.Guid: text = ((Guid)key).ToString(); break;
                        case Kind.TypeName: text = ((Type)key).FullName; break;
                        case Kind.Date: text = DateText(key); break;
                        default: return false;
                    }
                    keys.Add(text ?? throw new FallbackException("null dictionary key name"));
                }
                if (new HashSet<string>(keys, StringComparer.Ordinal).Count != keys.Count)
                    throw new FallbackException($"{m.Type}: two keys serialize to the same name");
                return true;
            }

            private int DictionaryBody(IDictionary dictionary, TypeModel m, List<string> keys)
            {
                TypeModel valueStorage = Models.Get(m.ValueType);
                IDictionaryEnumerator e = dictionary.GetEnumerator();
                int i = 0;
                while (e.MoveNext())
                {
                    if (i > 0)
                        s.Byte((byte)',');
                    s.Byte((byte)'"');
                    s.Raw(keys != null ? keys[i] : (string)e.Key);
                    s.Ascii("\":");
                    Value(valueStorage, e.Value);
                    i++;
                }
                return i;
            }

            private void PairList(IDictionary dictionary, TypeModel m)
            {
                TypeModel keyStorage = Models.Get(m.KeyType);
                TypeModel valueStorage = Models.Get(m.ValueType);
                s.Byte((byte)'[');
                IDictionaryEnumerator e = dictionary.GetEnumerator();
                bool first = true;
                while (e.MoveNext())
                {
                    if (!first)
                        s.Byte((byte)',');
                    first = false;
                    s.Ascii("{\"Key\":");
                    Value(keyStorage, e.Key);
                    s.Ascii(",\"Value\":");
                    Value(valueStorage, e.Value);
                    s.Byte((byte)'}');
                }
                s.Byte((byte)']');
            }


            private int DataMembers(fsData data)
            {
                int n = 0;
                foreach (KeyValuePair<string, fsData> kv in data.AsDictionary)
                {
                    if (n++ > 0)
                        s.Byte((byte)',');
                    s.Byte((byte)'"');
                    s.Raw(kv.Key);
                    s.Ascii("\":");
                    Print(kv.Value);
                }
                return n;
            }

            /// <summary>fsJsonPrinter.BuildCompressedString for converter-produced data.</summary>
            private void Print(fsData data)
            {
                switch (data.Type)
                {
                    case fsDataType.Null: s.Null(); break;
                    case fsDataType.Boolean: s.Bool(data.AsBool); break;
                    case fsDataType.Double: s.Double(data.AsDouble); break;
                    case fsDataType.Int64: s.Integer(data.AsInt64); break;
                    case fsDataType.String: s.String(data.AsString); break;
                    case fsDataType.Object:
                        s.Byte((byte)'{');
                        DataMembers(data);
                        s.Byte((byte)'}');
                        break;
                    case fsDataType.Array:
                        s.Byte((byte)'[');
                        bool first = true;
                        foreach (fsData item in data.AsList)
                        {
                            if (!first)
                                s.Byte((byte)',');
                            first = false;
                            Print(item);
                        }
                        s.Byte((byte)']');
                        break;
                    default:
                        throw new FallbackException("unknown fsData type " + data.Type);
                }
            }
        }

        private static fsData ConvertDirect(TypeModel m, object value)
        {
            fsResult result = m.Converter.TrySerialize(value, out fsData data, m.Type);
            if (result.Failed || result.HasWarnings)
                throw new FallbackException($"{m.Converter.GetType().Name}: {result.FormattedMessages}");
            return data;
        }

        private static string DateText(object value)
        {
            switch (value)
            {
                case DateTime dt: return dt.ToString(Models.Serializer.Config.CustomDateTimeFormatString ?? "o");
                case DateTimeOffset dto: return dto.ToString("o");
                case TimeSpan ts: return ts.ToString();
                default: throw new FallbackException("unexpected date type " + value.GetType());
            }
        }

        private static class SinkPool
        {
            private static readonly Stack<ByteSink> Free = new Stack<ByteSink>();

            public static ByteSink Rent(ByteSink like)
            {
                ByteSink sink;
                lock (Free)
                    sink = Free.Count > 0 ? Free.Pop() : new ByteSink(1 << 18);
                sink.Clear();
                sink.IntegerFormat = like.IntegerFormat;
                sink.PlainMinus = like.PlainMinus;
                return sink;
            }

            public static void Return(ByteSink sink)
            {
                lock (Free)
                    Free.Push(sink);
            }
        }
    }

    internal sealed class IdentityComparer : IEqualityComparer<object>
    {
        public static readonly IdentityComparer Instance = new IdentityComparer();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
