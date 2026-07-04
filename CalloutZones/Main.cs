using RoR2;
using BepInEx;
using System.Collections.Generic;
using UnityEngine;
using RoR2.UI;
using static RoR2.Navigation.NodeGraph;
using System;
using BepInEx.Configuration;
using TMPro;
[assembly: HG.Reflection.SearchableAttribute.OptIn]

namespace CalloutZones
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]

    public class Main : BaseUnityPlugin
    {
        public const string PluginGUID = PluginAuthor + "." + PluginName;
        public const string PluginAuthor = "cyanblur";
        public const string PluginName = "CalloutZones";
        public const string PluginVersion = "1.0.0";

        private const int MaxNodeDistance = 20;

        public static ConfigEntry<UIDisplayOption> showOnScreen { get; set; }
        public static ConfigEntry<float> fadeDelay { get; set; }
        public static ConfigEntry<bool> showOnPing { get; set; }
        public static ConfigEntry<bool> showOnPingMultiplayer { get; set; }
        public static ConfigEntry<bool> showOnPingGround { get; set; }
        public static ConfigEntry<bool> showOnPingInteractable { get; set; }
        public static ConfigEntry<int> cfgXHeight { get; set; }
        public static ConfigEntry<int> cfgYHeight { get; set; }
        public static ConfigEntry<Color> pingColor { get; set; }

        private static string currentScene = "";
        private static string currentZone = "";
        private static Dictionary<string, HashSet<int>> historicalLocations = new Dictionary<string, HashSet<int>>();
        private static Dictionary<string, Color> zoneColors = new Dictionary<string, Color>();
        private static Dictionary<string, Dictionary<int, string>> stageZoneMappings = new Dictionary<string, Dictionary<int, string>>();
        private static Dictionary<int, PingIndicator> nodePings = new Dictionary<int, PingIndicator>();
        private static HashSet<string> visitHistory = new HashSet<string>();

        private static string nearestPingNodeName = "";
        private static string nearestCharacterNodeName = "";

        private static HUD hud = null;
        private static GameObject zoneHudElement;
        private static HGTextMeshProUGUI mainText;
        private static TypewriteTextController typewriteTextController;
        private static bool canLoad = false;

        public void Awake()
        {
            showOnScreen = Config.Bind<UIDisplayOption>(
                "Functionality",
                "Show on HUD",
                UIDisplayOption.Always,
                "Adds the current zone to your HUD's healthbar group. Shows briefly upon entering a zone. If set to \"Once\" will only show that zone one time."
            );

            fadeDelay = Config.Bind<float>(
                "Functionality",
                "HUD text duration",
                2f,
                "Time in seconds before the HUD text fades. Set to 0 to never fade."
            );

            showOnPingGround = Config.Bind<bool>(
                "Functionality",
                "Show on ping ground message",
                true,
                "When pinging the ground in a zone, changes the ping message."
            );

            showOnPingInteractable = Config.Bind<bool>(
                "Functionality",
                "Show on ping interactable message",
                true,
                "When pinging an interactable in a zone, changes the ping message."
            );

            cfgXHeight = Config.Bind<int>(
                "Display",
                "X Offset",
                0,
                "Adjust the X position of the zone text."
            );

            cfgYHeight = Config.Bind<int>(
                "Display",
                "Y Offset",
                0,
                "Adjust the Y position of the zone text."
            );

            pingColor = Config.Bind<Color>(
                "Display",
                "Ping Text Color",
                Color.white,
                "Hex color of the chat message for pinged zones."
            );

            showOnPing = Config.Bind<bool>(
                "Display",
                "Show on pings",
                true,
                "When pinging in a zone, replaces your name with the zone name on the ping indicator. Single player only."
            );

            showOnPingMultiplayer = Config.Bind<bool>(
                "Display",
                "Show on pings (Online)",
                false,
                "When pinging in a zone, replaces your name or your teammate's name with the zone name on the ping indicator. Multiplayer only."
            );

            var zoneDefinitions = CalloutZones.DefaultZones; //CalloutZones.GetZonesFromFile();
            foreach (var scene in zoneDefinitions.Keys)
            {
                stageZoneMappings[scene] = new Dictionary<int, string>();
                foreach (var zone in zoneDefinitions[scene].Keys)
                {
                    foreach (var id in zoneDefinitions[scene][zone])
                    {
                        stageZoneMappings[scene][id] = zone;
                    }
                }
            }

            On.RoR2.Language.GetString_string += Language_GetString_string_GetStringOverride;
            On.RoR2.UI.PingIndicator.RebuildPing += PingIndicator_RebuildPing_GrabNearestNodeName;
            Stage.onStageStartGlobal += Stage_onStageStartGlobal;
            On.RoR2.UI.HUD.Awake += HUD_Awake;
            On.RoR2.UI.HUD.Update += HUD_Update_UpdatePosition;
            cfgXHeight.SettingChanged += CfgXHeight_SettingChanged;
            cfgYHeight.SettingChanged += CfgYHeight_SettingChanged;
            fadeDelay.SettingChanged += FadeDelay_SettingChanged;
        }

        private void Stage_onStageStartGlobal(Stage obj)
        {
            currentScene = SceneCatalog.currentSceneDef.cachedName;
            currentZone = "";
            historicalLocations = new Dictionary<string, HashSet<int>>();
            zoneColors = new Dictionary<string, Color>();
            if (!stageZoneMappings.ContainsKey(currentScene))
            {
                stageZoneMappings[currentScene] = new Dictionary<int, string>();
            }
            nodePings = new Dictionary<int, PingIndicator>();
            nearestPingNodeName = "";
            nearestCharacterNodeName = "";
            visitHistory = new HashSet<string>();
            canLoad = true;
        }

        private static void CfgYHeight_SettingChanged(object sender, EventArgs e)
        {
            SetNotifPosition();
        }

        private static void CfgXHeight_SettingChanged(object sender, EventArgs e)
        {
            SetNotifPosition();
        }

        private static void FadeDelay_SettingChanged(object sender, EventArgs e)
        {
            SetUIDuration();
        }

        private void HUD_Awake(On.RoR2.UI.HUD.orig_Awake orig, RoR2.UI.HUD self)
        {
            orig(self);
            hud = self;
        }

        private void PingIndicator_RebuildPing_GrabNearestNodeName(On.RoR2.UI.PingIndicator.orig_RebuildPing orig, PingIndicator self)
        {
            nearestPingNodeName = null;
            if (showOnPingGround.Value || showOnPingInteractable.Value)
            {
                var groundNodes = SceneInfo.instance.groundNodes;
                if (groundNodes != null)
                {
                    var closestNodeIndex = groundNodes.FindClosestNode(self.pingOrigin, HullClassification.Human, MaxNodeDistance);
                    if (closestNodeIndex != null && closestNodeIndex != NodeIndex.invalid)
                    {
                        if (stageZoneMappings[currentScene].ContainsKey(closestNodeIndex.nodeIndex))
                        {
                            nearestPingNodeName = stageZoneMappings[currentScene][closestNodeIndex.nodeIndex];
                        }
                    }
                }
            }
            orig(self);
            if (((showOnPing.Value && RoR2Application.isInSinglePlayer) || (showOnPingMultiplayer.Value && !RoR2Application.isInSinglePlayer)) && !string.IsNullOrWhiteSpace(nearestPingNodeName))
            {
                self.pingText.text = nearestPingNodeName;
            }
        }

        private string Language_GetString_string_GetStringOverride(On.RoR2.Language.orig_GetString_string orig, string token)
        {
            if (!string.IsNullOrWhiteSpace(nearestPingNodeName)
                && ((token == "PLAYER_PING_DEFAULT" && showOnPingGround.Value)
                || ((token == "PLAYER_PING_INTERACTABLE" || token == "PLAYER_PING_INTERACTABLE_WITH_COST") && showOnPingInteractable.Value)))
            {
                return orig(token) + $" <color=#{ColorUtility.ToHtmlStringRGB(pingColor.Value)}>({nearestPingNodeName})</color>";
            }
            else
            { 
                return orig(token);
            }
        }

        private void HUD_Update_UpdatePosition(On.RoR2.UI.HUD.orig_Update orig, HUD self)
        {
            orig(self);
            if (!hud.targetMaster?.playerCharacterMasterController?.body?.transform) return;
            var groundNodes = SceneInfo.instance.groundNodes;
            if (groundNodes != null)
            {
                var closestNodeIndex = groundNodes.FindClosestNode(hud.targetMaster.playerCharacterMasterController.body.transform.position, HullClassification.Human, MaxNodeDistance);
                if (closestNodeIndex != null && closestNodeIndex != NodeIndex.invalid)
                {
                    var nodeIndex = closestNodeIndex.nodeIndex;

                    // Debug zone building section, painting nodes with character
                    if (!string.IsNullOrWhiteSpace(currentZone) && !historicalLocations[currentZone].Contains(nodeIndex))
                    {
                        historicalLocations[currentZone].Add(nodeIndex);
                        if (stageZoneMappings[currentScene].ContainsKey(nodeIndex))
                        {
                            historicalLocations[stageZoneMappings[currentScene][nodeIndex]].Remove(nodeIndex);
                        }
                        stageZoneMappings[currentScene][nodeIndex] = currentZone;

                        // Ping the resulting location
                        if (nodePings.ContainsKey(nodeIndex))
                        {
                            nodePings[nodeIndex].DestroyPing();
                            nodePings.Remove(nodeIndex);
                        }
                        Vector3 node = new Vector3();
                        groundNodes.GetNodePosition(closestNodeIndex, out node);
                        nodePings[nodeIndex] = CustomDrawPing(node, currentZone);
                    }

                    // Update the current zone name for display if player has moved into a new node group
                    if (stageZoneMappings[currentScene].ContainsKey(closestNodeIndex.nodeIndex))
                    {
                        bool updateText = nearestCharacterNodeName != stageZoneMappings[currentScene][closestNodeIndex.nodeIndex];
                        if (updateText && !string.IsNullOrWhiteSpace(stageZoneMappings[currentScene][closestNodeIndex.nodeIndex]) && showOnScreen.Value != UIDisplayOption.Never)
                        {
                            nearestCharacterNodeName = stageZoneMappings[currentScene][closestNodeIndex.nodeIndex];
                            if (showOnScreen.Value == UIDisplayOption.Once)
                            {
                                if (visitHistory.Add(nearestCharacterNodeName))
                                {
                                    CreateDisplay();
                                }
                            }
                            else
                            {
                                CreateDisplay();
                            }
                        }
                    }
                }
            }
        }

        private static PingIndicator CustomDrawPing(Vector3 location, string zoneName, float duration = float.PositiveInfinity)
        {
            GameObject gameObject = GameObject.Instantiate(LegacyResourcesAPI.Load<GameObject>("Prefabs/PingIndicator"));
            PingIndicator pingIndicator = gameObject.GetComponent<PingIndicator>();
            pingIndicator.pingOrigin = location;
            pingIndicator.pingNormal = location;
            if (zoneColors.ContainsKey(zoneName))
            {
                pingIndicator.defaultPingColor = zoneColors[zoneName];
            }
            pingIndicator.defaultPingDuration = duration;
            pingIndicator.RebuildPing();
            if (string.IsNullOrWhiteSpace(zoneName))
            {
                zoneName = "[V]";
            }
            pingIndicator.pingText.text = zoneName;
            
            return pingIndicator;
        }

        private static void CreateDisplay()
        {
            if (!canLoad) return;
            if (!hud) return;

            if (!zoneHudElement)
            {
                ChildLocator childLocator = hud.gameObject.GetComponent<ChildLocator>();

                var bottomLeftClusterTransform = childLocator.FindChild("BottomLeftCluster");

                var mainContainer = hud.mainContainer;
                var mapNameCluster = mainContainer.transform.Find("MapNameCluster");

                zoneHudElement = UnityEngine.Object.Instantiate(mapNameCluster.gameObject, bottomLeftClusterTransform);

                var assignStageToken = zoneHudElement.GetComponent<AssignStageToken>();
                if (assignStageToken)
                {
                    Destroy(assignStageToken);
                }

                try
                {
                    var subTextTransform = zoneHudElement.transform.Find("Subtext");
                    if (subTextTransform)
                    {
                        var subText = subTextTransform.GetComponent<HGTextMeshProUGUI>();
                        if (subText)
                        {
                            subText.GetTextInfo("");
                            subText.maxVisibleCharacters = 0;
                        }
                    }

                }
                catch { }
                mainText = null;
                typewriteTextController = null;
            }

            if (!mainText)
            {
                var mainTextTransform = zoneHudElement.transform.Find("MainText");
                mainText = mainTextTransform.GetComponent<HGTextMeshProUGUI>();
                mainText.alignment = TextAlignmentOptions.Left;
            }

            mainText.GetTextInfo(nearestCharacterNodeName);

            if (!typewriteTextController)
            {
                typewriteTextController = zoneHudElement.GetComponent<TypewriteTextController>();
                typewriteTextController.labels = new TextMeshProUGUI[] { mainText };
                typewriteTextController.soundString = "";
                typewriteTextController.disableObjectOnFadeEnd = false;
            }

            typewriteTextController.StartTyping();

            SetNotifPosition();
            SetUIDuration();
        }

        public static void SetNotifPosition()
        {
            if (!zoneHudElement) return;
            if (!canLoad) return;
            zoneHudElement.transform.localPosition = new Vector3(cfgXHeight.Value + 460, cfgYHeight.Value);
        }

        public static void SetUIDuration()
        {
            if (!typewriteTextController) return;
            typewriteTextController.fadeOutDelay = fadeDelay.Value;
            typewriteTextController.fadeOutAfterCompletion = (fadeDelay.Value > 0);
        }

        [ConCommand(commandName = "set_zone", flags = ConVarFlags.None, helpText = "CalloutZones debug command for setting the name for zone painting: `set_zone [name | empty to clear]`")]
        public static void SetZoneName(ConCommandArgs args)
        {
            var name = "";
            if (args.Count > 0)
            {
                name = args.GetArgString(0);
            }
            if (!historicalLocations.ContainsKey(name) && !string.IsNullOrEmpty(name))
            {
                historicalLocations[name] = new HashSet<int>();
            }
            currentZone = name;
            if (!zoneColors.ContainsKey(currentZone) && !string.IsNullOrEmpty(name))
            {
                zoneColors[currentZone] = UnityEngine.Random.ColorHSV();
            }
        }

        [ConCommand(commandName = "show_zones", flags = ConVarFlags.None, helpText = "CalloutZones debug command for painting all the nodes with their current name. `show_zones [0 to clear]`")]
        public static void ShowZones(ConCommandArgs args)
        {
            var duration = float.PositiveInfinity;
            if (args.Count > 0)
            {
                duration = args.GetArgFloat(0);
            }
            if (duration == 0)
            {
                foreach(var key in nodePings.Keys)
                {
                    nodePings[key].DestroyPing();
                }
                nodePings = new Dictionary<int, PingIndicator>();
                return;
            }
            var groundNodes = SceneInfo.instance.groundNodes;

            foreach (var node in groundNodes.GetActiveNodesForHullMask(HullMask.Human))
            {
                string zoneName = "";
                if (stageZoneMappings[currentScene].ContainsKey(node.nodeIndex))
                {
                    zoneName = stageZoneMappings[currentScene][node.nodeIndex];
                }

                if (!historicalLocations.ContainsKey(zoneName))
                {
                    historicalLocations[zoneName] = new HashSet<int>();
                }
                historicalLocations[zoneName].Add(node.nodeIndex);

                if (nodePings.ContainsKey(node.nodeIndex))
                {
                    nodePings[node.nodeIndex].DestroyPing();
                    nodePings.Remove(node.nodeIndex);
                }
                if (!zoneColors.ContainsKey(zoneName))
                {
                    if (!string.IsNullOrWhiteSpace(zoneName))
                    {
                        zoneColors[zoneName] = UnityEngine.Random.ColorHSV();
                    }
                    else
                    {
                        zoneColors[zoneName] = Color.white;
                    }
                }
                groundNodes.GetNodePosition(node, out var nodePosition);
                nodePings[node.nodeIndex] = CustomDrawPing(nodePosition, zoneName);
            }
        }

        [ConCommand(commandName = "check_zones", flags = ConVarFlags.None, helpText = "CalloutZones debug command for outputting current stage zone arrays")]
        public static void CheckZones(ConCommandArgs args)
        {
            string output = "";
            foreach (var zone in historicalLocations.Keys)
            {
                var locations = new HashSet<int>();
                historicalLocations.TryGetValue(zone, out locations);
                if (locations.Count == 0 || string.IsNullOrWhiteSpace(zone))
                {
                    continue;
                }
                string idList = string.Join(",", locations);
                output += "{ \"" + zone + "\", new HashSet<int> {" + idList + "} },\n";
            }
            Debug.Log(output);
        }
    }

    public enum UIDisplayOption
    {
        Never = 0,
        Once = 1,
        Always = 2
    }
}