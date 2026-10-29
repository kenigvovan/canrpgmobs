using System;
using System.Globalization;
using System.Linq;
using canrpgclasses.Client;
using canrpgmobs.Data;
using canrpgmobs.Net;
using Vintagestory.API.Client;
using static canrpgmobs.Client.UiKit;

namespace canrpgmobs.Client.Editor
{
    // Test panel. Watch output goes into dynamic texts so typing isn't interrupted.
    public partial class MobEditorDialog
    {
        private const int TestRefreshMs = 1000;

        private string testKey = "";
        private int testLevel;
        private string testTier = "";
        private int testCount = 1;
        private string testGroup = "";
        private int testRadius = 24;
        private string testMessage = "";
        private string testInspect = "";
        private string testTrace = "";
        private bool testWatching;
        private long testTickId;

        private void AddTestPage(GuiComposer compo, double x, double y)
        {
            PanelInset(compo, x, y, -4)
                .AddStaticText(L("canrpgmobs:test-title", "Test panel"), UiKit.SectionFont(), B(x, y, PanelW, 24));

            double row = y + 32;
            // a named mob, or race + role picked one after the other
            compo.AddStaticText(L("canrpgmobs:test-mob", "Mob"), EditorStyle.FieldLabel(), B(x, row + 4, 60));
            compo.AddFitButton(testKey.Length == 0 ? L("canrpgmobs:test-pick-mob", "Named mob...") : MobKeyName(testKey),
                    () => OpenPick("mobsNamed", L("canrpgmobs:test-mob", "Mob"), null, v => testKey = v), B(x + 60, row, 170), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:test-pick-race-role", "Race + role"), () => OpenPick("racesNamed", L("canrpgmobs:editor-simple-race", "Race"), null,
                    race => capi.Event.EnqueueMainThreadTask(() => OpenPick("rolesNamed", L("canrpgmobs:editor-simple-role", "Role"), null,
                        role => testKey = race + IdRules.KeySeparator + role), "canrpgmobs-testpick")), B(x + 234, row, 70), EnumButtonStyle.Small)
                .AddStaticText(L("canrpgmobs:test-level-count", "Level / count"), EditorStyle.FieldLabel(), B(x + 308, row + 4, 90));
            NumberInput(compo, B(x + 400, row, 50), testLevel > 0 ? testLevel.ToString(CultureInfo.InvariantCulture) : "", v => testLevel = int.TryParse(v, out int level) ? level : 0);
            NumberInput(compo, B(x + 454, row, 40), testCount.ToString(CultureInfo.InvariantCulture), v => testCount = int.TryParse(v, out int count) ? count : 1);
            compo.AddSmallButton(L("canrpgmobs:test-spawn", "Spawn"), () => SendTest("spawn", testKey), B(x + 500, row, PanelW - 500), EnumButtonStyle.Small);

            row += RowStep;
            compo.AddStaticText(L("canrpgmobs:test-tier", "Tier"), EditorStyle.FieldLabel(), B(x, row + 4, 60));
            TierDropDown(compo, testTier, code => testTier = code, L("canrpgmobs:editor-default", "(default)"), B(x + 60, row, 240));

            row += RowStep;
            compo.AddStaticText(L("canrpgmobs:test-group", "Group"), EditorStyle.FieldLabel(), B(x, row + 4, 60));
            compo.AddFitButton(testGroup.Length == 0 ? L("canrpgmobs:editor-simple-choose", "Choose...") : NameOf("groups", testGroup),
                    () => OpenPick("groups", L("canrpgmobs:test-group", "Group"), null, v => testGroup = v), B(x + 60, row, 240), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:test-spawn-group", "Spawn group"), () => SendTest("group", testGroup), B(x + 308, row, 186), EnumButtonStyle.Small);

            row += RowStep;
            compo.AddStaticText(L("canrpgmobs:test-radius", "Radius"), EditorStyle.FieldLabel(), B(x, row + 4, 60));
            NumberInput(compo, B(x + 60, row, 60), testRadius.ToString(CultureInfo.InvariantCulture), v => testRadius = int.TryParse(v, out int radius) ? radius : testRadius);
            compo.AddSmallButton(L("canrpgmobs:test-kill", "Remove mobs"), () => SendTest("kill", ""), B(x + 126, row, 170), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:test-inspect", "Watch the mob I look at"), OnTestInspect, B(x + 302, row, 192), EnumButtonStyle.Small)
                .AddSmallButton(L("canrpgmobs:test-stop", "Stop"), OnTestStop, B(x + 500, row, PanelW - 500), EnumButtonStyle.Small);

            row += RowStep + 4;
            compo.AddDynamicText(testMessage, EditorStyle.Status(false), B(x, row, PanelW, 22), "testMessage");
            row += 26;
            compo.AddDynamicText(testInspect.Length > 0 ? testInspect : L("canrpgmobs:test-nothing", "Look at a mob and press Watch: its stats, fight and decisions show here."),
                EditorStyle.Body(), B(x, row, PanelW, 190), "testInspect");
            row += 196;
            compo.AddDynamicText(testTrace, EditorStyle.FieldLabel(), B(x, row, PanelW, Math.Max(24, y + BodyH - row - 4)), "testTrace");
        }

        private bool SendTest(string action, string key)
        {
            ClearPending();
            mod.SendTest(new MobTestPacket
            {
                Action = action, MobKey = key, Level = testLevel, Tier = testTier, Count = Math.Clamp(testCount, 1, 20), Radius = Math.Clamp(testRadius, 1, 128)
            });
            return true;
        }

        private bool OnTestInspect()
        {
            testWatching = true;
            EnsureTestTicks();
            return SendTest("inspect", "");
        }

        private bool OnTestStop()
        {
            ClearPending();
            StopWatching();
            Compose();
            return true;
        }

        // also called when leaving the test page, so the server stops tracing
        private void StopWatching()
        {
            if (!testWatching) return;
            testWatching = false;
            testInspect = testTrace = "";
            mod.SendTest(new MobTestPacket { Action = "stop" });
        }

        private void EnsureTestTicks()
        {
            if (testTickId != 0) return;
            testTickId = capi.Event.RegisterGameTickListener(_ =>
            {
                if (!IsOpened() || !testWatching) return;
                // catches all other ways of leaving the page
                if (page != Page.Test) StopWatching();
                else if (pick == null && itemPick == null) mod.SendTest(new MobTestPacket { Action = "refresh" });
            }, TestRefreshMs);
        }

        private void StopTest()
        {
            if (testTickId != 0) capi.Event.UnregisterGameTickListener(testTickId);
            testTickId = 0;
            StopWatching();
        }

        private void OnTestResult(MobTestResultPacket packet)
        {
            if (!IsOpened()) return;
            if (packet.Action == "look")
            {
                // recompose only on failure
                SetStatus(packet.Message, !packet.Ok);
                if (!packet.Ok) Compose();
                return;
            }

            if (!string.IsNullOrEmpty(packet.Message)) testMessage = packet.Message;
            if (packet.Inspect.Length > 0)
            {
                testInspect = packet.Inspect;
                testTrace = string.Join("\n", packet.Trace ?? Array.Empty<string>());
            }
            if (!packet.Ok && packet.Action == "refresh") testWatching = false;
            if (page != Page.Test || pick != null || SingleComposer == null) return;

            SingleComposer.GetDynamicText("testMessage")?.SetNewText(testMessage, false, false, false);
            SingleComposer.GetDynamicText("testInspect")?.SetNewText(testInspect, false, false, false);
            SingleComposer.GetDynamicText("testTrace")?.SetNewText(testTrace, false, false, false);
        }
    }
}
