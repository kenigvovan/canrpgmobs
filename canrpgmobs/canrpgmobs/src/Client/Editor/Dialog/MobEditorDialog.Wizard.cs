using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using canrpgmobs.Net;
using canrpgmobs.Runtime.Brain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using static canrpgmobs.Client.Editor.Controllers.JsonDraft;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Step by step mob creation. Reuses an existing race and role when picked, so only the mob itself
    // (and whatever is really new) goes into the bundle.
    public partial class MobEditorDialog
    {
        private enum WizardStep { Who, Fight, Strength, Gear, Loot, Done }

        private sealed class WizardDraft
        {
            public WizardStep Step;
            public WizardStep Reached;
            // who: an existing race, or a new one
            public string? RaceId;
            public string RaceNameEn = "";
            public string Body = MobBodies.Humanoid;
            public string Model = AppearanceModel.Seraph;
            public string Shape = "";
            public string Texture = "";
            public string Faction = FactionMatrix.Monsters;
            // fight: null role = the shared role of this behavior, made if missing
            public string Behavior = "warrior";
            public string? RoleId;
            // strength
            public int LevelMin = 1;
            public int LevelMax = 3;
            public string Difficulty = "normal";
            public string Pace = "normal";
            public bool Exact;
            // typed by hand, presets leave them alone
            public bool StatsTouched;
            public float Health;
            public float Damage;
            public float Speed;
            // gear and skills
            public string RightHand = "";
            public string LeftHand = "";
            public readonly List<string> Gear = new();
            public readonly string[] Spells = { "", "", "" };
            // off = skillsMode replace, the race's and role's skills are dropped
            public bool InheritSkills = true;
            // loot
            public readonly List<string> Loot = new();
            public readonly List<(string Code, bool Block, int Chance)> QuickLoot = new();
            // done
            public string NameEn = "";
            public string Id = "";
            public bool IdTouched;
            public bool ShowId;
            public string Zone = "";
        }

        private sealed record WizardRole(string Code, string Archetype, float Health, float Damage, float Speed, float MinRange, float MaxRange,
            bool HealsFirst, string? Tier, bool Boss);

        private static readonly WizardRole[] WizardRoles =
        {
            new("warrior", "meleeBrute", 30f, 4f, 0.03f, 0f, 4f, false, null, false),
            new("archer", "rangedArcher", 18f, 2.5f, 0.032f, 4f, 18f, false, null, false),
            new("ranged", "rangedKiter", 18f, 2f, 0.03f, 4f, 18f, false, null, false),
            new("healer", "support", 20f, 2f, 0.03f, 0f, 14f, true, null, false),
            new("coward", "cowardLooter", 14f, 2.5f, 0.035f, 0f, 4f, false, null, false),
            new("boss", "meleeBrute", 120f, 8f, 0.028f, 0f, 6f, false, "canrpgmobs:boss", true)
        };

        private static readonly (string Code, float Mul)[] WizardDifficulties = { ("weak", 0.6f), ("normal", 1f), ("tough", 1.5f), ("elite", 2.5f) };
        private static readonly (string Code, float Speed)[] WizardPaces = { ("slow", 0.025f), ("normal", 0.03f), ("fast", 0.036f) };
        private const string WizardBow = "game:bow-simple";
        private const string WizardArrow = "game:arrow-flint";

        private WizardDraft? wizard;
        // bumped on every wizard change, the preview watches it
        private int wizardVersion;
        private enum BundleKind { Wizard, Copy }

        // bundle (wizard or copy) waiting for the server reply
        private readonly record struct PendingBundle(BundleKind Kind, string Id, string? DoneMessage, bool Preview = false, string? Zone = null);

        private PendingBundle? pendingBundle;

        private WizardDraft NewWizard()
        {
            var draft = new WizardDraft();
            // most likely an existing people, the first one by name
            draft.RaceId = mod.Catalog.Races.OrderBy(r => NameOf("races", r.Key), StringComparer.CurrentCulture).Select(r => r.Key).FirstOrDefault();
            SetBehavior(draft, "warrior");
            return draft;
        }

        private WizardRole BehaviorOf(WizardDraft w) => WizardRoles.FirstOrDefault(r => r.Code == w.Behavior) ?? WizardRoles[0];

        private void SetBehavior(WizardDraft w, string code)
        {
            w.Behavior = code;
            var role = BehaviorOf(w);
            var existing = RolesForArchetype(role.Archetype);
            string shared = Q("role_" + code);
            w.RoleId = existing.Contains(shared) ? shared : existing.FirstOrDefault();
            if (code == "archer" && w.RightHand.Length == 0 && w.Body != MobBodies.Creature) w.RightHand = WizardBow;
            WizardStats(w);
            wizardVersion++;
        }

        private void WizardStats(WizardDraft w)
        {
            if (w.StatsTouched) return;
            var role = BehaviorOf(w);
            float mul = WizardDifficulties.FirstOrDefault(d => d.Code == w.Difficulty).Mul;
            if (mul <= 0) mul = 1f;
            w.Health = (float)Math.Round(role.Health * mul, 1);
            w.Damage = (float)Math.Round(role.Damage * mul, 2);
            w.Speed = w.Pace == "normal" ? role.Speed : WizardPaces.FirstOrDefault(p => p.Code == w.Pace).Speed;
        }

        // roles whose AI profile has this archetype; no profile = the default archetype
        private List<string> RolesForArchetype(string archetype)
        {
            var data = mod.Catalog.Data;
            return data.Roles
                .Where(r => (r.Value.aiProfile != null && data.AiProfiles.TryGetValue(r.Value.aiProfile, out var ai) ? ai.archetype : MobValidator.DefaultArchetype) == archetype)
                .Select(r => r.Key).OrderBy(k => NameOf("roles", k), StringComparer.CurrentCulture).ToList();
        }

        private int MobsUsingRace(string raceId) => mod.Catalog.Templates.Values.Count(t => t.race == raceId);

        private string WizardTemplateId(WizardDraft w)
        {
            if (!w.IdTouched) w.Id = UniqueSlug(w.NameEn.Length > 0 ? w.NameEn : RaceNameFor(w), "mob", id => EntryIds("mobs").Contains(Q(id)));
            return Q(w.Id);
        }

        private string RaceNameFor(WizardDraft w) => w.RaceId != null ? NameOf("races", w.RaceId) : w.RaceNameEn;

        private string UniqueSlug(string name, string fallback, Predicate<string> taken)
        {
            var text = new StringBuilder();
            foreach (char c in name.ToLowerInvariant())
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9') text.Append(c);
                else if ((c == ' ' || c == '-' || c == '_') && text.Length > 0 && text[text.Length - 1] != '_') text.Append('_');
            }
            string baseId = text.ToString().Trim('_');
            // cyrillic names give nothing, number the fallback instead
            if (baseId.Length == 0) baseId = fallback;
            string id = baseId;
            for (int n = 2; taken(id); n++) id = baseId + n;
            return id;
        }

        private bool OnWizardCreate(bool preview)
        {
            ClearPending();
            if (Busy()) return true;
            var w = wizard!;
            if (!CheckWizard(out var bundle, out string templateId))
            {
                Compose();
                return true;
            }
            mod.SendBundle(bundle.ToString(Formatting.None));
            awaitingResult = true;
            pendingBundle = new PendingBundle(BundleKind.Wizard, templateId, null, preview, w.Zone.Length > 0 ? w.Zone : null);
            SetStatus(L("canrpgmobs:editor-sending", "Sent, waiting for the server..."), false);
            Compose();
            return true;
        }

        private void FinishWizard(CatalogEditResultPacket packet, PendingBundle bundle)
        {
            string id = bundle.Id;
            string? copied = bundle.Kind == BundleKind.Copy ? bundle.DoneMessage : null;
            pendingBundle = null;
            if (!packet.Ok)
            {
                SetStatus(L("canrpgmobs:wizard-failed", "Not created: {0}", packet.Message), true);
                return;
            }

            if (copied == null) wizard = NewWizard();
            simpleMode = true;
            SwitchSection("mobs");
            OpenEntry("mobs", id);
            previewKey = id;
            if (copied != null)
            {
                SetStatus(copied, false);
                return;
            }
            if (bundle.Zone != null) AddMobToZone(bundle.Zone, id);
            if (bundle.Preview) mod.RequestPreview(id, 0, "");
            SetStatus(bundle.Zone != null
                ? L("canrpgmobs:wiz-created-zone", "Created {0} and added it to zone {1}.", Short(id), Short(bundle.Zone))
                : L("canrpgmobs:wiz-created", "Created {0}. Everything stays editable here.", Short(id)), false);
        }

        // a separate edit of the zone; its reply only goes to the chat
        private void AddMobToZone(string zoneId, string mobId)
        {
            var zone = mod.Store.Get("zones", zoneId)?.DeepClone() as JObject
                       ?? (mod.Catalog.Zones.TryGetValue(zoneId, out var model) ? JObject.FromObject(model, Serializer) : null);
            if (zone == null) return;
            EnsureArray(zone, "spawns").Add(new JObject { ["mob"] = mobId, ["weight"] = 10 });
            mod.SendEdit("zones", zoneId, null, zone.ToString(Formatting.None), "wizard");
        }

        // reason for failure goes to the status line
        private bool CheckWizard(out JObject bundle, out string templateId)
        {
            var w = wizard ??= NewWizard();
            templateId = WizardTemplateId(w);
            bundle = BuildWizardBundle(w, templateId, out var newIds);

            if (w.NameEn.Length == 0)
                return WizardRefuse(L("canrpgmobs:wizard-need-name", "Give the mob a name."));
            if (!IdRules.IsClean(w.Id))
                return WizardRefuse(L("canrpgmobs:wizard-bad-id", "The id may only use letters, digits and : _ - ."));
            if (w.RaceId == null && w.RaceNameEn.Length == 0)
                return WizardRefuse(L("canrpgmobs:wiz-need-race-name", "Name the new race on the first step."));
            if (w.RaceId == null && w.Body == MobBodies.Creature && w.Shape.Length == 0)
                return WizardRefuse(L("canrpgmobs:wizard-need-shape", "A creature needs a shape."));
            if (EntryIds("mobs").Contains(templateId))
                return WizardRefuse(L("canrpgmobs:wizard-id-taken", "{0} is taken: change the id.", templateId));
            if (CatalogEditGuard.RejectValue(bundle) is { } refused)
                return WizardRefuse(L("canrpgmobs:editor-not-saved", "Not saved: {0}", refused));

            var candidate = mod.Store.Clone();
            candidate.Merge(bundle);
            var catalog = CatalogLoader.Load(capi, capi.Logger, MobsApi.Vocabulary, candidate, out var report, logProblems: false);
            var problems = report.Problems.Where(p => p.Severity != Severity.Info && newIds.Any(id => Problem.PathMentions(p.Path, id))).ToList();
            string details = string.Join("; ", problems.Take(2).Select(p => p.Message));

            if (!CatalogStore.IsInCatalog(catalog.Data, "mobs", templateId) || catalog.Resolve(templateId) == null)
                return WizardRefuse(L("canrpgmobs:wizard-invalid", "This mob would not load: {0}", details));
            SetStatus(problems.Count == 0
                ? L("canrpgmobs:wizard-ok", "Ready: {0} can be created.", templateId)
                : L("canrpgmobs:wizard-ok-warnings", "Can be created, with warnings: {0}", details), problems.Count > 0);
            return true;
        }

        private bool WizardRefuse(string message)
        {
            SetStatus(message, true);
            return false;
        }

        // copy with race and role: all generated ids must be free
        private bool WizardIdTaken(string id)
        {
            string qualified = Q(id);
            return EntryIds("mobs").Contains(qualified) || EntryIds("races").Contains(qualified + "_race")
                   || EntryIds("roles").Contains(qualified + "_role") || EntryIds("aiProfiles").Contains(qualified + "_ai");
        }

        // only what doesn't exist yet goes in; newIds = everything the bundle creates
        private JObject BuildWizardBundle(WizardDraft w, string templateId, out List<string> newIds)
        {
            newIds = new List<string> { templateId };
            var role = BehaviorOf(w);
            var bundle = new JObject();
            string raceId = BundleRace(w, bundle, newIds, out bool creature);
            string roleId = BundleRole(w, role, bundle, newIds);

            int min = Math.Clamp(Math.Min(w.LevelMin, w.LevelMax), 1, 1000);
            int max = Math.Clamp(Math.Max(w.LevelMin, w.LevelMax), 1, 1000);
            var template = new JObject
            {
                ["id"] = templateId, ["name"] = EnglishName(w.NameEn), ["race"] = raceId, ["role"] = roleId, ["levelRange"] = new JArray(min, max),
                ["stats"] = new JObject { ["maxHealth"] = w.Health, ["meleeDamage"] = w.Damage, ["moveSpeed"] = w.Speed }
            };

            if (!creature)
            {
                var look = new JObject();
                if (w.RightHand.Length > 0) look["rightHand"] = w.RightHand;
                if (w.LeftHand.Length > 0) look["leftHand"] = w.LeftHand;
                if (w.Gear.Count > 0) look["gear"] = new JArray(w.Gear);
                if (look.Count > 0) template["appearance"] = look;
            }

            var skills = BundleSkills(w, role, creature);
            if (skills.Count > 0) template["skills"] = skills;
            if (!w.InheritSkills) template["skillsMode"] = MergeModes.Replace;

            var loot = BundleLoot(w, templateId, bundle, newIds);
            if (loot.Count > 0) template["loot"] = loot;

            string? tier = role.Tier ?? (w.Difficulty == "elite" ? Q("elite") : null);
            if (tier != null && mod.Catalog.Tiers.ContainsKey(tier)) template["defaultTier"] = tier;
            if (role.Boss)
                template["phases"] = new JArray(new JObject
                {
                    ["id"] = "enraged", ["hpBelow"] = 0.5, ["ccImmune"] = new JArray(ControlNames.Stun), ["damageMul"] = 1.3,
                    ["announce"] = new JObject { ["en"] = "{name} flies into a rage!", ["ru"] = "{name} впадает в ярость!" }
                });
            AddTo(bundle, "mobs", template);
            return bundle;
        }

        private static void AddTo(JObject bundle, string section, JObject entry) => EnsureArray(bundle, section).Add(entry);

        // English only, other languages come from lang files (<domain>:mob-<id> etc.)
        private static JObject EnglishName(string en)
        {
            var name = new JObject();
            if (en.Length > 0) name["en"] = en;
            return name;
        }

        // the picked race, or a new one in the bundle
        private string BundleRace(WizardDraft w, JObject bundle, List<string> newIds, out bool creature)
        {
            var data = mod.Catalog.Data;
            if (w.RaceId != null && data.Races.TryGetValue(w.RaceId, out var race))
            {
                creature = race.body == MobBodies.Creature;
                return w.RaceId;
            }

            creature = w.Body == MobBodies.Creature;
            string raceId = Q(UniqueSlug(w.RaceNameEn, "race", id => EntryIds("races").Contains(Q(id))));
            var appearance = creature
                ? new JObject { ["kind"] = AppearanceKinds.Shape, ["shape"] = w.Shape }
                : new JObject { ["kind"] = AppearanceKinds.Plm, ["model"] = w.Model.Length > 0 ? w.Model : AppearanceModel.Seraph };
            if (creature && w.Texture.Length > 0) appearance["textures"] = new JObject { ["all"] = w.Texture };
            var newRace = new JObject
            {
                ["id"] = raceId, ["name"] = EnglishName(w.RaceNameEn), ["body"] = w.Body,
                ["faction"] = w.Faction.Length > 0 ? w.Faction : FactionMatrix.Monsters, ["appearance"] = appearance,
                ["stats"] = new JObject { ["maxHealth"] = 20, ["meleeDamage"] = 3, ["moveSpeed"] = 0.03 }
            };
            // humanoid sets use seraph animations, creatures don't have them
            if (!creature && data.AnimSets.ContainsKey(Q("humanoid_base"))) newRace["animSet"] = Q("humanoid_base");
            if (!creature && data.SoundSets.ContainsKey(Q("humanoid"))) newRace["soundSet"] = Q("humanoid");
            AddTo(bundle, "races", newRace);
            newIds.Add(raceId);
            return raceId;
        }

        // an existing role, or the shared one of this behavior
        private string BundleRole(WizardDraft w, WizardRole role, JObject bundle, List<string> newIds)
        {
            var data = mod.Catalog.Data;
            if (w.RoleId != null && data.Roles.ContainsKey(w.RoleId)) return w.RoleId;

            string roleId = Q(UniqueSlug("role_" + role.Code, "role", id => EntryIds("roles").Contains(Q(id))));
            string aiId = Q("ai_" + role.Code);
            if (!data.AiProfiles.TryGetValue(aiId, out var ai) || ai.archetype != role.Archetype)
            {
                aiId = Q(UniqueSlug("ai_" + role.Code, "ai", id => EntryIds("aiProfiles").Contains(Q(id))));
                AddTo(bundle, "aiProfiles", NewAiProfile(role, aiId));
                newIds.Add(aiId);
            }
            AddTo(bundle, "roles", new JObject
            {
                ["id"] = roleId, ["aiProfile"] = aiId,
                ["name"] = EnglishName(TextIn("en", "canrpgmobs:wiz-name-" + role.Code, role.Code))
            });
            newIds.Add(roleId);
            return roleId;
        }

        private static JArray BundleSkills(WizardDraft w, WizardRole role, bool creature)
        {
            var skills = new JArray();
            for (int i = 0; i < w.Spells.Length; i++)
            {
                if (w.Spells[i].Length == 0) continue;
                // healer's first spell targets wounded allies
                bool heal = role.HealsFirst && skills.Count == 0;
                var rule = new JObject
                {
                    ["id"] = "skill" + (i + 1), ["spell"] = w.Spells[i], ["priority"] = 60 - i * 10,
                    ["minRange"] = heal ? 0f : role.MinRange, ["maxRange"] = role.MaxRange, ["cooldown"] = 6 + i * 2, ["initialCooldown"] = new JArray(1, 3)
                };
                if (heal)
                {
                    rule["target"] = SkillTargets.LowestHpAlly;
                    rule["allyHpBelow"] = 0.7;
                }
                if (!creature) rule["animation"] = new JObject { ["cast"] = AnimKeys.Cast };
                skills.Add(rule);
            }
            return skills;
        }

        // picked tables, plus one new table for the quick items
        private static JArray BundleLoot(WizardDraft w, string templateId, JObject bundle, List<string> newIds)
        {
            var loot = new JArray(w.Loot.Distinct());
            if (w.QuickLoot.Count == 0) return loot;
            string lootId = templateId + "_loot";
            var items = new JArray(w.QuickLoot.Select(q => new JObject
            {
                ["type"] = q.Block ? LootEntryModel.Block : LootEntryModel.Item, ["code"] = q.Code, ["chance"] = Math.Clamp(q.Chance, 1, 100) / 100.0
            }));
            // every item rolls its own chance
            AddTo(bundle, "lootTables", new JObject { ["id"] = lootId, ["rolls"] = new JArray(0, 0), ["guaranteed"] = items });
            newIds.Add(lootId);
            loot.Add(lootId);
            return loot;
        }

        private static JObject NewAiProfile(WizardRole role, string aiId)
        {
            var ai = new JObject { ["id"] = aiId, ["archetype"] = role.Archetype, ["aggroRange"] = role.Boss ? 24 : 20 };
            if (role.Code == "archer")
            {
                ai["preferredRange"] = new JArray(10, 18);
                ai["ranged"] = new JObject { ["arrow"] = WizardArrow };
            }
            return ai;
        }

        // the profile for a behavior: the shared ai_<code>, else any with its archetype, else made on the server now
        private string AiProfileFor(WizardRole role)
        {
            var data = mod.Catalog.Data;
            string shared = Q("ai_" + role.Code);
            if (data.AiProfiles.TryGetValue(shared, out var ai) && ai.archetype == role.Archetype) return shared;
            var any = data.AiProfiles.Where(p => p.Value.archetype == role.Archetype).Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (any != null) return any;
            string id = Q(UniqueSlug("ai_" + role.Code, "ai", i => EntryIds("aiProfiles").Contains(Q(i))));
            mod.SendBundle(new JObject { ["aiProfiles"] = new JArray(NewAiProfile(role, id)) }.ToString(Formatting.None));
            return id;
        }

        // role names go in both languages whatever the admin's locale
        private static string TextIn(string locale, string key, string fallback)
            => Lang.GetL(locale, key) is { } text && text != key ? text : fallback;
    }
}
