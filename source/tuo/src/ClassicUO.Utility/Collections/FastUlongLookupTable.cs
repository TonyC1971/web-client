using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ClassicUO.Utility.Collections;

/// <summary>
///     A fast lookup table that accepts ulong keys.
///     Instances of this class pre-allocate memory, depending on bias configuration.
///     <br />
///     <para>
///         For a typical nullable int value-type, a short biased allocation is ~124KiB and a long biased one is
///         ~94KiB
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         While <see cref="Dictionary{TKey,TValue}" /> is highly capable and usually delivers <em>O(1)</em>
///         time-complexity,
///         it makes use of some reflection
///         to broaden compatibility and does not make assumptions on the underlying data.
///     </para>
///     <para>
///         This data structure is explicitly tailored to the graphics sub-system and makes a few assumptions that allow it
///         to maintain a <em>O(1)</em> complexity whilst still omitting the abovementioned overheads.
///     </para>
///     <para>
///         To eke out the most performance possible, this class does not attempt to provide proper dictionary semantics,
///         instead opting for an opaque Get/Set
///         and delegating key management for the caller.
///     </para>
/// </remarks>
/// <typeparam name="T">The data type to be indexed</typeparam>
public class FastUlongLookupTable<T>
{
    // All, or close to all keys in the short-biased flows are expected to land in a 14 bit range or so.
    // By trading a tiny bit of lookup performance for the outliers, we can significantly reduce allocation size.
    private const ushort SHORT_BIAS_FAST_LOOKUP_CAPACITY = 16384;
    private const ushort LONG_BIAS_FAST_LOOKUP_CAPACITY = 16;

    // When the bias is for short keys (key <= SHORT_BIAS_FAST_LOOKUP_CAPACITY)
    private const ushort SHORT_BIAS_DICT_INIT_CAPACITY = 16;
    private const ushort LONG_BIAS_DICT_INIT_CAPACITY = 4096;

    private readonly bool _shortBiased;

    /// <summary>
    ///     A pre-allocated O(1) lookup array
    /// </summary>
    private readonly T[] _fastLookup;

    /// <summary>
    ///     A pre-allocated dictionary to fall back to if a key exceeds the fast lookup table
    /// </summary>
    private readonly Dictionary<ulong, T> _slowDict;

    /// <summary>
    ///     Determines whether a given key can be indexed by the fast lookup table
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsFastFlowSized(ulong key) =>
        key < (_shortBiased ? SHORT_BIAS_FAST_LOOKUP_CAPACITY : LONG_BIAS_FAST_LOOKUP_CAPACITY);


    /// <summary>
    ///     Constructs a new, pre-allocated lookup table
    /// </summary>
    /// <param name="shortBiased">
    ///     Determines whether the lookup table will be biased for <see cref="ushort" /> or <see cref="ulong" /> keys.
    ///     <br />
    ///     Bias significantly affects performance - short bias implies most keys are expected to be smaller than
    ///     <em>16384</em>
    ///     and can be directly accessed via an array index.
    ///     <para>
    ///         Setting <paramref name="shortBiased" /> to <em>false</em> means most operations will be done against the
    ///         internal <see cref="Dictionary{TKey,TValue}" />
    ///         which is slower
    ///     </para>
    /// </param>
    public FastUlongLookupTable(bool shortBiased)
    {
        _shortBiased = shortBiased;

        if (shortBiased)
        {
            _fastLookup = new T[SHORT_BIAS_FAST_LOOKUP_CAPACITY];
            _slowDict = new Dictionary<ulong, T>(SHORT_BIAS_DICT_INIT_CAPACITY);
        }
        else
        {
            _fastLookup = new T[LONG_BIAS_FAST_LOOKUP_CAPACITY];
            _slowDict = new Dictionary<ulong, T>(LONG_BIAS_DICT_INIT_CAPACITY);
        }
    }

    /// <summary>
    ///     Gets the value indexed by the key
    /// </summary>
    /// <param name="key">The key to get the value of</param>
    /// <returns>The requested value or default <see cref="T" />, if one does not exist</returns>
    public T Get(ulong key)
    {
        if (IsFastFlowSized(key))
            return _fastLookup[(int)key];

        _slowDict.TryGetValue(key, out T value);
        return value;
    }

    /// <summary>
    ///     Sets the value for a given key, overwriting any previously existing value
    /// </summary>
    /// <param name="key">The key to store the value under</param>
    /// <param name="value">The value to store</param>
    public void Set(ulong key, T value)
    {
        if (IsFastFlowSized(key))
            _fastLookup[(int)key] = value;
        else
            _slowDict[key] = value;
    }

    /// <summary>
    ///     Removes the given key from the lookup table
    /// </summary>
    /// <param name="key"></param>
    public void Remove(ulong key)
    {
        if (IsFastFlowSized(key))
            _fastLookup[(int)key] = default;
        else
            _slowDict.Remove(key);
    }

    /// <summary>
    ///     Clears the lookup table
    /// </summary>
    public void Clear()
    {
        Array.Clear(_fastLookup);
        _slowDict.Clear();
    }
}
