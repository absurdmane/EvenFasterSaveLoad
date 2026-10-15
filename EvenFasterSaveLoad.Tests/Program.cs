using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using EvenFasterSaveLoad.Json;
using FullSerializer;
using UnityEngine;

namespace EvenFasterSaveLoad.Tests
{
    public static class Program
    {
        private static int failures;
        private static int passes;

        public static int Main(string[] args)
        {
            FsHost.Override = new fsSerializer();

            Section("serializer: byte-identical output, 1 and 8 threads");
            foreach (int threads in new[] { 1, 8 })
            {
                SerializeCase($"everything [{threads}t]", typeof(Everything), Model.Everything(), threads);
                SerializeCase($"everything as object [{threads}t]", typeof(object), Model.Everything(), threads);
                SerializeCase($"save-like root, shared refs across elements [{threads}t]", typeof(SaveLike), Model.SaveLike(500), threads);
                SerializeCase($"save-like root, small [{threads}t]", typeof(SaveLike), Model.SaveLike(10), threads);
            }
            SerializeCase("char field (write only; vanilla can't read char)", typeof(WithChar), new WithChar(), 1);
            SerializeCase("string root", typeof(string), "just a string", 1);
            SerializeCase("int root", typeof(int), 7, 1);
            SerializeCase("empty object", typeof(Everything), Model.Sparse(), 1);
            SerializeCase("flags-keyed dictionary", typeof(Dictionary<Perms, int>), new Dictionary<Perms, int> { [Perms.Read] = 1, [Perms.Read | Perms.Write] = 2 }, 1);
            SerializeCase("object-keyed dictionary", typeof(Dictionary<object, int>), new Dictionary<object, int> { ["a"] = 1, [3] = 2 }, 1);
            SerializeCase("redeclared field (like Animal/Chicken)", typeof(Chicken), new Chicken(), 1);
            RoundTrip("redeclared field read", typeof(Chicken), new Chicken());
            ExpectFallback("redeclared field holding objects", typeof(DuplicateObjects), new DuplicateObjects());

            Section("deserializer: same objects and same success/warning outcome as vanilla");
            RoundTrip("everything", typeof(Everything), Model.Everything());
            RoundTrip("save-like", typeof(SaveLike), Model.SaveLike(200));
            RoundTrip("sparse", typeof(Everything), Model.Sparse());
            RoundTrip("as object", typeof(object), Model.Everything());
            CheckIdentity();
            ReadParity("missing commas", typeof(List<int>), "[1 2 3]");
            ReadParity("trailing comma", typeof(List<int>), "[1,2,]");
            ReadParity("whitespace + $content", typeof(object), " { \"$content\" : [1, 2.5, \"x\"] , \"$type\" : \"System.Collections.Generic.List`1[[System.Object, mscorlib]]\" } ");
            ReadParity("non-string $type -> warning", typeof(Circle), "{\"Name\":\"x\",\"$type\":5}");
            ReadParity("unassignable $type -> warning", typeof(Circle), "{\"Name\":\"x\",\"$type\":\"EvenFasterSaveLoad.Tests.Square\"}");
            ReadParity("wrong primitive shape", typeof(Everything), "{\"I\":\"oops\",\"F1\":true,\"Text\":5}");
            ReadParity("enum by number and by bad name", typeof(Everything), "{\"Color\":2,\"Perms\":\"Read,Nope\"}");
            ReadParity("legacy shape", typeof(Point), "{\"SourceId\":\"0\",\"Data\":{\"X\":4}}");
            ReadParity("unknown members ignored", typeof(Point), "{\"X\":1,\"Zzz\":[1,{\"a\":null}],\"Y\":2}");
            ReadParity("members out of order", typeof(Point), "{\"Y\":2,\"X\":1}");
            ReadParity("dictionary pair-list form", typeof(Dictionary<int, string>), "[{\"Key\":1,\"Value\":\"a\"},{\"Key\":2,\"Value\":null}]");
            ReadParity("dictionary bad key", typeof(Dictionary<Color, int>), "{\"Red\":1,\"Purple\":2}");
            ReadParity("escapes", typeof(string), "\"a\\u0041\\n\\\"\\/\\0\"");
            ReadParity("numbers", typeof(double[]), "[1, -2, 3.5, 1e3, -1E-3, Infinity, -Infinity, NaN, +4]");
            ReadParity("nullable", typeof(WithNullable), "{\"A\":true,\"B\":null,\"C\":\"Blue\",\"D\":3.25}");
            ReadParity("interface storage", typeof(WithInterfaces), "{\"Items\":[1,2],\"Map\":{\"k\":1}}");
            ReadParity("reuse existing list instance", typeof(Holder), "{\"Tags\":[\"x\"],\"Id\":3}");
            ReadFallback("comments", typeof(List<int>), "[1, /* two */ 2]");
            ReadFallback("duplicate key", typeof(Point), "{\"X\":1,\"X\":2}");

            Section("safety check before writing");
            CheckSafetyNet();

            Section("game types: what would go through vanilla");
            ScanGameTypes();

            foreach (string path in args)
                Benchmark(path);

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? $"ALL {passes} PASSED" : $"{failures} FAILED, {passes} passed");
            return failures == 0 ? 0 : 1;
        }

