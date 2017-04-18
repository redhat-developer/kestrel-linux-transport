using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Tmds.Kestrel.Linux
{
    [TypeConverter(typeof(CpuSetTypeConverter))]
    public struct CpuSet
    {
        int[] _cpus;

        public int[] Cpus => _cpus ?? Array.Empty<int>();

        public bool IsEmpty => _cpus == null || _cpus.Length == 0;

        private CpuSet(int[] cpus)
        {
            _cpus = cpus;
        }

        private static bool ParseFailed(bool tryParse, string error)
        {
            if (tryParse)
            {
                return false;
            }
            throw new FormatException(error);
        }

        public static bool Parse(string set, out CpuSet cpus, bool tryParse)
        {
            cpus = default(CpuSet);
            if (set == null)
            {
                if (tryParse)
                {
                    return false;
                }
                throw new ArgumentNullException(nameof(set));
            }
            if (set.Length == 0)
            {
                cpus = new CpuSet(Array.Empty<int>());
                return true;
            }
            int index = 0;
            var cpuList = new List<int>();
            do
            {
                int start;
                if (!TryParseNumber(set, ref index, out start))
                {
                    return ParseFailed(tryParse, $"Can not parse number at {index}");
                }
                if (index == set.Length)
                {
                    cpuList.Add(start);
                    break;
                }
                else if (set[index] == ',')
                {
                    cpuList.Add(start);
                    index++;
                    continue;
                }
                else if (set[index] == '-')
                {
                    index++;
                    int end;
                    if (!TryParseNumber(set, ref index, out end))
                    {
                        return ParseFailed(tryParse, $"Can not parse number at {index}");
                    }
                    if (start > end)
                    {
                        return ParseFailed(tryParse, "End of range is larger than start");
                    }
                    for (int i = start; i <= end; i++)
                    {
                        cpuList.Add(i);
                    }
                    if (index == set.Length)
                    {
                        break;
                    }
                    else if (set[index] == ',')
                    {
                        index++;
                        continue;
                    }
                    else
                    {
                        return ParseFailed(tryParse, $"Invalid character at {index}: '{set[index]}'");
                    }
                }
                else
                {
                    return ParseFailed(tryParse, $"Invalid character at {index}: '{set[index]}'");
                }
            } while (index != set.Length);
            var cpuArray = cpuList.ToArray();
            Array.Sort(cpuArray);
            cpus = new CpuSet(cpuArray);
            return true;
        }

        public static bool TryParse(string set, out CpuSet cpus)
        {
            return Parse(set, out cpus, tryParse: true);
        }

        public static CpuSet Parse(string set)
        {
            CpuSet cpus;
            Parse(set, out cpus, tryParse: false);
            return cpus;
        }

        private static bool TryParseNumber(string s, ref int index, out int value)
        {
            if (index == s.Length)
            {
                value = 0;
                return false;
            }
            int startIndex = index;
            while (index < s.Length && Char.IsDigit(s[index])) { index++; }
            return int.TryParse(s.Substring(startIndex, index - startIndex), out value);
        }

        public override string ToString()
        {
            return string.Join(",", _cpus);
        }
    }
}