using System;
using System.Collections.Generic;
using FullSerializer;
using UnityEngine;

namespace EvenFasterSaveLoad.Tests
{
    // Covers every converter path FullSerializer can take in a save file.

    public enum Color { Red, Green, Blue = 7 }

    [Flags]
    public enum Perms { None = 0, Read = 1, Write = 2, Exec = 4 }

    public abstract class Shape
    {
        public string Name;
        public Shape Next;
    }

    public class Circle : Shape
    {
        public float Radius;
    }

    public class Square : Shape
    {
        public double Side;
        public List<Shape> Children = new List<Shape>();
    }

    public struct Point
    {
        public int X;
        public int Y;
    }

    public class Callback : ISerializationCallbackReceiver
    {
        public int Value;
        [NonSerialized] public int Before;
        [NonSerialized] public int After;
        public void OnBeforeSerialize() => Before++;
        public void OnAfterDeserialize() => After++;
    }

    // The game's Animal/Chicken pair: both declare [SerializeField] private float nearplayercooldown.
    public class Animal
    {
        [SerializeField] private float nearplayercooldown = 1.5f;
        public string Species = "bird";
        public float BaseValue => nearplayercooldown;
    }

    public class Chicken : Animal
    {
        public int Eggs = 3;
        [SerializeField] private float nearplayercooldown = 7.25f;
        public List<int> Laid = new List<int> { 1, 2 };
        public float Value => nearplayercooldown;
    }

    public class DuplicateObjects
    {
        [fsProperty("same")] public List<int> First = new List<int> { 1 };
        public List<int> Middle = new List<int> { 2 };
        [fsProperty("same")] public List<int> Second = new List<int> { 3 };
    }

    public class WithChar
    {
        public char Letter = 'ж';
        public char Quote = '"';
    }

    public class WithNullable
    {
        public bool? A;
        public int? B = 5;
        public Color? C;
        public float? D;
    }

    public class WithInterfaces
    {
        public IList<int> Items;
        public IDictionary<string, int> Map;
    }

    public class Holder
    {
        public int Id;
        public List<string> Tags = new List<string> { "preset" };
        public Payload Payload;
    }

    public class Payload
    {
        public string Text;
        public SaveLike Owner;
        public List<int> Numbers = new List<int>();
    }

    /// <summary>Shaped like SaveController.SaveDataHeader: a few fields and one big list of entries.</summary>
    public class SaveLike
    {
        public string SaveName = "Autosave";
        public DateTime Date = new DateTime(2026, 10, 14, 22, 18, 21, DateTimeKind.Local);
        public List<Holder> Data = new List<Holder>();
        public Dictionary<string, Holder> Index = new Dictionary<string, Holder>();
    }