        // ---- helpers -----------------------------------------------------------------------------------

        private static void Section(string title) => Console.WriteLine($"\n== {title}");

        private static void Pass(string what)
        {
            passes++;
            Console.WriteLine($"ok   {what}");
        }

        private static void Fail(string what)
        {
            failures++;
            Console.WriteLine($"FAIL {what}");
        }

        private static string Vanilla(Type type, object value)
        {
            FsHost.Serializer.TrySerialize(type, value, out fsData data).AssertSuccessWithoutWarnings();
            return fsJsonPrinter.CompressedJson(data);
        }

        private static string Diff(string a, string b)
        {
            int at = 0;
            while (at < a.Length && at < b.Length && a[at] == b[at])
                at++;
            int from = Math.Max(0, at - 60);
            string Cut(string s) => s.Substring(Math.Min(from, s.Length), Math.Min(160, Math.Max(0, s.Length - from)));
            return $" at {at}\n    fast:    {Cut(a)}\n    vanilla: {Cut(b)}";
        }

        private static void SerializeCase(string name, Type type, object value, int threads)
        {
            string vanilla = Vanilla(type, value);
            var writer = new GraphWriter { Threads = threads };
            writer.Serialize(type, value);
            string fast = writer.Output.Decode();
            string mode = writer.UsedParallel ? ", parallel" : "";
            if (fast == vanilla)
                Pass($"{name} ({vanilla.Length} chars{mode})");
            else
                Fail($"{name}{Diff(fast, vanilla)}");
        }

        private static void ExpectFallback(string name, Type type, object value)
        {
            try
            {
                new GraphWriter().Serialize(type, value);
                Fail($"{name}: expected a vanilla fallback");
            }
            catch (FallbackException e)
            {
                Pass($"{name} -> vanilla ({e.Message})");
            }
        }

        private static void RoundTrip(string name, Type type, object value)
        {
            string json = Vanilla(type, value);
            ReadParity(name, type, json);
        }

        /// <summary>Vanilla and fast read the same JSON: same printed result, same failed/warning flags.</summary>
        private static void ReadParity(string name, Type type, string json)
        {
            fsSerializer fs = FsHost.Serializer;
            object viaVanilla = null;
            fsResult vr;
            try
            {
                vr = fs.TryDeserialize(fsJsonParser.Parse(json), type, ref viaVanilla);
            }
            catch (Exception e)
            {
                Fail($"{name}: vanilla threw {e.GetType().Name} - bad test input");
                return;
            }

            object viaFast;
            fsResult fr;
            try
            {
                viaFast = new TapeReader().Read(type, json, out fr);
            }
            catch (FallbackException e)
            {
                Fail($"{name}: unexpected fallback: {e.Message}");
                return;
            }

            string a = Print(type, viaFast), b = Print(type, viaVanilla);
            bool sameFlags = vr.Failed == fr.Failed && vr.HasWarnings == fr.HasWarnings;
            if (a == b && sameFlags)
                Pass($"{name} (failed={vr.Failed}, warnings={vr.HasWarnings})");
            else if (!sameFlags)
                Fail($"{name}: vanilla failed={vr.Failed} warnings={vr.HasWarnings} [{vr.FormattedMessages}], fast failed={fr.Failed} warnings={fr.HasWarnings} [{fr.FormattedMessages}]");
            else
                Fail($"{name}{Diff(a, b)}");
        }

        private static void ReadFallback(string name, Type type, string json)
        {
            try
            {
                new TapeReader().Read(type, json, out _);
                Fail($"{name}: expected a vanilla fallback");
            }
            catch (FallbackException e)
            {
                Pass($"{name} -> vanilla ({e.Message})");
            }
        }

        private static string Print(Type type, object value)
        {
            try
            {
                FsHost.Serializer.TrySerialize(type, value, out fsData data);
                return data == null ? "<null>" : fsJsonPrinter.CompressedJson(data);
            }
            catch (Exception e)
            {
                return "<" + e.GetType().Name + ">";
            }
        }

        private static void CheckIdentity()
        {
            string json = Vanilla(typeof(Everything), Model.Everything());
            var e = (Everything)new TapeReader().Read(typeof(Everything), json, out fsResult r);
            bool ok = r.Succeeded && !r.HasWarnings && e.Self == e && e.Single.Next.Next == e.Single &&
                      e.ShapeArray[0] == e.ShapeArray[2] && e.Callback.After == 1;
            if (ok)
                Pass("object identity, cycles and OnAfterDeserialize preserved");
            else
                Fail("object identity / callbacks");

            var save = Model.SaveLike(100);
            var back = (SaveLike)new TapeReader().Read(typeof(SaveLike), Vanilla(typeof(SaveLike), save), out _);
            bool shared = back.Data[3].Payload == back.Data[90].Payload && back.Data[4].Payload.Owner == back &&
                          back.Data[back.Data.Count - 1] == back.Data[5] && back.Index["k7"] == back.Data[7];
            if (shared)
                Pass("references across list elements preserved");
            else
                Fail("references across list elements");
        }

