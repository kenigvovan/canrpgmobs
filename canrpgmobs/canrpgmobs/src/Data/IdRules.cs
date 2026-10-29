namespace canrpgmobs.Data
{
    // ids are domain:name, mob keys are race|role
    public static class IdRules
    {
        public const char KeySeparator = '|';

        // letters, digits and : _ - . only
        public static bool IsClean(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            foreach (char c in id!)
                if (!(char.IsLetterOrDigit(c) || c == ':' || c == '_' || c == '-' || c == '.')) return false;
            return true;
        }

        public static string Qualify(string id, string domain)
        {
            id = id.Trim();
            return id.Length == 0 || id.Contains(':') ? id : domain + ":" + id;
        }

        // "goblin|shaman" -> "dom:goblin|dom:shaman", a plain template id is qualified as is
        public static string QualifyKey(string key, string domain)
        {
            key = key.Trim();
            int sep = key.IndexOf(KeySeparator);
            if (sep < 0) return Qualify(key, domain);
            return Qualify(key.Substring(0, sep), domain) + KeySeparator + Qualify(key.Substring(sep + 1), domain);
        }
    }
}
