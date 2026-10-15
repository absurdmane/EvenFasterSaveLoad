using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace EvenFasterSaveLoad.Json
{
    /// <summary>
    /// Growable UTF-8 output buffer that knows how fsJsonPrinter formats every JSON token.
    /// </summary>
    internal sealed class ByteSink
    {
        private static readonly byte[] HexDigits = Encoding.ASCII.GetBytes("0123456789abcdef");

        private byte[] data;
        private int length;

        public ByteSink(int capacity = 1 << 16)
        {
            data = new byte[capacity];
        }

        public int Length => length;

        /// <summary>
        /// fsJsonPrinter prints Int64 through TextWriter.Write(long), i.e. with the current culture,
        /// whose negative sign isn't always '-'. Captured per serialization.
        /// </summary>
        public NumberFormatInfo IntegerFormat = NumberFormatInfo.InvariantInfo;

        public bool PlainMinus = true;

        public void Clear() => length = 0;

        public void CaptureCulture()
        {
            IntegerFormat = CultureInfo.CurrentCulture.NumberFormat;
            PlainMinus = IntegerFormat.NegativeSign == "-";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Reserve(int count)
        {
            if (length + count > data.Length)
                Expand(count);
        }

        private void Expand(int count)
        {
            int size = Math.Max(data.Length * 2, length + count);
            Array.Resize(ref data, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Byte(byte b)
        {
            Reserve(1);
            data[length++] = b;
        }

        public void Bytes(byte[] bytes)
        {
            Reserve(bytes.Length);
            Buffer.BlockCopy(bytes, 0, data, length, bytes.Length);
            length += bytes.Length;
        }

        public void Append(ByteSink other)
        {
            Reserve(other.length);
            Buffer.BlockCopy(other.data, 0, data, length, other.length);
            length += other.length;
        }

        public void WriteTo(Stream stream) => stream.Write(data, 0, length);

        public string Decode() => Encoding.UTF8.GetString(data, 0, length);

        public byte[] ToArray()
        {
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            return copy;
        }

        // ---- tokens -------------------------------------------------------------------------------------

        /// <summary>Text copied as-is (fsJsonPrinter doesn't escape dictionary keys).</summary>
        public void Raw(string text)
        {
            Reserve(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= 0x80)
                {
                    // Non-ASCII: redo the whole string as UTF-8 over the partial ASCII copy.
                    Reserve(Encoding.UTF8.GetMaxByteCount(text.Length));
                    length += Encoding.UTF8.GetBytes(text, 0, text.Length, data, length);
                    return;
                }
                data[length + i] = (byte)c;
            }
            length += text.Length;
        }

        /// <summary>A quoted string escaped exactly like fsJsonPrinter.EscapeString.</summary>
        public void String(string text)
        {
            Reserve(text.Length * 6 + 2);
            byte[] d = data;
            int p = length;
            d[p++] = (byte)'"';
            foreach (char c in text)
            {
                if (c >= 0x80)
                {
                    d[p++] = (byte)'\\';
                    d[p++] = (byte)'u';
                    d[p++] = HexDigits[c >> 12];
                    d[p++] = HexDigits[(c >> 8) & 0xF];
                    d[p++] = HexDigits[(c >> 4) & 0xF];
                    d[p++] = HexDigits[c & 0xF];
                    continue;
                }
                char escape = EscapeLetter(c);
                if (escape != '\0')
                {
                    d[p++] = (byte)'\\';
                    d[p++] = (byte)escape;
                }
                else
                {
                    d[p++] = (byte)c;
                }
            }
            d[p++] = (byte)'"';
            length = p;
        }

        private static char EscapeLetter(char c)
        {
            switch (c)
            {
                case '"': return '"';
                case '\\': return '\\';
                case '\a': return 'a';
                case '\b': return 'b';
                case '\f': return 'f';
                case '\n': return 'n';
                case '\r': return 'r';
                case '\t': return 't';
                case '\0': return '0';
                default: return '\0';
            }
        }

        public void StringOrNull(string text)
        {
            if (text == null)
                Null();
            else
                String(text);
        }

        public void Bool(bool value)
        {
            if (value)
                Ascii("true");
            else
                Ascii("false");
        }

        public void Null() => Ascii("null");

        public void Ascii(string text)
        {
            Reserve(text.Length);
            for (int i = 0; i < text.Length; i++)
                data[length + i] = (byte)text[i];
            length += text.Length;
        }

        public void Integer(long value)
        {
            if (value < 0 && !PlainMinus)
            {
                Raw(value.ToString(IntegerFormat));
                return;
            }
            Reserve(20);
            ulong magnitude;
            if (value < 0)
            {
                data[length++] = (byte)'-';
                magnitude = unchecked((ulong)(-(value + 1)) + 1);
            }
            else
            {
                magnitude = (ulong)value;
            }
            Digits(magnitude);
        }

        private void Digits(ulong value)
        {
            Reserve(20);
            int start = length;
            do
            {
                data[length++] = (byte)('0' + (int)(value % 10));
                value /= 10;
            } while (value != 0);
            Array.Reverse(data, start, length - start);
        }

        public void Id(int id)
        {
            Byte((byte)'"');
            Digits((ulong)id);
            Byte((byte)'"');
        }

        /// <summary>fsPrimitiveConverter: finite floats go through decimal so 0.1f prints as 0.1.</summary>
        public void Single(float value)
        {
            if (value != float.MinValue && value != float.MaxValue && !float.IsInfinity(value) && !float.IsNaN(value))
                Double((double)(decimal)value);
            else
                Double(value);
        }

        /// <summary>fsJsonPrinter.ConvertDoubleToString: invariant "R"-less ToString, ".0" if it looks integral.</summary>
        public void Double(double value)
        {
            Span<char> chars = stackalloc char[64];
            int count;
            if (!DoubleFormat.UseSpanFormatting || !value.TryFormat(chars, out count, default, CultureInfo.InvariantCulture))
            {
                Raw(DoubleFormat.Vanilla(value));
                return;
            }

            Reserve(count + 2);
            bool integral = !double.IsInfinity(value) && !double.IsNaN(value);
            for (int i = 0; i < count; i++)
            {
                char c = chars[i];
                if (c >= 0x80)
                {
                    Raw(DoubleFormat.Vanilla(value));
                    return;
                }
                if (c == '.' || c == 'e' || c == 'E')
                    integral = false;
            }
            for (int i = 0; i < count; i++)
                data[length++] = (byte)chars[i];
            if (integral)
            {
                data[length++] = (byte)'.';
                data[length++] = (byte)'0';
            }
        }
    }

    internal static class DoubleFormat
    {
        /// <summary>
        /// double.TryFormat avoids a string per number, but it's only used if this runtime formats exactly like
        /// double.ToString (checked once at startup on values that tend to differ between implementations).
        /// </summary>
        public static readonly bool UseSpanFormatting = CheckSpanFormatting();

        public static string Vanilla(double d)
        {
            string text = d.ToString(CultureInfo.InvariantCulture);
            if (double.IsInfinity(d) || double.IsNaN(d))
                return text;
            if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0)
                text += ".0";
            return text;
        }

        private static bool CheckSpanFormatting()
        {
            double[] probes =
            {
                0, -0.0, 0.1, 0.3, 0.1 + 0.2, 1.0 / 3, 2.0 / 3, Math.PI, 1e15, 1e16, 1e17, 123456789012345678,
                1e-5, 1e-7, 5e-324, double.MaxValue, double.MinValue, double.Epsilon, double.NaN,
                double.PositiveInfinity, double.NegativeInfinity, 3.4028234663852886E+38, 1.401298464324817E-45,
                (double)(decimal)0.1f, (double)(decimal)123456.789f, 0.1f, 16777217, -2.5,
            };
            Span<char> buffer = stackalloc char[64];
            try
            {
                foreach (double d in probes)
                {
                    if (!d.TryFormat(buffer, out int n, default, CultureInfo.InvariantCulture))
                        return false;
                    if (!buffer.Slice(0, n).SequenceEqual(d.ToString(CultureInfo.InvariantCulture).AsSpan()))
                        return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
