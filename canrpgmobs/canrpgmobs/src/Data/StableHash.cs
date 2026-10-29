namespace canrpgmobs.Data
{
    // FNV-1a, the same on every run unlike string.GetHashCode
    public static class StableHash
    {
        public static uint Of(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text) hash = (hash ^ c) * 16777619;
                return hash;
            }
        }
    }
}
