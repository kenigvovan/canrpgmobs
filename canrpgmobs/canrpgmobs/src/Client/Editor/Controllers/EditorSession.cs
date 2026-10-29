using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using canrpgmobs.Catalog;
using canrpgmobs.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace canrpgmobs.Client.Editor.Controllers
{
    // map entries keep their parent and key so the key can be renamed in place
    public sealed class NavFrame
    {
        public NavFrame(NodeSpec node, JObject token, string label, JObject? mapParent = null, string? mapKey = null)
        {
            Node = node;
            Token = token;
            Label = label;
            MapParent = mapParent;
            MapKey = mapKey;
        }

        public NodeSpec Node { get; }
        public JObject Token { get; }
        public string Label { get; set; }
        public JObject? MapParent { get; }
        public string? MapKey { get; set; }
    }

    // Editor state, no GUI. The draft stays raw JSON so unknown fields survive a round trip.
    public sealed class EditorSession
    {
        private readonly List<NavFrame> stack = new();
        private string originalJson = "";
        private int dirtyAtVersion = -1;
        private bool dirtyValue;

        public EditorSession(EditorSchema schema) => Schema = schema;

        public EditorSchema Schema { get; }
        public string Section { get; private set; } = "mobs";
        public JObject? Draft { get; private set; }
        // null for new/duplicated entries
        public string? OpenedId { get; private set; }
        public IReadOnlyList<NavFrame> Stack => stack;
        public NavFrame? Current => stack.Count == 0 ? null : stack[stack.Count - 1];
        public string DraftId => Draft?["id"] is JValue { Type: JTokenType.String } id ? (string)id! : "";

        // bumped on every edit; cheaper to compare than serializing the draft
        public int Version { get; private set; }

        // only used for cache invalidation, extra calls are harmless
        public void Touch() => Version++;

        // cached per Version
        public bool IsDirty
        {
            get
            {
                if (dirtyAtVersion == Version) return dirtyValue;
                dirtyAtVersion = Version;
                return dirtyValue = Draft != null && Draft.ToString(Formatting.None) != originalJson;
            }
        }
        public List<Problem> Problems { get; } = new();

        public string? RenamedFrom
            => OpenedId != null && !JsonDraft.SameId(OpenedId, DraftId)
                ? OpenedId : null;

        public bool Matches(JObject entry) => entry.ToString(Formatting.None) == originalJson;

        public void SelectSection(string section)
        {
            Section = section;
            Close();
        }

        public void Close()
        {
            Touch();
            Draft = null;
            OpenedId = null;
            originalJson = "";
            stack.Clear();
            Problems.Clear();
        }

        public void Open(string section, JObject entry, string id)
        {
            Touch();
            Section = section;
            Draft = (JObject)entry.DeepClone();
            OpenedId = id;
            originalJson = Draft.ToString(Formatting.None);
            Problems.Clear();
            ResetStack();
        }

        public void OpenNew(string section, JObject entry)
        {
            Open(section, entry, "");
            OpenedId = null;
            originalJson = "";
        }

        public void Duplicate(string newId)
        {
            if (Draft == null) return;
            Touch();
            Draft = (JObject)Draft.DeepClone();
            Draft["id"] = newId;
            OpenedId = null;
            originalJson = "";
            Problems.Clear();
            ResetStack();
        }

        public void Revert()
        {
            Touch();
            if (OpenedId == null || originalJson.Length == 0)
            {
                Close();
                return;
            }
            Draft = JObject.Parse(originalJson);
            Problems.Clear();
            ResetStack();
        }

        public void MarkSaved()
        {
            if (Draft == null) return;
            Touch();
            originalJson = Draft.ToString(Formatting.None);
            OpenedId = DraftId;
            if (stack.Count > 0) stack[0].Label = DraftId;
        }

        public void PopTo(int depth)
        {
            while (stack.Count > Math.Max(1, depth + 1)) stack.RemoveAt(stack.Count - 1);
        }

        public ChildSpec? Child(string key) => Current?.Node.Children.FirstOrDefault(c => c.Key == key);

        public void OpenObject(ChildSpec child)
        {
            var frame = Current;
            if (frame == null) return;
            if (frame.Token[child.Key] is not JObject obj)
            {
                obj = child.NewItem();
                frame.Token[child.Key] = obj;
                Touch();
            }
            stack.Add(new NavFrame(Schema.Node(child.NodeId), obj, child.Key));
        }

        public void ClearObject(ChildSpec child)
        {
            Touch();
            Current?.Token.Remove(child.Key);
        }

        public bool OpenItem(ChildSpec child, int index)
        {
            if (Current?.Token[child.Key] is not JArray array || index < 0 || index >= array.Count || array[index] is not JObject item) return false;
            string label = item["id"] is JValue { Type: JTokenType.String } id ? (string)id! : $"{child.Key} {index + 1}";
            stack.Add(new NavFrame(Schema.Node(child.NodeId), item, label));
            return true;
        }

        public int AddItem(ChildSpec child)
        {
            var frame = Current!;
            if (frame.Token[child.Key] is not JArray array)
            {
                array = new JArray();
                frame.Token[child.Key] = array;
            }
            array.Add(child.NewItem());
            Touch();
            return array.Count - 1;
        }

        public void RemoveItem(ChildSpec child, int index)
        {
            if (Current?.Token[child.Key] is not JArray array || index < 0 || index >= array.Count) return;
            array.RemoveAt(index);
            Touch();
        }

        public bool OpenMapEntry(ChildSpec child, string key)
        {
            if (Current?.Token[child.Key] is not JObject map || map[key] is not JObject item) return false;
            stack.Add(new NavFrame(Schema.Node(child.NodeId), item, key, map, key));
            return true;
        }

        public string AddMapEntry(ChildSpec child)
        {
            var frame = Current!;
            if (frame.Token[child.Key] is not JObject map)
            {
                map = new JObject();
                frame.Token[child.Key] = map;
            }
            string key = "new";
            for (int n = 2; map.ContainsKey(key); n++) key = "new" + n;
            map[key] = child.NewItem();
            Touch();
            return key;
        }

        public void RemoveMapEntry(ChildSpec child, string key)
        {
            if (Current?.Token[child.Key] is not JObject map) return;
            map.Remove(key);
            Touch();
        }

        public bool RenameMapKey(NavFrame frame, string newKey)
        {
            newKey = newKey.Trim();
            if (frame.MapParent == null || frame.MapKey == null || newKey.Length == 0 || newKey == frame.MapKey || frame.MapParent.ContainsKey(newKey))
                return false;
            var value = frame.MapParent[frame.MapKey];
            frame.MapParent.Remove(frame.MapKey);
            frame.MapParent[newKey] = value;
            frame.MapKey = newKey;
            frame.Label = newKey;
            Touch();
            return true;
        }

        // follows a validator problem path as far as it goes
        public bool JumpTo(string path)
        {
            if (Draft == null) return false;
            ResetStack();
            var segments = path.Split(new[] { " > " }, StringSplitOptions.None);
            string qualified = JsonDraft.Q(DraftId);
            int start = Array.FindIndex(segments, s => s.EndsWith(" " + qualified) || s.EndsWith(" " + DraftId));
            if (start < 0) return false;
            for (int i = start + 1; i < segments.Length; i++)
                if (!Step(segments[i])) break;
            return true;
        }

        private bool Step(string segment)
        {
            Match m;
            if ((m = Regex.Match(segment, @"^skill (.+)$")).Success) return PushById("skills", m.Groups[1].Value);
            if ((m = Regex.Match(segment, @"^condition (\d+)")).Success) return PushIndex("conditions", int.Parse(m.Groups[1].Value) - 1);
            if ((m = Regex.Match(segment, @"^(entries|guaranteed)\[(\d+)\]")).Success) return PushIndex(m.Groups[1].Value, int.Parse(m.Groups[2].Value));
            if ((m = Regex.Match(segment, @"^members\[(\d+)\]")).Success) return PushIndex("members", int.Parse(m.Groups[1].Value));
            if ((m = Regex.Match(segment, @"^phase (.+)$")).Success) return PushById("phases", m.Groups[1].Value);
            if ((m = Regex.Match(segment, @"^action (\d+) ")).Success) return PushIndex("actions", int.Parse(m.Groups[1].Value) - 1);
            if (segment.StartsWith("action (") && Child("action") is { } actionChild && Current!.Token["action"] is JObject)
            {
                OpenObject(actionChild);
                return true;
            }
            if ((m = Regex.Match(segment, @"^anim (.+)$")).Success) return PushMap("anims", m.Groups[1].Value) || PushMap("animOverrides", m.Groups[1].Value);
            if ((m = Regex.Match(segment, @"^sound (.+)$")).Success) return PushMap("sounds", m.Groups[1].Value) || PushMap("soundOverrides", m.Groups[1].Value);
            if ((m = Regex.Match(segment, @"^preset (.+)$")).Success)
            {
                if (Child("appearance") is { } appearance && Current!.Token["appearance"] is JObject) OpenObject(appearance);
                return PushById("presets", m.Groups[1].Value);
            }
            return false;
        }

        private bool PushById(string key, string id)
        {
            if (Child(key) is not { } child || Current!.Token[key] is not JArray array) return false;
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JObject item && item["id"] is JValue value && value.ToString() == id) return OpenItem(child, i);
            return false;
        }

        private bool PushIndex(string key, int index) => Child(key) is { } child && OpenItem(child, index);

        private bool PushMap(string key, string mapKey) => Child(key) is { } child && OpenMapEntry(child, mapKey);

        private void ResetStack()
        {
            stack.Clear();
            if (Draft != null) stack.Add(new NavFrame(Schema.SectionNode(Section), Draft, DraftId));
        }
    }
}
