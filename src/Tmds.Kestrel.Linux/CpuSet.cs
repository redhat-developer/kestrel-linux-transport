using System;
using System.Collections.Generic;

namespace Tmds.Kestrel.Linux
{
    struct CpuSet
    {
        int[] _cpus;

        public int[] Cpus => _cpus;

        public bool IsDefault => _cpus == null;

        private CpuSet(int[] cpus)
        {
            _cpus = cpus;
        }

        public static bool TryParse(string set, out CpuSet cpus)
        {
            cpus = default(CpuSet);
            if (string.IsNullOrEmpty(set))
            {
                return true;
            }
            int index = 0;
            var cpuList = new List<int>();
            do
            {
                int start;
                if (!TryParseNumber(set, ref index, out start))
                {
                    return false;
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
                        return false;
                    }
                    if (start > end)
                    {
                        return false;
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
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            } while (index != set.Length);
            cpus = new CpuSet(cpuList.ToArray());
            return true;
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