    public class Everything
    {
        public bool Flag = true;
        public sbyte SB = -5;
        public byte B = 200;
        public short S = -30000;
        public ushort US = 60000;
        public int I = int.MinValue;
        public uint UI = uint.MaxValue;
        public long L = long.MinValue;
        public ulong UL = 12345678901234UL;
        public float F1 = 0.1f, F2 = 1f, F3 = -3.5f, F4 = 1e-8f, F5 = float.MaxValue, F6 = 123456.789f, F7 = float.NaN;
        public double D1 = 0.1, D2 = 2, D3 = -1e300, D4 = double.Epsilon, D5 = Math.PI, D6 = double.NegativeInfinity;
        public decimal M = 12.5m;
        public string Text = "quote\" backslash\\ tab\t nl\n nul\0 bell\a unicode: Юхани Койвисто ä ö € 😀";
        public string Nothing;
        public string Empty = "";
        public Color Color = Color.Blue;
        public Perms Perms = Perms.Read | Perms.Exec;
        public Perms NoPerms = Perms.None;
        public Point Point = new Point { X = 3, Y = -4 };
        public Vector3 Position = new Vector3(1.5f, -0.25f, 1e6f);
        public int[] Numbers = { 1, 2, 3 };
        public int[] EmptyNumbers = new int[0];
        public string[][] Jagged = { new[] { "a" }, new string[0], null };
        public List<string> Names = new List<string> { "x", null, "z" };
        public HashSet<string> Set = new HashSet<string> { "one", "two" };
        public LinkedList<int> Linked = new LinkedList<int>(new[] { 5, 6 });
        public Queue<int> Queue = new Queue<int>(new[] { 7, 8 });
        public Dictionary<string, List<int>> ByName = new Dictionary<string, List<int>> { ["a"] = new List<int> { 1 }, ["б"] = new List<int>() };
        public Dictionary<int, string> ByNumber = new Dictionary<int, string> { [1] = "one", [-2] = "minus two" };
        public Dictionary<Color, float> ByColor = new Dictionary<Color, float> { [Color.Red] = 0.5f, [Color.Blue] = 2f };
        public Dictionary<Perms, int> ByPerms = new Dictionary<Perms, int> { [Perms.Write] = 1 };
        public Dictionary<string, int> EmptyDict = new Dictionary<string, int>();
        public Dictionary<Point, string> ByPoint = new Dictionary<Point, string> { [new Point { X = 1 }] = "p" };
        public Dictionary<Guid, Shape> ByGuid = new Dictionary<Guid, Shape>();
        public KeyValuePair<string, int> Pair = new KeyValuePair<string, int>("k", 9);
        public Guid Id = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        public DateTime When = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc);
        public DateTimeOffset WhenOffset = new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.FromHours(3));
        public TimeSpan Span = TimeSpan.FromMinutes(90);
        public Type SomeType = typeof(List<int>);
        public object Boxed = 42;
        public object BoxedString = "boxed";
        public object BoxedObject = new Circle { Name = "boxed", Radius = 1 };
        public object BoxedList = new List<int> { 1 };
        public bool? MaybeBool = true;
        public int? MaybeInt;
        public Color? MaybeColor = Color.Green;
        public Shape Single;
        public List<Shape> Shapes = new List<Shape>();
        public Shape[] ShapeArray;
        public Callback Callback = new Callback { Value = 5 };
        public Rect Area = new Rect(1, 2, 3, 4);
        public LayerMask Mask;
        public Bounds Box = new Bounds(new Vector3(1, 2, 3), new Vector3(4, 5, 6));
        public List<Point> Points = new List<Point> { new Point { X = 1, Y = 2 }, new Point() };
        public IList<int> AsInterface = new List<int> { 4, 5 };
        public Everything Self;
        public int AutoProperty { get; set; } = 77;
    }

    public static class Model
    {
        public static Everything Everything()
        {
            var e = new Everything();
            var circle = new Circle { Name = "c", Radius = 2.5f };
            var square = new Square { Name = "s", Side = 3 };
            var other = new Square { Name = "t", Side = 0.5 };
            circle.Next = square;
            square.Next = circle; // cycle
            square.Children.Add(circle);
            square.Children.Add(other);
            square.Children.Add(other); // shared
            e.Single = circle;
            e.Shapes.Add(square);
            e.Shapes.Add(circle);
            e.Shapes.Add(null);
            e.ShapeArray = new Shape[] { other, square, other };
            e.ByGuid[e.Id] = other;
            e.Self = e;
            e.Mask.value = 5;
            return e;
        }

        public static Everything Sparse() => new Everything
        {
            Names = null, ByName = null, Set = new HashSet<string>(), Shapes = null, Callback = null,
            BoxedObject = null, Boxed = null, Jagged = null, BoxedList = null, SomeType = null,
        };

        public static SaveLike SaveLike(int count)
        {
            var save = new SaveLike();
            var sharedPayload = new Payload { Text = "shared", Owner = save };
            for (int i = 0; i < count; i++)
            {
                var h = new Holder { Id = i, Tags = new List<string> { "t" + i } };
                if (i % 3 == 0)
                    h.Payload = sharedPayload; // shared between many list elements
                else if (i % 3 == 1)
                    h.Payload = new Payload { Text = "own " + i, Owner = save, Numbers = { i, -i } }; // refers back to the root
                save.Data.Add(h);
                if (i % 7 == 0)
                    save.Index["k" + i] = h; // later references into the list
            }
            if (count > 5)
                save.Data.Add(save.Data[5]); // repeat of an earlier element inside the same list
            return save;
        }
    }
}
