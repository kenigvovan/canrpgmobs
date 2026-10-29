using System;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Data
{
    public static class FormatMigrator
    {
        public const int CurrentVersion = 1;

        // Steps[i] upgrades version i + 1 to i + 2
        private static readonly Action<JObject>[] Steps = Array.Empty<Action<JObject>>();

        // false = file from a newer build, skip it
        public static bool Migrate(JObject file, string where, ValidationReport report)
        {
            var token = file["formatVersion"];
            int version = token?.Type == JTokenType.Integer ? token.Value<int>() : 0;
            if (version <= 0)
            {
                report.Info(where, $"no formatVersion, read as version {CurrentVersion}");
                version = CurrentVersion;
            }
            if (version > CurrentVersion)
            {
                report.Error(where, $"formatVersion {version} is newer than this build supports ({CurrentVersion}) - file skipped, update canrpgmobs");
                return false;
            }
            for (; version < CurrentVersion; version++) Steps[version - 1](file);
            file["formatVersion"] = CurrentVersion;
            return true;
        }

        // identity trees have no shape changes yet
        public static int MigrateIdentity(int version) => Math.Clamp(version, 1, CurrentVersion);
    }
}
