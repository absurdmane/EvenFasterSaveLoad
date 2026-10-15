using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace EvenFasterSaveLoad.Json
{
    internal enum Token : byte { Null, True, False, Integer, Real, String, Object, Array }

    /// <summary>One JSON token. Containers are followed by their children; <see cref="End"/> skips the subtree.</summary>
    internal struct Entry
    {
        public Token Token;
        public byte Flags;
        /// <summary>Index of the first entry after this token's subtree.</summary>
        public int End;
        /// <summary>
        /// String: start offset (low 32 bits) and length (high 32 bits) in the source text.
        /// Integer: the value. Real: the IEEE bits. Object: member count (children are key, value, key, ...).
        /// Array: element count.
        /// </summary>
        public long Payload;
    }

    /// <summary>
    /// Parses JSON into a flat token array that points back into the source string, so most strings are never
    /// materialized. It accepts the same grammar as fsJsonParser; anything unusual (comments, duplicate keys,
    /// malformed input) throws <see cref="FallbackException"/> so vanilla can produce its own result or error.
    /// </summary>
    internal sealed class JsonTape
    {
        public const byte Escaped = 1;   // String: contains backslash escapes
        public const byte HasDollar = 2; // Object: some key starts with '$' (or is escaped)

        public string Text;
        public Entry[] Entries = new Entry[4096];
        public int Count;

        private int pos;
        private readonly List<int> keyScratch = new List<int>();
        private readonly HashSet<string> keySet = new HashSet<string>(StringComparer.Ordinal);

        public ref Entry this[int index] => ref Entries[index];

        public void Parse(string text)
        {
            Text = text;
            Count = 0;
            pos = 0;
            int estimate = text.Length / 6 + 16;
            if (Entries.Length < estimate)
                Entries = new Entry[estimate];
            SkipSpace();
            ParseValue();
            // fsJsonParser ignores whatever follows the root value; so do we.
        }

        public void Release() => Text = null;

        // ---- grammar ------------------------------------------------------------------------------------

        private static FallbackException Error(string what) => new FallbackException("JSON: " + what);

        private int Add(Token token)
        {
            if (Count == Entries.Length)
                Array.Resize(ref Entries, Entries.Length * 2);
            int index = Count++;
            Entries[index] = new Entry { Token = token, End = index + 1 };
            return index;
        }

        private void SkipSpace()
        {
            string t = Text;
            while (pos < t.Length)
            {
                char c = t[pos];
                if (c == ' ' || c == '\n' || c == '\r' || c == '\t' || (c > 127 && char.IsWhiteSpace(c)) || (c < ' ' && char.IsWhiteSpace(c)))
                {
                    pos++;
                    continue;
                }
                if (c == '/')
                    throw Error("comments");
                return;
            }
        }

        private void ParseValue()
        {
            if (pos >= Text.Length)
                throw Error("unexpected end");
            char c = Text[pos];
            switch (c)
            {
                case '"':
                    ParseString();
                    return;
                case '{':
                    ParseObject();
                    return;
                case '[':
                    ParseArray();
                    return;
                case 't':
                    Literal("true", Token.True);
                    return;
                case 'f':
                    Literal("false", Token.False);
                    return;
                case 'n':
                    Literal("null", Token.Null);
                    return;
                default:
                    if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'I' || c == 'N')
                    {
                        ParseNumber();
                        return;
                    }
                    throw Error("invalid token");
            }
        }

        private void Literal(string word, Token token)
        {
            if (string.CompareOrdinal(Text, pos, word, 0, word.Length) != 0)
                throw Error("expected " + word);
            pos += word.Length;
            Add(token);
        }

        private void ParseNumber()
        {
            int start = pos;
            string t = Text;
            pos++;
            while (pos < t.Length)
            {
                char c = t[pos];
                if (c == ',' || c == '}' || c == ']' || char.IsWhiteSpace(c))
                    break;
                pos++;
            }
            ReadOnlySpan<char> text = t.AsSpan(start, pos - start);

            bool real = text.IndexOfAny('.', 'e', 'E') >= 0 ||
                        text.SequenceEqual("Infinity".AsSpan()) || text.SequenceEqual("-Infinity".AsSpan()) || text.SequenceEqual("NaN".AsSpan());
            int index = Add(real ? Token.Real : Token.Integer);
            if (real)
            {
                if (!double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    throw Error("bad number");
                Entries[index].Payload = BitConverter.DoubleToInt64Bits(d);
            }
            else
            {
                if (!long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out long l))
                    throw Error("bad integer");
                Entries[index].Payload = l;
            }
        }

        private int ParseString()
        {
            string t = Text;
            int index = Add(Token.String);
            int start = ++pos;
            byte flags = 0;
            while (true)
            {
                int quote = t.IndexOf('"', pos);
                if (quote < 0)
                    throw Error("unterminated string");
                int slash = t.IndexOf('\\', pos, quote - pos);
                if (slash < 0)
                {
                    pos = quote;
                    break;
                }
                flags = Escaped;
                pos = slash;
                SkipEscape(); // may consume an escaped quote
            }
            Entries[index].Flags = flags;
            Entries[index].Payload = (uint)start | ((long)(pos - start) << 32);
            pos++;
            return index;
        }

        private void SkipEscape()
        {
            // pos is at the backslash
            if (pos + 1 >= Text.Length)
                throw Error("bad escape");
            char c = Text[pos + 1];
            switch (c)
            {
                case '\\': case '/': case '"': case 'a': case 'b': case 'f': case 'n': case 'r': case 't': case '0':
                    pos += 2;
                    return;
                case 'u':
                    if (pos + 5 >= Text.Length || !IsHex(Text[pos + 2]) || !IsHex(Text[pos + 3]) || !IsHex(Text[pos + 4]) || !IsHex(Text[pos + 5]))
                        throw Error("bad \\u escape");
                    pos += 6;
                    return;
                default:
                    throw Error("bad escape");
            }
        }

        private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        // fsJsonParser treats commas as optional separators; mirror that loop exactly.
        private void ParseArray()
        {
            int index = Add(Token.Array);
            pos++;
            SkipSpace();
            long count = 0;
            while (pos < Text.Length && Text[pos] != ']')
            {
                ParseValue();
                count++;
                SkipSpace();
                if (pos < Text.Length && Text[pos] == ',')
                {
                    pos++;
                    SkipSpace();
                }
            }
            if (pos >= Text.Length)
                throw Error("no closing ]");
            pos++;
            Entries[index].Payload = count;
            Entries[index].End = Count;
        }

        private void ParseObject()
        {
            int index = Add(Token.Object);
            pos++;
            SkipSpace();
            long count = 0;
            byte flags = 0;
            int keysFrom = keyScratch.Count;
            while (pos < Text.Length && Text[pos] != '}')
            {
                SkipSpace();
                if (pos >= Text.Length || Text[pos] != '"')
                    throw Error("expected key");
                int key = ParseString();
                ref Entry k = ref Entries[key];
                if ((k.Flags & Escaped) != 0 || ((int)(k.Payload >> 32) > 0 && Text[(int)k.Payload] == '$'))
                    flags |= HasDollar;
                keyScratch.Add(key);
                SkipSpace();
                if (pos >= Text.Length || Text[pos] != ':')
                    throw Error("expected :");
                pos++;
                SkipSpace();
                ParseValue();
                count++;
                SkipSpace();
                if (pos < Text.Length && Text[pos] == ',')
                {
                    pos++;
                    SkipSpace();
                }
            }
            if (pos >= Text.Length)
                throw Error("no closing }");
            pos++;
            RejectDuplicateKeys(keysFrom);
            keyScratch.RemoveRange(keysFrom, keyScratch.Count - keysFrom);
            Entries[index].Flags = flags;
            Entries[index].Payload = count;
            Entries[index].End = Count;
        }

        // fsJsonParser uses Dictionary.Add, which throws on a duplicate key.
        private void RejectDuplicateKeys(int from)
        {
            int to = keyScratch.Count;
            if (to - from <= 12)
            {
                for (int i = from; i < to; i++)
                for (int j = i + 1; j < to; j++)
                {
                    if (SameString(keyScratch[i], keyScratch[j]))
                        throw Error("duplicate key");
                }
                return;
            }
            keySet.Clear();
            for (int i = from; i < to; i++)
            {
                if (!keySet.Add(GetString(keyScratch[i])))
                    throw Error("duplicate key");
            }
        }

        // ---- access -------------------------------------------------------------------------------------

        public long Integer(int index) => Entries[index].Payload;

        public double Real(int index) => BitConverter.Int64BitsToDouble(Entries[index].Payload);

        public string GetString(int index)
        {
            ref Entry e = ref Entries[index];
            int start = (int)e.Payload;
            int length = (int)(e.Payload >> 32);
            if ((e.Flags & Escaped) == 0)
                return Text.Substring(start, length);
            return Unescape(start, length);
        }

        private string Unescape(int start, int length)
        {
            var sb = new StringBuilder(length);
            int end = start + length;
            for (int i = start; i < end; i++)
            {
                char c = Text[i];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                char e = Text[++i];
                switch (e)
                {
                    case 'a': sb.Append('\a'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '0': sb.Append('\0'); break;
                    case 'u':
                        sb.Append((char)int.Parse(Text.Substring(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break; // \\ \/ \"
                }
            }
            return sb.ToString();
        }

        public bool StringEquals(int index, string value)
        {
            ref Entry e = ref Entries[index];
            if ((e.Flags & Escaped) != 0)
                return GetString(index) == value;
            int length = (int)(e.Payload >> 32);
            return length == value.Length && string.CompareOrdinal(Text, (int)e.Payload, value, 0, length) == 0;
        }

        private bool SameString(int a, int b)
        {
            ref Entry x = ref Entries[a];
            ref Entry y = ref Entries[b];
            if (((x.Flags | y.Flags) & Escaped) != 0)
                return GetString(a) == GetString(b);
            int length = (int)(x.Payload >> 32);
            return length == (int)(y.Payload >> 32) &&
                   string.CompareOrdinal(Text, (int)x.Payload, Text, (int)y.Payload, length) == 0;
        }

        /// <summary>Value entry of the member named <paramref name="key"/>, or -1.</summary>
        public int Member(int obj, string key)
        {
            int count = (int)Entries[obj].Payload;
            int k = obj + 1;
            for (int i = 0; i < count; i++)
            {
                if (StringEquals(k, key))
                    return k + 1;
                k = Entries[k + 1].End;
            }
            return -1;
        }
    }
}
