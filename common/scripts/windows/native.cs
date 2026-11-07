// Atelier's helpers in C#, for the work that PowerShell would do slowly: see base.ps1,
// which compiles them with Add-Type, part of Windows PowerShell.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Management.Automation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Atelier
{
    // An error with what the page or the caller needs to know about it
    public class AppException : Exception
    {
        public string Code;
        public int Status;
        public object Detail;
        public AppException(string message) : base(message) { }
        public AppException(string message, string code) : base(message) { Code = code; }
    }

    // JSON as JavaScript reads and writes it: objects become dictionaries with
    // case-sensitive keys, arrays lists, numbers int, long or double. Writing takes
    // PowerShell's own objects too, and indents by one space as JSON.stringify(x, null, 1).
    public static class Json
    {
        public static object Read(string text)
        {
            var p = new Parser(text);
            p.Space();
            object value = p.Value();
            p.Space();
            if (p.Pos < p.Length) throw p.Error("Unexpected text after the JSON value");
            return value;
        }

        public static string Write(object value) { return Write(value, false); }

        public static string Write(object value, bool indent)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indent ? 0 : -1);
            return sb.ToString();
        }

        static void Newline(StringBuilder sb, int depth)
        {
            sb.Append('\n');
            sb.Append(' ', depth);
        }

        static void WriteValue(StringBuilder sb, object v, int depth)
        {
            var ps = v as PSObject;
            if (ps != null)
            {
                if (ps.BaseObject is PSCustomObject) { WriteProperties(sb, ps, depth); return; }
                v = ps.BaseObject;
            }
            if (v == null || v is DBNull) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is char) { WriteString(sb, v.ToString()); return; }
            if (v is double || v is float) { WriteDouble(sb, Convert.ToDouble(v)); return; }
            if (v is int || v is long || v is short || v is byte || v is sbyte || v is uint || v is ulong || v is ushort || v is decimal)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is DateTime)
            {
                WriteString(sb, ((DateTime)v).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                return;
            }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }
            var dict = v as IDictionary;
            if (dict != null) { WriteObject(sb, dict, depth); return; }
            var list = v as IEnumerable;
            if (list != null) { WriteArray(sb, list, depth); return; }
            WriteString(sb, v.ToString());
        }

        static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
            else sb.Append(Number(d));
        }

        // A number as JavaScript prints it: the fewest digits that read back as the same
        // number, without an exponent between 1e-7 and 1e21
        public static string Number(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsInfinity(d)) return d > 0 ? "Infinity" : "-Infinity";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e21) return d.ToString("F0", CultureInfo.InvariantCulture);
            string s = null;
            for (int p = 15; p <= 17; p++)
            {
                s = d.ToString("G" + p, CultureInfo.InvariantCulture);
                if (double.Parse(s, CultureInfo.InvariantCulture) == d) break;
            }
            int e = s.IndexOf('E');
            if (e < 0) return s;
            decimal m;
            if (Math.Abs(d) >= 1e-7 && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out m))
                return m.ToString(CultureInfo.InvariantCulture);
            string exp = s.Substring(e + 1).TrimStart('+');
            bool negative = exp.StartsWith("-");
            exp = exp.TrimStart('-').TrimStart('0');
            return s.Substring(0, e) + "e" + (negative ? "-" : "+") + (exp.Length > 0 ? exp : "0");
        }

        static void WriteObject(StringBuilder sb, IDictionary dict, int depth)
        {
            bool first = true;
            sb.Append('{');
            foreach (DictionaryEntry e in dict)
            {
                if (!first) sb.Append(',');
                first = false;
                if (depth >= 0) Newline(sb, depth + 1);
                WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                sb.Append(depth >= 0 ? ": " : ":");
                WriteValue(sb, e.Value, depth >= 0 ? depth + 1 : -1);
            }
            if (!first && depth >= 0) Newline(sb, depth);
            sb.Append('}');
        }

        static void WriteProperties(StringBuilder sb, PSObject ps, int depth)
        {
            bool first = true;
            sb.Append('{');
            foreach (var p in ps.Properties)
            {
                if (!first) sb.Append(',');
                first = false;
                if (depth >= 0) Newline(sb, depth + 1);
                WriteString(sb, p.Name);
                sb.Append(depth >= 0 ? ": " : ":");
                WriteValue(sb, p.Value, depth >= 0 ? depth + 1 : -1);
            }
            if (!first && depth >= 0) Newline(sb, depth);
            sb.Append('}');
        }

        static void WriteArray(StringBuilder sb, IEnumerable list, int depth)
        {
            bool first = true;
            sb.Append('[');
            foreach (object item in list)
            {
                if (!first) sb.Append(',');
                first = false;
                if (depth >= 0) Newline(sb, depth + 1);
                WriteValue(sb, item, depth >= 0 ? depth + 1 : -1);
            }
            if (!first && depth >= 0) Newline(sb, depth);
            sb.Append(']');
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        bool pairedHigh = char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
                        bool pairedLow = char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1]);
                        if (c < 0x20 || (char.IsSurrogate(c) && !pairedHigh && !pairedLow))
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        class Parser
        {
            readonly string s;
            public int Pos;
            public int Length { get { return s.Length; } }

            public Parser(string text)
            {
                s = text ?? "";
                if (s.Length > 0 && s[0] == (char)0xFEFF) Pos = 1;
            }

            public Exception Error(string message) { return new FormatException(message + " at position " + Pos); }

            public void Space()
            {
                while (Pos < s.Length && (s[Pos] == ' ' || s[Pos] == '\t' || s[Pos] == '\n' || s[Pos] == '\r')) Pos++;
            }

            public object Value()
            {
                if (Pos >= s.Length) throw Error("Unexpected end of JSON");
                char c = s[Pos];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return Str();
                if (c == 't' && Word("true")) return true;
                if (c == 'f' && Word("false")) return false;
                if (c == 'n' && Word("null")) return null;
                if (c == '-' || (c >= '0' && c <= '9')) return Num();
                throw Error("Unexpected character");
            }

            bool Word(string w)
            {
                if (Pos + w.Length > s.Length || string.CompareOrdinal(s, Pos, w, 0, w.Length) != 0) return false;
                Pos += w.Length;
                return true;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Pos++;
                Space();
                if (Pos < s.Length && s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    Space();
                    if (Pos >= s.Length || s[Pos] != '"') throw Error("Expected a property name");
                    string key = Str();
                    Space();
                    if (Pos >= s.Length || s[Pos] != ':') throw Error("Expected ':'");
                    Pos++;
                    Space();
                    d[key] = Value();
                    Space();
                    if (Pos < s.Length && s[Pos] == ',') { Pos++; continue; }
                    if (Pos < s.Length && s[Pos] == '}') { Pos++; return d; }
                    throw Error("Expected ',' or '}'");
                }
            }

            List<object> Arr()
            {
                var list = new List<object>();
                Pos++;
                Space();
                if (Pos < s.Length && s[Pos] == ']') { Pos++; return list; }
                while (true)
                {
                    Space();
                    list.Add(Value());
                    Space();
                    if (Pos < s.Length && s[Pos] == ',') { Pos++; continue; }
                    if (Pos < s.Length && s[Pos] == ']') { Pos++; return list; }
                    throw Error("Expected ',' or ']'");
                }
            }

            string Str()
            {
                Pos++;
                var sb = new StringBuilder();
                int start = Pos;
                while (true)
                {
                    if (Pos >= s.Length) throw Error("Unterminated string");
                    char c = s[Pos];
                    if (c == '"')
                    {
                        sb.Append(s, start, Pos - start);
                        Pos++;
                        return sb.ToString();
                    }
                    if (c != '\\') { Pos++; continue; }
                    sb.Append(s, start, Pos - start);
                    Pos++;
                    if (Pos >= s.Length) throw Error("Unterminated string");
                    char e = s[Pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > s.Length) throw Error("Bad escape");
                            sb.Append((char)Convert.ToInt32(s.Substring(Pos, 4), 16));
                            Pos += 4;
                            break;
                        default: throw Error("Bad escape");
                    }
                    start = Pos;
                }
            }

            object Num()
            {
                int start = Pos;
                bool real = false;
                if (s[Pos] == '-') Pos++;
                while (Pos < s.Length)
                {
                    char c = s[Pos];
                    if (c >= '0' && c <= '9') Pos++;
                    else if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { real = true; Pos++; }
                    else break;
                }
                string t = s.Substring(start, Pos - start);
                long l;
                if (!real && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l))
                    return l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l;
                double d;
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Error("Bad number");
                return d;
            }
        }
    }

    // CRC-32 as zip files have it, computed by Windows itself
    public static class Crc
    {
        [DllImport("ntdll.dll")]
        static extern uint RtlComputeCrc32(uint initial, IntPtr data, int length);

        public static uint Compute(byte[] data, int offset, int count, uint previous)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try { return RtlComputeCrc32(previous, IntPtr.Add(handle.AddrOfPinnedObject(), offset), count); }
            finally { handle.Free(); }
        }

        public static string OfFile(string path)
        {
            var buffer = new byte[1 << 20];
            uint crc = 0;
            using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                for (int n; (n = f.Read(buffer, 0, buffer.Length)) > 0;) crc = Compute(buffer, 0, n, crc);
            }
            return crc.ToString("x8");
        }
    }

    // N64 ROMs in the two byte orders other than the console's own
    public static class Rom
    {
        public static byte[] Swap16(byte[] d)
        {
            var o = (byte[])d.Clone();
            for (int i = 0; i + 1 < o.Length; i += 2) { o[i] = d[i + 1]; o[i + 1] = d[i]; }
            return o;
        }

        public static byte[] Swap32(byte[] d)
        {
            var o = (byte[])d.Clone();
            for (int i = 0; i + 3 < o.Length; i += 4) { o[i] = d[i + 3]; o[i + 1] = d[i + 2]; o[i + 2] = d[i + 1]; o[i + 3] = d[i]; }
            return o;
        }
    }

    public static class Files
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileEx(string from, string to, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetDiskFreeSpaceEx(string dir, out ulong available, out ulong total, out ulong free);

        // A file's date in milliseconds since 1970, worked out as Node.js does from its
        // seconds and nanoseconds, so that it prints the same
        public static double MtimeMs(string path)
        {
            long ticks = File.GetLastWriteTimeUtc(path).Ticks - 621355968000000000L;
            long seconds = ticks / 10000000L, rest = ticks % 10000000L;
            if (rest < 0) { seconds--; rest += 10000000L; }
            return seconds * 1e3 + (rest * 100) / 1e6;
        }

        // Renames a file or a folder, replacing a file already there, as Node's renameSync
        public static void Rename(string from, string to)
        {
            if (!MoveFileEx(from, to, 1))
            {
                int code = Marshal.GetLastWin32Error();
                throw new IOException(new Win32Exception(code).Message + ": " + from + " -> " + to, code);
            }
        }

        // Deletes a file or a folder with all it holds, read-only or not; nothing if it is gone
        public static void Remove(string path)
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                if (info.IsReadOnly) info.IsReadOnly = false;
                info.Delete();
                return;
            }
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return;
            if ((dir.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var entry in dir.GetFileSystemInfos())
                {
                    if (entry is DirectoryInfo) Remove(entry.FullName);
                    else
                    {
                        if ((entry.Attributes & FileAttributes.ReadOnly) != 0) entry.Attributes &= ~FileAttributes.ReadOnly;
                        entry.Delete();
                    }
                }
            }
            if ((dir.Attributes & FileAttributes.ReadOnly) != 0) dir.Attributes &= ~FileAttributes.ReadOnly;
            dir.Delete(false);
        }

        // The space free for the user on the disk of a folder, also of a network share
        public static double Free(string dir)
        {
            ulong available, total, free;
            if (!dir.EndsWith("\\")) dir += "\\";
            return GetDiskFreeSpaceEx(dir, out available, out total, out free) ? available : double.PositiveInfinity;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        static extern int WNetGetConnection(string local, StringBuilder remote, ref int length);

        // The \\server\share a network drive such as "Z:" stands for, also while it isn't
        // connected, or null
        public static string NetworkShare(string letter)
        {
            var remote = new StringBuilder(1024);
            int length = remote.Capacity;
            int code = WNetGetConnection(letter, remote, ref length);
            return code == 0 || code == 1201 ? remote.ToString() : null;
        }

        // The entries of a folder, or a TimeoutException when its disk doesn't answer in time.
        // Read on a thread of its own: a reading of the library keeps the pool's threads
        // busy, and waiting for one took seconds, up to the timeout on a large library.
        public static FileSystemInfo[] List(string dir, int timeoutMs)
        {
            var task = Task.Factory.StartNew(() => new DirectoryInfo(dir).GetFileSystemInfos(),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                if (!task.Wait(timeoutMs)) throw new TimeoutException("No answer");
            }
            catch (AggregateException e)
            {
                throw e.InnerException;
            }
            return task.Result;
        }
    }

    // Names compared as people read them, as JavaScript's Intl.Collator with base
    // sensitivity and numeric ordering: case and accents aside, numbers by value
    public class NameComparer : IComparer, IComparer<string>
    {
        const CompareOptions Base = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;
        readonly CompareInfo info;

        public NameComparer(string culture)
        {
            CultureInfo c;
            try { c = CultureInfo.GetCultureInfo((culture ?? "").Replace('_', '-')); }
            catch { c = CultureInfo.CurrentCulture; }
            info = c.CompareInfo;
        }

        static bool Digit(char c) { return c >= '0' && c <= '9'; }

        public int Compare(string a, string b)
        {
            a = a ?? "";
            b = b ?? "";
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                bool da = Digit(a[i]), db = Digit(b[j]);
                int ie = i, je = j;
                while (ie < a.Length && Digit(a[ie]) == da) ie++;
                while (je < b.Length && Digit(b[je]) == db) je++;
                int c;
                if (da && db)
                {
                    string na = a.Substring(i, ie - i).TrimStart('0'), nb = b.Substring(j, je - j).TrimStart('0');
                    c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                }
                else c = info.Compare(a, i, ie - i, b, j, je - j, Base);
                if (c != 0) return c;
                i = ie;
                j = je;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }

        public int Compare(object a, object b)
        {
            return Compare(a == null ? null : a.ToString(), b == null ? null : b.ToString());
        }

        // The items in the order of their names, then of their second keys as
        // localeCompare orders them, the rest as they came (a stable sort, as JavaScript's)
        public static object[] Sort(IList items, IList names, IList seconds, string culture)
        {
            var byName = new NameComparer(culture);
            var locale = CultureInfo.CurrentCulture.CompareInfo;
            int count = items.Count;
            var order = new int[count];
            var first = new string[count];
            var second = new string[count];
            for (int n = 0; n < count; n++)
            {
                order[n] = n;
                first[n] = names == null || names[n] == null ? "" : names[n].ToString();
                second[n] = seconds == null || seconds[n] == null ? "" : seconds[n].ToString();
            }
            Array.Sort(order, (x, y) =>
            {
                int c = byName.Compare(first[x], first[y]);
                if (c == 0 && seconds != null) c = locale.Compare(second[x], second[y]);
                return c != 0 ? c : x.CompareTo(y);
            });
            var result = new object[count];
            for (int n = 0; n < count; n++) result[n] = items[order[n]];
            return result;
        }
    }

    // A program run with its output read as it comes: the last percentage it printed is
    // its progress
    public class Runner
    {
        readonly Process process;
        readonly StringBuilder output = new StringBuilder();
        readonly StringBuilder errors = new StringBuilder();
        readonly Task reading;
        readonly Task readingErrors;
        public volatile int Percent = -1;

        public Runner(string exe, string arguments, string cwd, int codePage)
        {
            var encoding = codePage > 0 ? Encoding.GetEncoding(codePage) : new UTF8Encoding(false);
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            };
            if (!string.IsNullOrEmpty(cwd)) info.WorkingDirectory = cwd;
            process = Process.Start(info);
            process.StandardInput.Close();
            reading = Task.Run(() => Read(process.StandardOutput, output, true));
            readingErrors = Task.Run(() => Read(process.StandardError, errors, false));
        }

        void Read(StreamReader reader, StringBuilder into, bool progress)
        {
            var buffer = new char[4096];
            for (int n; (n = reader.Read(buffer, 0, buffer.Length)) > 0;)
            {
                var text = new string(buffer, 0, n);
                lock (into)
                {
                    into.Append(text);
                    if (into.Length > 4000000) into.Remove(0, into.Length - 2000000);
                }
                if (!progress) continue;
                var all = Regex.Matches(text, @"([0-9]{1,3})%");
                if (all.Count > 0) Percent = Math.Min(100, int.Parse(all[all.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        public bool Wait(int ms)
        {
            if (!process.WaitForExit(ms)) return false;
            Task.WaitAll(new[] { reading, readingErrors }, 5000);
            return true;
        }

        public int ExitCode { get { return process.ExitCode; } }
        public string Output { get { lock (output) return output.ToString(); } }
        public string Errors { get { lock (errors) return errors.ToString(); } }

        public void Kill()
        {
            try { process.Kill(); } catch { }
        }

        // A command line from arguments, quoted as Windows programs split it again
        public static string Join(IEnumerable arguments)
        {
            var sb = new StringBuilder();
            foreach (object o in arguments)
            {
                string a = o == null ? "" : o.ToString();
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                {
                    sb.Append(a);
                    continue;
                }
                sb.Append('"');
                int slashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { slashes++; continue; }
                    sb.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                    slashes = 0;
                    sb.Append(c);
                }
                sb.Append('\\', slashes * 2);
                sb.Append('"');
            }
            return sb.ToString();
        }
    }

    // The zip files of Catalogue's archive, made with the zip support of .NET Framework,
    // which comes with Windows. Writing, checking and extracting run on a thread of their
    // own, so that the script that started them follows their progress and can stop them,
    // as with Runner. Every file read or written goes through a CRC-32, so that what was
    // read can be compared with what the zip gives back.
    public class Zip
    {
        public class Item
        {
            public string Path;
            public long Size;
            public string Crc;
        }

        readonly Task task;
        volatile bool stopped;
        long done;
        long total;

        // The files written, checked or extracted, with forward slashes in their paths
        public Item[] Items;
        public Exception Error;

        Zip(Action<Zip> work)
        {
            task = Task.Run(() =>
            {
                try { work(this); }
                catch (Exception e) { Error = e; }
            });
        }

        public double Fraction
        {
            get
            {
                long t = Interlocked.Read(ref total);
                return t > 0 ? Math.Min(1.0, (double)Interlocked.Read(ref done) / t) : 0;
            }
        }

        public bool Wait(int ms) { return task.Wait(ms); }
        public void Kill() { stopped = true; }

        void Step(int n)
        {
            Interlocked.Add(ref done, n);
            if (stopped) throw new OperationCanceledException("Stopped");
        }

        // Writes a zip of files and folders named relative to dir, their paths kept as
        // given; a folder is added with all it holds, an empty one as a folder entry.
        // Files with one of the packed extensions are compressed already and are stored
        // as they are; the rest is deflated, at the quicker level when the whole is
        // larger than fastAbove bytes, as for large disc images the better one takes
        // long for little gain.
        public static Zip Create(string zip, string dir, string[] names, string[] packed, long fastAbove)
        {
            var stored = new HashSet<string>(packed ?? new string[0], StringComparer.OrdinalIgnoreCase);
            return new Zip(z => z.Write(zip, dir, names, stored, fastAbove));
        }

        // Reads every file of a zip through, for its size and CRC
        public static Zip Check(string zip) { return new Zip(z => z.Read(zip, null)); }

        // Extracts a zip into a folder, which none of its files may leave
        public static Zip Extract(string zip, string dir) { return new Zip(z => z.Read(zip, dir)); }

        static void Collect(string dir, string rel, List<KeyValuePair<string, FileInfo>> into)
        {
            string full = System.IO.Path.Combine(dir, rel);
            var file = new FileInfo(full);
            if (file.Exists)
            {
                into.Add(new KeyValuePair<string, FileInfo>(rel.Replace('\\', '/'), file));
                return;
            }
            var folder = new DirectoryInfo(full);
            if (!folder.Exists) throw new FileNotFoundException("Not found: " + full);
            var inside = folder.GetFileSystemInfos();
            if (inside.Length == 0)
            {
                into.Add(new KeyValuePair<string, FileInfo>(rel.Replace('\\', '/'), null));
                return;
            }
            Array.Sort(inside, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var entry in inside) Collect(dir, System.IO.Path.Combine(rel, entry.Name), into);
        }

        // Zip keeps dates from 1980 to 2107, in local time
        static DateTimeOffset ZipTime(DateTime local)
        {
            if (local.Year < 1980) local = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
            if (local.Year > 2107) local = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
            return new DateTimeOffset(local);
        }

        void Write(string zip, string dir, string[] names, HashSet<string> stored, long fastAbove)
        {
            var files = new List<KeyValuePair<string, FileInfo>>();
            foreach (string name in names) Collect(dir, name.Replace('/', '\\').Trim('\\'), files);
            long size = 0;
            foreach (var f in files) if (f.Value != null) size += f.Value.Length;
            Interlocked.Exchange(ref total, size);
            var deflate = size > fastAbove ? CompressionLevel.Fastest : CompressionLevel.Optimal;
            var items = new List<Item>();
            var buffer = new byte[1 << 20];
            using (var output = new FileStream(zip, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var f in files)
                {
                    if (f.Value == null)
                    {
                        archive.CreateEntry(f.Key + "/");
                        continue;
                    }
                    var entry = archive.CreateEntry(f.Key, stored.Contains(f.Value.Extension) ? CompressionLevel.NoCompression : deflate);
                    entry.LastWriteTime = ZipTime(f.Value.LastWriteTime);
                    uint crc = 0;
                    long length = 0;
                    using (var from = new FileStream(f.Value.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                    using (var to = entry.Open())
                    {
                        for (int n; (n = from.Read(buffer, 0, buffer.Length)) > 0;)
                        {
                            crc = Crc.Compute(buffer, 0, n, crc);
                            to.Write(buffer, 0, n);
                            length += n;
                            Step(n);
                        }
                    }
                    items.Add(new Item { Path = f.Key, Size = length, Crc = crc.ToString("x8") });
                }
            }
            Items = items.ToArray();
        }

        void Read(string zip, string dir)
        {
            string root = dir == null ? null : System.IO.Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            var items = new List<Item>();
            var buffer = new byte[1 << 20];
            using (var input = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                long size = 0;
                foreach (var e in archive.Entries) size += e.Length;
                Interlocked.Exchange(ref total, size);
                foreach (var e in archive.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    bool folder = name.EndsWith("/");
                    string target = null;
                    if (root != null)
                    {
                        target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name.TrimEnd('/').Replace('/', '\\')));
                        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("A file in the zip would land outside its folder: " + name);
                        Directory.CreateDirectory(folder ? target : System.IO.Path.GetDirectoryName(target));
                    }
                    if (folder) continue;
                    uint crc = 0;
                    long length = 0;
                    using (var from = e.Open())
                    using (var to = target == null ? null : new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        for (int n; (n = from.Read(buffer, 0, buffer.Length)) > 0;)
                        {
                            crc = Crc.Compute(buffer, 0, n, crc);
                            if (to != null) to.Write(buffer, 0, n);
                            length += n;
                            Step(n);
                        }
                    }
                    if (target != null) File.SetLastWriteTime(target, e.LastWriteTime.LocalDateTime);
                    items.Add(new Item { Path = name, Size = length, Crc = crc.ToString("x8") });
                }
            }
            Items = items.ToArray();
        }
    }

    // ES-DE's game lists, ROM folders and media folders, read here because they touch
    // every game: in PowerShell a large library would take seconds
    public static class Gamelist
    {
        static readonly Regex Game = new Regex(@"<game>([\s\S]*?)</game>", RegexOptions.Compiled);
        static readonly Dictionary<string, Regex> Tags = new Dictionary<string, Regex>();

        public static string Decode(string s)
        {
            return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
        }

        static Regex TagRegex(string name)
        {
            lock (Tags)
            {
                Regex r;
                if (!Tags.TryGetValue(name, out r))
                {
                    r = new Regex("<" + name + @">([\s\S]*?)</" + name + ">", RegexOptions.Compiled);
                    Tags[name] = r;
                }
                return r;
            }
        }

        // The decoded text of the first <name> element in a block of XML, or ""
        public static string Tag(string block, string name)
        {
            if (string.IsNullOrEmpty(block)) return "";
            var m = TagRegex(name).Match(block);
            return m.Success ? Decode(m.Groups[1].Value.Trim()) : "";
        }

        // The <game> blocks of a gamelist by their path, with forward slashes, and
        // without "./" when bare; the last of the same path counts
        public static Dictionary<string, string> Blocks(string file, bool bare)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(file)) return d;
            foreach (Match m in Game.Matches(File.ReadAllText(file)))
            {
                string body = m.Groups[1].Value;
                string path = Tag(body, "path").Replace('\\', '/');
                if (bare && path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
                d[path] = body;
            }
            return d;
        }

        // The fields of a block that have a value, in the order given
        public static OrderedDictionary Fields(string block, IList names)
        {
            var fields = new OrderedDictionary(StringComparer.OrdinalIgnoreCase);
            foreach (object n in names)
            {
                string value = Tag(block, n.ToString());
                if (value.Length > 0) fields[n.ToString()] = value;
            }
            return fields;
        }

        // The emulator chosen in ES-DE for a whole system, at the start of its gamelist, or ""
        public static string AlternativeEmulator(string file)
        {
            if (!File.Exists(file)) return "";
            var buffer = new char[1 << 16];
            int n;
            using (var r = new StreamReader(file, Encoding.UTF8)) n = r.Read(buffer, 0, buffer.Length);
            string head = new string(buffer, 0, n);
            int g = head.IndexOf("<game>", StringComparison.Ordinal);
            if (g >= 0) head = head.Substring(0, g);
            var m = Regex.Match(head, @"<alternativeEmulator>([\s\S]*?)</alternativeEmulator>");
            return m.Success ? Tag(m.Groups[1].Value, "label") : "";
        }

        // Text as ConvertTo-XmlText writes it in a gamelist
        public static string Escape(string s) { return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"); }

        static string NormalPath(string tagText)
        {
            string path = Decode(tagText.Trim()).Replace('\\', '/');
            return path.StartsWith("./", StringComparison.Ordinal) ? path.Substring(2) : path;
        }

        // Where each game's block is in a gamelist file, by path, in bytes (its start and
        // length in one number: see At), kept until the file changes: one game's details are
        // then read without reading the whole file
        class Located
        {
            public long Length, Ticks;
            public Dictionary<string, long> Blocks;
        }
        static long At(int start, int length) { return (long)start << 32 | (uint)length; }
        static readonly Dictionary<string, Located> located = new Dictionary<string, Located>(StringComparer.OrdinalIgnoreCase);
        static readonly byte[] GameOpen = Encoding.ASCII.GetBytes("<game>"), GameClose = Encoding.ASCII.GetBytes("</game>");
        static readonly byte[] PathOpen = Encoding.ASCII.GetBytes("<path>"), PathClose = Encoding.ASCII.GetBytes("</path>");

        static int IndexOf(byte[] b, byte[] needle, int from, int to)
        {
            byte first = needle[0];
            int last = to - needle.Length;
            for (int i = from; i <= last; i++)
            {
                if (b[i] != first) continue;
                int k = 1;
                while (k < needle.Length && b[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        static Located Locate(string file, FileInfo info)
        {
            byte[] b = File.ReadAllBytes(file);
            var blocks = new Dictionary<string, long>(StringComparer.Ordinal);
            int i = 0;
            while ((i = IndexOf(b, GameOpen, i, b.Length)) >= 0)
            {
                int start = i + GameOpen.Length;
                int end = IndexOf(b, GameClose, start, b.Length);
                if (end < 0) break;
                int ps = IndexOf(b, PathOpen, start, end);
                int pe = ps < 0 ? -1 : IndexOf(b, PathClose, ps + PathOpen.Length, end);
                if (pe >= 0)
                {
                    string path = NormalPath(Encoding.UTF8.GetString(b, ps + PathOpen.Length, pe - ps - PathOpen.Length));
                    if (path.Length > 0) blocks[path] = At(start, end - start);
                }
                i = end + GameClose.Length;
            }
            return new Located { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Blocks = blocks };
        }

        static bool SameName(byte[] b, int at, int length, byte[] name)
        {
            if (length != name.Length) return false;
            for (int k = 0; k < length; k++) if (b[at + k] != name[k]) return false;
            return true;
        }

        static bool EndsName(byte c) { return c == '>' || c == '/' || c == ' ' || c == '\t' || c == '\r' || c == '\n'; }

        // The fields a compact reading takes, as tag names in bytes: the hashed ones first,
        // then those kept that aren't hashed
        sealed class Wanted
        {
            public List<byte[]> Open = new List<byte[]>(), Close = new List<byte[]>();
            public int[] HashAt, KeepAt;
            public int DescAt = -1;
        }
        static readonly byte[] PathName = Encoding.ASCII.GetBytes("path");

        static Wanted WantedOf(IList hashed, IList kept)
        {
            var names = new List<string>();
            var hashAt = new List<int>();
            var keepAt = new List<int>();
            foreach (object o in hashed) { string n = o.ToString(); int i = names.IndexOf(n); if (i < 0) { i = names.Count; names.Add(n); } hashAt.Add(i); }
            foreach (object o in kept) { string n = o.ToString(); int i = names.IndexOf(n); if (i < 0) { i = names.Count; names.Add(n); } keepAt.Add(i); }
            var w = new Wanted { HashAt = hashAt.ToArray(), KeepAt = keepAt.ToArray(), DescAt = names.IndexOf("desc") };
            foreach (string n in names)
            {
                w.Open.Add(Encoding.ASCII.GetBytes(n));
                w.Close.Add(Encoding.ASCII.GetBytes("</" + n + ">"));
            }
            return w;
        }

        // One block, between start and end in b: its path, and the game as Compact keeps it
        static ListedGame ParseBlock(byte[] b, int start, int end, Wanted w, out string path)
        {
            var values = new string[w.Open.Count];
            path = null;
            int pos = start;
            while (pos < end)
            {
                int lt = pos;
                while (lt < end && b[lt] != '<') lt++;
                if (lt >= end - 1) break;
                int ns = lt + 1;
                if (b[ns] == '/') { pos = ns + 1; continue; }
                int ne = ns;
                while (ne < end && !EndsName(b[ne])) ne++;
                int gt = ne;
                while (gt < end && b[gt] != '>') gt++;
                if (gt >= end) break;
                pos = gt + 1;
                // <name/> is a field without a value
                if (b[gt - 1] == '/') continue;
                int which = -1;
                bool isPath = SameName(b, ns, ne - ns, PathName);
                if (!isPath) for (int k = 0; k < w.Open.Count && which < 0; k++) if (SameName(b, ns, ne - ns, w.Open[k])) which = k;
                if (!isPath && which < 0) continue;
                if ((isPath && path != null) || (which >= 0 && values[which] != null)) continue;
                byte[] closing = isPath ? PathClose : w.Close[which];
                int c = IndexOf(b, closing, gt + 1, end);
                if (c < 0) break;
                string text = Encoding.UTF8.GetString(b, gt + 1, c - gt - 1);
                if (isPath) path = NormalPath(text);
                else values[which] = Decode(text.Trim());
                pos = c + closing.Length;
            }
            var forHash = new string[w.HashAt.Length];
            bool any = false;
            for (int k = 0; k < forHash.Length; k++)
            {
                forHash[k] = values[w.HashAt[k]] ?? "";
                if (forHash[k].Length > 0) any = true;
            }
            var keep = new string[w.KeepAt.Length];
            for (int k = 0; k < keep.Length; k++) keep[k] = Library.Shared(values[w.KeepAt[k]] ?? "");
            // The words of its description, for the search
            int[] desc = w.DescAt >= 0 ? Words.DescIds(values[w.DescAt]) : null;
            return new ListedGame { Values = keep, Hash = FieldHash.Of(forHash), Any = any, Desc = desc, At = start, Length = end - start };
        }

        // Every game of a gamelist in one pass over its bytes, by path: of each, the fields
        // kept, in the order of kept ("" for none), a hash of the hashed ones in theirs (see
        // FieldHash), whether it has any of those, and where its block is. Each field is the
        // first of its name in the block, decoded as Tag decodes it; the last block of a path
        // counts, as in Blocks. What a large gamelist gives is kept on this computer too (see
        // the copy below), and read from there while the file is the same.
        public static Dictionary<string, ListedGame> Compact(string file, IList hashed, IList kept)
        {
            var games = new Dictionary<string, ListedGame>(StringComparer.Ordinal);
            var before = new FileInfo(file);
            if (!before.Exists) return games;
            string signature = CacheSignature(hashed, kept);
            if (before.Length >= CacheFrom)
            {
                var cached = ReadCompact(file, before, signature);
                if (cached != null)
                {
                    var known = new Dictionary<string, long>(cached.Count, StringComparer.Ordinal);
                    foreach (var pair in cached) known[pair.Key] = At((int)pair.Value.At, pair.Value.Length);
                    lock (located) located[file] = new Located { Length = before.Length, Ticks = before.LastWriteTimeUtc.Ticks, Blocks = known };
                    return cached;
                }
            }
            var w = WantedOf(hashed, kept);
            byte[] b = File.ReadAllBytes(file);
            // The blocks first, then read side by side
            var starts = new List<int>();
            var ends = new List<int>();
            int g = 0;
            while ((g = IndexOf(b, GameOpen, g, b.Length)) >= 0)
            {
                int start = g + GameOpen.Length;
                int end = IndexOf(b, GameClose, start, b.Length);
                if (end < 0) break;
                g = end + GameClose.Length;
                starts.Add(start);
                ends.Add(end);
            }
            var found = new ListedGame[starts.Count];
            var paths = new string[starts.Count];
            Parallel.For(0, starts.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, n =>
            {
                string path;
                found[n] = ParseBlock(b, starts[n], ends[n], w, out path);
                paths[n] = path;
            });
            var blocks = new Dictionary<string, long>(found.Length, StringComparer.Ordinal);
            for (int n = 0; n < found.Length; n++)
            {
                if (string.IsNullOrEmpty(paths[n])) continue;
                games[paths[n]] = found[n];
                blocks[paths[n]] = At(starts[n], ends[n] - starts[n]);
            }
            // Where the blocks are, for one game's details later (see Block), and the copy,
            // unless the file changed since
            var info = new FileInfo(file);
            if (info.Exists && info.Length == b.Length && info.Length == before.Length && info.LastWriteTimeUtc == before.LastWriteTimeUtc)
            {
                lock (located) located[file] = new Located { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Blocks = blocks };
                if (info.Length >= CacheFrom)
                {
                    long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
                    Task.Run(() =>
                    {
                        try { SaveCompact(file, length, ticks, signature, games); }
                        catch (Exception e) { Trace.WriteLine("Catalogue copy of " + file + ": " + e.Message); }
                    });
                }
            }
            return games;
        }

        // ---- The copy kept on this computer ----
        // What Compact finds in a gamelist of 4 MB or more, saved with the copies of the
        // catalog (see Catalog.CacheDir), in gamelists/<hash of its path>.bin, with the size
        // and date the file had: the gamelist is read again only once ES-DE or the app wrote
        // it. Its strings once each, then the games, the words of their descriptions by their
        // place among the file's words.
        const int CacheMagic = 0x474c5441, CacheVersion = 1;
        const long CacheFrom = 4 << 20;
        static readonly object cacheWriting = new object();

        static string CacheFile(string file)
        {
            if (string.IsNullOrEmpty(Catalog.CacheDir)) return null;
            return Path.Combine(Catalog.CacheDir, "gamelists", Catalog.CacheName(Path.GetFullPath(file)) + ".bin");
        }

        static string CacheSignature(IList hashed, IList kept)
        {
            var sb = new StringBuilder();
            foreach (object o in hashed) sb.Append(o).Append(',');
            sb.Append('|');
            foreach (object o in kept) sb.Append(o).Append(',');
            return sb.ToString();
        }

        static void SaveCompact(string file, long length, long ticks, string signature, Dictionary<string, ListedGame> games)
        {
            string cache = CacheFile(file);
            if (cache == null) return;
            lock (cacheWriting)
            {
                var strings = new Catalog.StringTable();
                var words = new Catalog.StringTable();
                var wordOf = Catalog.WordsById();
                // Once to find the strings, then again to write them
                WriteCompact(new BinaryWriter(Stream.Null), games, strings, words, wordOf);
                Catalog.CacheFolder(cache);
                string temp = cache + "." + Process.GetCurrentProcess().Id + ".tmp";
                using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var w = new BinaryWriter(f, new UTF8Encoding(false));
                    w.Write(CacheMagic);
                    w.Write(CacheVersion);
                    w.Write(signature);
                    w.Write(length);
                    w.Write(ticks);
                    Catalog.WriteNumber(w, words.List.Count);
                    foreach (string s in words.List) w.Write(s);
                    Catalog.WriteNumber(w, strings.List.Count);
                    foreach (string s in strings.List) w.Write(s);
                    WriteCompact(w, games, strings, words, wordOf);
                    w.Flush();
                }
                Files.Rename(temp, cache);
            }
        }

        static void WriteCompact(BinaryWriter w, Dictionary<string, ListedGame> games, Catalog.StringTable strings, Catalog.StringTable words, string[] wordOf)
        {
            Catalog.WriteNumber(w, games.Count);
            foreach (var pair in games)
            {
                var g = pair.Value;
                Catalog.WriteNumber(w, strings.Of(pair.Key));
                var values = g.Values ?? new string[0];
                Catalog.WriteNumber(w, values.Length);
                foreach (string v in values) Catalog.WriteNumber(w, strings.Of(v));
                w.Write(g.Hash);
                w.Write((byte)(g.Any ? 1 : 0));
                Catalog.WriteNumber(w, g.At);
                Catalog.WriteNumber(w, g.Length);
                if (g.Desc == null) Catalog.WriteNumber(w, 0);
                else
                {
                    Catalog.WriteNumber(w, g.Desc.Length + 1);
                    foreach (int id in g.Desc) Catalog.WriteNumber(w, words.Of(id >= 0 && id < wordOf.Length ? wordOf[id] ?? "" : ""));
                }
            }
        }

        // What the copy holds of a gamelist, or null without a copy of the file as it is
        static Dictionary<string, ListedGame> ReadCompact(string file, FileInfo info, string signature)
        {
            string cache = CacheFile(file);
            if (cache == null || !File.Exists(cache)) return null;
            try
            {
                using (var f = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    var r = new BinaryReader(f, new UTF8Encoding(false));
                    if (r.ReadInt32() != CacheMagic || r.ReadInt32() != CacheVersion || r.ReadString() != signature) return null;
                    if (r.ReadInt64() != info.Length || r.ReadInt64() != info.LastWriteTimeUtc.Ticks) return null;
                    var wordIds = new int[Catalog.ReadNumber(r)];
                    for (int i = 0; i < wordIds.Length; i++) wordIds[i] = Words.Id(r.ReadString());
                    var strings = new string[Catalog.ReadNumber(r) + 1];
                    for (int i = 1; i < strings.Length; i++) strings[i] = r.ReadString();
                    var pooled = new bool[strings.Length];
                    int count = (int)Catalog.ReadNumber(r);
                    var games = new Dictionary<string, ListedGame>(count, StringComparer.Ordinal);
                    for (int k = 0; k < count; k++)
                    {
                        string path = strings[Catalog.ReadNumber(r)];
                        var g = new ListedGame { Values = new string[Catalog.ReadNumber(r)] };
                        for (int i = 0; i < g.Values.Length; i++)
                        {
                            long at = Catalog.ReadNumber(r);
                            // Short values many games share go in the library's pool, once each
                            if (!pooled[at]) { strings[at] = Library.Shared(strings[at]); pooled[at] = true; }
                            g.Values[i] = strings[at] ?? "";
                        }
                        g.Hash = r.ReadUInt64();
                        g.Any = r.ReadByte() != 0;
                        g.At = Catalog.ReadNumber(r);
                        g.Length = (int)Catalog.ReadNumber(r);
                        long desc = Catalog.ReadNumber(r);
                        if (desc > 0)
                        {
                            g.Desc = new int[desc - 1];
                            for (int i = 0; i < g.Desc.Length; i++) g.Desc[i] = wordIds[Catalog.ReadNumber(r) - 1];
                        }
                        if (path != null) games[path] = g;
                    }
                    return games;
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("Catalogue copy of " + file + ": " + e.Message);
                return null;
            }
        }

        // One game of a gamelist, as Compact keeps it, or null
        public static ListedGame CompactOne(string file, string rel, IList hashed, IList kept)
        {
            string block = Block(file, rel);
            if (block == null) return null;
            byte[] b = Encoding.UTF8.GetBytes(block);
            string path;
            var game = ParseBlock(b, 0, b.Length, WantedOf(hashed, kept), out path);
            game.At = -1;
            return game;
        }

        // The block of one game in a gamelist, as Blocks gives it, or null
        public static string Block(string file, string rel)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var info = new FileInfo(file);
                if (!info.Exists) return null;
                Located l;
                lock (located)
                {
                    if (!located.TryGetValue(file, out l) || l.Length != info.Length || l.Ticks != info.LastWriteTimeUtc.Ticks)
                        located[file] = l = Locate(file, info);
                }
                long at;
                if (!l.Blocks.TryGetValue(rel, out at)) return null;
                var bytes = new byte[(int)(uint)at];
                using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    f.Seek(at >> 32, SeekOrigin.Begin);
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = f.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                // The file changed while it was read: it is located again
                info.Refresh();
                if (info.Length == l.Length && info.LastWriteTimeUtc.Ticks == l.Ticks) return Encoding.UTF8.GetString(bytes);
            }
            return null;
        }

        // ---- The sort names of the collections ----
        // ES-DE sorts a custom collection by each game's <collectionsortname>: for the games
        // of the collections shown it is set to "<system's full name> <sortname or name>", so
        // that a collection lists its games system by system, and it is taken out of the
        // other games, leaving those the user wrote (which don't start with the full name).
        // One pass over a gamelist's bytes, its blocks looked at side by side; the file is
        // written, with a backup the first time, only when a game changed. paths are the full
        // paths of the games in the collections. Returns how many games changed.
        static readonly byte[] SortOpen = Encoding.ASCII.GetBytes("<collectionsortname>"), SortClose = Encoding.ASCII.GetBytes("</collectionsortname>");
        static readonly byte[] SortNameOpen = Encoding.ASCII.GetBytes("<sortname>"), SortNameClose = Encoding.ASCII.GetBytes("</sortname>");
        static readonly byte[] NameOpen = Encoding.ASCII.GetBytes("<name>"), NameClose = Encoding.ASCII.GetBytes("</name>");

        public static int SetCollectionSortNames(string file, string fullName, string systemDir, HashSet<string> paths)
        {
            if (!File.Exists(file)) return 0;
            byte[] b = File.ReadAllBytes(file);
            int from = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            // Each block with the spaces before it on its line
            var starts = new List<int>();
            var opens = new List<int>();
            var ends = new List<int>();
            int pos = from;
            while (true)
            {
                int open = IndexOf(b, GameOpen, pos, b.Length);
                if (open < 0) break;
                int close = IndexOf(b, GameClose, open + GameOpen.Length, b.Length);
                if (close < 0) break;
                int start = open;
                while (start > pos && (b[start - 1] == ' ' || b[start - 1] == '\t')) start--;
                starts.Add(start);
                opens.Add(open);
                ends.Add(close);
                pos = close + GameClose.Length;
            }
            var with = new byte[starts.Count][];
            int changed = 0;
            Parallel.For(0, starts.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, n =>
            {
                string s = SortNamed(b, starts[n], opens[n], ends[n], fullName ?? "", systemDir, paths);
                if (s == null) return;
                with[n] = Encoding.UTF8.GetBytes(s);
                Interlocked.Increment(ref changed);
            });
            if (changed == 0) return 0;
            string backup = file + ".atelier-backup";
            if (!File.Exists(backup)) File.Copy(file, backup);
            string temp = file + "." + Process.GetCurrentProcess().Id + ".tmp";
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                int at = from;
                for (int n = 0; n < starts.Count; n++)
                {
                    if (with[n] == null) continue;
                    f.Write(b, at, starts[n] - at);
                    f.Write(with[n], 0, with[n].Length);
                    at = ends[n] + GameClose.Length;
                }
                f.Write(b, at, b.Length - at);
            }
            Files.Rename(temp, file);
            return changed;
        }

        // The first of a tag between from and to, decoded as Tag decodes it, or ""
        static string TagIn(byte[] b, int from, int to, byte[] open, byte[] close)
        {
            int o = IndexOf(b, open, from, to);
            if (o < 0) return "";
            int c = IndexOf(b, close, o + open.Length, to);
            if (c < 0) return "";
            return Decode(Encoding.UTF8.GetString(b, o + open.Length, c - o - open.Length).Trim());
        }

        // A block with its sort name as it should be, from its spaces before to </game>, or
        // null when it is already so
        static string SortNamed(byte[] b, int start, int open, int end, string fullName, string systemDir, HashSet<string> paths)
        {
            int bodyStart = open + GameOpen.Length;
            int so = IndexOf(b, SortOpen, bodyStart, end);
            int sc = so < 0 ? -1 : IndexOf(b, SortClose, so + SortOpen.Length, end);
            bool has = sc >= 0;
            string current = has ? Decode(Encoding.UTF8.GetString(b, so + SortOpen.Length, sc - so - SortOpen.Length).Trim()) : "";
            bool ours = has && current.StartsWith(fullName + " ", StringComparison.Ordinal);
            string rel = TagIn(b, bodyStart, end, PathOpen, PathClose);
            if (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel.Substring(2);
            string full = null;
            if (!string.IsNullOrEmpty(systemDir))
            {
                try { full = Path.GetFullPath(Path.Combine(systemDir, rel.Replace('/', '\\'))); }
                catch (Exception) { full = null; }
            }
            bool member = full != null && paths != null && paths.Contains(full);
            string value = null;
            if (!member)
            {
                if (!ours) return null;
            }
            else
            {
                string own = TagIn(b, bodyStart, end, SortNameOpen, SortNameClose);
                if (own.Length == 0) own = TagIn(b, bodyStart, end, NameOpen, NameClose);
                string sort = fullName + " " + own;
                if (has && (!ours || current == sort)) return null;
                value = sort.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            }
            string indent = Encoding.UTF8.GetString(b, start, open - start);
            string body = Encoding.UTF8.GetString(b, bodyStart, end - bodyStart);
            string line = value == null ? "" : "\n" + indent + "\t<collectionsortname>" + value + "</collectionsortname>";
            if (has)
            {
                // The tag with the end of the line before it and its spaces
                int o = body.IndexOf("<collectionsortname>", StringComparison.Ordinal);
                int c = body.IndexOf("</collectionsortname>", o + SortOpen.Length, StringComparison.Ordinal) + SortClose.Length;
                int s = o;
                while (s > 0 && (body[s - 1] == ' ' || body[s - 1] == '\t')) s--;
                if (s > 0 && body[s - 1] == '\n') s--;
                return indent + "<game>" + body.Substring(0, s) + line + body.Substring(c) + "</game>";
            }
            return indent + "<game>" + body.TrimEnd() + line + "\n" + indent + "</game>";
        }

        static readonly Dictionary<string, Regex> Fillers = new Dictionary<string, Regex>();
        static Regex FieldRegex(string name)
        {
            lock (Fillers)
            {
                Regex r;
                if (!Fillers.TryGetValue(name, out r)) Fillers[name] = r = new Regex("<" + name + @">[\s\S]*?</" + name + ">|<" + name + @"\s*/>", RegexOptions.Compiled);
                return r;
            }
        }
        static readonly Regex TrailingSpace = new Regex(@"\s*$");

        static object Unwrap(object v) { var ps = v as PSObject; return ps != null ? ps.BaseObject : v; }

        // The lines of the scraped details to set, in the order of the fields
        static List<KeyValuePair<string, string>> Lines(IDictionary values, IList fields)
        {
            var lines = new List<KeyValuePair<string, string>>();
            foreach (object f in fields)
            {
                string name = f.ToString();
                object v = values.Contains(name) ? Unwrap(values[name]) : null;
                string s = v == null ? "" : v.ToString();
                if (s.Length > 0) lines.Add(new KeyValuePair<string, string>(name, "<" + name + ">" + Escape(s) + "</" + name + ">"));
            }
            return lines;
        }

        // Changes a gamelist in one pass: edits are a game's path => its scraped details to
        // set (the other fields of the game stay as they are), or null to take the game out.
        // A game the gamelist doesn't have yet is added at its end. Returns whether the
        // file changed; it is written under another name first and then takes the place of
        // the old one.
        public static bool Apply(string file, IDictionary edits, IList fields)
        {
            var set = new Dictionary<string, IDictionary>(StringComparer.Ordinal);
            var remove = new HashSet<string>(StringComparer.Ordinal);
            foreach (DictionaryEntry e in edits)
            {
                var values = Unwrap(e.Value) as IDictionary;
                if (values == null) remove.Add(e.Key.ToString());
                else set[e.Key.ToString()] = values;
            }
            string xml = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8) : "<?xml version=\"1.0\"?>\n<gameList>\n</gameList>\n";
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + "." + Process.GetCurrentProcess().Id + ".tmp";
            var updated = new HashSet<string>(StringComparer.Ordinal);
            bool changed = false, complete = false;
            int copied = 0, pos = 0;
            var buffer = new char[1 << 16];
            var writer = new StreamWriter(temp, false, new UTF8Encoding(false), 1 << 20);
            // The text of the old file between two places, as it was
            Action<int, int> copy = (from, count) =>
            {
                while (count > 0)
                {
                    int n = Math.Min(count, buffer.Length);
                    xml.CopyTo(from, buffer, 0, n);
                    writer.Write(buffer, 0, n);
                    from += n;
                    count -= n;
                }
            };
            try
            {
            while (true)
            {
                int start = xml.IndexOf("<game>", pos, StringComparison.Ordinal);
                if (start < 0) break;
                int end = xml.IndexOf("</game>", start + 6, StringComparison.Ordinal);
                if (end < 0) break;
                int after = end + 7;
                pos = after;
                int ps = xml.IndexOf("<path>", start + 6, end - start - 6, StringComparison.Ordinal);
                int pe = ps < 0 ? -1 : xml.IndexOf("</path>", ps + 6, end - ps - 6, StringComparison.Ordinal);
                if (pe < 0) continue;
                string rel = NormalPath(xml.Substring(ps + 6, pe - ps - 6));
                IDictionary values = null;
                bool drop = remove.Contains(rel);
                if (!drop && (updated.Contains(rel) || !set.TryGetValue(rel, out values))) continue;
                int lineStart = start;
                while (lineStart > copied && (xml[lineStart - 1] == ' ' || xml[lineStart - 1] == '\t')) lineStart--;
                if (drop)
                {
                    // With its indent and the end of its line
                    copy(copied, lineStart - copied);
                    int skip = after;
                    while (skip < xml.Length && (xml[skip] == ' ' || xml[skip] == '\t')) skip++;
                    if (skip < xml.Length && xml[skip] == '\r') skip++;
                    if (skip < xml.Length && xml[skip] == '\n') skip++;
                    copied = pos = skip;
                }
                else
                {
                    string indent = xml.Substring(lineStart, start - lineStart);
                    string body = xml.Substring(start + 6, end - start - 6);
                    foreach (var line in Lines(values, fields))
                    {
                        var current = FieldRegex(line.Key);
                        string replacement = line.Value.Replace("$", "$$");
                        if (current.IsMatch(body)) body = current.Replace(body, replacement, 1);
                        else body = TrailingSpace.Replace(body, ("\n" + indent + "\t" + line.Value + "\n" + indent).Replace("$", "$$"), 1);
                    }
                    copy(copied, start + 6 - copied);
                    writer.Write(body);
                    writer.Write("</game>");
                    copied = after;
                    updated.Add(rel);
                }
                changed = true;
            }
            var added = new StringBuilder();
            foreach (var pair in set)
            {
                if (updated.Contains(pair.Key)) continue;
                added.Append("\t<game>\n\t\t<path>./").Append(Escape(pair.Key)).Append("</path>\n");
                foreach (var line in Lines(pair.Value, fields)) added.Append("\t\t").Append(line.Value).Append('\n');
                added.Append("\t</game>\n");
            }
            if (added.Length > 0)
            {
                // The new games go before the end of the list, which ends the file
                int listEnd = xml.LastIndexOf("</gameList>", StringComparison.Ordinal);
                if (listEnd < copied || xml.Substring(listEnd + 11).Trim().Length > 0) throw new IOException(file + " has no </gameList>");
                copy(copied, listEnd - copied);
                writer.Write(added.ToString());
                writer.Write("</gameList>\n");
                changed = true;
            }
            else copy(copied, xml.Length - copied);
            complete = true;
            }
            finally
            {
                writer.Dispose();
                if (!complete || !changed) File.Delete(temp);
            }
            if (!changed) return false;
            Files.Rename(temp, file);
            return true;
        }
    }

    public static class Paths
    {
        // A path without its extension, the name its media files carry
        public static string BaseOf(string rel)
        {
            int dot = rel.LastIndexOf('.');
            if (dot < 0 || dot == rel.Length - 1) return rel;
            if (rel.IndexOf('/', dot) >= 0 || rel.IndexOf('\\', dot) >= 0) return rel;
            return rel.Substring(0, dot);
        }

        // A file's extension as Node.js gives it: none for a name that starts with its only dot
        public static string Ext(string path)
        {
            string name = Path.GetFileName(path);
            int i = name.LastIndexOf('.');
            return i <= 0 ? "" : name.Substring(i);
        }

        // A path made relative to a folder, with forward slashes
        public static string Relative(string dir, string file)
        {
            string root = dir.TrimEnd('\\');
            if (file.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return file.Substring(root.Length + 1).Replace('\\', '/');
            var a = Path.GetFullPath(root).TrimEnd('\\').Split('\\');
            var b = Path.GetFullPath(file).TrimEnd('\\').Split('\\');
            if (!string.Equals(a[0], b[0], StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(file).Replace('\\', '/');
            int common = 0;
            while (common < a.Length && common < b.Length && string.Equals(a[common], b[common], StringComparison.OrdinalIgnoreCase)) common++;
            var parts = new List<string>();
            for (int i = common; i < a.Length; i++) parts.Add("..");
            for (int i = common; i < b.Length; i++) parts.Add(b[i]);
            return string.Join("/", parts);
        }

        // The first of base + extension that exists, or null
        public static string FirstExisting(string basePath, IList extensions)
        {
            foreach (object e in extensions)
            {
                string file = basePath + e;
                if (File.Exists(file)) return file;
            }
            return null;
        }

        public static long MtimeRounded(string file) { return (long)Math.Floor(Files.MtimeMs(file) + 0.5); }
    }

    // The media files of one system as Catalogue reads them: by type and by the game's
    // path without extension, in ES-DE's order of extensions, with the user's own
    // images that Atelier set aside for the types it draws itself
    public class MediaIndex
    {
        readonly Dictionary<string, Dictionary<string, List<string>>> shown = new Dictionary<string, Dictionary<string, List<string>>>();
        readonly Dictionary<string, Dictionary<string, List<string>>> setAside = new Dictionary<string, Dictionary<string, List<string>>>();

        // types: type => its extensions; roots: type => its folder; asideRoots: type =>
        // the folder of its set-aside images, for the types that have one
        public MediaIndex(IDictionary types, IDictionary roots, IDictionary asideRoots)
        {
            foreach (DictionaryEntry t in types)
            {
                string type = t.Key.ToString();
                var exts = ToStrings((IEnumerable)t.Value);
                shown[type] = Scan(roots[type] as string, exts);
                if (asideRoots.Contains(type)) setAside[type] = Scan(asideRoots[type] as string, exts);
            }
        }

        // The media of one game only, looked up file by file: what a transfer needs, without
        // reading every media folder of its system
        public MediaIndex(IDictionary types, IDictionary roots, IDictionary asideRoots, string rel)
        {
            string key = Paths.BaseOf(rel);
            foreach (DictionaryEntry t in types)
            {
                string type = t.Key.ToString();
                var exts = ToStrings((IEnumerable)t.Value);
                shown[type] = Lookup(roots[type] as string, exts, key);
                if (asideRoots.Contains(type)) setAside[type] = Lookup(asideRoots[type] as string, exts, key);
            }
        }

        static Dictionary<string, List<string>> Lookup(string root, List<string> exts, string key)
        {
            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(root)) return found;
            string stem = Path.Combine(root, key.Replace('/', '\\'));
            var files = new List<string>();
            foreach (string ext in exts) if (File.Exists(stem + ext)) files.Add(stem + ext);
            if (files.Count > 0) found[key] = files;
            return found;
        }

        static List<string> ToStrings(IEnumerable values)
        {
            var list = new List<string>();
            foreach (object v in values) list.Add(v is PSObject ? ((PSObject)v).BaseObject.ToString() : v.ToString());
            return list;
        }

        static Dictionary<string, List<string>> Scan(string root, List<string> exts)
        {
            var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;
            var ranked = new Dictionary<string, List<KeyValuePair<int, string>>>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                int rank = exts.IndexOf(ext);
                if (rank < 0) continue;
                string rel = Paths.Relative(root, file);
                string key = rel.Substring(0, rel.Length - ext.Length);
                List<KeyValuePair<int, string>> list;
                if (!ranked.TryGetValue(key, out list)) ranked[key] = list = new List<KeyValuePair<int, string>>();
                list.Add(new KeyValuePair<int, string>(rank, file));
            }
            foreach (var pair in ranked)
            {
                var sorted = new List<KeyValuePair<int, string>>(pair.Value);
                sorted.Sort((x, y) => x.Key.CompareTo(y.Key));
                found[pair.Key] = sorted.ConvertAll(x => x.Value);
            }
            return found;
        }

        // Whether an image was drawn by Atelier: its PNG text chunk says so
        public static bool IsAtelierImage(string file)
        {
            try
            {
                var head = new byte[120];
                int n;
                using (var f = File.OpenRead(file)) n = f.Read(head, 0, head.Length);
                string text = Encoding.GetEncoding(28591).GetString(head, 0, n);
                return text.Contains("Atelier achievement progress");
            }
            catch
            {
                return false;
            }
        }

        // The user's own media file of a type for a game: the one ES-DE shows, or for the
        // types Atelier draws, the one set aside in its place
        public string UserMedia(string rel, string type)
        {
            string key = Paths.BaseOf(rel);
            bool aside = setAside.ContainsKey(type);
            Dictionary<string, List<string>> byKey;
            List<string> files;
            if (shown.TryGetValue(type, out byKey) && byKey.TryGetValue(key, out files))
            {
                foreach (string f in files) if (!(aside && IsAtelierImage(f))) return f;
            }
            if (aside && setAside[type].TryGetValue(key, out files) && files.Count > 0) return files[0];
            return null;
        }
    }

    // The games of a system as Catalogue sees them: each file with one of the system's
    // extensions, or folder named like one, with every file it needs (see
    // catalogue/scripts/windows/library.ps1)
    public static class LocalGames
    {
        static readonly Regex CueFile = new Regex(@"^\s*(?:FILE|DATAFILE|AUDIOFILE)\s+(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        static readonly Regex GdiTrack = new Regex(@"^\s*\d+\s+\d+\s+\d+\s+\d+\s+(?:""([^""]+)""|(\S+))");
        static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        // What a cue sheet renamed by hand may have lost track of: emulators then take the
        // image named like the sheet
        static readonly string[] Images = { ".bin", ".img", ".iso" };
        // The files that change as a game is played, by extension: the saves and states of
        // RetroArch and of the emulators that write them next to the game. They don't go with
        // a game for their name, or it would have to be archived again after every session.
        static readonly Regex Played = new Regex(@"^\.(srm|sav|rtc|ldci|eep|sra|fla|mpk|mcr|mcd|state\d*|ss\d|sgm|dsv|ds\d|ml\d|oops)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // The systems of programs, whose games need what lies around them though nothing names it
        static readonly HashSet<string> ProgramSystems = new HashSet<string>(StringComparer.Ordinal) { "dos", "pc", "scummvm" };
        static readonly HashSet<string> Programs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".bat", ".com", ".conf", ".svm", ".scummvm" };

        // The files that name others
        public static readonly HashSet<string> Descriptors = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cue", ".toc", ".gdi", ".m3u", ".ccd", ".mds" };

        // What reading a game's descriptors tells besides its files: what they name that
        // can't be found, as [descriptor, name], and whether the files follow from the
        // descriptors alone. If not, as with a file looked for beside a sheet because it isn't
        // where the sheet says, they follow from which files there are now too, and can change
        // with no descriptor changing.
        public sealed class Reading
        {
            public readonly List<string[]> Missing = new List<string[]>();
            public bool Stable = true;
        }

        public static bool IsProgramSystem(string system) { return ProgramSystems.Contains(system); }

        // Whether a game is a program in a folder of its own, which then is the game. A
        // program straight in the system's folder has no folder of its own.
        public static bool IsProgram(string system, string rel)
        {
            return rel.IndexOf('/') >= 0 && ProgramSystems.Contains(system) && Programs.Contains(Paths.Ext(rel));
        }

        // Whether a file of this extension that is no game can go with the game named like it
        public static bool GoesWithGame(string ext, ICollection<string> extensions) { return !extensions.Contains(ext) && !Played.IsMatch(ext); }

        public static bool Inside(string folder, string file)
        {
            return file.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // Every file under a folder, none when it can't be read
        public static string[] AllIn(string folder)
        {
            try { return Directory.GetFiles(folder, "*", SearchOption.AllDirectories); }
            catch { return new string[0]; }
        }

        // The files of a folder named like a game, or null when something in it is a game
        // of its own. own holds the game's files so far.
        public static List<string> NamedFolder(string folder, ICollection<string> extensions, ICollection<string> own)
        {
            var files = new List<string>();
            try
            {
                foreach (string e in Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories))
                {
                    if (extensions.Contains(Paths.Ext(e).ToLowerInvariant()) && !own.Contains(e)) return null;
                    if (File.Exists(e)) files.Add(e);
                }
            }
            catch { return null; }
            return files;
        }

        // The text of a descriptor: the one it says it is or that reads as UTF-8, as plain
        // English always does; or else the ways it may be written, the likeliest first, as
        // tools have long saved cue sheets in the encoding of their computer
        static string[] Readings(byte[] bytes)
        {
            bool marked = (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) ||
                (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            if (marked) using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true)) return new[] { reader.ReadToEnd() };
            try { return new[] { StrictUtf8.GetString(bytes) }; }
            catch (ArgumentException) { }
            return new[] { Encoding.Default.GetString(bytes), Encoding.GetEncoding(1252).GetString(bytes), Encoding.GetEncoding(932).GetString(bytes) };
        }

        // The names a descriptor gives, as written in it
        static List<string> Names(string ext, string text)
        {
            var names = new List<string>();
            if (ext == ".cue" || ext == ".toc")
            {
                foreach (Match m in CueFile.Matches(text)) names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                return names;
            }
            var lines = Regex.Split(text, @"\r?\n");
            if (ext == ".gdi")
            {
                for (int i = 1; i < lines.Length; i++)
                {
                    var m = GdiTrack.Match(lines[i]);
                    if (m.Success) names.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                }
                return names;
            }
            foreach (string line in lines)
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                // RetroArch allows a label after the path: "Game (Disc 1).cue|Disc 1"
                int bar = t.IndexOf('|');
                names.Add(bar < 0 ? t : t.Substring(0, bar).Trim());
            }
            return names;
        }

        // A file a descriptor names: where it says, or beside the descriptor when that is
        // nowhere, as with the path of the computer it was made on. A file outside the
        // system's folder can't be part of a game: nothing could bring it back.
        static string Located(string dir, string name, string root, out bool asWritten)
        {
            asWritten = false;
            try
            {
                string full = Path.GetFullPath(Path.Combine(dir, name));
                if (Inside(root, full) && File.Exists(full))
                {
                    asWritten = true;
                    return full;
                }
                full = Path.Combine(dir, name.Substring(name.LastIndexOfAny(new[] { '/', '\\' }) + 1));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }

        // The files a descriptor refers to
        public static List<string> References(string file, string root, Reading reading)
        {
            var found = new List<string>();
            string ext = Paths.Ext(file).ToLowerInvariant();
            if (!Descriptors.Contains(ext)) return found;
            string dir = Path.GetDirectoryName(file);
            string stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(file));
            string descriptor = Path.GetFileName(file);
            if (ext == ".ccd" || ext == ".mds")
            {
                // They name nothing: their files are the ones beside them
                reading.Stable = false;
                string image = stem + (ext == ".ccd" ? ".img" : ".mdf");
                if (File.Exists(image)) found.Add(image);
                else reading.Missing.Add(new[] { descriptor, Path.GetFileName(image) });
                if (ext == ".ccd" && File.Exists(stem + ".sub")) found.Add(stem + ".sub");
                return found;
            }
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch
            {
                reading.Stable = false;
                return found;
            }
            List<string> absent = null;
            bool sure = false;
            foreach (string text in Readings(bytes))
            {
                var names = Names(ext, text);
                var here = new List<string>();
                var gone = new List<string>();
                bool written = true;
                for (int i = 0; i < names.Count; i++)
                {
                    bool asWritten;
                    string full = Located(dir, names[i], root, out asWritten);
                    written &= asWritten;
                    if (full == null && i == 0 && ext != ".gdi" && ext != ".m3u") full = Paths.FirstExisting(stem, Images);
                    if (full != null) here.Add(full);
                    else gone.Add(names[i]);
                }
                if (absent == null || gone.Count < absent.Count) { found = here; absent = gone; sure = written; }
                if (gone.Count == 0) break;
            }
            if (!sure) reading.Stable = false;
            foreach (string name in absent) reading.Missing.Add(new[] { descriptor, name });
            return found;
        }

        // Every file a game's file names, and those they name in turn
        public static List<string> NeedsOf(string entry, string root, Reading reading)
        {
            var seen = new List<string>();
            var seenSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            queue.Enqueue(entry);
            while (queue.Count > 0)
            {
                foreach (string r in References(queue.Dequeue(), root, reading))
                {
                    if (seenSet.Contains(r) || string.Equals(r, entry, StringComparison.OrdinalIgnoreCase)) continue;
                    seenSet.Add(r);
                    seen.Add(r);
                    queue.Enqueue(r);
                }
            }
            return seen;
        }

        // The files named like a file, which go with its game unless they are games, saves,
        // or named by another game of that name: Gioco.bin belongs to Gioco.cue, not to the
        // Gioco.chd beside it. As LocalScan.Scan finds them in its folder listings; a lookup
        // by name takes no longer in a folder of 500,000 games.
        static void AddAlike(string file, ICollection<string> extensions, string root, HashSet<string> own, List<string> extra)
        {
            string dir = Path.GetDirectoryName(file);
            string stem = Paths.BaseOf(Path.GetFileName(file));
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var named = new List<string>();
            foreach (var e in FastDir.List(dir, stem + ".*"))
            {
                if (e.IsDir || !string.Equals(Paths.BaseOf(e.Name), stem, StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Paths.Ext(e.Name).ToLowerInvariant();
                string full = Path.Combine(dir, e.Name);
                if (own.Contains(full)) continue;
                if (extensions.Contains(ext)) { if (Descriptors.Contains(ext)) named.Add(full); }
                else if (GoesWithGame(ext, extensions)) found.Add(full);
            }
            if (found.Count == 0) return;
            foreach (string descriptor in named) found.ExceptWith(NeedsOf(descriptor, root, new Reading()));
            foreach (string f in found) if (own.Add(f)) extra.Add(f);
        }

        // A game as ScanOne gives it, from its file or folder and the other files it has. Its
        // files' paths start at its folder, or at the system's when one lies outside that
        // (wide), as with a playlist in one folder and its discs in another.
        static Hashtable Build(string systemDir, string entry, bool isDir, List<string> extra)
        {
            string dir = Path.GetDirectoryName(entry);
            var abs = new List<string>();
            if (isDir) abs.AddRange(Directory.GetFiles(entry, "*", SearchOption.AllDirectories));
            else { abs.Add(entry); abs.AddRange(extra); }
            bool wide = !isDir && extra.Exists(x => !Inside(dir, x));
            if (wide) dir = systemDir;
            var files = new List<Hashtable>();
            long size = 0;
            foreach (string file in abs)
            {
                var info = new FileInfo(file);
                var f = new Hashtable();
                f["rel"] = Paths.Relative(dir, file);
                f["abs"] = file;
                f["size"] = info.Length;
                f["mtime"] = Paths.MtimeRounded(file);
                size += info.Length;
                files.Add(f);
            }
            var locale = CultureInfo.CurrentCulture.CompareInfo;
            files.Sort((x, y) => locale.Compare((string)x["rel"], (string)y["rel"]));
            var game = new Hashtable();
            game["entry"] = entry;
            game["isDir"] = isDir;
            game["dir"] = dir;
            game["wide"] = wide;
            game["rel"] = Paths.Relative(systemDir, entry);
            game["files"] = files.ToArray();
            game["size"] = size;
            return game;
        }

        // One game read again on its own, as LocalScan.Scan finds it: { entry, isDir, dir,
        // wide, rel, files: [{ rel, abs, size, mtime }], size, missing: [[descriptor, name]] },
        // or null when it is gone. A transfer reads its game this way, without reading the
        // whole system again.
        public static Hashtable ScanOne(string systemDir, string rel, ICollection<string> extensions, string system)
        {
            string entry = Path.GetFullPath(Path.Combine(systemDir, rel.Replace('/', '\\')));
            bool isDir = Directory.Exists(entry);
            if (!isDir && !File.Exists(entry)) return null;
            var reading = new Reading();
            var extra = new List<string>();
            if (!isDir)
            {
                extra = NeedsOf(entry, systemDir, reading);
                var own = new HashSet<string>(extra, StringComparer.OrdinalIgnoreCase);
                own.Add(entry);
                if (IsProgram(system, rel.Replace('\\', '/')))
                {
                    foreach (string f in AllIn(Path.GetDirectoryName(entry))) if (own.Add(f)) extra.Add(f);
                }
                else
                {
                    var named = new List<string>(extra);
                    AddAlike(entry, extensions, systemDir, own, extra);
                    foreach (string f in named) if (Descriptors.Contains(Paths.Ext(f))) AddAlike(f, extensions, systemDir, own, extra);
                    string folder = Paths.BaseOf(entry);
                    if (Directory.Exists(folder) && !extensions.Contains(Paths.Ext(folder).ToLowerInvariant()))
                    {
                        var inside = NamedFolder(folder, extensions, own);
                        if (inside != null) foreach (string f in inside) if (own.Add(f)) extra.Add(f);
                    }
                }
            }
            var game = Build(systemDir, entry, isDir, extra);
            game["missing"] = reading.Missing.ToArray();
            return game;
        }
    }

    // The shape of Catalogue's library, set once by the app (LibraryShape.Set): its types
    // of media, the details a scraper writes, in a gamelist's order (which their hash
    // follows), and the details kept of every game: some of those, then fields only ES-DE
    // has, such as favorites
    public static class LibraryShape
    {
        public static string[] MediaTypes = new string[0];
        public static string[] Scraped = new string[0];
        public static string[] Kept = new string[0];
        public static int KeptScraped;

        static string[] Strings(IList list)
        {
            var a = new string[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = list[i].ToString();
            return a;
        }

        public static void Set(IList mediaTypes, IList scraped, IList keptScraped, IList keptOwn)
        {
            MediaTypes = Strings(mediaTypes);
            Scraped = Strings(scraped);
            var kept = new List<string>(Strings(keptScraped));
            KeptScraped = kept.Count;
            kept.AddRange(Strings(keptOwn));
            Kept = kept.ToArray();
        }

        public static int MediaType(string name) { return Array.IndexOf(MediaTypes, name); }
        public static int KeptAt(string name) { return Array.IndexOf(Kept, name); }

        internal static object Unwrap(object v) { var ps = v as PSObject; return ps != null ? ps.BaseObject : v; }
        internal static string Str(IDictionary d, string key) { object v = d != null && d.Contains(key) ? Unwrap(d[key]) : null; return v == null ? null : v.ToString(); }
        internal static long Long(IDictionary d, string key)
        {
            object v = d != null && d.Contains(key) ? Unwrap(d[key]) : null;
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
    }

    // A game in the archive, as the library keeps it: its entry in the catalog without the
    // details it doesn't keep (its description, for one), with a hash of every scraped
    // detail to tell whether ES-DE's are the same
    public sealed class ArchivedGame
    {
        public string Rel, Archived, Updated, Device;
        public bool IsDir;
        public long Size, Packed;
        public LibFile[] Files;
        public LibMedia[] Media;
        public string[] Values;
        public ulong Hash;
        public int[] Desc;

        public static ArchivedGame From(string rel, object entry)
        {
            var e = LibraryShape.Unwrap(entry) as IDictionary;
            if (e == null) return null;
            var a = new ArchivedGame
            {
                Rel = rel, Archived = Library.Shared(LibraryShape.Str(e, "archived")), Updated = Library.Shared(LibraryShape.Str(e, "updated")),
                Device = Library.Shared(LibraryShape.Str(e, "device")), Size = LibraryShape.Long(e, "size"), Packed = LibraryShape.Long(e, "packed"),
            };
            object isDir = e.Contains("isDir") ? LibraryShape.Unwrap(e["isDir"]) : null;
            a.IsDir = isDir is bool && (bool)isDir;
            var files = new List<LibFile>();
            var list = e.Contains("files") ? LibraryShape.Unwrap(e["files"]) as IEnumerable : null;
            if (list != null)
            {
                foreach (object o in list)
                {
                    var f = LibraryShape.Unwrap(o) as IDictionary;
                    if (f == null) continue;
                    // A game of one file is mostly that file: one string for both
                    string path = LibraryShape.Str(f, "path");
                    if (path == rel) path = rel;
                    files.Add(new LibFile { Rel = path, Size = LibraryShape.Long(f, "size"), Mtime = LibraryShape.Long(f, "mtime"), Crc = LibraryShape.Str(f, "crc") });
                }
            }
            a.Files = files.ToArray();
            var media = new List<LibMedia>();
            var byType = e.Contains("media") ? LibraryShape.Unwrap(e["media"]) as IDictionary : null;
            if (byType != null)
            {
                string stem = Paths.BaseOf(rel);
                foreach (DictionaryEntry m in byType)
                {
                    var d = LibraryShape.Unwrap(m.Value) as IDictionary;
                    string type = m.Key.ToString();
                    string file = LibraryShape.Str(d, "file");
                    if (d == null || file == null) continue;
                    string ext = Library.Shared(Paths.Ext(file).ToLowerInvariant());
                    media.Add(new LibMedia
                    {
                        Type = LibraryShape.MediaType(type), Ext = ext, Size = LibraryShape.Long(d, "size"), Mtime = LibraryShape.Long(d, "mtime"),
                        // Kept only when it isn't where Catalogue puts it
                        File = file == type + "/" + stem + ext ? null : file,
                    });
                }
            }
            a.Media = media.ToArray();
            var fields = e.Contains("fields") ? LibraryShape.Unwrap(e["fields"]) as IDictionary : null;
            var scraped = new string[LibraryShape.Scraped.Length];
            for (int i = 0; i < scraped.Length; i++) scraped[i] = LibraryShape.Str(fields, LibraryShape.Scraped[i]) ?? "";
            a.Hash = FieldHash.Of(scraped);
            a.Desc = Words.DescIds(LibraryShape.Str(fields, "desc"));
            a.Values = new string[LibraryShape.KeptScraped];
            for (int i = 0; i < a.Values.Length; i++) a.Values[i] = Library.Shared(LibraryShape.Str(fields, LibraryShape.Kept[i]) ?? "");
            return a;
        }

        // The archive's media file of a type, as its entry names it, or null
        public string MediaFile(int type)
        {
            if (Media == null) return null;
            foreach (var m in Media)
            {
                if (m.Type != type) continue;
                return m.File ?? LibraryShape.MediaTypes[type] + "/" + Paths.BaseOf(Rel) + m.Ext;
            }
            return null;
        }
    }

    // The archive's catalog: what it holds of each system's games, their zip, files,
    // details and media (see catalogue/scripts/windows/store.ps1). A system's catalog is in
    // 256 parts, @metadata/<system>/catalog/00.json to ff.json, each game in the part its
    // path falls in, so that a change rewrites one small file however many games the
    // system has; @metadata/<system>/system.json names the system. What the library keeps
    // of each part is kept with the size and date the part had, and read again only when
    // those change, whether this computer or another changed it; a transfer reads its
    // game's whole entry from its part. Changes wait in memory, where reading finds them
    // already, until Flush writes each part they touch as it is on the disk at that moment
    // with them on top. The gamelist.xml beside the parts, written from the catalog so that
    // the archive reads without the app too, is written again as the app leaves, for the
    // systems whose catalog it changed (WriteGamelists). The first version kept a system's
    // catalog in one catalog.json, which is split into parts the first time it is read.
    public static class Catalog
    {
        public const string Meta = "@metadata";
        public const int Format = 2;
        const int PartCount = 256;
        static readonly object Removed = new object();
        static readonly int Pid = Process.GetCurrentProcess().Id;

        class Part
        {
            public long Length = -1;
            public long Ticks;
            public Dictionary<string, ArchivedGame> Games;
            public readonly Dictionary<string, object> Pending = new Dictionary<string, object>(StringComparer.Ordinal);
        }

        class SystemCatalog
        {
            public readonly object Lock = new object();
            public readonly Part[] Parts = new Part[PartCount];
            public string Root, System, FullName, PendingFullName;
            public bool FullNameRead, OldChecked, Changed;
            // Whether the copy on this computer was read, and whether it differs from the parts kept
            public bool CacheTried, CacheStale;
            // The last part read whole, for the next entry a transfer asks for
            public int FullPart = -1;
            public long FullLength, FullTicks;
            public Dictionary<string, object> Full;

            public Part At(int n) { return Parts[n] ?? (Parts[n] = new Part()); }
        }

        static readonly Dictionary<string, SystemCatalog> catalogs = new Dictionary<string, SystemCatalog>(StringComparer.OrdinalIgnoreCase);

        static string RootKey(string root) { return Path.GetFullPath(root).TrimEnd('\\') + "|"; }

        static SystemCatalog Of(string root, string system)
        {
            string key = RootKey(root) + system;
            lock (catalogs)
            {
                SystemCatalog c;
                if (!catalogs.TryGetValue(key, out c)) catalogs[key] = c = new SystemCatalog { Root = root, System = system };
                return c;
            }
        }

        // The systems known of an archive, or of every archive
        static List<SystemCatalog> Known(string root)
        {
            string prefix = string.IsNullOrEmpty(root) ? null : RootKey(root);
            var list = new List<SystemCatalog>();
            lock (catalogs)
            {
                foreach (var pair in catalogs) if (prefix == null || pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) list.Add(pair.Value);
            }
            return list;
        }

        public static string MetaDir(string root, string system) { return Path.Combine(root, Meta, system); }
        static string PartsDir(SystemCatalog c) { return Path.Combine(MetaDir(c.Root, c.System), "catalog"); }
        static string PartFile(SystemCatalog c, int n) { return Path.Combine(PartsDir(c), n.ToString("x2") + ".json"); }
        static string SystemFile(SystemCatalog c) { return Path.Combine(MetaDir(c.Root, c.System), "system.json"); }
        static string OldFile(SystemCatalog c) { return Path.Combine(MetaDir(c.Root, c.System), "catalog.json"); }

        // The part a game's path falls in: by its FNV-1a hash
        public static int PartOf(string rel)
        {
            uint h = 2166136261;
            foreach (char ch in rel)
            {
                h ^= ch;
                h *= 16777619;
            }
            return (int)(h % PartCount);
        }

        static Dictionary<string, object> NewGames() { return new Dictionary<string, object>(StringComparer.Ordinal); }

        static void WriteAtomic(string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string temp = file + "." + Pid + "." + Thread.CurrentThread.ManagedThreadId + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            Files.Rename(temp, file);
        }

        // A part as it is on the disk now; one being replaced by another computer at that
        // moment is read again
        static Dictionary<string, object> ReadPart(string file)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(file)) return NewGames();
                    var d = Json.Read(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                    object games;
                    if (d != null && d.TryGetValue("games", out games) && games is Dictionary<string, object>) return (Dictionary<string, object>)games;
                    return NewGames();
                }
                catch (IOException)
                {
                    if (attempt >= 4) throw;
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }

        static Dictionary<string, ArchivedGame> Compact(Dictionary<string, object> games)
        {
            var d = new Dictionary<string, ArchivedGame>(games.Count, StringComparer.Ordinal);
            foreach (var g in games)
            {
                var a = ArchivedGame.From(g.Key, g.Value);
                if (a != null) d[g.Key] = a;
            }
            return d;
        }

        static void Keep(Part p, Dictionary<string, ArchivedGame> games, FileInfo info)
        {
            p.Games = games;
            if (info == null || !info.Exists)
            {
                p.Length = -1;
                p.Ticks = 0;
            }
            else
            {
                p.Length = info.Length;
                p.Ticks = info.LastWriteTimeUtc.Ticks;
            }
        }

        static bool Fresh(Part p, FileInfo info)
        {
            if (p.Games == null) return false;
            if (info == null || !info.Exists) return p.Length == -1;
            return p.Length == info.Length && p.Ticks == info.LastWriteTimeUtc.Ticks;
        }

        // A part written with games; compact, when given, is what the library keeps of them
        static void WritePart(SystemCatalog c, int n, Dictionary<string, object> games, Dictionary<string, ArchivedGame> compact = null)
        {
            string file = PartFile(c, n);
            if (games.Count == 0) Files.Remove(file);
            else WriteAtomic(file, Json.Write(new Dictionary<string, object> { { "games", games } }));
            var info = new FileInfo(file);
            Keep(c.At(n), compact ?? Compact(games), info);
            c.CacheStale = true;
            c.FullPart = n;
            c.Full = games;
            c.FullLength = info.Exists ? info.Length : -1;
            c.FullTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : 0;
        }

        static void WriteSystem(SystemCatalog c, string fullName)
        {
            WriteAtomic(SystemFile(c), Json.Write(new Dictionary<string, object> { { "fullName", fullName } }, true));
            c.FullName = fullName;
            c.FullNameRead = true;
        }

        // The archive says which format it has: once, whichever system was split first
        static readonly HashSet<string> marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static void MarkFormat(string root)
        {
            lock (marked)
            {
                if (!marked.Add(RootKey(root))) return;
                string file = Path.Combine(root, Meta, "archive.json");
                if (!File.Exists(file)) return;
                var d = Json.Read(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return;
                object f;
                if (d.TryGetValue("format", out f) && f is int && (int)f >= Format) return;
                d["format"] = Format;
                WriteAtomic(file, Json.Write(d, true));
            }
        }

        // The catalog.json of the first version, split into parts, which take its place
        static void Migrate(SystemCatalog c, bool always)
        {
            if (c.OldChecked && !always) return;
            c.OldChecked = true;
            string old = OldFile(c);
            if (!File.Exists(old)) return;
            var d = Json.Read(File.ReadAllText(old, Encoding.UTF8)) as Dictionary<string, object>;
            object v;
            if (d != null && d.TryGetValue("fullName", out v) && v is string && !File.Exists(SystemFile(c))) WriteSystem(c, (string)v);
            var games = d != null && d.TryGetValue("games", out v) ? v as Dictionary<string, object> : null;
            if (games != null)
            {
                var byPart = new Dictionary<int, List<KeyValuePair<string, object>>>();
                foreach (var g in games)
                {
                    int n = PartOf(g.Key);
                    List<KeyValuePair<string, object>> list;
                    if (!byPart.TryGetValue(n, out list)) byPart[n] = list = new List<KeyValuePair<string, object>>();
                    list.Add(g);
                }
                foreach (var pair in byPart)
                {
                    var part = ReadPart(PartFile(c, pair.Key));
                    foreach (var g in pair.Value) if (!part.ContainsKey(g.Key)) part[g.Key] = g.Value;
                    WritePart(c, pair.Key, part);
                }
            }
            File.Delete(old);
            c.Changed = true;
            MarkFormat(c.Root);
        }

        // The systems an archive has games of, with those that have changes still to write
        public static List<string> Systems(string root)
        {
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dir = new DirectoryInfo(Path.Combine(root, Meta));
            if (dir.Exists)
            {
                foreach (var sub in dir.GetDirectories())
                {
                    if (Directory.Exists(Path.Combine(sub.FullName, "catalog")) || File.Exists(Path.Combine(sub.FullName, "catalog.json")))
                    {
                        found.Add(sub.Name);
                        seen.Add(sub.Name);
                    }
                }
            }
            foreach (var c in Known(root))
            {
                if (seen.Contains(c.System)) continue;
                lock (c.Lock)
                {
                    foreach (var p in c.Parts)
                    {
                        if (p == null || p.Pending.Count == 0) continue;
                        found.Add(c.System);
                        break;
                    }
                }
            }
            return found;
        }

        // The full name of a system, as the archive has it, or null
        public static string FullName(string root, string system)
        {
            var c = Of(root, system);
            lock (c.Lock)
            {
                Migrate(c, false);
                if (c.PendingFullName != null) return c.PendingFullName;
                if (!c.FullNameRead)
                {
                    c.FullNameRead = true;
                    string file = SystemFile(c);
                    if (File.Exists(file))
                    {
                        var d = Json.Read(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                        object v;
                        if (d != null && d.TryGetValue("fullName", out v)) c.FullName = v as string;
                    }
                }
                return c.FullName;
            }
        }

        // A game's whole entry, with the changes still to write, or null
        public static object Entry(string root, string system, string rel)
        {
            var c = Of(root, system);
            lock (c.Lock)
            {
                Migrate(c, false);
                int n = PartOf(rel);
                var p = c.At(n);
                object e;
                if (p.Pending.TryGetValue(rel, out e)) return e == Removed ? null : e;
                var info = new FileInfo(PartFile(c, n));
                bool exists = info.Exists;
                if (c.FullPart != n || c.FullLength != (exists ? info.Length : -1) || c.FullTicks != (exists ? info.LastWriteTimeUtc.Ticks : 0))
                {
                    c.Full = ReadPart(info.FullName);
                    c.FullPart = n;
                    c.FullLength = exists ? info.Length : -1;
                    c.FullTicks = exists ? info.LastWriteTimeUtc.Ticks : 0;
                    if (!Fresh(p, info)) Keep(p, Compact(c.Full), info);
                }
                return c.Full.TryGetValue(rel, out e) ? e : null;
            }
        }

        // ---- The copy kept on this computer ----
        // What the library keeps of each part, saved in the app's data folder with the size
        // and date the part had: the next time the app starts, only the parts changed since
        // are read from the archive, which on a network share or a hard disk is most of the
        // time a start takes. One file a system, <CacheDir>/archive-<hash of the archive's
        // folder>/<system>.bin: its strings once each, then the parts and their games, the
        // words of their descriptions by their place among the file's words. A copy made with
        // other media types or details than the app's now, or that can't be read, is left
        // aside and made again.
        public static string CacheDir;
        const int CacheMagic = 0x434c5441, CacheVersion = 1;
        static readonly object cacheWriting = new object();

        static string Signature()
        {
            var sb = new StringBuilder();
            sb.Append(Format).Append('|').Append(string.Join(",", LibraryShape.MediaTypes)).Append('|').Append(string.Join(",", LibraryShape.Scraped)).Append('|');
            for (int i = 0; i < LibraryShape.KeptScraped; i++) sb.Append(LibraryShape.Kept[i]).Append(',');
            return sb.ToString();
        }

        // A name for a copy, from what it is a copy of: a 64-bit hash of it
        internal static string CacheName(string of)
        {
            ulong h = 14695981039346656037;
            foreach (char ch in of.ToLowerInvariant())
            {
                h ^= ch;
                h *= 1099511628211;
            }
            return h.ToString("x16");
        }

        static string CacheFile(SystemCatalog c)
        {
            if (string.IsNullOrEmpty(CacheDir)) return null;
            return Path.Combine(CacheDir, "archive-" + CacheName(RootKey(c.Root)), c.System + ".bin");
        }

        // Makes the folder of a copy, without what a save cut short by the app leaving left there
        internal static void CacheFolder(string file)
        {
            string dir = Path.GetDirectoryName(file);
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir, Path.GetFileName(file) + ".*.tmp"))
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // Every word by its id
        internal static string[] WordsById()
        {
            var all = Words.All();
            var byId = new string[all.Length];
            foreach (var pair in all) byId[pair.Value] = pair.Key;
            return byId;
        }

        internal static void WriteNumber(BinaryWriter w, long value)
        {
            ulong v = (ulong)value;
            while (v >= 0x80)
            {
                w.Write((byte)(v | 0x80));
                v >>= 7;
            }
            w.Write((byte)v);
        }

        internal static long ReadNumber(BinaryReader r)
        {
            ulong v = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                byte b = r.ReadByte();
                v |= (ulong)(b & 0x7f) << shift;
                if (b < 0x80) return (long)v;
            }
            throw new InvalidDataException("Bad number");
        }

        // The strings of the games to save, each once: 0 stands for none
        internal sealed class StringTable
        {
            public readonly Dictionary<string, int> At = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<string> List = new List<string>();
            public int Of(string s)
            {
                if (s == null) return 0;
                int i;
                if (!At.TryGetValue(s, out i)) { List.Add(s); At[s] = i = List.Count; }
                return i;
            }
        }

        // Saves what the library keeps of a system's parts, as they are now
        static void SaveCache(SystemCatalog c)
        {
            string file = CacheFile(c);
            if (file == null) return;
            var parts = new Dictionary<string, ArchivedGame>[PartCount];
            var lengths = new long[PartCount];
            var ticks = new long[PartCount];
            lock (c.Lock)
            {
                for (int n = 0; n < PartCount; n++)
                {
                    var p = c.Parts[n];
                    if (p == null || p.Games == null || p.Length < 0) continue;
                    parts[n] = p.Games;
                    lengths[n] = p.Length;
                    ticks[n] = p.Ticks;
                }
                c.CacheStale = false;
            }
            lock (cacheWriting)
            {
                var strings = new StringTable();
                var words = new StringTable();
                var wordOf = WordsById();
                // Once to find the strings, then again to write them
                WriteParts(new BinaryWriter(Stream.Null), parts, lengths, ticks, strings, words, wordOf);
                CacheFolder(file);
                string temp = file + "." + Pid + ".tmp";
                using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var w = new BinaryWriter(f, new UTF8Encoding(false));
                    w.Write(CacheMagic);
                    w.Write(CacheVersion);
                    w.Write(Signature());
                    WriteNumber(w, words.List.Count);
                    foreach (string s in words.List) w.Write(s);
                    WriteNumber(w, strings.List.Count);
                    foreach (string s in strings.List) w.Write(s);
                    WriteParts(w, parts, lengths, ticks, strings, words, wordOf);
                    w.Flush();
                }
                Files.Rename(temp, file);
            }
        }

        static void WriteParts(BinaryWriter w, Dictionary<string, ArchivedGame>[] parts, long[] lengths, long[] ticks, StringTable strings, StringTable words, string[] wordOf)
        {
            for (int n = 0; n < PartCount; n++)
            {
                if (parts[n] == null) { w.Write((byte)0); continue; }
                w.Write((byte)1);
                WriteNumber(w, lengths[n]);
                WriteNumber(w, ticks[n]);
                WriteNumber(w, parts[n].Count);
                foreach (var pair in parts[n])
                {
                    var a = pair.Value;
                    WriteNumber(w, strings.Of(pair.Key));
                    WriteNumber(w, strings.Of(a.Archived));
                    WriteNumber(w, strings.Of(a.Updated));
                    WriteNumber(w, strings.Of(a.Device));
                    w.Write((byte)(a.IsDir ? 1 : 0));
                    WriteNumber(w, a.Size);
                    WriteNumber(w, a.Packed);
                    w.Write(a.Hash);
                    var files = a.Files ?? new LibFile[0];
                    WriteNumber(w, files.Length);
                    foreach (var f in files)
                    {
                        WriteNumber(w, strings.Of(f.Rel));
                        WriteNumber(w, strings.Of(f.Crc));
                        WriteNumber(w, f.Size);
                        WriteNumber(w, f.Mtime);
                    }
                    var media = a.Media ?? new LibMedia[0];
                    WriteNumber(w, media.Length);
                    foreach (var m in media)
                    {
                        WriteNumber(w, m.Type);
                        WriteNumber(w, strings.Of(m.Ext));
                        WriteNumber(w, strings.Of(m.File));
                        WriteNumber(w, m.Size);
                        WriteNumber(w, m.Mtime);
                        w.Write((byte)(m.Aside ? 1 : 0));
                    }
                    var values = a.Values ?? new string[0];
                    WriteNumber(w, values.Length);
                    foreach (string v in values) WriteNumber(w, strings.Of(v));
                    if (a.Desc == null) WriteNumber(w, 0);
                    else
                    {
                        WriteNumber(w, a.Desc.Length + 1);
                        foreach (int id in a.Desc) WriteNumber(w, words.Of(id >= 0 && id < wordOf.Length ? wordOf[id] ?? "" : ""));
                    }
                }
            }
        }

        // The parts saved of a system, with their size and date: null for those not saved,
        // all of them null without a copy that can be used
        static Dictionary<string, ArchivedGame>[] ReadCache(SystemCatalog c, long[] lengths, long[] ticks)
        {
            var parts = new Dictionary<string, ArchivedGame>[PartCount];
            string file = CacheFile(c);
            if (file == null || !File.Exists(file)) return parts;
            try
            {
                using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    var r = new BinaryReader(f, new UTF8Encoding(false));
                    if (r.ReadInt32() != CacheMagic || r.ReadInt32() != CacheVersion || r.ReadString() != Signature()) return parts;
                    var wordIds = new int[ReadNumber(r)];
                    for (int i = 0; i < wordIds.Length; i++) wordIds[i] = Words.Id(r.ReadString());
                    var strings = new string[ReadNumber(r) + 1];
                    for (int i = 1; i < strings.Length; i++) strings[i] = r.ReadString();
                    // Short values many games share go in the library's pool, once each
                    var pooled = new bool[strings.Length];
                    Func<long, string> shared = i =>
                    {
                        if (!pooled[i]) { strings[i] = Library.Shared(strings[i]); pooled[i] = true; }
                        return strings[i];
                    };
                    for (int n = 0; n < PartCount; n++)
                    {
                        if (r.ReadByte() == 0) continue;
                        lengths[n] = ReadNumber(r);
                        ticks[n] = ReadNumber(r);
                        int count = (int)ReadNumber(r);
                        var games = new Dictionary<string, ArchivedGame>(count, StringComparer.Ordinal);
                        for (int k = 0; k < count; k++)
                        {
                            string rel = strings[ReadNumber(r)];
                            var a = new ArchivedGame { Rel = rel, Archived = shared(ReadNumber(r)), Updated = shared(ReadNumber(r)), Device = shared(ReadNumber(r)) };
                            a.IsDir = r.ReadByte() != 0;
                            a.Size = ReadNumber(r);
                            a.Packed = ReadNumber(r);
                            a.Hash = r.ReadUInt64();
                            a.Files = new LibFile[ReadNumber(r)];
                            for (int i = 0; i < a.Files.Length; i++)
                                a.Files[i] = new LibFile { Rel = strings[ReadNumber(r)], Crc = strings[ReadNumber(r)], Size = ReadNumber(r), Mtime = ReadNumber(r) };
                            a.Media = new LibMedia[ReadNumber(r)];
                            for (int i = 0; i < a.Media.Length; i++)
                                a.Media[i] = new LibMedia { Type = (int)ReadNumber(r), Ext = shared(ReadNumber(r)), File = strings[ReadNumber(r)], Size = ReadNumber(r), Mtime = ReadNumber(r), Aside = r.ReadByte() != 0 };
                            a.Values = new string[ReadNumber(r)];
                            for (int i = 0; i < a.Values.Length; i++) a.Values[i] = shared(ReadNumber(r)) ?? "";
                            long desc = ReadNumber(r);
                            if (desc > 0)
                            {
                                a.Desc = new int[desc - 1];
                                for (int i = 0; i < a.Desc.Length; i++) a.Desc[i] = wordIds[ReadNumber(r) - 1];
                            }
                            if (rel != null) games[rel] = a;
                        }
                        parts[n] = games;
                    }
                }
                return parts;
            }
            catch (Exception e)
            {
                Trace.WriteLine("Catalogue cache of " + c.System + ": " + e.Message);
                return new Dictionary<string, ArchivedGame>[PartCount];
            }
        }

        // Saves the copy of each system whose parts this app wrote since: called as it leaves
        public static void SaveCaches()
        {
            foreach (var c in Known(null))
            {
                bool stale;
                lock (c.Lock) stale = c.CacheStale;
                if (!stale) continue;
                try { SaveCache(c); }
                catch (Exception e) { Trace.WriteLine("Catalogue cache of " + c.System + ": " + e.Message); }
            }
        }

        // Every game of a system as the library keeps it, by path, with the changes still to
        // write. Only the parts that changed since they were read are read again, several
        // at a time: on a network share most of the time goes in waiting for it. The first
        // time, the parts come from the copy on this computer when they haven't changed since
        // it was saved, and the copy is saved again when some had.
        public static Dictionary<string, ArchivedGame> Games(string root, string system)
        {
            var c = Of(root, system);
            var listed = new FileInfo[PartCount];
            var stale = new List<int>();
            bool tried;
            lock (c.Lock) tried = c.CacheTried;
            Dictionary<string, ArchivedGame>[] cached = null;
            long[] cachedLengths = new long[PartCount], cachedTicks = new long[PartCount];
            if (!tried) cached = ReadCache(c, cachedLengths, cachedTicks);
            lock (c.Lock)
            {
                if (cached != null && !c.CacheTried)
                {
                    c.CacheTried = true;
                    for (int n = 0; n < PartCount; n++)
                    {
                        var p = c.At(n);
                        if (cached[n] == null || p.Games != null) continue;
                        p.Games = cached[n];
                        p.Length = cachedLengths[n];
                        p.Ticks = cachedTicks[n];
                    }
                }
                Migrate(c, true);
                var dir = new DirectoryInfo(PartsDir(c));
                if (dir.Exists)
                {
                    foreach (var f in dir.GetFiles("*.json"))
                    {
                        int n;
                        if (f.Name.Length == 7 && int.TryParse(f.Name.Substring(0, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out n)) listed[n] = f;
                    }
                }
                for (int n = 0; n < PartCount; n++)
                {
                    var p = c.At(n);
                    if (listed[n] == null) { if (!Fresh(p, null)) Keep(p, new Dictionary<string, ArchivedGame>(StringComparer.Ordinal), null); }
                    else if (!Fresh(p, listed[n])) stale.Add(n);
                }
            }
            var read = new Dictionary<string, ArchivedGame>[PartCount];
            try
            {
                Parallel.ForEach(stale, new ParallelOptions { MaxDegreeOfParallelism = 6 }, n => { read[n] = Compact(ReadPart(listed[n].FullName)); });
            }
            catch (AggregateException e)
            {
                throw e.Flatten().InnerExceptions[0];
            }
            lock (c.Lock)
            {
                foreach (int n in stale) Keep(c.At(n), read[n], listed[n]);
                if (stale.Count > 0) c.CacheStale = true;
                // Saved again at once the first time, so that the next start finds it
                if (stale.Count > 0 && !tried) Task.Run(() => { try { SaveCache(c); } catch (Exception e) { Trace.WriteLine("Catalogue cache of " + c.System + ": " + e.Message); } });
                int count = 0;
                foreach (var p in c.Parts) count += p.Games.Count + p.Pending.Count;
                var all = new Dictionary<string, ArchivedGame>(count, StringComparer.Ordinal);
                foreach (var p in c.Parts)
                {
                    foreach (var g in p.Games) all[g.Key] = g.Value;
                    foreach (var g in p.Pending)
                    {
                        if (g.Value == Removed) all.Remove(g.Key);
                        else all[g.Key] = ArchivedGame.From(g.Key, g.Value);
                    }
                }
                return all;
            }
        }

        // Sets a game's entry, and the system's full name when given; written by Flush
        public static void Put(string root, string system, string rel, object entry, string fullName)
        {
            var c = Of(root, system);
            lock (c.Lock)
            {
                Migrate(c, false);
                c.At(PartOf(rel)).Pending[rel] = LibraryShape.Unwrap(entry);
                if (!string.IsNullOrEmpty(fullName) && fullName != c.FullName) c.PendingFullName = fullName;
            }
        }

        // Takes a game out; written by Flush
        public static void Delete(string root, string system, string rel)
        {
            var c = Of(root, system);
            lock (c.Lock)
            {
                Migrate(c, false);
                c.At(PartOf(rel)).Pending[rel] = Removed;
            }
        }

        // Writes the changes waiting, of an archive or of every archive (root null or ""): each
        // part they touch is read as it is on the disk now, whoever wrote it last, and written
        // with them on top. A system left without games loses its catalog. Returns how many
        // parts were written.
        public static int Flush(string root)
        {
            int written = 0;
            foreach (var c in Known(root))
            {
                lock (c.Lock)
                {
                    bool changed = false;
                    for (int n = 0; n < PartCount; n++)
                    {
                        var p = c.Parts[n];
                        if (p == null || p.Pending.Count == 0) continue;
                        var before = new FileInfo(PartFile(c, n));
                        var games = ReadPart(PartFile(c, n));
                        foreach (var g in p.Pending)
                        {
                            if (g.Value == Removed) games.Remove(g.Key);
                            else games[g.Key] = g.Value;
                        }
                        // Nobody else changed the part since it was kept: only the changes are
                        // made compact again
                        Dictionary<string, ArchivedGame> compact = null;
                        var after = new FileInfo(PartFile(c, n));
                        if (Fresh(p, before) && before.Exists == after.Exists && (!after.Exists || (after.Length == before.Length && after.LastWriteTimeUtc == before.LastWriteTimeUtc)))
                        {
                            compact = new Dictionary<string, ArchivedGame>(p.Games, StringComparer.Ordinal);
                            foreach (var g in p.Pending)
                            {
                                var a = g.Value == Removed ? null : ArchivedGame.From(g.Key, g.Value);
                                if (a == null) compact.Remove(g.Key);
                                else compact[g.Key] = a;
                            }
                        }
                        WritePart(c, n, games, compact);
                        p.Pending.Clear();
                        written++;
                        changed = true;
                    }
                    if (c.PendingFullName != null)
                    {
                        WriteSystem(c, c.PendingFullName);
                        c.PendingFullName = null;
                    }
                    if (!changed) continue;
                    c.Changed = true;
                    string dir = PartsDir(c);
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.json").Length == 0)
                    {
                        // Its gamelist and name go with it; its folder goes once its media are gone
                        c.Changed = false;
                        Files.Remove(dir);
                        Files.Remove(SystemFile(c));
                        Files.Remove(Path.Combine(MetaDir(c.Root, c.System), "gamelist.xml"));
                        c.FullName = null;
                        c.FullNameRead = true;
                    }
                }
            }
            return written;
        }

        // The gamelist.xml of each system whose catalog was written, written again from it,
        // as the app leaves: for a large system it takes a while. Its parts are read a few at
        // a time and written one after the other, each with its games in the order of their
        // paths. fields are the details a gamelist holds, in its order.
        public static void WriteGamelists(IList fields)
        {
            var names = new List<string>();
            foreach (object f in fields) names.Add(f.ToString());
            var order = StringComparer.Create(CultureInfo.CurrentCulture, false);
            foreach (var c in Known(null))
            {
                lock (c.Lock)
                {
                    if (!c.Changed) continue;
                    c.Changed = false;
                }
                try { WriteGamelist(c, names, order); }
                catch (Exception e) { Trace.WriteLine("Catalogue gamelist of " + c.System + ": " + e.Message); }
            }
        }

        static string GamelistPart(Dictionary<string, object> games, List<string> fields, StringComparer order)
        {
            var sb = new StringBuilder();
            var rels = new List<string>(games.Keys);
            rels.Sort(order);
            foreach (string rel in rels)
            {
                sb.Append("\t<game>\n\t\t<path>./").Append(Gamelist.Escape(rel)).Append("</path>\n");
                var entry = LibraryShape.Unwrap(games[rel]) as IDictionary;
                var values = entry != null && entry.Contains("fields") ? LibraryShape.Unwrap(entry["fields"]) as IDictionary : null;
                if (values != null)
                {
                    foreach (string f in fields)
                    {
                        string s = LibraryShape.Str(values, f) ?? "";
                        if (s.Length > 0) sb.Append("\t\t<").Append(f).Append('>').Append(Gamelist.Escape(s)).Append("</").Append(f).Append(">\n");
                    }
                }
                sb.Append("\t</game>\n");
            }
            return sb.ToString();
        }

        static void WriteGamelist(SystemCatalog c, List<string> fields, StringComparer order)
        {
            string file = Path.Combine(MetaDir(c.Root, c.System), "gamelist.xml");
            if (!Directory.Exists(PartsDir(c)))
            {
                Files.Remove(file);
                return;
            }
            string temp = file + "." + Pid + ".gamelist.tmp";
            const int Batch = 16;
            using (var w = new StreamWriter(temp, false, new UTF8Encoding(false), 1 << 20))
            {
                w.Write("<?xml version=\"1.0\"?>\n<gameList>\n");
                for (int first = 0; first < PartCount; first += Batch)
                {
                    var text = new string[Batch];
                    Parallel.For(0, Batch, k => { text[k] = GamelistPart(ReadPart(PartFile(c, first + k)), fields, order); });
                    foreach (string t in text) w.Write(t);
                }
                w.Write("</gameList>\n");
            }
            Files.Rename(temp, file);
        }
    }

    // ======== Catalogue's library ========
    // Every game of ES-DE and of the archive, side by side, read here in compact records:
    // a system can hold hundreds of thousands of games.

    // A file of a game, by its path relative to the folder the game is in
    public struct LibFile
    {
        public string Rel, Crc;
        public long Size, Mtime;
    }

    // A media file of a game: its type (an index in the library's media types), its
    // extension, size and date
    public struct LibMedia
    {
        public int Type;
        public string Ext, File;
        public long Size, Mtime;
        public bool Aside;

        // The size of media files, of the types the library knows
        public static long Total(LibMedia[] media)
        {
            long n = 0;
            if (media != null) foreach (var m in media) if (m.Type >= 0) n += m.Size;
            return n;
        }

        // Adds the sizes of media files to sizes by type, indexes in the library's media types
        public static void AddTo(long[] byType, LibMedia[] media)
        {
            if (media != null) foreach (var m in media) if (m.Type >= 0 && m.Type < byType.Length) byType[m.Type] += m.Size;
        }

        public static long[] NoSizes() { return new long[LibraryShape.MediaTypes.Length]; }

        // Sizes by type as { type: size }, without the types that have none
        public static Hashtable ByType(long[] byType)
        {
            var d = new Hashtable();
            for (int t = 0; t < byType.Length; t++) if (byType[t] > 0) d[LibraryShape.MediaTypes[t]] = byType[t];
            return d;
        }
    }

    // A game in ES-DE: its path in the system's folder, and for a game of one file its size
    // and date; Files lists the files of a folder or of a game that needs others, by their
    // paths from the game's folder, or from the system's when one lies outside that (Wide)
    public sealed class LocalGame
    {
        public string Rel;
        public bool IsDir, Wide;
        public long Size, Mtime;
        public LibFile[] Files;
        public LibMedia[] Media;
    }

    // Folder listings straight from Windows (FindFirstFileEx, with its large fetches and
    // without the short names): each entry's name, size and date, without an object per
    // file as DirectoryInfo makes
    public static class FastDir
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FindData
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string ShortName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string name, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr find, out FindData data);
        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr find);

        public struct Entry
        {
            public string Name;
            public bool IsDir;
            public long Size, Ticks;
        }

        const int Basic = 1, LargeFetch = 2;
        const uint DirectoryFlag = 0x10;

        // The entries of a folder, but . and ..; none when it can't be read. A name with its
        // end left open ("Game.*") takes no time, however large the folder is.
        public static List<Entry> List(string dir, string pattern = "*")
        {
            var list = new List<Entry>();
            FindData d;
            IntPtr h = FindFirstFileExW(Path.Combine(dir, pattern), Basic, out d, 0, IntPtr.Zero, LargeFetch);
            if (h == new IntPtr(-1)) return list;
            try
            {
                do
                {
                    if (d.Name == "." || d.Name == "..") continue;
                    long fileTime = (long)d.WriteHigh << 32 | d.WriteLow;
                    list.Add(new Entry
                    {
                        Name = d.Name, IsDir = (d.Attributes & DirectoryFlag) != 0, Size = (long)d.SizeHigh << 32 | d.SizeLow,
                        Ticks = DateTime.FromFileTimeUtc(fileTime).Ticks,
                    });
                }
                while (FindNextFileW(h, out d));
            }
            finally
            {
                FindClose(h);
            }
            return list;
        }

        // A date as Files.MtimeMs gives it, rounded to the millisecond, from UTC ticks
        public static long Ms(long ticks)
        {
            ticks -= 621355968000000000L;
            long seconds = ticks / 10000000L, rest = ticks % 10000000L;
            if (rest < 0) { seconds--; rest += 10000000L; }
            return (long)Math.Floor(seconds * 1e3 + (rest * 100) / 1e6 + 0.5);
        }
    }

    public static class LocalScan
    {
        // A date as Files.MtimeMs gives it, rounded to the millisecond
        public static long Ms(DateTime utc) { return FastDir.Ms(utc.Ticks); }

        static LibFile FileOf(string dir, FileInfo f)
        {
            return new LibFile { Rel = Paths.Relative(dir, f.FullName), Size = f.Length, Mtime = Ms(f.LastWriteTimeUtc) };
        }

        // Next links the files named alike, in others
        struct Found
        {
            public string Rel;
            public bool IsDir;
            public long Size, Ticks;
            public int Next;
        }

        // A file of a game, with its size and date when the folder listings gave them
        struct Part
        {
            public string Abs;
            public bool Listed;
            public long Size, Ticks;
        }

        static string Full(string systemDir, string rel) { return Path.Combine(systemDir, rel.Replace('/', '\\')); }

        // ---- The copy kept on this computer ----
        // Opening its descriptors is what takes long in a system of disc images. So what the
        // descriptors of a system with many of them name is saved with the copies of the
        // catalog (see Catalog.CacheDir), in descriptors/<hash of the system's folder>.bin,
        // with the size and date of each file: a descriptor is read again once it, or a file
        // it leads to, is gone or has another size or date, all of which the folder listings
        // tell. Only what follows from the descriptors alone is kept (see LocalGames.Reading):
        // the rest is read every time, so a file added later is found. A copy that can't be
        // read is left aside and made again.
        const int CacheMagic = 0x44444154, CacheVersion = 1;
        public static int CacheFrom = 1000;

        sealed class Known
        {
            public long Size, Ticks;
            public string[] Files;
            public long[] Sizes, Dates;
        }

        static string CacheFile(string systemDir)
        {
            if (string.IsNullOrEmpty(Catalog.CacheDir)) return null;
            return Path.Combine(Catalog.CacheDir, "descriptors", Catalog.CacheName(Path.GetFullPath(systemDir)) + ".bin");
        }

        static Dictionary<string, Known> LoadKnown(string cache)
        {
            var known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
            if (cache == null || !File.Exists(cache)) return known;
            try
            {
                using (var f = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                using (var r = new BinaryReader(f, new UTF8Encoding(false)))
                {
                    if (r.ReadInt32() != CacheMagic || r.ReadInt32() != CacheVersion) return known;
                    long count = Catalog.ReadNumber(r);
                    for (long n = 0; n < count; n++)
                    {
                        string rel = r.ReadString();
                        var k = new Known { Size = Catalog.ReadNumber(r), Ticks = Catalog.ReadNumber(r) };
                        int files = (int)Catalog.ReadNumber(r);
                        k.Files = new string[files];
                        k.Sizes = new long[files];
                        k.Dates = new long[files];
                        for (int i = 0; i < files; i++)
                        {
                            k.Files[i] = r.ReadString();
                            k.Sizes[i] = Catalog.ReadNumber(r);
                            k.Dates[i] = Catalog.ReadNumber(r);
                        }
                        known[rel] = k;
                    }
                }
            }
            catch (Exception)
            {
                known.Clear();
            }
            return known;
        }

        static void SaveKnown(string cache, List<Found> descriptors, Known[] kept)
        {
            try
            {
                Catalog.CacheFolder(cache);
                string temp = cache + "." + Process.GetCurrentProcess().Id + ".tmp";
                using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var w = new BinaryWriter(f, new UTF8Encoding(false));
                    w.Write(CacheMagic);
                    w.Write(CacheVersion);
                    long count = 0;
                    foreach (var k in kept) if (k != null) count++;
                    Catalog.WriteNumber(w, count);
                    for (int n = 0; n < kept.Length; n++)
                    {
                        var k = kept[n];
                        if (k == null) continue;
                        w.Write(descriptors[n].Rel);
                        Catalog.WriteNumber(w, k.Size);
                        Catalog.WriteNumber(w, k.Ticks);
                        Catalog.WriteNumber(w, k.Files.Length);
                        for (int i = 0; i < k.Files.Length; i++)
                        {
                            w.Write(k.Files[i]);
                            Catalog.WriteNumber(w, k.Sizes[i]);
                            Catalog.WriteNumber(w, k.Dates[i]);
                        }
                    }
                    w.Flush();
                }
                Files.Rename(temp, cache);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Whether a descriptor and the files it leads to are as the copy has them
        static bool Unchanged(Known k, Found descriptor, Dictionary<string, Found> listed)
        {
            if (k.Size != descriptor.Size || k.Ticks != descriptor.Ticks) return false;
            for (int i = 0; i < k.Files.Length; i++)
            {
                Found f;
                if (!listed.TryGetValue(k.Files[i], out f) || f.Size != k.Sizes[i] || f.Ticks != k.Dates[i]) return false;
            }
            return true;
        }

        // What the copy keeps of a descriptor just read, or null when a file it leads to is in
        // no listing, such as a save, and couldn't be checked next time
        static Known Describe(Found descriptor, List<string> files, string systemDir, Dictionary<string, Found> listed)
        {
            var k = new Known { Size = descriptor.Size, Ticks = descriptor.Ticks, Files = new string[files.Count], Sizes = new long[files.Count], Dates = new long[files.Count] };
            for (int i = 0; i < files.Count; i++)
            {
                string rel = Paths.Relative(systemDir, files[i]);
                Found f;
                if (!listed.TryGetValue(rel, out f)) return null;
                k.Files[i] = rel;
                k.Sizes[i] = f.Size;
                k.Dates[i] = f.Ticks;
            }
            return k;
        }

        static int IndexOf(List<Part> parts, string file)
        {
            for (int i = 0; i < parts.Count; i++) if (string.Equals(parts[i].Abs, file, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        static bool Names(List<string> named, string file)
        {
            foreach (string n in named) if (string.Equals(n, file, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Notes that a game has a file, which tells the files more games have
        static void Claim(Dictionary<string, List<string>> claims, string file, string rel)
        {
            lock (claims)
            {
                List<string> users;
                if (!claims.TryGetValue(file, out users) || users == null) claims[file] = users = new List<string>();
                if (!users.Contains(rel)) users.Add(rel);
            }
        }

        // The games under a system's folder, as LocalGames.ScanOne finds each, with the sizes
        // and dates the folder listings give: no file is opened, but the descriptors that
        // name the files a game needs (cue sheets, playlists...). shared gets the files more
        // than one game has, with those games.
        public static List<LocalGame> Scan(string systemDir, ICollection<string> extensions, IList tempPrefixes, string system, out Dictionary<string, List<string>> shared)
        {
            var entries = new List<Found>();
            // The files that are no games, those named alike linked from heads, by their path
            // without extension; and the folders that are no games
            var others = new List<Found>();
            var heads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<string>();
            pending.Push("");
            var prefixes = new List<string>();
            foreach (object p in tempPrefixes) prefixes.Add(p.ToString());
            while (pending.Count > 0)
            {
                string relDir = pending.Pop();
                var list = FastDir.List(relDir.Length == 0 ? systemDir : Full(systemDir, relDir));
                // ES-DE skips folders with a noload.txt
                if (relDir.Length > 0 && list.Exists(e => string.Equals(e.Name, "noload.txt", StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var e in list)
                {
                    bool temp = false;
                    foreach (string p in prefixes) if (e.Name.StartsWith(p, StringComparison.Ordinal)) temp = true;
                    if (temp) continue;
                    string ext = Paths.Ext(e.Name).ToLowerInvariant();
                    string rel = relDir.Length == 0 ? e.Name : relDir + "/" + e.Name;
                    if (e.IsDir)
                    {
                        if (extensions.Contains(ext)) entries.Add(new Found { Rel = rel, IsDir = true });
                        else
                        {
                            pending.Push(rel);
                            folders.Add(rel);
                        }
                    }
                    else if (extensions.Contains(ext)) entries.Add(new Found { Rel = rel, Size = e.Size, Ticks = e.Ticks });
                    else if (LocalGames.GoesWithGame(ext, extensions))
                    {
                        string key = Paths.BaseOf(rel);
                        int next;
                        others.Add(new Found { Rel = rel, Size = e.Size, Ticks = e.Ticks, Next = heads.TryGetValue(key, out next) ? next : -1 });
                        heads[key] = others.Count - 1;
                    }
                }
            }

            // A file another game refers to belongs to that game. Opening its descriptors is
            // what takes long in a system of disc images: they are read side by side and, when
            // there are many, only those that changed since the copy on this computer. What the
            // listings say of each file also saves asking a disk for its size and date.
            var descriptors = entries.FindAll(e => !e.IsDir && LocalGames.Descriptors.Contains(Path.GetExtension(e.Rel)));
            var listed = new Dictionary<string, Found>(StringComparer.OrdinalIgnoreCase);
            var known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
            string cache = null;
            if (descriptors.Count >= CacheFrom)
            {
                foreach (var list in new[] { entries, others }) foreach (var e in list) if (!e.IsDir) listed[e.Rel] = e;
                cache = CacheFile(systemDir);
                known = LoadKnown(cache);
            }
            var read = new List<string>[descriptors.Count];
            var kept = new Known[read.Length];
            int fresh = 0;
            Parallel.For(0, read.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, i =>
            {
                var d = descriptors[i];
                Known k;
                if (known.TryGetValue(d.Rel, out k) && Unchanged(k, d, listed))
                {
                    var files = new List<string>(k.Files.Length);
                    foreach (string f in k.Files) files.Add(Full(systemDir, f));
                    read[i] = files;
                    kept[i] = k;
                    return;
                }
                var reading = new LocalGames.Reading();
                read[i] = LocalGames.NeedsOf(Full(systemDir, d.Rel), systemDir, reading);
                if (cache == null || !reading.Stable) return;
                kept[i] = Describe(d, read[i], systemDir, listed);
                if (kept[i] != null) Interlocked.Increment(ref fresh);
            });
            if (cache != null && (fresh > 0 || known.Count != Array.FindAll(kept, k => k != null).Length)) SaveKnown(cache, descriptors, kept);

            var needs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var claims = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < read.Length; i++)
            {
                if (read[i].Count == 0) continue;
                needs[Full(systemDir, descriptors[i].Rel)] = read[i];
                foreach (string s in read[i]) if (!needed.Add(s)) claims[s] = null;
            }

            // The descriptors that are games, as a file named like one may be named by it
            var named = new List<string>();
            foreach (string d in LocalGames.Descriptors) if (extensions.Contains(d)) named.Add(d);
            bool programs = LocalGames.IsProgramSystem(system);
            bool alike = heads.Count > 0 || folders.Count > 0 || programs;
            var around = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var locale = CultureInfo.CurrentCulture.CompareInfo;
            var made = new LocalGame[entries.Count];
            var theirNeeds = new List<string>[entries.Count];

            // The games side by side, as each is made on its own
            Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, n =>
            {
                var e = entries[n];
                string full = needs.Count > 0 || e.IsDir || alike ? Full(systemDir, e.Rel) : null;
                if (needed.Count > 0 && needed.Contains(full)) return;
                var game = new LocalGame { Rel = e.Rel, IsDir = e.IsDir };
                made[n] = game;
                if (e.IsDir)
                {
                    string parent = Path.GetDirectoryName(full);
                    var inside = new List<LibFile>();
                    foreach (var f in new DirectoryInfo(full).GetFiles("*", SearchOption.AllDirectories)) inside.Add(FileOf(parent, f));
                    inside.Sort((x, y) => locale.Compare(x.Rel, y.Rel));
                    foreach (var f in inside) game.Size += f.Size;
                    game.Files = inside.ToArray();
                    return;
                }
                if (full == null)
                {
                    game.Size = e.Size;
                    game.Mtime = FastDir.Ms(e.Ticks);
                    return;
                }

                var parts = new List<Part>();
                List<string> own;
                if (needs.TryGetValue(full, out own))
                {
                    foreach (string x in own) parts.Add(new Part { Abs = x });
                    theirNeeds[n] = own;
                }
                if (LocalGames.IsProgram(system, e.Rel))
                {
                    string folder = Path.GetDirectoryName(full);
                    string[] all;
                    lock (around) { if (!around.TryGetValue(folder, out all)) around[folder] = all = LocalGames.AllIn(folder); }
                    foreach (string x in all)
                    {
                        if (string.Equals(x, full, StringComparison.OrdinalIgnoreCase)) continue;
                        if (IndexOf(parts, x) < 0) parts.Add(new Part { Abs = x });
                        Claim(claims, x, e.Rel);
                    }
                }
                else
                {
                    string key = Paths.BaseOf(e.Rel);
                    var looked = new List<string> { key };
                    if (own != null) foreach (string x in own) if (LocalGames.Descriptors.Contains(Paths.Ext(x))) looked.Add(Paths.BaseOf(Paths.Relative(systemDir, x)));
                    foreach (string name in looked)
                    {
                        int at;
                        if (!heads.TryGetValue(name, out at)) continue;
                        for (; at >= 0; at = others[at].Next)
                        {
                            var o = others[at];
                            string file = Full(systemDir, o.Rel);
                            if (IndexOf(parts, file) >= 0) continue;
                            bool theirs = false;
                            foreach (string d in named)
                            {
                                List<string> their;
                                if (needs.TryGetValue(Full(systemDir, name + d), out their) && Names(their, file)) theirs = true;
                            }
                            if (theirs) continue;
                            parts.Add(new Part { Abs = file, Listed = true, Size = o.Size, Ticks = o.Ticks });
                            Claim(claims, file, e.Rel);
                        }
                    }
                    if (folders.Contains(key))
                    {
                        var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { full };
                        foreach (var p in parts) mine.Add(p.Abs);
                        var inside = LocalGames.NamedFolder(Full(systemDir, key), extensions, mine);
                        if (inside != null)
                        {
                            foreach (string x in inside)
                            {
                                if (IndexOf(parts, x) < 0) parts.Add(new Part { Abs = x });
                                Claim(claims, x, e.Rel);
                            }
                        }
                    }
                }
                if (programs) Claim(claims, full, e.Rel);
                if (parts.Count == 0)
                {
                    game.Size = e.Size;
                    game.Mtime = FastDir.Ms(e.Ticks);
                    return;
                }

                // Paths start at the game's folder, or at the system's when a file lies outside that
                string dir = Path.GetDirectoryName(full);
                foreach (var p in parts) if (!LocalGames.Inside(dir, p.Abs)) game.Wide = true;
                if (game.Wide) dir = systemDir;
                var files = new List<LibFile>(parts.Count + 1) { new LibFile { Rel = Paths.Relative(dir, full), Size = e.Size, Mtime = FastDir.Ms(e.Ticks) } };
                foreach (var p in parts)
                {
                    Found f;
                    if (p.Listed) files.Add(new LibFile { Rel = Paths.Relative(dir, p.Abs), Size = p.Size, Mtime = FastDir.Ms(p.Ticks) });
                    else if (listed.Count > 0 && listed.TryGetValue(Paths.Relative(systemDir, p.Abs), out f)) files.Add(new LibFile { Rel = Paths.Relative(dir, p.Abs), Size = f.Size, Mtime = FastDir.Ms(f.Ticks) });
                    else files.Add(FileOf(dir, new FileInfo(p.Abs)));
                }
                files.Sort((x, y) => locale.Compare(x.Rel, y.Rel));
                foreach (var f in files) game.Size += f.Size;
                game.Files = files.ToArray();
            });

            var games = new List<LocalGame>(entries.Count);
            foreach (var g in made) if (g != null) games.Add(g);

            // Only a file some game holds by name or place, or that two descriptors name, can
            // be shared: the rest of the named files are not looked at again
            shared = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (claims.Count == 0) return games;
            for (int n = 0; n < theirNeeds.Length; n++)
            {
                if (theirNeeds[n] == null) continue;
                foreach (string x in theirNeeds[n]) if (claims.ContainsKey(x)) Claim(claims, x, entries[n].Rel);
            }
            foreach (var c in claims) if (c.Value != null && c.Value.Count > 1) shared[c.Key] = c.Value;
            return games;
        }
    }

    // A game as a gamelist has it: the fields kept, in the order asked for ("" when it has
    // none), a hash of the scraped fields to tell whether they are the archive's, and where
    // its block is in the file
    public sealed class ListedGame
    {
        public string[] Values;
        public ulong Hash;
        public bool Any;
        public int[] Desc;
        public long At;
        public int Length;
    }

    public static class FieldHash
    {
        // The FNV-1a hash of fields in their order, a field ending with a character no text has
        public static ulong Of(IList<string> values)
        {
            ulong h = 14695981039346656037UL;
            foreach (string v in values)
            {
                if (v != null) foreach (char c in v) { h ^= c; h *= 1099511628211UL; }
                h ^= 0xFFFF;
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    // The words of the collections' search, folded as catalogue/ui/collections.js folds
    // them: in lower case, without accents, split on whatever is neither a letter nor a
    // number. Each word has an id, the same for the whole app, found by a 64-bit hash of the
    // word: the descriptions of a large library hold millions of words, looked up without
    // making a string of each (see DescIds).
    public static class Words
    {
        static readonly ConcurrentDictionary<ulong, int> ids = new ConcurrentDictionary<ulong, int>();
        static string[] words = new string[4096];
        static readonly object adding = new object();
        static int next = -1;
        const ulong Offset = 14695981039346656037, Prime = 1099511628211;

        public static int Count { get { return Thread.VolatileRead(ref next) + 1; } }

        static ulong HashOf(string w)
        {
            ulong h = Offset;
            foreach (char c in w)
            {
                h ^= c;
                h *= Prime;
            }
            return h;
        }

        public static int Id(string w) { return IdOf(HashOf(w), w, 0, w.Length, false); }

        // The id of the word with that hash, which is text's from start for length, in lower
        // case when lower: given the first time the word is seen
        static int IdOf(ulong h, string text, int start, int length, bool lower)
        {
            int id;
            if (ids.TryGetValue(h, out id)) return id;
            lock (adding)
            {
                if (ids.TryGetValue(h, out id)) return id;
                id = next + 1;
                if (id >= words.Length) Array.Resize(ref words, words.Length * 2);
                string w = start == 0 && length == text.Length ? text : text.Substring(start, length);
                words[id] = lower ? w.ToLowerInvariant() : w;
                Thread.VolatileWrite(ref next, id);
                ids[h] = id;
                return id;
            }
        }

        // Every word with its id
        public static KeyValuePair<string, int>[] All()
        {
            lock (adding)
            {
                var a = new KeyValuePair<string, int>[next + 1];
                for (int i = 0; i < a.Length; i++) a[i] = new KeyValuePair<string, int>(words[i], i);
                return a;
            }
        }

        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            bool ascii = true;
            foreach (char c in s) if (c > 127) { ascii = false; break; }
            if (ascii) return s.ToLowerInvariant();
            string d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d) if (c < '̀' || c > 'ͯ') sb.Append(c);
            return sb.ToString().ToLowerInvariant();
        }

        static bool Part(char c) { return char.IsLetter(c) || char.IsNumber(c) || char.IsSurrogate(c); }

        // The words of a text, folded, in their order
        public static List<string> Of(string s)
        {
            var found = new List<string>();
            string f = Fold(s);
            int i = 0;
            while (i < f.Length)
            {
                while (i < f.Length && !Part(f[i])) i++;
                int start = i;
                while (i < f.Length && Part(f[i])) i++;
                if (i > start) found.Add(f.Substring(start, i - start));
            }
            return found;
        }

        static bool Digits(string w)
        {
            foreach (char c in w) if (c < '0' || c > '9') return false;
            return true;
        }

        // The words of a description that a search can find, each once: a search word finds
        // a description only with three letters or more, and never as a number
        // A text of ASCII, the most of them, is read char by char: a word becomes a string only
        // the first time it is seen
        [ThreadStatic] static HashSet<int> seenWords;

        static bool AsciiPart(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); }

        public static int[] DescIds(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var seen = seenWords ?? (seenWords = new HashSet<int>());
            seen.Clear();
            bool ascii = true;
            foreach (char c in s) if (c > 127) { ascii = false; break; }
            if (ascii)
            {
                int i = 0, n = s.Length;
                while (i < n)
                {
                    while (i < n && !AsciiPart(s[i])) i++;
                    int start = i;
                    ulong h = Offset;
                    bool digits = true, upper = false;
                    for (; i < n && AsciiPart(s[i]); i++)
                    {
                        char c = s[i];
                        if (c >= 'A' && c <= 'Z') { c = (char)(c + 32); upper = true; }
                        if (c > '9') digits = false;
                        h ^= c;
                        h *= Prime;
                    }
                    if (i - start >= 3 && !digits) seen.Add(IdOf(h, s, start, i - start, upper));
                }
            }
            else foreach (string w in Of(s)) if (w.Length >= 3 && !Digits(w)) seen.Add(Id(w));
            if (seen.Count == 0) return null;
            var a = new int[seen.Count];
            seen.CopyTo(a);
            return a;
        }

        public static bool OnlyDigits(string w) { return Digits(w); }
    }

    // How a game of the library stands with the archive, and as its icon shows it
    public static class LibStatus
    {
        public const byte Local = 0, Archived = 1, Changed = 2, Details = 3, Remote = 4, Gone = 255;
        public static readonly string[] Names = { "local", "archived", "changed", "details", "remote" };
        // The icon: installed, sync, upload, download, unavailable (see stateOf in catalogue/ui/main.js)
        public const int Installed = 0, Sync = 1, Upload = 2, Download = 3, Unavailable = 4;
        public static readonly string[] States = { "installed", "sync", "upload", "download", "unavailable" };
    }

    // A game of the library: in ES-DE, in the archive or both
    public sealed class LibGame
    {
        public int Index, System;
        public string Id, SystemName, Rel, Name;
        public byte Status;
        public LocalGame Local;
        public ListedGame Listed;
        public ArchivedGame Archived;
        public byte Media;

        // A detail of the game, by its place in LibraryShape.Kept: the scraped ones from
        // ES-DE's gamelist when it has any for the game, else from the archive; the others
        // only ES-DE has
        public string Field(int k)
        {
            if (k < 0) return "";
            if (k < LibraryShape.KeptScraped)
            {
                if (Listed != null && Listed.Any) return Listed.Values[k];
                if (Archived != null) return Archived.Values[k];
                return "";
            }
            return Listed != null ? Listed.Values[k] : "";
        }

        public long SizeOf { get { return Local != null ? Local.Size : Archived != null ? Archived.Size : 0; } }

        // The size of the game's media files in ES-DE, in the archive, and where SizeOf counts
        public long LocalMediaSize { get { return Local != null ? LibMedia.Total(Local.Media) : 0; } }
        public long ArchivedMediaSize { get { return Archived != null ? LibMedia.Total(Archived.Media) : 0; } }
        public long MediaSizeOf { get { return Local != null ? LocalMediaSize : ArchivedMediaSize; } }
    }

    // A system of the library, with its games in the order of their names and how they
    // stand, counted by the icon they show
    public sealed class LibSystem
    {
        public int Id;
        public string Name, FullName, Dir, MediaDir, Gamelist;
        public bool Known;
        public List<string> Extensions = new List<string>();
        public Dictionary<string, string> Aside = new Dictionary<string, string>(StringComparer.Ordinal);
        public int[] Games = new int[0];
        public int[] Counts = new int[5];
        public long Packed, Size, UploadSize;
        // The size of the media files of the same games: in the archive, in ES-DE, not archived
        public long ArchiveMedia, LocalMedia, UploadMedia;
        // The files more than one game in ES-DE has, with those games
        public Dictionary<string, List<string>> Shared;
    }

    public static class Library
    {
        // The library as it is now: replaced as a whole when read again, changed game by game
        // after the transfers, under the gate
        sealed class Snapshot
        {
            public LibSystem[] Systems = new LibSystem[0];
            public Dictionary<string, LibSystem> SystemByName = new Dictionary<string, LibSystem>(StringComparer.Ordinal);
            public LibGame[] Games = new LibGame[0];
            public Dictionary<string, LibGame> ById = new Dictionary<string, LibGame>(StringComparer.Ordinal);
            public readonly Dictionary<string, int[]> Orders = new Dictionary<string, int[]>(StringComparer.Ordinal);
        }

        sealed class Work
        {
            public LibSystem System;
            public List<LocalGame> Local = new List<LocalGame>();
            public Dictionary<string, ListedGame> Listed = new Dictionary<string, ListedGame>(StringComparer.Ordinal);
            public Dictionary<string, LibMedia>[] Media;
            public Dictionary<string, ArchivedGame> Archived = new Dictionary<string, ArchivedGame>(StringComparer.Ordinal);
        }

        static Snapshot current = new Snapshot();
        // The games changed while the library is read again, changed again in the new one
        static readonly HashSet<string> touched = new HashSet<string>(StringComparer.Ordinal);
        static bool building;
        static readonly ReaderWriterLockSlim gate = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
        static readonly object buildLock = new object();
        static long version;
        static string root, culture;
        static int[] compared = new int[0];
        static IDictionary crcs;
        static IList prefixes = new List<string>();
        static string[][] mediaExts = new string[0][];
        // The media types whose files the page shows: covers, screenshots, titlescreens, fanart, videos
        static int[] shown = new int[0];

        // How long the last reading took, part by part
        public static string Timings = "";
        static readonly ConcurrentDictionary<string, long> taskTimes = new ConcurrentDictionary<string, long>();
        static Action Timed(string name, Action a) { return () => { var c = Stopwatch.StartNew(); a(); taskTimes[name] = c.ElapsedMilliseconds; }; }
        // The slowest reads of the last reading
        public static string SlowestReads(int count)
        {
            var all = taskTimes.ToArray();
            Array.Sort(all, (a, b) => b.Value.CompareTo(a.Value));
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(count, all.Length); i++) sb.Append(all[i].Key).Append(" ").Append(all[i].Value).Append(" ms; ");
            return sb.ToString();
        }
        public static long Version { get { return Interlocked.Read(ref version); } }
        public static int Count { get { return current.Games.Length; } }

        static object Unwrap(object v) { return LibraryShape.Unwrap(v); }

        // What the library is read with: the archive (null without one), the types of media
        // whose files tell whether a game is the archive's, the cache of CRCs (path => { size,
        // mtime, crc }), the language names are sorted in, the prefixes of the folders a
        // download leaves, and the extensions of each type of media, in ES-DE's order
        public static void Configure(string archiveRoot, IList comparedTypes, IDictionary crcCache, string language, IList tempPrefixes, IDictionary extensions)
        {
            root = string.IsNullOrEmpty(archiveRoot) ? null : archiveRoot;
            var c = new List<int>();
            foreach (object t in comparedTypes) { int i = LibraryShape.MediaType(t.ToString()); if (i >= 0) c.Add(i); }
            compared = c.ToArray();
            crcs = crcCache;
            culture = language;
            prefixes = tempPrefixes;
            mediaExts = new string[LibraryShape.MediaTypes.Length][];
            for (int i = 0; i < mediaExts.Length; i++)
            {
                var list = new List<string>();
                var e = Unwrap(extensions[LibraryShape.MediaTypes[i]]) as IEnumerable;
                if (e != null) foreach (object x in e) list.Add(Unwrap(x).ToString());
                mediaExts[i] = list.ToArray();
            }
            var s = new List<int>();
            foreach (string t in new[] { "covers", "screenshots", "titlescreens", "fanart", "videos" }) s.Add(LibraryShape.MediaType(t));
            shown = s.ToArray();
        }

        // Reading the library on a thread of its own: Configure with these settings, then
        // Build. Asked for again while it reads, it reads once more when it is done, with the
        // last settings asked for.
        static readonly object laterLock = new object();
        static object[] laterArgs;
        static bool laterRunning;
        public static string Error;

        // Whether the library is being read, or waits to be
        public static bool Busy { get { lock (laterLock) return laterRunning; } }

        public static void BuildLater(string archiveRoot, IList comparedTypes, IDictionary crcCache, string language, IList tempPrefixes, IDictionary extensions, IList systems)
        {
            lock (laterLock)
            {
                laterArgs = new object[] { archiveRoot, comparedTypes, crcCache, language, tempPrefixes, extensions, systems };
                if (laterRunning) return;
                laterRunning = true;
            }
            Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    object[] a;
                    lock (laterLock)
                    {
                        a = laterArgs;
                        laterArgs = null;
                        if (a == null)
                        {
                            laterRunning = false;
                            return;
                        }
                    }
                    try
                    {
                        Configure((string)a[0], (IList)a[1], (IDictionary)a[2], (string)a[3], (IList)a[4], (IDictionary)a[5]);
                        Build((IList)a[6]);
                        Error = null;
                    }
                    catch (Exception e)
                    {
                        Error = e.Message;
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        // Reads the library again, every system side by side: the folders of ES-DE, its
        // gamelists and media, and the archive's catalog. systems are ES-DE's: [{ name,
        // fullName, dir, extensions, gamelist, mediaDir, aside: { type: folder } }]. Returns
        // the library's version.
        public static long Build(IList systems)
        {
            lock (buildLock)
            {
                lock (touched) { building = true; touched.Clear(); }
                try { return BuildNow(systems); }
                catch
                {
                    lock (touched) { building = false; touched.Clear(); }
                    throw;
                }
            }
        }

        static long BuildNow(IList systems)
        {
            {
                var clock = Stopwatch.StartNew();
                var times = new StringBuilder();
                var list = new List<LibSystem>();
                var byName = new Dictionary<string, LibSystem>(StringComparer.Ordinal);
                foreach (object o in systems)
                {
                    var d = Unwrap(o) as IDictionary;
                    if (d == null) continue;
                    var s = new LibSystem
                    {
                        Name = LibraryShape.Str(d, "name"), FullName = LibraryShape.Str(d, "fullName"), Dir = LibraryShape.Str(d, "dir"),
                        Gamelist = LibraryShape.Str(d, "gamelist"), MediaDir = LibraryShape.Str(d, "mediaDir"), Known = true,
                    };
                    var exts = d.Contains("extensions") ? Unwrap(d["extensions"]) as IEnumerable : null;
                    if (exts != null) foreach (object x in exts) s.Extensions.Add(Unwrap(x).ToString().ToLowerInvariant());
                    var aside = d.Contains("aside") ? Unwrap(d["aside"]) as IDictionary : null;
                    if (aside != null) foreach (DictionaryEntry a in aside) s.Aside[a.Key.ToString()] = Unwrap(a.Value).ToString();
                    if (s.Name == null || byName.ContainsKey(s.Name)) continue;
                    list.Add(s);
                    byName[s.Name] = s;
                }
                var withArchive = new HashSet<string>(StringComparer.Ordinal);
                if (root != null)
                {
                    foreach (string name in Catalog.Systems(root))
                    {
                        withArchive.Add(name);
                        if (byName.ContainsKey(name)) continue;
                        var s = new LibSystem { Name = name, FullName = Catalog.FullName(root, name) ?? name, Known = false };
                        list.Add(s);
                        byName[name] = s;
                    }
                }
                var work = new Work[list.Count];
                var tasks = new List<Action>();
                for (int i = 0; i < list.Count; i++)
                {
                    var w = work[i] = new Work { System = list[i], Media = new Dictionary<string, LibMedia>[LibraryShape.MediaTypes.Length] };
                    var s = list[i];
                    if (s.Known && Directory.Exists(s.Dir))
                    {
                        var extSet = new HashSet<string>(s.Extensions, StringComparer.Ordinal);
                        tasks.Add(Timed("scan " + s.Name, () => { w.Local = LocalScan.Scan(s.Dir, extSet, prefixes, s.Name, out s.Shared); }));
                        tasks.Add(Timed("gamelist " + s.Name, () => { w.Listed = Gamelist.Compact(s.Gamelist, LibraryShape.Scraped, LibraryShape.Kept); }));
                        for (int t = 0; t < LibraryShape.MediaTypes.Length; t++)
                        {
                            int type = t;
                            tasks.Add(Timed("media " + s.Name + " " + LibraryShape.MediaTypes[t], () => { w.Media[type] = ScanMedia(s, type); }));
                        }
                    }
                    if (withArchive.Contains(s.Name)) tasks.Add(Timed("catalog " + s.Name, () => { w.Archived = Catalog.Games(root, s.Name); }));
                }
                try { Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, tasks.ToArray()); }
                catch (AggregateException e) { throw e.Flatten().InnerExceptions[0]; }
                times.Append("read ").Append(clock.ElapsedMilliseconds).Append(" ms");

                var merged = new List<LibGame>[list.Count];
                Parallel.For(0, list.Count, i => { merged[i] = Merge(work[i]); });
                var all = new List<LibGame>();
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].Id = i;
                    foreach (var g in merged[i]) g.System = i;
                    all.AddRange(merged[i]);
                }
                times.Append(", merged ").Append(clock.ElapsedMilliseconds).Append(" ms");
                var snap = Arrange(list, all);
                times.Append(", sorted ").Append(clock.ElapsedMilliseconds).Append(" ms");
                Timings = times.ToString();
                List<string> again;
                gate.EnterWriteLock();
                try
                {
                    current = snap;
                    Interlocked.Increment(ref version);
                    lock (touched)
                    {
                        building = false;
                        again = new List<string>(touched);
                        touched.Clear();
                    }
                }
                finally
                {
                    gate.ExitWriteLock();
                }
                foreach (string key in again)
                {
                    int bar = key.IndexOf('|');
                    Update(key.Substring(0, bar), key.Substring(bar + 1));
                }
                // What the reading left behind, and the library before, go back to Windows
                work = null;
                merged = null;
                all = null;
                tasks = null;
                long before = clock.ElapsedMilliseconds;
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                Timings += ", collected in " + (clock.ElapsedMilliseconds - before) + " ms";
                return Version;
            }
        }

        // 0 to count - 1 in the order of compare: parts sorted side by side, then merged two by
        // two, side by side too
        static int[] SortInParts(int count, Comparison<int> compare)
        {
            var a = new int[count];
            for (int n = 0; n < count; n++) a[n] = n;
            int parts = Math.Max(1, Math.Min(Environment.ProcessorCount, count / 20000));
            if (parts == 1)
            {
                Array.Sort(a, compare);
                return a;
            }
            var bounds = new int[parts + 1];
            for (int p = 0; p <= parts; p++) bounds[p] = (int)((long)count * p / parts);
            var comparer = Comparer<int>.Create(compare);
            Parallel.For(0, parts, p => Array.Sort(a, bounds[p], bounds[p + 1] - bounds[p], comparer));
            var runs = new List<int>(bounds);
            var b = new int[count];
            while (runs.Count > 2)
            {
                var next = new List<int> { 0 };
                var pairs = new List<int[]>();
                for (int r = 0; r + 1 < runs.Count; r += 2)
                {
                    int lo = runs[r], mid = runs[r + 1], hi = r + 2 < runs.Count ? runs[r + 2] : runs[r + 1];
                    pairs.Add(new[] { lo, mid, hi });
                    next.Add(hi);
                }
                var src = a;
                var dst = b;
                Parallel.ForEach(pairs, pr =>
                {
                    int i = pr[0], j = pr[1], k = pr[0];
                    while (i < pr[1] && j < pr[2]) dst[k++] = compare(src[i], src[j]) <= 0 ? src[i++] : src[j++];
                    while (i < pr[1]) dst[k++] = src[i++];
                    while (j < pr[2]) dst[k++] = src[j++];
                });
                a = dst;
                b = src;
                runs = next;
            }
            return a;
        }

        // The games in the order of their names, each system with its own in that order
        static Snapshot Arrange(List<LibSystem> systems, List<LibGame> all)
        {
            var names = new string[all.Count];
            var ids = new string[all.Count];
            for (int n = 0; n < all.Count; n++)
            {
                names[n] = all[n].Name;
                ids[n] = all[n].Id;
            }
            // As NameComparer.Sort orders them, in several parts at once, merged in turn
            var byName = new NameComparer(culture);
            var locale = CultureInfo.CurrentCulture.CompareInfo;
            Comparison<int> compare = (x, y) =>
            {
                int c = byName.Compare(names[x], names[y]);
                if (c == 0) c = locale.Compare(ids[x], ids[y]);
                return c != 0 ? c : x.CompareTo(y);
            };
            var order = SortInParts(all.Count, compare);
            var sorted = new object[order.Length];
            for (int n = 0; n < order.Length; n++) sorted[n] = all[order[n]];
            var snap = new Snapshot { Systems = systems.ToArray(), Games = new LibGame[sorted.Length] };
            var bySystem = new List<int>[systems.Count];
            for (int i = 0; i < systems.Count; i++)
            {
                bySystem[i] = new List<int>();
                snap.SystemByName[systems[i].Name] = systems[i];
            }
            for (int n = 0; n < sorted.Length; n++)
            {
                var g = (LibGame)sorted[n];
                g.Index = n;
                snap.Games[n] = g;
                snap.ById[g.Id] = g;
                bySystem[g.System].Add(n);
            }
            for (int i = 0; i < systems.Count; i++)
            {
                systems[i].Games = bySystem[i].ToArray();
                Tally(snap, systems[i]);
            }
            return snap;
        }

        static int StateOf(LibSystem s, LibGame g)
        {
            switch (g.Status)
            {
                case LibStatus.Archived: return LibStatus.Installed;
                case LibStatus.Local: return LibStatus.Upload;
                case LibStatus.Remote: return s.Known ? LibStatus.Download : LibStatus.Unavailable;
                default: return LibStatus.Sync;
            }
        }

        // How a system's games stand, counted
        // A game's part in its system's counts, taken away (sign -1) or added (1): after a
        // transfer only its game is counted again
        static void Recount(LibSystem s, LibGame g, int sign)
        {
            if (g == null || g.Status == LibStatus.Gone) return;
            int state = StateOf(s, g);
            var counts = (int[])s.Counts.Clone();
            counts[state] += sign;
            s.Counts = counts;
            if (g.Archived != null) { s.Packed += sign * g.Archived.Packed; s.ArchiveMedia += sign * g.ArchivedMediaSize; }
            if (g.Local != null) { s.Size += sign * g.Local.Size; s.LocalMedia += sign * g.LocalMediaSize; }
            if (state == LibStatus.Upload) { s.UploadSize += sign * g.Local.Size; s.UploadMedia += sign * g.LocalMediaSize; }
        }

        static void Tally(Snapshot snap, LibSystem s)
        {
            var counts = new int[5];
            long packed = 0, size = 0, upload = 0, archiveMedia = 0, localMedia = 0, uploadMedia = 0;
            foreach (int n in s.Games)
            {
                var g = snap.Games[n];
                if (g.Status == LibStatus.Gone) continue;
                int state = StateOf(s, g);
                counts[state]++;
                if (g.Archived != null) { packed += g.Archived.Packed; archiveMedia += g.ArchivedMediaSize; }
                if (g.Local != null) { size += g.Local.Size; localMedia += g.LocalMediaSize; }
                if (state == LibStatus.Upload) { upload += g.Local.Size; uploadMedia += g.LocalMediaSize; }
            }
            s.Counts = counts;
            s.Packed = packed;
            s.Size = size;
            s.UploadSize = upload;
            s.ArchiveMedia = archiveMedia;
            s.LocalMedia = localMedia;
            s.UploadMedia = uploadMedia;
        }

        // ES-DE's games and the archive's side by side, a large system's in several parts at once
        static List<LibGame> Merge(Work w)
        {
            var s = w.System;
            var local = w.Local;
            var made = new LibGame[local.Count];
            Parallel.For(0, local.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, i =>
            {
                var l = local[i];
                l.Media = MediaOf(w, l.Rel);
                ListedGame listed;
                w.Listed.TryGetValue(l.Rel, out listed);
                ArchivedGame a;
                w.Archived.TryGetValue(l.Rel, out a);
                // One string for the path, wherever it is kept: the catalog keeps its own
                if (a != null) { if (a.Rel == l.Rel) l.Rel = a.Rel; else a.Rel = l.Rel; }
                made[i] = Make(s, l.Rel, l, listed, a);
            });
            var games = new List<LibGame>(local.Count + w.Archived.Count);
            games.AddRange(made);
            var rels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in local) rels.Add(l.Rel);
            foreach (var pair in w.Archived) if (!rels.Contains(pair.Key)) games.Add(Make(s, pair.Key, null, null, pair.Value));
            return games;
        }

        // One string for each short value many games share, such as a date or a developer
        static readonly ConcurrentDictionary<string, string> pool = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        public static string Shared(string v) { return v == null || v.Length == 0 || v.Length > 48 ? v : pool.GetOrAdd(v, v); }

        static LibMedia[] MediaOf(Work w, string rel)
        {
            string key = Paths.BaseOf(rel);
            List<LibMedia> found = null;
            foreach (var byKey in w.Media)
            {
                LibMedia m;
                if (byKey == null || !byKey.TryGetValue(key, out m)) continue;
                if (found == null) found = new List<LibMedia>();
                found.Add(m);
            }
            return found == null ? null : found.ToArray();
        }

        static int nameAt = -1;

        static LibGame Make(LibSystem s, string rel, LocalGame local, ListedGame listed, ArchivedGame a)
        {
            var g = new LibGame { Rel = rel, Id = s.Name + "/" + rel, SystemName = s.Name, Local = local, Listed = local != null ? listed : null, Archived = a };
            if (nameAt < 0) nameAt = LibraryShape.KeptAt("name");
            if (local == null) g.Status = LibStatus.Remote;
            else if (a == null) g.Status = LibStatus.Local;
            else
            {
                bool same;
                try { same = SameFiles(s, local, a); } catch { same = false; }
                g.Status = !same ? LibStatus.Changed : SameDetails(local, g.Listed, a) ? LibStatus.Archived : LibStatus.Details;
            }
            if (local != null && g.Listed != null && g.Listed.Values[nameAt].Length > 0) g.Name = g.Listed.Values[nameAt];
            else if (a != null && a.Values[nameAt].Length > 0) g.Name = a.Values[nameAt];
            else g.Name = Paths.BaseOf(rel.Substring(rel.LastIndexOf('/') + 1));
            byte bits = 0;
            for (int k = 0; k < shown.Length; k++)
            {
                int type = shown[k];
                bool has = false;
                if (local != null && local.Media != null) foreach (var m in local.Media) if (m.Type == type) has = true;
                if (!has && a != null && a.Media != null) foreach (var m in a.Media) if (m.Type == type) has = true;
                if (has) bits |= (byte)(1 << k);
            }
            g.Media = bits;
            return g;
        }

        // Whether a game in ES-DE has the files of its entry in the archive: same size and
        // date, or a date that differs with the same CRC, kept in the cache of CRCs
        static bool SameFiles(LibSystem s, LocalGame local, ArchivedGame a)
        {
            string entry = Path.Combine(s.Dir, local.Rel.Replace('/', '\\'));
            string dir = local.Wide ? s.Dir : Path.GetDirectoryName(entry);
            var files = local.Files ?? new[] { new LibFile { Rel = Path.GetFileName(entry), Size = local.Size, Mtime = local.Mtime } };
            if (a.Files == null || a.Files.Length != files.Length) return false;
            var archived = new Dictionary<string, LibFile>(StringComparer.Ordinal);
            foreach (var f in a.Files) if (f.Rel != null) archived[f.Rel] = f;
            foreach (var f in files)
            {
                LibFile x;
                if (!archived.TryGetValue(f.Rel, out x) || x.Size != f.Size) return false;
                if (Math.Abs(x.Mtime - f.Mtime) <= 2000) continue;
                string abs = Path.Combine(dir, f.Rel.Replace('/', '\\'));
                string crc = null;
                var known = crcs != null && crcs.Contains(abs) ? Unwrap(crcs[abs]) as IDictionary : null;
                if (known != null && LibraryShape.Long(known, "size") == f.Size && LibraryShape.Long(known, "mtime") == f.Mtime) crc = LibraryShape.Str(known, "crc");
                if (crc == null)
                {
                    crc = Crc.OfFile(abs);
                    if (crcs != null)
                    {
                        var record = new OrderedDictionary();
                        record["size"] = f.Size;
                        record["mtime"] = f.Mtime;
                        record["crc"] = crc;
                        crcs[abs] = record;
                    }
                }
                if (!string.Equals(crc, x.Crc, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        static ulong emptyHash;
        static bool emptyHashSet;

        // Whether the scraped details and the media of the compared types are the archive's.
        // A type the game doesn't have in ES-DE is no difference: it may just not have been
        // downloaded.
        static bool SameDetails(LocalGame local, ListedGame listed, ArchivedGame a)
        {
            if (!emptyHashSet)
            {
                var empty = new string[LibraryShape.Scraped.Length];
                for (int i = 0; i < empty.Length; i++) empty[i] = "";
                emptyHash = FieldHash.Of(empty);
                emptyHashSet = true;
            }
            if ((listed != null ? listed.Hash : emptyHash) != a.Hash) return false;
            if (local.Media == null) return true;
            foreach (var m in local.Media)
            {
                if (Array.IndexOf(compared, m.Type) < 0) continue;
                bool same = false;
                if (a.Media != null) foreach (var x in a.Media) if (x.Type == m.Type && x.Size == m.Size && Math.Abs(x.Mtime - m.Mtime) <= 2000) same = true;
                if (!same) return false;
            }
            return true;
        }

        // The user's own media of one type for a system's games, by the game's path without
        // extension: what MediaIndex.UserMedia gives, with the sizes and dates of the folder
        // listings. Atelier's own images are told by their PNG text, and what they say is
        // kept by the file's size and date, so that a file is opened once.
        static Dictionary<string, LibMedia> ScanMedia(LibSystem s, int t)
        {
            string type = LibraryShape.MediaTypes[t];
            var exts = mediaExts[t];
            var picked = new Dictionary<string, LibMedia>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(s.MediaDir)) return picked;
            var best = new Dictionary<string, MFile>(StringComparer.Ordinal);
            var more = new Dictionary<string, List<MFile>>(StringComparer.Ordinal);
            Walk(Path.Combine(s.MediaDir, type), "", exts, best, more);
            string asideRoot;
            bool setAside = s.Aside.TryGetValue(type, out asideRoot);
            var asideBest = new Dictionary<string, MFile>(StringComparer.Ordinal);
            if (setAside) Walk(asideRoot, "", exts, asideBest, new Dictionary<string, List<MFile>>(StringComparer.Ordinal));
            foreach (var pair in best)
            {
                if (!setAside)
                {
                    picked[pair.Key] = MediaFrom(t, pair.Value, false);
                    continue;
                }
                List<MFile> candidates;
                if (more.TryGetValue(pair.Key, out candidates)) candidates.Sort((x, y) => x.Rank.CompareTo(y.Rank));
                else candidates = new List<MFile> { pair.Value };
                foreach (var f in candidates)
                {
                    if (IsAtelier(Path.Combine(f.Dir, f.Name), f.Size, f.Ticks)) continue;
                    picked[pair.Key] = MediaFrom(t, f, false);
                    break;
                }
                MFile kept;
                if (!picked.ContainsKey(pair.Key) && asideBest.TryGetValue(pair.Key, out kept)) picked[pair.Key] = MediaFrom(t, kept, true);
            }
            foreach (var pair in asideBest) if (!best.ContainsKey(pair.Key)) picked[pair.Key] = MediaFrom(t, pair.Value, true);
            return picked;
        }

        struct MFile
        {
            public string Dir, Name, Ext;
            public int Rank;
            public long Size, Ticks;
        }

        // The rank of a file's extension among a type's, or -1
        static int RankOf(string name, int dot, string[] exts)
        {
            int length = name.Length - dot;
            for (int r = 0; r < exts.Length; r++)
            {
                if (exts[r].Length == length && string.Compare(name, dot, exts[r], 0, length, StringComparison.OrdinalIgnoreCase) == 0) return r;
            }
            return -1;
        }

        // The media files under a folder, by the game's path without extension: the one of the
        // first extension in best, the others too in more
        static void Walk(string root, string relDir, string[] exts, Dictionary<string, MFile> best, Dictionary<string, List<MFile>> more)
        {
            string dir = relDir.Length == 0 ? root : Path.Combine(root, relDir.Replace('/', '\\'));
            if (string.IsNullOrEmpty(root)) return;
            foreach (var e in FastDir.List(dir))
            {
                if (e.IsDir)
                {
                    Walk(root, relDir.Length == 0 ? e.Name : relDir + "/" + e.Name, exts, best, more);
                    continue;
                }
                int dot = e.Name.LastIndexOf('.');
                if (dot <= 0) continue;
                int rank = RankOf(e.Name, dot, exts);
                if (rank < 0) continue;
                string key = relDir.Length == 0 ? e.Name.Substring(0, dot) : relDir + "/" + e.Name.Substring(0, dot);
                var f = new MFile { Dir = dir, Name = e.Name, Ext = exts[rank], Rank = rank, Size = e.Size, Ticks = e.Ticks };
                MFile was;
                if (!best.TryGetValue(key, out was))
                {
                    best[key] = f;
                    continue;
                }
                List<MFile> list;
                if (!more.TryGetValue(key, out list)) more[key] = list = new List<MFile> { was };
                list.Add(f);
                if (f.Rank < was.Rank) best[key] = f;
            }
        }

        static LibMedia MediaFrom(int t, MFile f, bool aside)
        {
            return new LibMedia { Type = t, Ext = f.Ext, Size = f.Size, Mtime = FastDir.Ms(f.Ticks), Aside = aside };
        }

        static LibMedia MediaFrom(int t, FileInfo f, bool aside)
        {
            return new LibMedia { Type = t, Ext = Path.GetExtension(f.Name).ToLowerInvariant(), Size = f.Length, Mtime = LocalScan.Ms(f.LastWriteTimeUtc), Aside = aside };
        }

        static readonly Dictionary<string, long[]> atelierMarks = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);

        static bool IsAtelier(FileInfo f) { return IsAtelier(f.FullName, f.Length, f.LastWriteTimeUtc.Ticks); }

        static bool IsAtelier(string file, long size, long ticks)
        {
            lock (atelierMarks)
            {
                long[] mark;
                if (atelierMarks.TryGetValue(file, out mark) && mark[0] == size && mark[1] == ticks) return mark[2] == 1;
            }
            bool drawn = MediaIndex.IsAtelierImage(file);
            lock (atelierMarks) atelierMarks[file] = new[] { size, ticks, drawn ? 1L : 0L };
            return drawn;
        }

        // One game read again on its own, after a transfer: its files in ES-DE, its gamelist
        // entry, its media and its entry in the archive. A game in neither place leaves the
        // lists. Returns the library's version.
        public static long Update(string system, string rel)
        {
            lock (touched) { if (building) touched.Add(system + "|" + rel); }
            gate.EnterUpgradeableReadLock();
            try
            {
                var snap = current;
                LibSystem s;
                if (!snap.SystemByName.TryGetValue(system, out s)) return Version;
                LocalGame local = null;
                ListedGame listed = null;
                if (s.Known && Directory.Exists(s.Dir))
                {
                    var one = LocalGames.ScanOne(s.Dir, rel, s.Extensions, s.Name);
                    if (one != null)
                    {
                        var files = (Array)one["files"];
                        local = new LocalGame { Rel = rel, IsDir = (bool)one["isDir"], Wide = (bool)one["wide"], Size = (long)one["size"] };
                        if (local.IsDir || files.Length != 1)
                        {
                            var list = new List<LibFile>();
                            foreach (object o in files)
                            {
                                var f = (Hashtable)o;
                                list.Add(new LibFile { Rel = (string)f["rel"], Size = (long)f["size"], Mtime = (long)f["mtime"] });
                            }
                            local.Files = list.ToArray();
                        }
                        else
                        {
                            var f = (Hashtable)files.GetValue(0);
                            local.Mtime = (long)f["mtime"];
                        }
                        local.Media = MediaOfOne(s, rel);
                        listed = Gamelist.CompactOne(s.Gamelist, rel, LibraryShape.Scraped, LibraryShape.Kept);
                    }
                }
                ArchivedGame a = null;
                if (root != null) a = ArchivedGame.From(rel, Catalog.Entry(root, system, rel));
                var fresh = local == null && a == null ? null : Make(s, rel, local, listed, a);
                gate.EnterWriteLock();
                try
                {
                    LibGame g;
                    string id = system + "/" + rel;
                    if (!snap.ById.TryGetValue(id, out g))
                    {
                        // A game the library didn't have: it joins at the end, until the library is read again
                        if (fresh == null) return Version;
                        fresh.System = s.Id;
                        fresh.Index = snap.Games.Length;
                        var games = new LibGame[snap.Games.Length + 1];
                        Array.Copy(snap.Games, games, snap.Games.Length);
                        games[fresh.Index] = fresh;
                        snap.Games = games;
                        snap.ById[id] = fresh;
                        g = fresh;
                        var ofSystem = new int[s.Games.Length + 1];
                        Array.Copy(s.Games, ofSystem, s.Games.Length);
                        ofSystem[s.Games.Length] = fresh.Index;
                        s.Games = ofSystem;
                        Recount(s, g, 1);
                    }
                    else if (fresh == null)
                    {
                        Recount(s, g, -1);
                        g.Status = LibStatus.Gone;
                    }
                    else
                    {
                        Recount(s, g, -1);
                        g.Status = fresh.Status;
                        g.Local = fresh.Local;
                        g.Listed = fresh.Listed;
                        g.Archived = fresh.Archived;
                        g.Media = fresh.Media;
                        g.Name = fresh.Name;
                        Recount(s, g, 1);
                    }
                    snap.Orders.Clear();
                    Touched(g.Index);
                    return Interlocked.Increment(ref version);
                }
                finally
                {
                    gate.ExitWriteLock();
                }
            }
            finally
            {
                gate.ExitUpgradeableReadLock();
            }
        }

        // The user's own media of one game, looked up file by file
        static LibMedia[] MediaOfOne(LibSystem s, string rel)
        {
            string key = Paths.BaseOf(rel).Replace('/', '\\');
            var found = new List<LibMedia>();
            for (int t = 0; t < LibraryShape.MediaTypes.Length; t++)
            {
                string type = LibraryShape.MediaTypes[t];
                string asideRoot;
                bool setAside = s.Aside.TryGetValue(type, out asideRoot);
                bool done = false;
                foreach (string ext in mediaExts[t])
                {
                    var f = new FileInfo(Path.Combine(s.MediaDir, type, key + ext));
                    if (!f.Exists || (setAside && IsAtelier(f))) continue;
                    found.Add(MediaFrom(t, f, false));
                    done = true;
                    break;
                }
                if (done || !setAside) continue;
                foreach (string ext in mediaExts[t])
                {
                    var f = new FileInfo(Path.Combine(asideRoot, key + ext));
                    if (!f.Exists) continue;
                    found.Add(MediaFrom(t, f, true));
                    break;
                }
            }
            return found.Count == 0 ? null : found.ToArray();
        }

        // The file of a game's media of a type: ES-DE's own, else the archive's; or null
        public static string MediaPath(string id, string type)
        {
            int t = LibraryShape.MediaType(type);
            if (t < 0) return null;
            gate.EnterReadLock();
            try
            {
                LibGame g;
                if (!current.ById.TryGetValue(id, out g) || g.Status == LibStatus.Gone) return null;
                var s = current.Systems[g.System];
                if (g.Local != null && g.Local.Media != null)
                {
                    foreach (var m in g.Local.Media)
                    {
                        if (m.Type != t) continue;
                        string key = Paths.BaseOf(g.Rel).Replace('/', '\\') + m.Ext;
                        return m.Aside ? Path.Combine(s.Aside[type], key) : Path.Combine(s.MediaDir, type, key);
                    }
                }
                if (g.Archived != null && root != null)
                {
                    string file = g.Archived.MediaFile(t);
                    if (file != null) return Path.Combine(Catalog.MetaDir(root, s.Name), file.Replace('/', '\\'));
                }
                return null;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // What the page asks of the library

        static string MediaUrl(LibGame g, string type) { return "/media/" + type + "/" + Uri.EscapeDataString(g.Id); }

        static string Shown(LibGame g, int k, string type) { return (g.Media & (1 << k)) != 0 ? MediaUrl(g, type) : null; }

        // Whether a game is in a section of the Catalogue (archive, local, unarchived) or in
        // what a collection or a search keeps to (all, local, remote)
        static bool In(LibGame g, string section)
        {
            if (g.Status == LibStatus.Gone) return false;
            switch (section)
            {
                case "archive": return g.Archived != null;
                case "local": return g.Local != null;
                case "unarchived": return g.Status == LibStatus.Local;
                case "remote": return g.Local == null;
                default: return true;
            }
        }

        // Indexes in the order chosen: by name (the library's order), by size, largest
        // first, or by the date archived, latest first
        static int[] Sorted(Snapshot snap, List<int> list, string sort)
        {
            var a = list.ToArray();
            var games = snap.Games;
            if (sort == "size") Array.Sort(a, (x, y) => { int c = games[y].SizeOf.CompareTo(games[x].SizeOf); return c != 0 ? c : x.CompareTo(y); });
            else if (sort == "recent")
            {
                Array.Sort(a, (x, y) =>
                {
                    string ax = games[x].Archived != null ? games[x].Archived.Archived ?? "" : "", ay = games[y].Archived != null ? games[y].Archived.Archived ?? "" : "";
                    int c = string.CompareOrdinal(ay, ax);
                    return c != 0 ? c : x.CompareTo(y);
                });
            }
            return a;
        }

        static int[] Order(Snapshot snap, LibSystem s, string section, string sort)
        {
            string key = s.Name + "|" + section + "|" + sort;
            int[] order;
            lock (snap.Orders) { if (snap.Orders.TryGetValue(key, out order)) return order; }
            var list = new List<int>(s.Games.Length);
            foreach (int n in s.Games) if (In(snap.Games[n], section)) list.Add(n);
            order = Sorted(snap, list, sort);
            lock (snap.Orders) snap.Orders[key] = order;
            return order;
        }

        static void WriteRow(StringBuilder sb, Snapshot snap, LibGame g)
        {
            var s = snap.Systems[g.System];
            sb.Append("{\"id\":");
            Json.WriteString(sb, g.Id);
            sb.Append(",\"system\":");
            Json.WriteString(sb, s.Name);
            sb.Append(",\"systemName\":");
            Json.WriteString(sb, s.FullName);
            sb.Append(",\"name\":");
            Json.WriteString(sb, g.Name);
            sb.Append(",\"status\":\"").Append(LibStatus.Names[g.Status]).Append("\",\"known\":").Append(s.Known ? "true" : "false");
            sb.Append(",\"local\":");
            if (g.Local != null) sb.Append("{\"size\":").Append(g.Local.Size).Append(",\"media\":").Append(g.LocalMediaSize).Append('}');
            else sb.Append("null");
            sb.Append(",\"archive\":");
            if (g.Archived != null)
            {
                sb.Append("{\"size\":").Append(g.Archived.Size).Append(",\"packed\":").Append(g.Archived.Packed).Append(",\"media\":").Append(g.ArchivedMediaSize)
                  .Append(",\"archived\":");
                Json.WriteString(sb, g.Archived.Archived);
                sb.Append('}');
            }
            else sb.Append("null");
            sb.Append(",\"cover\":");
            string cover = Shown(g, 0, "covers");
            if (cover != null) Json.WriteString(sb, cover);
            else sb.Append("null");
            sb.Append('}');
        }

        // The library's systems, each with how its games stand: { version, total, systems:
        // [{ id, name, known, games, counts: [installed, sync, upload, download,
        // unavailable], packed, size, upload, archiveMedia, localMedia, uploadMedia }] },
        // as JSON
        public static string Summary()
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var sb = new StringBuilder();
                int total = 0;
                foreach (var s in snap.Systems) foreach (int c in s.Counts) total += c;
                sb.Append("{\"version\":").Append(Version).Append(",\"ready\":").Append(Version > 0 ? "true" : "false").Append(",\"building\":").Append(Busy ? "true" : "false")
                  .Append(",\"error\":");
                if (Error != null) Json.WriteString(sb, Error);
                else sb.Append("null");
                sb.Append(",\"total\":").Append(total).Append(",\"systems\":[");
                bool first = true;
                foreach (var s in snap.Systems)
                {
                    int games = 0;
                    foreach (int c in s.Counts) games += c;
                    if (games == 0) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":");
                    Json.WriteString(sb, s.Name);
                    sb.Append(",\"name\":");
                    Json.WriteString(sb, s.FullName);
                    sb.Append(",\"known\":").Append(s.Known ? "true" : "false").Append(",\"games\":").Append(games).Append(",\"counts\":[");
                    for (int i = 0; i < s.Counts.Length; i++) { if (i > 0) sb.Append(','); sb.Append(s.Counts[i]); }
                    sb.Append("],\"packed\":").Append(s.Packed).Append(",\"size\":").Append(s.Size).Append(",\"upload\":").Append(s.UploadSize)
                      .Append(",\"archiveMedia\":").Append(s.ArchiveMedia).Append(",\"localMedia\":").Append(s.LocalMedia).Append(",\"uploadMedia\":").Append(s.UploadMedia).Append('}');
                }
                sb.Append("]}");
                return sb.ToString();
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // A page of a system's games in a section, in the order chosen: { version, total,
        // from, games: [...] }, as JSON; at, when given, is a game whose place in the list
        // is wanted too (place, or -1)
        public static string List(string system, string section, string sort, int from, int count, string at)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                LibSystem s;
                int[] order = snap.SystemByName.TryGetValue(system ?? "", out s) ? Order(snap, s, section, sort) : new int[0];
                return Page(snap, order, from, count, at);
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        static string Page(Snapshot snap, int[] order, int from, int count, string at)
        {
            var sb = new StringBuilder();
            from = Math.Max(0, Math.Min(from, order.Length));
            int end = Math.Min(order.Length, from + Math.Max(0, count));
            sb.Append("{\"version\":").Append(Version).Append(",\"total\":").Append(order.Length).Append(",\"from\":").Append(from);
            if (at != null)
            {
                int place = -1;
                LibGame g;
                if (snap.ById.TryGetValue(at, out g)) place = Array.IndexOf(order, g.Index);
                sb.Append(",\"place\":").Append(place);
            }
            sb.Append(",\"games\":[");
            for (int i = from; i < end; i++)
            {
                if (i > from) sb.Append(',');
                WriteRow(sb, snap, snap.Games[order[i]]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Everything the page shows of one game, or null
        public static Hashtable Details(string id)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                LibGame g;
                if (id == null || !snap.ById.TryGetValue(id, out g) || g.Status == LibStatus.Gone) return null;
                var s = snap.Systems[g.System];
                var d = new Hashtable();
                d["id"] = g.Id;
                d["system"] = s.Name;
                d["systemName"] = s.FullName;
                d["known"] = s.Known;
                d["rel"] = g.Rel;
                d["name"] = g.Name;
                d["status"] = LibStatus.Names[g.Status];
                d["altemulator"] = g.Field(LibraryShape.KeptAt("altemulator"));
                if (g.Local != null) d["local"] = new Hashtable { { "size", g.Local.Size }, { "files", g.Local.Files == null ? 1 : g.Local.Files.Length } };
                else d["local"] = null;
                if (g.Archived != null)
                {
                    var types = new ArrayList();
                    if (g.Archived.Media != null) foreach (var m in g.Archived.Media) if (m.Type >= 0) types.Add(LibraryShape.MediaTypes[m.Type]);
                    d["archive"] = new Hashtable
                    {
                        { "size", g.Archived.Size }, { "packed", g.Archived.Packed }, { "archived", g.Archived.Archived }, { "updated", g.Archived.Updated },
                        { "device", g.Archived.Device }, { "media", types },
                    };
                }
                else d["archive"] = null;
                string released = g.Field(LibraryShape.KeptAt("releasedate"));
                d["fields"] = new Hashtable
                {
                    { "developer", g.Field(LibraryShape.KeptAt("developer")) }, { "publisher", g.Field(LibraryShape.KeptAt("publisher")) },
                    { "released", released.Length > 4 ? released.Substring(0, 4) : released }, { "genre", g.Field(LibraryShape.KeptAt("genre")) },
                    { "players", g.Field(LibraryShape.KeptAt("players")) }, { "rating", g.Field(LibraryShape.KeptAt("rating")) },
                };
                d["cover"] = Shown(g, 0, "covers");
                d["image"] = Shown(g, 1, "screenshots") ?? Shown(g, 2, "titlescreens");
                d["backdrop"] = Shown(g, 3, "fanart") ?? Shown(g, 1, "screenshots");
                d["video"] = Shown(g, 4, "videos");
                return d;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The games of a list of ids, or of a system's section, still in the library
        public static List<LibGame> Pick(IList ids, string system, string section)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var found = new List<LibGame>();
                if (ids != null)
                {
                    foreach (object o in ids)
                    {
                        LibGame g;
                        if (o != null && snap.ById.TryGetValue(Unwrap(o).ToString(), out g) && g.Status != LibStatus.Gone) found.Add(g);
                    }
                }
                LibSystem s;
                if (system != null && snap.SystemByName.TryGetValue(system, out s))
                {
                    foreach (int n in Order(snap, s, section ?? "all", "name")) found.Add(snap.Games[n]);
                }
                return found;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The games of a few that are in a section or where (see In)
        public static List<LibGame> Keep(List<LibGame> games, string where)
        {
            return games.FindAll(g => In(g, where ?? "all"));
        }

        // The ids of a system's games in a section, in the order chosen, for marking them all
        public static string Ids(string system, string section, string sort)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                LibSystem s;
                var sb = new StringBuilder("[");
                if (system != null && snap.SystemByName.TryGetValue(system, out s))
                {
                    bool first = true;
                    foreach (int n in Order(snap, s, section ?? "all", sort ?? "name"))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Json.WriteString(sb, snap.Games[n].Id);
                    }
                }
                return sb.Append(']').ToString();
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // Whether a transfer can be done on a game: archive, update-details, download,
        // remove-local, remove-archive, or any for the transfers of saves, decided elsewhere
        public static bool Can(LibGame g, string action)
        {
            bool known;
            gate.EnterReadLock();
            try { known = current.Systems.Length > g.System && current.Systems[g.System].Known; } finally { gate.ExitReadLock(); }
            switch (action)
            {
                case "archive": return g.Local != null && (g.Status == LibStatus.Local || g.Status == LibStatus.Changed);
                case "update-details": return g.Local != null && g.Status == LibStatus.Details;
                case "download": return g.Archived != null && known && (g.Status == LibStatus.Remote || g.Status == LibStatus.Changed);
                case "remove-local": return g.Local != null;
                case "remove-archive": return g.Archived != null;
                default: return true;
            }
        }

        // What the actions of a few games can do: for each transfer, how many games and how
        // much (the size of the games, as the page shows it), and the state of the first;
        // for archiving and downloading, the size of the media that go too, by type
        // ({ type: size }), the page counting those of the types chosen
        public static Hashtable Actions(List<LibGame> games)
        {
            var d = new Hashtable();
            foreach (string action in new[] { "archive", "update-details", "download", "remove-local", "remove-archive" })
            {
                int count = 0;
                long size = 0;
                string first = null;
                var media = LibMedia.NoSizes();
                foreach (var g in games)
                {
                    if (!Can(g, action)) continue;
                    count++;
                    size += g.SizeOf;
                    if (action == "archive") LibMedia.AddTo(media, g.Local.Media);
                    else if (action == "download") LibMedia.AddTo(media, g.Archived.Media);
                    if (first == null) first = LibStatus.Names[g.Status];
                }
                d[action] = new Hashtable { { "count", count }, { "size", size }, { "first", first } };
                if (action == "archive" || action == "download") ((Hashtable)d[action])["media"] = LibMedia.ByType(media);
            }
            int unsafeCount = 0, inArchive = 0, onlyArchive = 0, notArchivedFirst = 0, install = 0, unarchived = 0;
            long installSize = 0, unarchivedSize = 0;
            var installMedia = LibMedia.NoSizes();
            var unarchivedMedia = LibMedia.NoSizes();
            foreach (var g in games)
            {
                bool known;
                gate.EnterReadLock();
                try { known = current.Systems.Length > g.System && current.Systems[g.System].Known; } finally { gate.ExitReadLock(); }
                if (g.Local == null && known) { install++; installSize += g.SizeOf; if (g.Archived != null) LibMedia.AddTo(installMedia, g.Archived.Media); }
                if (g.Status == LibStatus.Local) { unarchived++; unarchivedSize += g.SizeOf; LibMedia.AddTo(unarchivedMedia, g.Local.Media); }
                if (g.Local != null && g.Status != LibStatus.Archived && g.Status != LibStatus.Details) unsafeCount++;
                if (g.Archived != null) inArchive++;
                if (g.Archived != null && g.Local == null) onlyArchive++;
                if (g.Local != null && g.Status != LibStatus.Local) notArchivedFirst++;
            }
            d["unsafe"] = unsafeCount;
            d["inArchive"] = inArchive;
            d["onlyArchive"] = onlyArchive;
            d["count"] = games.Count;
            d["name"] = games.Count > 0 ? games[0].Name : null;
            d["install"] = new Hashtable { { "count", install }, { "size", installSize }, { "media", LibMedia.ByType(installMedia) } };
            d["unarchived"] = new Hashtable { { "count", unarchived }, { "size", unarchivedSize }, { "media", LibMedia.ByType(unarchivedMedia) } };
            d["allFresh"] = games.TrueForAll(g => !Can(g, "archive") || g.Status == LibStatus.Local);
            return d;
        }

        // The systems of a few games, each once
        public static List<string> SystemsOf(List<LibGame> games)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var g in games) if (seen.Add(g.SystemName)) list.Add(g.SystemName);
            return list;
        }

        // What can go of a game's files when it leaves ES-DE: not what another game there
        // still has, which goes with the last of them. Its own file always goes, or a game
        // that shares a folder with others could never leave.
        public static List<string> OwnFiles(string system, string rel, string[] files)
        {
            var own = new List<string>(files.Length);
            gate.EnterReadLock();
            try
            {
                LibSystem s;
                current.SystemByName.TryGetValue(system, out s);
                string entry = s == null ? null : Path.GetFullPath(Path.Combine(s.Dir, rel.Replace('/', '\\')));
                foreach (string file in files)
                {
                    List<string> users;
                    bool kept = false;
                    if (s != null && s.Shared != null && !string.Equals(file, entry, StringComparison.OrdinalIgnoreCase) && s.Shared.TryGetValue(file, out users))
                    {
                        foreach (string other in users)
                        {
                            string path = Path.Combine(s.Dir, other.Replace('/', '\\'));
                            if (other != rel && (File.Exists(path) || Directory.Exists(path))) kept = true;
                        }
                    }
                    if (!kept) own.Add(file);
                }
                return own;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The folders of the files RetroArch gets for a few games in ES-DE, each once: a folder
        // ES-DE treats as a file is run through the file of the same name inside it
        public static List<string> ContentDirs(List<LibGame> games)
        {
            gate.EnterReadLock();
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var list = new List<string>();
                foreach (var g in games)
                {
                    if (g.Local == null) continue;
                    var s = current.Systems[g.System];
                    string entry = Path.Combine(s.Dir, g.Rel.Replace('/', '\\'));
                    string dir = g.Local.IsDir ? entry : Path.GetDirectoryName(entry);
                    if (seen.Add(dir)) list.Add(dir);
                }
                return list;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The games of a few in ES-DE that have saves here, by the name of their file
        // (names), or in the archive, by their path without extension (archived: system =>
        // those paths)
        public static List<LibGame> WithSaves(List<LibGame> games, ICollection<string> names, IDictionary archived)
        {
            var list = new List<LibGame>();
            foreach (var g in games)
            {
                if (g.Local == null) continue;
                string name = Paths.BaseOf(g.Rel.Substring(g.Rel.LastIndexOf('/') + 1));
                var bases = archived != null && archived.Contains(g.SystemName) ? Unwrap(archived[g.SystemName]) as ICollection<string> : null;
                if ((names != null && names.Contains(name)) || (bases != null && bases.Contains(Paths.BaseOf(g.Rel)))) list.Add(g);
            }
            return list;
        }

        // The size of each type of media: of ES-DE's for archiving, of the archive's for
        // downloading
        public static Hashtable MediaSizes()
        {
            gate.EnterReadLock();
            try
            {
                var archive = new long[LibraryShape.MediaTypes.Length];
                var download = new long[LibraryShape.MediaTypes.Length];
                foreach (var g in current.Games)
                {
                    if (g.Status == LibStatus.Gone) continue;
                    if (g.Local != null && g.Local.Media != null) foreach (var m in g.Local.Media) archive[m.Type] += m.Size;
                    if (g.Archived != null && g.Archived.Media != null) foreach (var m in g.Archived.Media) if (m.Type >= 0) download[m.Type] += m.Size;
                }
                var a = new Hashtable();
                var b = new Hashtable();
                for (int t = 0; t < archive.Length; t++)
                {
                    a[LibraryShape.MediaTypes[t]] = archive[t];
                    b[LibraryShape.MediaTypes[t]] = download[t];
                }
                return new Hashtable { { "archive", a }, { "download", b } };
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The search of the collections, as runSearch in catalogue/ui/collections.js had it,
        // on an index of every game's words: of its name (weighing 8), its file's name (4),
        // its platform (4), its developer and publisher (3), its genre (3), its year (3) and
        // its description (1, for search words of 3 letters or more that aren't numbers). A
        // search word finds the words that start with it; a game is found when every word of
        // the search finds something, and comes in the order of its score, the sum of what
        // each word found at best. The filters (facets) keep to a platform, genre, decade,
        // year, developer, publisher, number of players, region, favorites or completed games.

        static readonly string[] Facets = { "system", "genre", "decade", "year", "developer", "publisher", "players", "region", "favorite", "completed" };
        static readonly HashSet<string> Regions = new HashSet<string>(StringComparer.Ordinal)
        {
            "World", "Europe", "USA", "Japan", "Asia", "Australia", "Brazil", "Canada", "China", "France", "Germany", "Hong Kong", "Italy",
            "Korea", "Netherlands", "Russia", "Scandinavia", "Spain", "Sweden", "Taiwan", "UK",
        };
        static readonly Regex RegionPart = new Regex(@"\(([^)]*)\)", RegexOptions.Compiled);
        static readonly Regex Number = new Regex(@"\d+", RegexOptions.Compiled);

        sealed class SearchIndex
        {
            public Snapshot Of;
            public int[][] Tokens, Desc;
            public string[] Year, Developer, Publisher;
            public string[][] Genres, Regions;
            public int[] Players;
            public bool[] Favorite, Completed;
            public string[] Sorted;
            public int[] SortedIds;
            public int Vocabulary;
            public readonly HashSet<int> Dirty = new HashSet<int>();
        }
        static SearchIndex searchIndex;
        static readonly object searchLock = new object();

        static void AddWords(Dictionary<int, int> best, string text, int weight)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (string w in Words.Of(text))
            {
                int id = Words.Id(w), was;
                if (!best.TryGetValue(id, out was) || was < weight) best[id] = weight;
            }
        }

        // The genres and the regions of a game, as lists many games share: they are the same
        // for most of a system's games
        static readonly ConcurrentDictionary<string, string[]> genreLists = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        static readonly ConcurrentDictionary<string, string[]> regionLists = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        static readonly string[] NoValues = new string[0];

        static string[] GenresOf(string genre)
        {
            if (genre.Length == 0) return NoValues;
            return genreLists.GetOrAdd(genre, v =>
            {
                var genres = new List<string>();
                foreach (string part in Regex.Split(v, @"\s*[/,;]\s*")) if (part.Trim().Length > 0) genres.Add(Shared(part.Trim()));
                return genres.ToArray();
            });
        }

        static string[] RegionsOf(string file)
        {
            List<string> regions = null;
            foreach (Match m in RegionPart.Matches(file))
                foreach (string r in m.Groups[1].Value.Split(','))
                    if (Regions.Contains(r.Trim())) (regions ?? (regions = new List<string>())).Add(r.Trim());
            if (regions == null) return NoValues;
            var list = regions.ToArray();
            return regionLists.GetOrAdd(string.Join("\n", list), list);
        }

        static void Tokenize(SearchIndex x, Snapshot snap, int n, Dictionary<int, int> best)
        {
            var g = snap.Games[n];
            var s = snap.Systems[g.System];
            best.Clear();
            string file = g.Rel.Substring(g.Rel.LastIndexOf('/') + 1);
            string released = g.Field(LibraryShape.KeptAt("releasedate"));
            string year = released.Length >= 4 && Words.OnlyDigits(released.Substring(0, 4)) ? released.Substring(0, 4) : "";
            string developer = g.Field(LibraryShape.KeptAt("developer")), publisher = g.Field(LibraryShape.KeptAt("publisher"));
            string genre = g.Field(LibraryShape.KeptAt("genre"));
            AddWords(best, g.Name, 8);
            AddWords(best, file, 4);
            AddWords(best, s.FullName + " " + s.Name, 4);
            AddWords(best, developer + " " + publisher, 3);
            AddWords(best, genre, 3);
            AddWords(best, year, 3);
            var tokens = new int[best.Count];
            int k = 0;
            foreach (var pair in best) tokens[k++] = pair.Key << 4 | pair.Value;
            x.Tokens[n] = tokens;
            x.Desc[n] = g.Listed != null && g.Listed.Any ? g.Listed.Desc : g.Archived != null ? g.Archived.Desc : null;
            x.Year[n] = Shared(year);
            x.Developer[n] = developer;
            x.Publisher[n] = publisher;
            x.Genres[n] = GenresOf(genre);
            int players = 0;
            foreach (Match m in Number.Matches(g.Field(LibraryShape.KeptAt("players"))))
            {
                int p;
                if (int.TryParse(m.Value, out p) && p > players) players = p;
            }
            x.Players[n] = players;
            x.Regions[n] = RegionsOf(file);
            x.Favorite[n] = g.Field(LibraryShape.KeptAt("favorite")) == "true";
            x.Completed[n] = g.Field(LibraryShape.KeptAt("completed")) == "true";
        }

        // The index of the library as it is now, made again for a new library, and for the
        // games changed since in place; under the gate
        static SearchIndex Searchable(Snapshot snap)
        {
            lock (searchLock)
            {
                var x = searchIndex;
                int count = snap.Games.Length;
                if (x == null || x.Of != snap || x.Tokens.Length != count)
                {
                    x = new SearchIndex
                    {
                        Of = snap, Tokens = new int[count][], Desc = new int[count][], Year = new string[count], Developer = new string[count],
                        Publisher = new string[count], Genres = new string[count][], Regions = new string[count][], Players = new int[count],
                        Favorite = new bool[count], Completed = new bool[count],
                    };
                    Parallel.For(0, count, () => new Dictionary<int, int>(), (n, loop, best) => { Tokenize(x, snap, n, best); return best; }, best => { });
                    searchIndex = x;
                }
                else if (x.Dirty.Count > 0)
                {
                    var best = new Dictionary<int, int>();
                    foreach (int n in x.Dirty) if (n < count) Tokenize(x, snap, n, best);
                    x.Dirty.Clear();
                }
                if (x.Sorted == null || x.Vocabulary != Words.Count)
                {
                    var all = Words.All();
                    Array.Sort(all, (a, b) => string.CompareOrdinal(a.Key, b.Key));
                    x.Sorted = new string[all.Length];
                    x.SortedIds = new int[all.Length];
                    for (int i = 0; i < all.Length; i++)
                    {
                        x.Sorted[i] = all[i].Key;
                        x.SortedIds[i] = all[i].Value;
                    }
                    x.Vocabulary = Words.Count;
                }
                return x;
            }
        }

        static void Touched(int n)
        {
            lock (searchLock) { if (searchIndex != null) searchIndex.Dirty.Add(n); }
        }

        sealed class Query
        {
            public List<bool[]> Masks = new List<bool[]>();
            public List<bool> Desc = new List<bool>();
            public string[] Values = new string[Facets.Length];
            public bool HasWords { get { return Masks.Count > 0; } }
        }

        static Query QueryOf(SearchIndex x, IDictionary search)
        {
            var q = new Query();
            int size = Words.Count + 1;
            foreach (string w in Words.Of(LibraryShape.Str(search, "text") ?? ""))
            {
                var mask = new bool[size];
                int lo = 0, hi = x.Sorted.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (string.CompareOrdinal(x.Sorted[mid], w) < 0) lo = mid + 1;
                    else hi = mid;
                }
                for (int i = lo; i < x.Sorted.Length && x.Sorted[i].StartsWith(w, StringComparison.Ordinal); i++) if (x.SortedIds[i] < size) mask[x.SortedIds[i]] = true;
                q.Masks.Add(mask);
                q.Desc.Add(w.Length >= 3 && !Words.OnlyDigits(w));
            }
            for (int k = 0; k < Facets.Length; k++) q.Values[k] = (LibraryShape.Str(search, Facets[k]) ?? "").Trim();
            return q;
        }

        static int Score(SearchIndex x, int n, Query q)
        {
            int sum = 0;
            for (int k = 0; k < q.Masks.Count; k++)
            {
                var mask = q.Masks[k];
                int best = 0;
                foreach (int t in x.Tokens[n])
                {
                    int id = t >> 4;
                    if (id < mask.Length && mask[id] && (t & 15) > best) best = t & 15;
                }
                if (best == 0 && q.Desc[k] && x.Desc[n] != null)
                {
                    foreach (int id in x.Desc[n]) if (id < mask.Length && mask[id]) { best = 1; break; }
                }
                if (best == 0) return 0;
                sum += best;
            }
            return sum;
        }

        static bool Holds(SearchIndex x, Snapshot snap, int n, int k, string value)
        {
            if (value.Length == 0) return true;
            switch (Facets[k])
            {
                case "system": return snap.Systems[snap.Games[n].System].Name == value;
                case "genre": return Array.IndexOf(x.Genres[n], value) >= 0;
                case "decade": return x.Year[n].Length == 4 && x.Year[n].Substring(0, 3) + "0" == value;
                case "year": return x.Year[n] == value;
                case "developer": return x.Developer[n] == value;
                case "publisher": return x.Publisher[n] == value;
                case "players":
                    int p;
                    if (!int.TryParse(value, out p)) return true;
                    return p == 1 ? x.Players[n] == 1 : x.Players[n] >= p;
                case "region": return Array.IndexOf(x.Regions[n], value) >= 0;
                case "favorite": return x.Favorite[n];
                case "completed": return x.Completed[n];
            }
            return true;
        }

        // The facets a game fails, as bits
        static int Fails(SearchIndex x, Snapshot snap, int n, Query q)
        {
            int fails = 0;
            for (int k = 0; k < Facets.Length; k++) if (!Holds(x, snap, n, k, q.Values[k])) fails |= 1 << k;
            return fails;
        }

        // The games a search finds, as indexes in the order it gives them, with how many it
        // finds in ES-DE and only in the archive
        static List<int> Run(Snapshot snap, SearchIndex x, Query q, string where, out int local, out int remote)
        {
            var found = new List<KeyValuePair<int, int>>();
            int inLocal = 0, inRemote = 0;
            for (int n = 0; n < snap.Games.Length; n++)
            {
                var g = snap.Games[n];
                if (g.Status == LibStatus.Gone || Fails(x, snap, n, q) != 0) continue;
                int score = q.HasWords ? Score(x, n, q) : 1;
                if (score == 0) continue;
                if (g.Local != null) inLocal++;
                else inRemote++;
                if (In(g, where)) found.Add(new KeyValuePair<int, int>(n, score));
            }
            if (q.HasWords) found.Sort((a, b) => { int c = b.Value.CompareTo(a.Value); return c != 0 ? c : a.Key.CompareTo(b.Key); });
            local = inLocal;
            remote = inRemote;
            return found.ConvertAll(p => p.Key);
        }

        // The ids of the games a search finds, wherever they are: what a collection that keeps
        // up with it holds
        public static List<string> SearchIds(IDictionary search)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var x = Searchable(snap);
                int l, r;
                return Run(snap, x, QueryOf(x, search), "all", out l, out r).ConvertAll(n => snap.Games[n].Id);
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // A page of what a search finds, kept to where (all, local, remote): { version,
        // total, counts: { all, local, remote }, inCollection, from, games }, as JSON; a game
        // in the collection given carries member: true
        public static string Search(IDictionary search, string where, int from, int count, string collection)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var x = Searchable(snap);
                int local, remote;
                var found = Run(snap, x, QueryOf(x, search), where, out local, out remote);
                var members = collection != null ? MemberSet(snap, collection) : new HashSet<int>();
                int inside = 0;
                foreach (int n in found) if (members.Contains(n)) inside++;
                var sb = new StringBuilder();
                from = Math.Max(0, Math.Min(from, found.Count));
                int end = Math.Min(found.Count, from + Math.Max(0, count));
                sb.Append("{\"version\":").Append(Version).Append(",\"total\":").Append(found.Count).Append(",\"counts\":{\"all\":").Append(local + remote)
                  .Append(",\"local\":").Append(local).Append(",\"remote\":").Append(remote).Append("},\"inCollection\":").Append(inside).Append(",\"from\":").Append(from).Append(",\"games\":[");
                for (int i = from; i < end; i++)
                {
                    if (i > from) sb.Append(',');
                    WriteRow(sb, snap, snap.Games[found[i]]);
                    if (members.Contains(found[i])) { sb.Length--; sb.Append(",\"member\":true}"); }
                }
                sb.Append("]}");
                return sb.ToString();
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The ids of what a search finds that the collection doesn't hold yet
        public static List<string> SearchOutside(IDictionary search, string where, string collection)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var x = Searchable(snap);
                int l, r;
                var members = MemberSet(snap, collection);
                var ids = new List<string>();
                foreach (int n in Run(snap, x, QueryOf(x, search), where, out l, out r)) if (!members.Contains(n)) ids.Add(snap.Games[n].Id);
                return ids;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The values each filter can take with the rest of the search, with how many games
        // each finds: { key: [{ value, label, count }] }, the most common first, or in their
        // own order for platforms, decades, years and players, as JSON
        public static string FacetValues(IDictionary search, string where)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var x = Searchable(snap);
                var q = QueryOf(x, search);
                var counts = new Dictionary<string, int>[Facets.Length];
                var labels = new Dictionary<string, string>[Facets.Length];
                for (int k = 0; k < Facets.Length; k++)
                {
                    counts[k] = new Dictionary<string, int>(StringComparer.Ordinal);
                    labels[k] = new Dictionary<string, string>(StringComparer.Ordinal);
                }
                Action<int, string, string> add = (k, value, label) =>
                {
                    int c;
                    counts[k].TryGetValue(value, out c);
                    counts[k][value] = c + 1;
                    labels[k][value] = label;
                };
                for (int n = 0; n < snap.Games.Length; n++)
                {
                    var g = snap.Games[n];
                    if (g.Status == LibStatus.Gone || !In(g, where)) continue;
                    int fails = Fails(x, snap, n, q);
                    // Counted for a filter when the others all hold
                    if ((fails & (fails - 1)) != 0) continue;
                    if (q.HasWords && Score(x, n, q) == 0) continue;
                    for (int k = 0; k < Facets.Length; k++)
                    {
                        if ((fails & ~(1 << k)) != 0) continue;
                        switch (Facets[k])
                        {
                            case "system": add(k, snap.Systems[g.System].Name, snap.Systems[g.System].FullName); break;
                            case "genre": foreach (string v in x.Genres[n]) add(k, v, v); break;
                            case "decade": if (x.Year[n].Length == 4) add(k, x.Year[n].Substring(0, 3) + "0", null); break;
                            case "year": if (x.Year[n].Length == 4) add(k, x.Year[n], x.Year[n]); break;
                            case "developer": if (x.Developer[n].Length > 0) add(k, x.Developer[n], x.Developer[n]); break;
                            case "publisher": if (x.Publisher[n].Length > 0) add(k, x.Publisher[n], x.Publisher[n]); break;
                            case "players":
                                if (x.Players[n] == 1) add(k, "1", null);
                                for (int p = 2; p <= Math.Min(4, x.Players[n]); p++) add(k, p.ToString(CultureInfo.InvariantCulture), null);
                                break;
                            case "region": foreach (string v in x.Regions[n]) add(k, v, v); break;
                            case "favorite": if (x.Favorite[n]) add(k, "true", null); break;
                            case "completed": if (x.Completed[n]) add(k, "true", null); break;
                        }
                    }
                }
                var names = new NameComparer(culture);
                var sb = new StringBuilder("{");
                for (int k = 0; k < Facets.Length; k++)
                {
                    var values = new List<string>(counts[k].Keys);
                    string key = Facets[k];
                    var c = counts[k];
                    var l = labels[k];
                    if (key == "decade" || key == "year" || key == "players") values.Sort((a, b) => names.Compare(a, b));
                    else if (key == "system") values.Sort((a, b) => names.Compare(l[a], l[b]));
                    else values.Sort((a, b) => { int d = c[b].CompareTo(c[a]); return d != 0 ? d : names.Compare(l[a] ?? a, l[b] ?? b); });
                    if (k > 0) sb.Append(',');
                    Json.WriteString(sb, key);
                    sb.Append(":[");
                    for (int i = 0; i < values.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"value\":");
                        Json.WriteString(sb, values[i]);
                        sb.Append(",\"label\":");
                        if (l[values[i]] != null) Json.WriteString(sb, l[values[i]]);
                        else sb.Append("null");
                        sb.Append(",\"count\":").Append(c[values[i]]).Append('}');
                    }
                    sb.Append(']');
                }
                return sb.Append('}').ToString();
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The collections, as the app reads them from the archive: [{ id, name, created,
        // device, search, games: [ids], excluded: [ids] }]. What each holds is worked out
        // here: the games named in it, and those its search finds but for the ones taken out.

        sealed class Collection
        {
            public string Id, Name, Created, Device;
            public IDictionary Search;
            public string[] Games, Excluded;
            public long Version = -1;
            public int[] Members;
            public int Missing;
        }
        static List<Collection> collections = new List<Collection>();
        static readonly object collectionsLock = new object();

        static string[] StringsOf(IDictionary d, string key)
        {
            var list = new List<string>();
            var e = d.Contains(key) ? Unwrap(d[key]) as IEnumerable : null;
            if (e != null && !(e is string)) foreach (object o in e) if (o != null) list.Add(Unwrap(o).ToString());
            return list.ToArray();
        }

        public static void SetCollections(IList views)
        {
            var list = new List<Collection>();
            if (views != null)
            {
                foreach (object o in views)
                {
                    var d = Unwrap(o) as IDictionary;
                    if (d == null) continue;
                    list.Add(new Collection
                    {
                        Id = LibraryShape.Str(d, "id"), Name = LibraryShape.Str(d, "name"), Created = LibraryShape.Str(d, "created"), Device = LibraryShape.Str(d, "device"),
                        Search = d.Contains("search") ? Unwrap(d["search"]) as IDictionary : null, Games = StringsOf(d, "games"), Excluded = StringsOf(d, "excluded"),
                    });
                }
            }
            lock (collectionsLock) collections = list;
        }

        // What a collection holds now, in the library's order; under the gate
        static Collection Members(Snapshot snap, string id)
        {
            Collection c;
            lock (collectionsLock) c = collections.Find(x => x.Id == id);
            if (c == null) return null;
            lock (c)
            {
                if (c.Version == Version && c.Members != null) return c;
                var ids = new HashSet<string>(c.Games, StringComparer.Ordinal);
                if (c.Search != null)
                {
                    var excluded = new HashSet<string>(c.Excluded, StringComparer.Ordinal);
                    var x = Searchable(snap);
                    int l, r;
                    foreach (int n in Run(snap, x, QueryOf(x, c.Search), "all", out l, out r)) if (!excluded.Contains(snap.Games[n].Id)) ids.Add(snap.Games[n].Id);
                }
                var members = new List<int>();
                int missing = 0;
                foreach (string gid in ids)
                {
                    LibGame g;
                    if (snap.ById.TryGetValue(gid, out g) && g.Status != LibStatus.Gone) members.Add(g.Index);
                    else missing++;
                }
                members.Sort();
                c.Members = members.ToArray();
                c.Missing = missing;
                c.Version = Version;
                return c;
            }
        }

        static HashSet<int> MemberSet(Snapshot snap, string id)
        {
            var c = Members(snap, id);
            return c == null ? new HashSet<int>() : new HashSet<int>(c.Members);
        }

        // Each collection with what it holds: { version, collections: [{ id, name, created,
        // device, search, games, local, install, installSize, unarchived, unarchivedSize,
        // missing, platforms, image }] }, image being the screenshot of the first that has
        // one, as JSON
        public static string CollectionsSummary()
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                List<Collection> list;
                lock (collectionsLock) list = new List<Collection>(collections);
                var union = new HashSet<int>();
                foreach (var x in list) { var m = Members(snap, x.Id); if (m != null) union.UnionWith(m.Members); }
                int unionLocal = 0;
                foreach (int n in union) if (snap.Games[n].Local != null) unionLocal++;
                var sb = new StringBuilder();
                sb.Append("{\"version\":").Append(Version).Append(",\"games\":").Append(union.Count).Append(",\"local\":").Append(unionLocal).Append(",\"collections\":[");
                for (int i = 0; i < list.Count; i++)
                {
                    var c = Members(snap, list[i].Id);
                    int local = 0, install = 0, unarchived = 0;
                    long installSize = 0, unarchivedSize = 0;
                    var platforms = new HashSet<int>();
                    var names = new List<string>();
                    string image = null;
                    foreach (int n in c.Members)
                    {
                        var g = snap.Games[n];
                        var s = snap.Systems[g.System];
                        if (platforms.Add(g.System)) names.Add(s.FullName);
                        if (g.Local != null) local++;
                        if (g.Local == null && s.Known) { install++; installSize += g.SizeOf; }
                        if (g.Status == LibStatus.Local) { unarchived++; unarchivedSize += g.SizeOf; }
                        if (image == null) image = Shown(g, 1, "screenshots") ?? Shown(g, 2, "titlescreens");
                    }
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":");
                    Json.WriteString(sb, c.Id);
                    sb.Append(",\"name\":");
                    Json.WriteString(sb, c.Name);
                    sb.Append(",\"created\":");
                    Json.WriteString(sb, c.Created);
                    sb.Append(",\"device\":");
                    Json.WriteString(sb, c.Device);
                    sb.Append(",\"search\":").Append(c.Search == null ? "null" : Json.Write(c.Search));
                    sb.Append(",\"games\":").Append(c.Members.Length).Append(",\"local\":").Append(local).Append(",\"install\":").Append(install)
                      .Append(",\"installSize\":").Append(installSize).Append(",\"unarchived\":").Append(unarchived).Append(",\"unarchivedSize\":").Append(unarchivedSize)
                      .Append(",\"missing\":").Append(c.Missing).Append(",\"platforms\":[");
                    for (int k = 0; k < names.Count; k++) { if (k > 0) sb.Append(','); Json.WriteString(sb, names[k]); }
                    sb.Append("],\"image\":");
                    if (image != null) Json.WriteString(sb, image);
                    else sb.Append("null");
                    sb.Append('}');
                }
                return sb.Append("]}").ToString();
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // A page of a collection's games, kept to where (all, local, remote), in the order
        // chosen, as List gives a platform's
        public static string CollectionList(string id, string where, string sort, int from, int count, string at)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var c = Members(snap, id);
                var list = new List<int>();
                if (c != null) foreach (int n in c.Members) if (In(snap.Games[n], where)) list.Add(n);
                return Page(snap, Sorted(snap, list, sort), from, count, at);
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // The ids of a collection's games, kept to where
        public static List<string> CollectionIds(string id, string where)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var c = Members(snap, id);
                var ids = new List<string>();
                if (c != null) foreach (int n in c.Members) if (In(snap.Games[n], where)) ids.Add(snap.Games[n].Id);
                return ids;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // Whether each of a few games is in a collection, by id
        public static HashSet<string> InCollection(string id, IList ids)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                var set = MemberSet(snap, id);
                var inside = new HashSet<string>(StringComparer.Ordinal);
                foreach (object o in ids)
                {
                    LibGame g;
                    if (o != null && snap.ById.TryGetValue(Unwrap(o).ToString(), out g) && set.Contains(g.Index)) inside.Add(g.Id);
                }
                return inside;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }

        // Of ids, whether each game is in ES-DE as the library has it now: 1 it is, 0 it isn't,
        // -1 the library doesn't know it (or hasn't been read yet)
        public static sbyte[] LocalOf(IList<string> ids)
        {
            var found = new sbyte[ids.Count];
            gate.EnterReadLock();
            try
            {
                var snap = current;
                for (int i = 0; i < found.Length; i++)
                {
                    LibGame g;
                    if (ids[i] == null || !snap.ById.TryGetValue(ids[i], out g) || g.Status == LibStatus.Gone) found[i] = -1;
                    else found[i] = (sbyte)(g.Local != null ? 1 : 0);
                }
            }
            finally
            {
                gate.ExitReadLock();
            }
            return found;
        }

        // How many of games are in the archive and in a collection, which removing them from
        // the archive takes out of every computer's collections
        public static int ArchivedInCollections(IList<LibGame> games)
        {
            var ids = new List<object>();
            foreach (var g in games) if (g != null && g.Archived != null) ids.Add(g.Id);
            return InAnyCollection(ids);
        }

        // Whether a game is in any collection, for those of a few
        public static int InAnyCollection(IList ids)
        {
            gate.EnterReadLock();
            try
            {
                var snap = current;
                List<Collection> list;
                lock (collectionsLock) list = new List<Collection>(collections);
                var sets = list.ConvertAll(c => MemberSet(snap, c.Id));
                int kept = 0;
                foreach (object o in ids)
                {
                    LibGame g;
                    if (o == null || !snap.ById.TryGetValue(Unwrap(o).ToString(), out g)) continue;
                    if (sets.Exists(s => s.Contains(g.Index))) kept++;
                }
                return kept;
            }
            finally
            {
                gate.ExitReadLock();
            }
        }
    }

    // The transfers waiting, one after the other: each game once per transfer, however many
    // are asked for at once
    public static class JobQueue
    {
        static readonly LinkedList<Hashtable> queue = new LinkedList<Hashtable>();
        static readonly Dictionary<string, LinkedListNode<Hashtable>> byKey = new Dictionary<string, LinkedListNode<Hashtable>>(StringComparer.Ordinal);
        static readonly Dictionary<string, int> waiting = new Dictionary<string, int>(StringComparer.Ordinal);

        public static int Count { get { lock (queue) return queue.Count; } }

        // Adds a transfer for each game that can have it, with its name and size; returns
        // how many were added
        public static int Add(string action, List<LibGame> games, bool force)
        {
            int added = 0;
            lock (queue)
            {
                foreach (var g in games)
                {
                    if (!Library.Can(g, action)) continue;
                    string key = action + "|" + g.Id;
                    if (byKey.ContainsKey(key)) continue;
                    var job = new Hashtable
                    {
                        { "action", action }, { "id", g.Id }, { "rel", g.Rel }, { "name", g.Name }, { "size", g.SizeOf }, { "force", force },
                        { "system", g.Id.Substring(0, g.Id.Length - g.Rel.Length - 1) }, { "step", null }, { "fraction", 0.0 },
                    };
                    byKey[key] = queue.AddLast(job);
                    int c;
                    waiting.TryGetValue(g.Id, out c);
                    waiting[g.Id] = c + 1;
                    added++;
                }
            }
            return added;
        }

        // The next transfer, taken out of the queue, or null
        public static Hashtable Take()
        {
            lock (queue)
            {
                if (queue.Count == 0) return null;
                var job = queue.First.Value;
                queue.RemoveFirst();
                Forget(job);
                return job;
            }
        }

        static void Forget(Hashtable job)
        {
            string id = (string)job["id"];
            byKey.Remove((string)job["action"] + "|" + id);
            int c;
            if (waiting.TryGetValue(id, out c)) { if (c <= 1) waiting.Remove(id); else waiting[id] = c - 1; }
        }

        // Empties the queue; returns how many were waiting
        public static int Clear()
        {
            lock (queue)
            {
                int n = queue.Count;
                queue.Clear();
                byKey.Clear();
                waiting.Clear();
                return n;
            }
        }

        // The first transfers waiting
        public static ArrayList First(int count)
        {
            lock (queue)
            {
                var list = new ArrayList();
                foreach (var job in queue)
                {
                    if (list.Count >= count) break;
                    list.Add(job);
                }
                return list;
            }
        }

        // Which of a few games wait for a transfer
        public static ArrayList Waiting(IList ids)
        {
            var list = new ArrayList();
            if (ids == null) return list;
            lock (queue)
            {
                foreach (object o in ids)
                {
                    string id = o == null ? null : LibraryShape.Unwrap(o).ToString();
                    if (id != null && waiting.ContainsKey(id)) list.Add(id);
                }
            }
            return list;
        }
    }

    // ======== Catalogue's collections ========
    // What catalogue/scripts/windows/collections.ps1 does with every game of a collection,
    // done here: a collection, or a search kept up to date, can hold hundreds of thousands
    // of games.

    // The games of a collection as its file lists them, { system, path, name }, in their
    // order, each once by its key "<system>/<path>": read as Read-Collections read them,
    // the path with forward slashes and without "./"
    public sealed class CollectionGames
    {
        sealed class Entry
        {
            public string Key, System, Path, Name;
        }
        readonly List<Entry> list;
        readonly HashSet<string> keys;

        public CollectionGames()
        {
            list = new List<Entry>();
            keys = new HashSet<string>(StringComparer.Ordinal);
        }

        CollectionGames(CollectionGames of)
        {
            list = new List<Entry>(of.list);
            keys = new HashSet<string>(of.keys, StringComparer.Ordinal);
        }

        // A copy to change, the games' entries shared
        public CollectionGames Clone() { return new CollectionGames(this); }

        public int Count { get { return list.Count; } }
        public bool Contains(string key) { return key != null && keys.Contains(key); }

        public string[] Keys()
        {
            var a = new string[list.Count];
            for (int i = 0; i < a.Length; i++) a[i] = list[i].Key;
            return a;
        }

        public static CollectionGames From(object games)
        {
            var c = new CollectionGames();
            c.Add(games);
            return c;
        }

        static object Unwrap(object v) { var ps = v as PSObject; return ps != null ? ps.BaseObject : v; }

        static string Text(IDictionary d, string key)
        {
            object v = d.Contains(key) ? Unwrap(d[key]) : null;
            return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        // A list's items; one value alone, as @() made it in PowerShell, is a list of one
        static IEnumerable Items(object list)
        {
            var x = Unwrap(list);
            if (x == null) return new object[0];
            if (x is string || x is IDictionary || x is LibGame || !(x is IEnumerable)) return new object[] { x };
            return (IEnumerable)x;
        }

        // An entry { system, path, name } or a game of the library, as an entry; false for
        // one without a system or a path
        static bool EntryOf(object o, out string system, out string path, out string name)
        {
            var x = Unwrap(o);
            var g = x as LibGame;
            if (g != null)
            {
                system = g.SystemName ?? "";
                path = g.Rel ?? "";
                name = g.Name ?? "";
            }
            else
            {
                system = path = name = "";
                var d = x as IDictionary;
                if (d == null) return false;
                system = Text(d, "system");
                path = Text(d, "path").Replace('\\', '/');
                if (path.StartsWith("./", StringComparison.Ordinal)) path = path.Substring(2);
                name = Text(d, "name");
            }
            return system.Length > 0 && path.Length > 0;
        }

        // Adds the games not in it yet, given as entries or games of the library; tells
        // whether any was added
        public bool Add(object games)
        {
            bool added = false;
            foreach (object o in Items(games))
            {
                string system, path, name;
                if (!EntryOf(o, out system, out path, out name)) continue;
                string key = system + "/" + path;
                if (!keys.Add(key)) continue;
                list.Add(new Entry { Key = key, System = system, Path = path, Name = name });
                added = true;
            }
            return added;
        }

        // Takes out the games given by key, as entries or as games of the library; tells
        // whether any was there
        public bool Remove(object games)
        {
            var drop = new HashSet<string>(StringComparer.Ordinal);
            foreach (object o in Items(games))
            {
                var x = Unwrap(o);
                if (x is string) { drop.Add((string)x); continue; }
                string system, path, name;
                if (EntryOf(x, out system, out path, out name)) drop.Add(system + "/" + path);
                else if (x != null && !(x is IDictionary) && !(x is LibGame)) drop.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            }
            drop.IntersectWith(keys);
            if (drop.Count == 0) return false;
            list.RemoveAll(e => drop.Contains(e.Key));
            keys.ExceptWith(drop);
            return true;
        }

        // The games as the file lists them
        public List<object> ToJson()
        {
            var a = new List<object>(list.Count);
            foreach (var e in list) a.Add(new Dictionary<string, object> { { "system", e.System }, { "path", e.Path }, { "name", e.Name } });
            return a;
        }

        // Of ids, those in it (inside) or not, in their order
        public string[] Having(object ids, bool inside)
        {
            var found = new List<string>();
            foreach (object o in Items(ids))
            {
                var x = Unwrap(o);
                string id = x == null ? null : Convert.ToString(x, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(id)) continue;
                if (keys.Contains(id) == inside) found.Add(id);
            }
            return found.ToArray();
        }

        // Its keys whose game the library no longer has
        public string[] Missing()
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in Library.Pick(Keys(), null, null)) present.Add(g.Id);
            var gone = new List<string>();
            foreach (var e in list) if (!present.Contains(e.Key)) gone.Add(e.Key);
            return gone.ToArray();
        }

        // Of games, those whose id is among ids (inside) or not
        public static List<LibGame> Among(IEnumerable<LibGame> games, object ids, bool inside)
        {
            var set = new HashSet<string>(Strings(ids), StringComparer.Ordinal);
            var found = new List<LibGame>();
            foreach (var g in games) if (g != null && set.Contains(g.Id) == inside) found.Add(g);
            return found;
        }

        // Of games, those only in ES-DE, to be archived, but for the ids left out:
        // { games, size, media }, media by type ({ type: size })
        public static Hashtable OnlyInEsde(IEnumerable<LibGame> games, object leftOut)
        {
            var skip = new HashSet<string>(Strings(leftOut), StringComparer.Ordinal);
            var found = new List<LibGame>();
            long size = 0;
            var media = LibMedia.NoSizes();
            foreach (var g in games)
            {
                if (g == null || g.Status != LibStatus.Local || skip.Contains(g.Id)) continue;
                found.Add(g);
                size += g.SizeOf;
                LibMedia.AddTo(media, g.Local.Media);
            }
            return new Hashtable { { "games", found }, { "size", size }, { "media", LibMedia.ByType(media) } };
        }

        // What adding games to a collection does: of ids, those not in it yet (fresh); of
        // those, the ones its search finds (matched) that were taken out are let in again
        // (include), the others are added (add); those only in ES-DE are to be archived
        public static Hashtable AddPlan(string collection, object ids, object matched, CollectionGames excluded)
        {
            var members = new HashSet<string>(Library.CollectionIds(collection, "all"), StringComparer.Ordinal);
            var wanted = new List<object>();
            foreach (string id in Strings(ids)) if (!string.IsNullOrEmpty(id) && !members.Contains(id)) wanted.Add(id);
            var fresh = Library.Pick(wanted, null, null);
            var found = new HashSet<string>(Strings(matched), StringComparer.Ordinal);
            var include = new List<string>();
            var add = new List<LibGame>();
            var archive = OnlyInEsde(fresh, null);
            foreach (var g in fresh)
            {
                if (excluded.Contains(g.Id) && found.Contains(g.Id)) include.Add(g.Id);
                else add.Add(g);
            }
            return new Hashtable { { "fresh", fresh }, { "include", include.ToArray() }, { "add", add }, { "archive", archive["games"] }, { "archiveSize", archive["size"] }, { "archiveMedia", archive["media"] } };
        }

        // The games of a collection by key: those added by hand, then of found (what its
        // search found on this computer, or null) those nobody took out
        public static List<string> Members(CollectionGames games, CollectionGames excluded, object found)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in games.list) if (seen.Add(e.Key)) result.Add(e.Key);
            if (found == null) return result;
            foreach (object o in Items(found))
            {
                var x = Unwrap(o);
                string k = x == null ? "" : Convert.ToString(x, CultureInfo.InvariantCulture);
                if (k.Length > 0 && !excluded.Contains(k) && seen.Add(k)) result.Add(k);
            }
            return result;
        }

        // A list as strings: none for null
        public static string[] Strings(object list)
        {
            if (Unwrap(list) == null) return new string[0];
            var a = new List<string>();
            foreach (object o in Items(list))
            {
                var x = Unwrap(o);
                a.Add(x == null ? null : Convert.ToString(x, CultureInfo.InvariantCulture));
            }
            return a.ToArray();
        }

        // Whether two lists hold the same lines in any order, as Test-SameLines told: a
        // null list counts as one line of nothing
        public static bool SameLines(object a, object b)
        {
            var x = Unwrap(a) == null ? new string[] { null } : Strings(a);
            var y = Unwrap(b) == null ? new string[] { null } : Strings(b);
            if (x.Length != y.Length) return false;
            Array.Sort(x, StringComparer.Ordinal);
            Array.Sort(y, StringComparer.Ordinal);
            for (int i = 0; i < x.Length; i++) if (!string.Equals(x[i] ?? "", y[i] ?? "", StringComparison.Ordinal)) return false;
            return true;
        }
    }

    // The collection files of the archive as Read-Collections reads them, kept with the size
    // and date each had: a file is read again only once it changed, and one just written
    // is known without reading it (Wrote). Each reading gets games of its own to change.
    public static class CollectionFiles
    {
        sealed class Kept
        {
            public long Length, Ticks;
            public Dictionary<string, object> Data;
            public CollectionGames Games, Excluded;
        }
        static readonly Dictionary<string, Kept> kept = new Dictionary<string, Kept>(StringComparer.OrdinalIgnoreCase);

        static Hashtable Answer(Kept k)
        {
            return new Hashtable { { "data", k.Data }, { "games", k.Games.Clone() }, { "excluded", k.Excluded.Clone() } };
        }

        // { data: the file's JSON but its games, games, excluded }, or null for a file that
        // isn't a JSON object
        public static Hashtable Read(string file)
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            Kept k;
            lock (kept)
            {
                if (kept.TryGetValue(file, out k) && k.Length == info.Length && k.Ticks == info.LastWriteTimeUtc.Ticks) return Answer(k);
            }
            Dictionary<string, object> data;
            try { data = Json.Read(File.ReadAllText(file)) as Dictionary<string, object>; }
            catch (Exception) { return null; }
            if (data == null) return null;
            object games, excluded;
            data.TryGetValue("games", out games);
            data.TryGetValue("excluded", out excluded);
            k = new Kept { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Games = CollectionGames.From(games), Excluded = CollectionGames.From(excluded) };
            data.Remove("games");
            data.Remove("excluded");
            k.Data = data;
            lock (kept) kept[file] = k;
            return Answer(k);
        }

        // A file just written: what reading it gives is data (its JSON but its games, as
        // text), games and excluded (null when it has none)
        public static void Wrote(string file, CollectionGames games, CollectionGames excluded, string data)
        {
            var info = new FileInfo(file);
            var d = Json.Read(data) as Dictionary<string, object>;
            if (!info.Exists || d == null) return;
            var k = new Kept { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Data = d, Games = games.Clone(), Excluded = excluded == null ? new CollectionGames() : excluded.Clone() };
            lock (kept) kept[file] = k;
        }
    }

    // The paths of ES-DE's custom collections: a line of a custom-<name>.cfg names a game by
    // its full path, its system's folder as es_systems.xml has it, %ROMPATH% for the ROM
    // directory; full paths are compared ignoring case. Made from Open-Esde's systems:
    // name -> [folder, path in es_systems.xml, full name].
    public sealed class EsdePaths
    {
        readonly Dictionary<string, string[]> systems = new Dictionary<string, string[]>(StringComparer.Ordinal);
        readonly List<KeyValuePair<string, string>> byDir = new List<KeyValuePair<string, string>>();
        readonly string romDir, tilde, appDir;

        public EsdePaths(IDictionary systemsByName, string romDir, string tilde, string appDir)
        {
            foreach (DictionaryEntry pair in systemsByName)
            {
                var v = LibraryShape.Unwrap(pair.Value) as IList;
                var s = new string[3];
                for (int i = 0; i < 3; i++)
                {
                    object x = v != null && i < v.Count ? LibraryShape.Unwrap(v[i]) : null;
                    s[i] = x == null ? "" : x.ToString();
                }
                string name = pair.Key.ToString();
                systems[name] = s;
                if (s[0].Length > 0) byDir.Add(new KeyValuePair<string, string>(s[0].TrimEnd('\\') + "\\", name));
            }
            // The longest first, so that a folder inside another's finds its own system
            byDir.Sort((a, b) => a.Key.Length != b.Key.Length ? b.Key.Length - a.Key.Length : string.CompareOrdinal(a.Value, b.Value));
            this.romDir = romDir ?? "";
            this.tilde = tilde ?? "";
            this.appDir = appDir ?? "";
        }

        // ~ and %ESPATH% as ES-DE reads them (Expand-EsdePath)
        public string Expand(string p)
        {
            if (p.StartsWith("~", StringComparison.Ordinal) && (p.Length == 1 || p[1] == '\\' || p[1] == '/')) p = tilde + p.Substring(1);
            int i = p.IndexOf("%ESPATH%", StringComparison.Ordinal);
            if (i >= 0) p = p.Substring(0, i) + appDir + p.Substring(i + 8);
            return p;
        }

        // The full path a line stands for, or null
        public string FullOfLine(string line)
        {
            if (line == null) return null;
            string p = line.Trim();
            if (p.Length == 0) return null;
            p = Expand(p.Replace("%ROMPATH%", romDir.TrimEnd('\\', '/')));
            try { return Path.GetFullPath(p.Replace('/', '\\')); }
            catch (Exception) { return null; }
        }

        // The full path of a game of a system, or null
        public string FullPath(string system, string rel)
        {
            string[] s;
            if (system == null || !systems.TryGetValue(system, out s) || s[0].Length == 0) return null;
            try { return Path.GetFullPath(Path.Combine(s[0], (rel ?? "").Replace('/', '\\'))); }
            catch (Exception) { return null; }
        }

        // A game's line in a custom collection, as ES-DE writes it
        public string Line(string system, string rel)
        {
            string[] s;
            string dir = system != null && systems.TryGetValue(system, out s) && s[1].Length > 0 ? s[1] : "%ROMPATH%/" + system;
            return dir.Replace('\\', '/').TrimEnd('/') + "/" + rel;
        }

        // The system and the path in its folder of a full path, or null
        public string[] GameOf(string full)
        {
            if (full == null) return null;
            foreach (var d in byDir)
                if (full.StartsWith(d.Key, StringComparison.OrdinalIgnoreCase)) return new[] { d.Value, full.Substring(d.Key.Length).Replace('\\', '/') };
            return null;
        }

        static bool Exists(string full) { return full != null && (File.Exists(full) || Directory.Exists(full)); }

        // The lines of the games of keys "<system>/<path>" that are in ES-DE, in their order:
        // those the library has in ES-DE, and of the others those the disk has (a download
        // just ended, say), the files looked at side by side
        public List<string> LinesOf(IList<string> keys)
        {
            var lines = new string[keys.Count];
            var local = Library.LocalOf(keys);
            Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) }, i =>
            {
                string key = keys[i];
                int slash = key == null ? -1 : key.IndexOf('/');
                if (slash < 0) return;
                string system = key.Substring(0, slash), rel = key.Substring(slash + 1);
                if (local[i] == 1 || Exists(FullPath(system, rel))) lines[i] = Line(system, rel);
            });
            var found = new List<string>();
            foreach (string l in lines) if (l != null) found.Add(l);
            return found;
        }

        // The lines of a custom-<name>.cfg, trimmed, with forward slashes, without empty
        // ones; null without the file
        public static string[] ReadLines(string file)
        {
            if (!File.Exists(file)) return null;
            var found = new List<string>();
            foreach (string line in Regex.Split(File.ReadAllText(file), @"\r?\n"))
            {
                string t = line.Trim();
                if (t.Length > 0) found.Add(t.Replace('\\', '/'));
            }
            return found.ToArray();
        }

        // Adds the full paths of lines to a set
        public void AddFulls(object lines, HashSet<string> set)
        {
            var list = CollectionGames.Strings(lines);
            var fulls = new string[list.Length];
            Parallel.For(0, list.Length, i => { fulls[i] = FullOfLine(list[i]); });
            foreach (string f in fulls) if (f != null) set.Add(f);
        }

        // What ES-DE's own editor changed in a collection, from the lines written before to
        // those it has now: the games added { system, path, name }, and those taken out that
        // are still there, with their keys (Sync-EsdeCollectionEdits)
        public Hashtable EditsOf(object before, object now)
        {
            var was = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddFulls(before, was);
            var nowLines = CollectionGames.Strings(now);
            var nowFulls = new string[nowLines.Length];
            Parallel.For(0, nowLines.Length, i => { nowFulls[i] = FullOfLine(nowLines[i]); });
            var isNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var add = new List<object>();
            foreach (string full in nowFulls)
            {
                if (full == null || !isNow.Add(full) || was.Contains(full)) continue;
                var game = GameOf(full);
                if (game != null) add.Add(EntryOf(game));
            }
            var remove = new List<object>();
            var keys = new List<string>();
            foreach (string full in was)
            {
                if (isNow.Contains(full) || !Exists(full)) continue;
                var game = GameOf(full);
                if (game == null) continue;
                remove.Add(EntryOf(game));
                keys.Add(game[0] + "/" + game[1]);
            }
            return new Hashtable { { "add", add }, { "remove", remove }, { "keys", keys.ToArray() } };
        }

        static Hashtable EntryOf(string[] game)
        {
            string file = game[1].Substring(game[1].LastIndexOf('/') + 1);
            return new Hashtable { { "system", game[0] }, { "path", game[1] }, { "name", Paths.BaseOf(file) } };
        }
    }

    // Older versions stored covers with a platform banner as the miximages of the games in
    // collections, the user's own miximages set aside in backupDir meanwhile. TakeOff takes
    // the banners out of the media folders, each known by the marker in its first bytes,
    // and puts back what was set aside; a miximage scraped since stays, as it would have
    // replaced the old one anyway. Returns how many banners went.
    public static class OldBanners
    {
        static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        static bool IsBanner(string file)
        {
            try
            {
                using (var f = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var head = new byte[96];
                    int n = f.Read(head, 0, head.Length);
                    return Latin1.GetString(head, 0, n).Contains("Atelier platform banner");
                }
            }
            catch (Exception) { return false; }
        }

        static void RemoveEmpty(string dir)
        {
            foreach (string sub in Directory.GetDirectories(dir)) RemoveEmpty(sub);
            bool empty;
            using (var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator()) empty = !entries.MoveNext();
            if (empty) Directory.Delete(dir);
        }

        public static int TakeOff(string mediaDir, string backupDir)
        {
            int removed = 0;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) };
            if (Directory.Exists(mediaDir))
            {
                foreach (string system in Directory.GetDirectories(mediaDir))
                {
                    string dir = Path.Combine(system, "miximages");
                    if (!Directory.Exists(dir)) continue;
                    Parallel.ForEach(Directory.EnumerateFiles(dir, "*.png", SearchOption.AllDirectories), options, file =>
                    {
                        if (!IsBanner(file)) return;
                        Files.Remove(file);
                        Interlocked.Increment(ref removed);
                    });
                    RemoveEmpty(dir);
                }
            }
            if (Directory.Exists(backupDir))
            {
                int root = backupDir.TrimEnd(Path.DirectorySeparatorChar).Length + 1;
                foreach (string saved in Directory.GetFiles(backupDir, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(mediaDir, saved.Substring(root));
                    if (File.Exists(target)) { Files.Remove(saved); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(saved, target);
                }
                RemoveEmpty(backupDir);
            }
            return removed;
        }
    }
}
