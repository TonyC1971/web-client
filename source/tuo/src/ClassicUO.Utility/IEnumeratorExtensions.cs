using System.Collections.Generic;
using System.Linq;

namespace ClassicUO.Utility;

public static class IEnumeratorExtensions
{
    /// <summary>
    /// Try to convert an enumerable from int to uint
    /// </summary>
    /// <param name="enumerable"></param>
    /// <returns></returns>
    public static IEnumerable<uint> ToUint(this IEnumerable<int> enumerable)
    {
        if(enumerable == null) return [];

        int[] ints = enumerable as int[] ?? enumerable.ToArray();

        uint[] converted = new uint[ints.Length];

        for (int i = 0; i < ints.Length; i++) converted[i] = (uint)ints[i];

        return converted;
    }
}
