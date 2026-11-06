using System.Text;

namespace NetPulse.Unity.Generators
{
    public static class StableId
    {
        public static ushort Compute(string name)
        {
            var hash = 2166136261u;
            var bytes = Encoding.UTF8.GetBytes(name);

            for (var i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash = unchecked(hash * 16777619u);
            }

            return unchecked((ushort)(hash ^ (hash >> 16)));
        }
    }
}
