// What the linked Smart Tools code uses that .NET Framework 4.5 does not have. net10 already has all of it.
#if NET45
using System.Globalization;
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.All)]
    internal sealed class TupleElementNamesAttribute : Attribute
    {
        public TupleElementNamesAttribute(string?[] transformNames) { }
    }
}

namespace System
{
    // Own tuple types (arity 2-4): keeps the .NET Framework 4.5 build a single exe with no System.ValueTuple.dll next to it.
    internal struct ValueTuple<T1, T2> : IEquatable<ValueTuple<T1, T2>>
    {
        public T1 Item1; public T2 Item2;
        public ValueTuple(T1 item1, T2 item2) { Item1 = item1; Item2 = item2; }
        public bool Equals(ValueTuple<T1, T2> o) => EqualityComparer<T1>.Default.Equals(Item1, o.Item1) && EqualityComparer<T2>.Default.Equals(Item2, o.Item2);
        public override bool Equals(object? obj) => obj is ValueTuple<T1, T2> o && Equals(o);
        public override int GetHashCode() => (EqualityComparer<T1>.Default.GetHashCode(Item1!) * 397) ^ EqualityComparer<T2>.Default.GetHashCode(Item2!);
    }

    internal struct ValueTuple<T1, T2, T3> : IEquatable<ValueTuple<T1, T2, T3>>
    {
        public T1 Item1; public T2 Item2; public T3 Item3;
        public ValueTuple(T1 item1, T2 item2, T3 item3) { Item1 = item1; Item2 = item2; Item3 = item3; }
        public bool Equals(ValueTuple<T1, T2, T3> o) => EqualityComparer<T1>.Default.Equals(Item1, o.Item1) && EqualityComparer<T2>.Default.Equals(Item2, o.Item2) && EqualityComparer<T3>.Default.Equals(Item3, o.Item3);
        public override bool Equals(object? obj) => obj is ValueTuple<T1, T2, T3> o && Equals(o);
        public override int GetHashCode() => (((EqualityComparer<T1>.Default.GetHashCode(Item1!) * 397) ^ EqualityComparer<T2>.Default.GetHashCode(Item2!)) * 397) ^ EqualityComparer<T3>.Default.GetHashCode(Item3!);
    }

    internal struct ValueTuple<T1, T2, T3, T4>
    {
        public T1 Item1; public T2 Item2; public T3 Item3; public T4 Item4;
        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4) { Item1 = item1; Item2 = item2; Item3 = item3; Item4 = item4; }
    }
}

namespace System.Linq
{
    internal static class Net45Linq
    {
        public static IEnumerable<TSource> DistinctBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in source)
                if (seen.Add(keySelector(item))) yield return item;
        }
    }
}

namespace System
{
    /// <summary>Just enough of System.Index / System.Range for the a[..b] and a[x..] slices on strings and the list patterns.</summary>
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;
        public Index(int value, bool fromEnd = false) { _value = fromEnd ? ~value : value; }
        public static Index Start => new Index(0);
        public static Index End => new Index(~0);
        public static Index FromStart(int value) => new Index(value);
        public static Index FromEnd(int value) => new Index(~value);
        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;
        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
        public static implicit operator Index(int value) => FromStart(value);
        public bool Equals(Index other) => _value == other._value;
        public override bool Equals(object? obj) => obj is Index other && Equals(other);
        public override int GetHashCode() => _value;
    }

    internal readonly struct Range : IEquatable<Range>
    {
        public Index Start { get; }
        public Index End { get; }
        public Range(Index start, Index end) { Start = start; End = end; }
        public static Range StartAt(Index start) => new Range(start, Index.End);
        public static Range EndAt(Index end) => new Range(Index.Start, end);
        public static Range All => new Range(Index.Start, Index.End);
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length), end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end) throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
        public bool Equals(Range other) => Start.Equals(other.Start) && End.Equals(other.End);
        public override bool Equals(object? obj) => obj is Range other && Equals(other);
        public override int GetHashCode() => Start.GetHashCode() * 31 + End.GetHashCode();
    }

    internal static class Net45Extensions
    {
        public static HashSet<T> ToHashSet<T>(this IEnumerable<T> source, IEqualityComparer<T>? comparer = null) => new HashSet<T>(source, comparer);

        public static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value) where TKey : notnull
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }

        public static bool StartsWith(this string text, char c) => text.Length > 0 && text[0] == c;
        public static bool EndsWith(this string text, char c) => text.Length > 0 && text[text.Length - 1] == c;
        public static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
        public static string ReplaceLineEndings(this string text, string replacement) =>
            string.Join(replacement, text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        public static string Replace(this string text, string oldValue, string newValue, StringComparison comparison)
        {
            int index = text.IndexOf(oldValue, comparison);
            while (index >= 0)
            {
                text = text.Substring(0, index) + newValue + text.Substring(index + oldValue.Length);
                index = text.IndexOf(oldValue, index + newValue.Length, comparison);
            }
            return text;
        }
        public static string[] Split(this string text, char separator, StringSplitOptions options) => text.Split(new[] { separator }, options);
        public static string Join(this IEnumerable<string> source, string separator) => string.Join(separator, source);
        public static IEnumerable<T> Append<T>(this IEnumerable<T> source, T item) => source.Concat(new[] { item });
        public static Task WaitForExitAsync(this System.Diagnostics.Process process, CancellationToken token = default)
        {
            var done = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => done.TrySetResult(true);
            if (process.HasExited) done.TrySetResult(true);
            token.Register(() => done.TrySetCanceled());
            return done.Task;
        }
        public static IEnumerable<T> Prepend<T>(this IEnumerable<T> source, T item) => new[] { item }.Concat(source);
    }
}
#endif