        /// <summary>
        /// Identical output passes; a fast result that no longer matches the object (simulated by changing the
        /// object after writing) must make the check hand back the vanilla JSON and trip the fast serializer.
        /// </summary>
        private static void CheckSafetyNet()
        {
            var save = Model.SaveLike(100);
            var writer = new GraphWriter { Threads = 4 };
            writer.Serialize(typeof(SaveLike), save);
            var pending = new EvenFasterSaveLoad.Patches.SavePatches.PendingWrite { Writer = writer, Type = typeof(SaveLike), Value = save };

            string identical = EvenFasterSaveLoad.Patches.SavePatches.SafetyCheck("Same.tnmt", pending);
            if (identical == null && !Plugin.FastSerializerTripped)
                Pass("identical output is written as is");
            else
                Fail("identical output was rejected");

            save.Data[42].Id = 999999; // the fast bytes are now "wrong" for this object
            string replacement = EvenFasterSaveLoad.Patches.SavePatches.SafetyCheck("Broken.tnmt", pending);
            if (replacement != null && replacement == Vanilla(typeof(SaveLike), save) && Plugin.FastSerializerTripped)
                Pass("a difference writes the game's own JSON and switches fast saving off");
            else
                Fail("a difference was not caught");
        }

        // ---- game types --------------------------------------------------------------------------------

        private static void ScanGameTypes()
        {
            Assembly game = typeof(SaveController).Assembly;
            Type unityObject = typeof(UnityEngine.Object);
            Type[] types;
            try
            {
                types = game.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }

            // Saved objects: anything with a public GUID field (SaveController.SerializeData looks it up by name).
            var roots = types.Where(t => unityObject.IsAssignableFrom(t) && !t.IsAbstract && t.GetField("GUID") != null).ToList();
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>(roots);
            var blockers = new SortedDictionary<string, List<string>>();
            int mainThread = 0;

            void Note(string kind, string reason, Type t)
            {
                string key = kind + ": " + reason;
                if (!blockers.TryGetValue(key, out var list))
                    blockers[key] = list = new List<string>();
                if (list.Count < 6)
                    list.Add(t.Name);
            }

            while (queue.Count > 0)
            {
                Type t = queue.Dequeue();
                if (t == null || !seen.Add(t))
                    continue;
                TypeModel m;
                try
                {
                    m = Models.Get(t);
                }
                catch (Exception e)
                {
                    Note("model error", e.GetType().Name, t);
                    continue;
                }
                if (m.WriteBlocker != null && !t.IsAbstract && !t.IsInterface)
                    Note("write", Short(m.WriteBlocker), t);
                if (m.ReadBlocker != null && !t.IsAbstract && !t.IsInterface && m.Kind != Kind.Direct)
                    Note("read", Short(m.ReadBlocker), t);
                if (m.MainThreadOnly)
                    mainThread++;

                foreach (Type next in new[] { m.ElementType, m.KeyType, m.ValueType })
                    queue.Enqueue(next);
                if (m.WriteMembers != null)
                {
                    foreach (Member member in m.WriteMembers.Concat(m.ReadMembers))
                    {
                        queue.Enqueue(member.StorageType);
                        // Runtime values can be any concrete subclass from the game assembly.
                        if (!member.StorageType.IsSealed && !member.StorageType.IsValueType)
                        {
                            foreach (Type sub in types.Where(x => !x.IsAbstract && member.StorageType.IsAssignableFrom(x)).Take(200))
                                queue.Enqueue(sub);
                        }
                    }
                }
            }

            Console.WriteLine($"     {roots.Count} saveable classes, {seen.Count} reachable types, {mainThread} need the main thread to write");
            if (blockers.Count == 0)
                Console.WriteLine("     nothing reachable falls back to vanilla");
            foreach (var kv in blockers)
                Console.WriteLine($"     {kv.Key}  e.g. {string.Join(", ", kv.Value)}");
        }

        private static string Short(string reason)
        {
            int colon = reason.IndexOf(": ", StringComparison.Ordinal);
            return colon >= 0 ? reason.Substring(colon + 2) : reason;
        }

        // ---- benchmark ---------------------------------------------------------------------------------

        private static void Benchmark(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var tape = new JsonTape();
            for (int i = 0; i < 2; i++)
            {
                fsJsonParser.Parse(json);
                tape.Parse(json);
            }
            var clock = Stopwatch.StartNew();
            fsJsonParser.Parse(json);
            long vanilla = clock.ElapsedMilliseconds;
            clock.Restart();
            tape.Parse(json);
            long fast = clock.ElapsedMilliseconds;
            Console.WriteLine($"bench {Path.GetFileName(path)} ({json.Length / 1024} KB, {tape.Count} tokens): parse vanilla {vanilla} ms, tape {fast} ms");
        }
    }
}
