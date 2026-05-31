using RoR2;
using BepInEx;
using System.Collections.Generic;
using UnityEngine;
using RoR2.UI;
using static RoR2.Navigation.NodeGraph;
using System;
using BepInEx.Configuration;
using TMPro;
using UnityEngine.XR;
using static RoR2.UI.TypewriteTextController;
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

        public const int MaxNodeDistance = 20;

        public static ConfigEntry<UIDisplayOption> showOnScreen { get; set; }
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
        public static GameObject zoneHudElement;
        public static HGTextMeshProUGUI mainText;
        public static TypewriteTextController typewriteTextController;
        public static bool canLoad = false;

        private static int messageFailures = 0;

        public void Awake()
        {
            showOnScreen = Config.Bind<UIDisplayOption>(
                "Functionality",
                "Show on screen",
                UIDisplayOption.Always,
                "Adds the current zone to your UI's healthbar group. Shows briefly upon entering a zone. If set to \"Once\" will only show that zone one time."
            );

            showOnPingGround = Config.Bind<bool>(
                "Functionality",
                "Show on ping ground",
                true,
                "When pinging the ground in a zone, changes the ping message."
            );

            showOnPingInteractable = Config.Bind<bool>(
                "Functionality",
                "Show on ping interactable",
                true,
                "When pinging an interactable in a zone, changes the ping message."
            );

            cfgXHeight = Config.Bind<int>(
                "Display",
                "X Offset",
                460,
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

            On.RoR2.PlayerCharacterMasterController.Update += UpdatePosition;
            On.RoR2.Language.GetString_string += GetStringOverride;
            On.RoR2.UI.PingIndicator.RebuildPing += PingIndicator_RebuildPing_GrabNearestNodeName;
            On.RoR2.Run.BeginStage += Run_BeginStage;
            On.RoR2.UI.HUD.Awake += HUD_Awake;
            cfgXHeight.SettingChanged += CfgXHeight_SettingChanged;
            cfgYHeight.SettingChanged += CfgYHeight_SettingChanged;
        }

        private static void CfgYHeight_SettingChanged(object sender, EventArgs e)
        {
            SetNotifPosition();
        }

        private static void CfgXHeight_SettingChanged(object sender, EventArgs e)
        {
            SetNotifPosition();
        }

        private void HUD_Awake(On.RoR2.UI.HUD.orig_Awake orig, RoR2.UI.HUD self)
        {
            orig(self);
            hud = self;
        }

        private void Run_BeginStage(On.RoR2.Run.orig_BeginStage orig, Run self)
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
            messageFailures = 0;

            orig(self);
        }

        private void PingIndicator_RebuildPing_GrabNearestNodeName(On.RoR2.UI.PingIndicator.orig_RebuildPing o, PingIndicator self)
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
            o(self);
        }

        private string GetStringOverride(On.RoR2.Language.orig_GetString_string o, string token)
        {
            if (!string.IsNullOrWhiteSpace(nearestPingNodeName)
                && ((token == "PLAYER_PING_DEFAULT" && showOnPingGround.Value)
                || ((token == "PLAYER_PING_INTERACTABLE" || token == "PLAYER_PING_INTERACTABLE_WITH_COST") && showOnPingInteractable.Value)))
            {
                return o(token) + $" <color=#{ColorUtility.ToHtmlStringRGB(pingColor.Value)}>({nearestPingNodeName})</color>";
            }
            else
            { 
                return o(token);
            }
        }

        private void UpdatePosition(On.RoR2.PlayerCharacterMasterController.orig_Update o, PlayerCharacterMasterController self)
        {
            o(self);
            var body = self.body;
            if (body != null)
            {
                var groundNodes = SceneInfo.instance.groundNodes;
                if (groundNodes != null)
                {
                    var closestNodeIndex = groundNodes.FindClosestNode(body.transform.position, HullClassification.Human, MaxNodeDistance);
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
                Destroy(assignStageToken);

                var mainTextTransform = zoneHudElement.transform.Find("MainText");
                mainText = mainTextTransform.GetComponent<HGTextMeshProUGUI>();
                mainText.GetTextInfo(nearestCharacterNodeName);
                mainText.alignment = TextAlignmentOptions.Left;

                try
                {
                    var subText = zoneHudElement.transform.Find("Subtext");
                    subText.GetComponent<HGTextMeshProUGUI>().GetTextInfo("");
                }
                catch  { }

                typewriteTextController = zoneHudElement.GetComponent<TypewriteTextController>();
                typewriteTextController.UpdateAllLabelTextInfo();
                typewriteTextController.labels = new TextMeshProUGUI[] { mainText };
                typewriteTextController.soundString = "";
                typewriteTextController.disableObjectOnFadeEnd = false;
            }

            if (!zoneHudElement)
                return;

            mainText.GetTextInfo(nearestCharacterNodeName);

            try
            {
                typewriteTextController.StartTyping();
            }
            catch (Exception ex)
            {
                messageFailures++;
                if (messageFailures == 3)
                {
                    Debug.Log("Switching TypewriteTextController behaviors to traceable output");
                    On.RoR2.UI.TypewriteTextController.GenerateTimingInfo += TypewriteTextController_GenerateTimingInfo;
                    On.RoR2.UI.TypewriteTextController.SetTime += TypewriteTextController_SetTime;
                }
                
            }
            SetNotifPosition();
        }

        private static void TypewriteTextController_GenerateTimingInfo(On.RoR2.UI.TypewriteTextController.orig_GenerateTimingInfo o, TypewriteTextController self)
        {
            string failureTracePoint = "0";
            try
            {
                self.UpdateAllLabelTextInfo();
                failureTracePoint = "1";
                TimedTextChunk currentTimedTextChunk = default(TimedTextChunk);
                bool flag = self.delayBetweenSentences > 0f;
                failureTracePoint = "A";
                bool flag2 = self.delayBetweenTexts > 0f;
                failureTracePoint = "B";
                bool flag3 = self.delayBetweenNewLines > 0f;
                failureTracePoint = "C";
                self.totalCharacterCount = 0;
                failureTracePoint = "D";
                for (int i = 0; i < self.labels.Length; i++)
                {
                    TextMeshProUGUI textMeshProUGUI = self.labels[i];
                    failureTracePoint = "E0";
                    if (!textMeshProUGUI)
                    {
                        continue;
                    }
                    TMP_TextInfo textInfo = textMeshProUGUI.textInfo;
                    failureTracePoint = "E1";
                    StartNewChunk(textMeshProUGUI, self.delayBetweenKeys);
                    failureTracePoint = "E2";
                    self.totalCharacterCount += textInfo.characterCount;
                    failureTracePoint = "E3";
                    if (flag2 && i > 0)
                    {
                        currentTimedTextChunk.duration = self.delayBetweenTexts;
                        failureTracePoint = "E4";
                        StartNewChunk(textMeshProUGUI, self.delayBetweenKeys);
                        failureTracePoint = "E5";
                    }
                    else if (i == 0)
                    {
                        currentTimedTextChunk.duration = self.initialDelay;
                        failureTracePoint = "E6";
                        StartNewChunk(textMeshProUGUI, self.delayBetweenKeys);
                        failureTracePoint = "E7";
                    }
                    failureTracePoint = "F";
                    int num = Math.Min(textInfo.characterCount, textInfo.characterInfo.Length);
                    failureTracePoint = "G";
                    while (currentTimedTextChunk.endCharIndex < num)
                    {
                        ref TMP_CharacterInfo reference = ref textInfo.characterInfo[currentTimedTextChunk.endCharIndex];
                        failureTracePoint = "H1";
                        if (flag && IsEndOfSentence(textInfo, in reference))
                        {
                            StartNewChunk(textMeshProUGUI, self.delayBetweenSentences);
                            failureTracePoint = "H2";
                            currentTimedTextChunk.endCharIndex++;
                            failureTracePoint = "H3";
                            StartNewChunk(textMeshProUGUI, self.delayBetweenKeys);
                            failureTracePoint = "H4";
                        }
                        else if (flag3 && reference.character == '\n')
                        {
                            StartNewChunk(textMeshProUGUI, self.delayBetweenNewLines);
                            failureTracePoint = "H5";
                            currentTimedTextChunk.endCharIndex++;
                            failureTracePoint = "H6";
                            StartNewChunk(textMeshProUGUI, self.delayBetweenKeys);
                            failureTracePoint = "H7";
                        }
                        currentTimedTextChunk.endCharIndex++;
                        failureTracePoint = "H8";
                    }
                }
                StartNewChunk(null, 0f);
                failureTracePoint = "I";
                self.textChunks = sharedChunkBuilder.ToArray();
                failureTracePoint = "J";
                TypewriteTextController.sharedChunkBuilder.Clear();
                failureTracePoint = "K";
                self.totalTypingDuration = 0f;
                failureTracePoint = "L";
                if (self.textChunks.Length != 0)
                {
                    failureTracePoint = "M";
                    ref TimedTextChunk reference2 = ref self.textChunks[self.textChunks.Length - 1];
                    failureTracePoint = "N";
                    self.totalTypingDuration = reference2.startTime + reference2.duration;
                    failureTracePoint = "O";
                }
                self.totalFadingDuration = self.fadeOutDelay + self.fadeOutDuration;
                failureTracePoint = "P";
                self.typingTimeScale = 1f;
                self.fadingTimeScale = 1f;
                failureTracePoint = "Q";
                if (self.timeToFit > 0f)
                {
                    failureTracePoint = "R";
                    if (self.includeFadeoutInTimeToFit)
                    {
                        failureTracePoint = "S";
                        self.fadingTimeScale = (self.typingTimeScale = (self.totalTypingDuration + self.fadeOutDelay + self.fadeOutDuration) / self.timeToFit);
                        failureTracePoint = "T";
                    }
                    else
                    {
                        self.typingTimeScale = self.totalTypingDuration / self.timeToFit;
                        failureTracePoint = "U";
                    }
                }
                void StartNewChunk(TextMeshProUGUI label, float interval)
                {
                    failureTracePoint = "V0";
                    if (currentTimedTextChunk.endCharIndex != currentTimedTextChunk.startCharIndex)
                    {
                        failureTracePoint = "V1";
                        currentTimedTextChunk.duration = (float)(currentTimedTextChunk.endCharIndex - currentTimedTextChunk.startCharIndex) * currentTimedTextChunk.interval;

                        failureTracePoint = "V2";
                    }
                    if (currentTimedTextChunk.duration > 0f)
                    {
                        failureTracePoint = "V3";
                        sharedChunkBuilder.Add(currentTimedTextChunk);
                        failureTracePoint = "V4";
                    }
                    float startTime = currentTimedTextChunk.startTime + currentTimedTextChunk.duration;
                    failureTracePoint = "V5";
                    int num2 = currentTimedTextChunk.endCharIndex;
                    failureTracePoint = "V6";
                    if ((object)label != currentTimedTextChunk.label)
                    {
                        failureTracePoint = "V7";
                        num2 = 0;
                    }
                    currentTimedTextChunk = new TimedTextChunk
                    {
                        label = label,
                        startTime = startTime,
                        duration = 0f,
                        startCharIndex = num2,
                        endCharIndex = num2,
                        interval = interval
                    };
                    failureTracePoint = "V8";
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Exception in GenerateTimingInfo at trace point {failureTracePoint}: {ex}");
            }
        }

        private static void TypewriteTextController_SetTime(On.RoR2.UI.TypewriteTextController.orig_SetTime orig, TypewriteTextController self, float t)
        {
            string failureTracePoint = "A";
            try
            {
                float num = self.totalTypingDuration / self.typingTimeScale;
                failureTracePoint = "B";
                float typingTime = Mathf.Clamp(t * self.typingTimeScale, 0f, self.totalTypingDuration);
                failureTracePoint = "C";
                float fadingTime = Mathf.Clamp((t - num) * self.fadingTimeScale, 0f, self.totalFadingDuration);
                failureTracePoint = "D";
                self.SetTypingTime(typingTime);
                failureTracePoint = "E";
                self.SetFadingTime(fadingTime);
                failureTracePoint = "F";
                if (self.isDoneTyping && self.isDoneFading)
                {
                    failureTracePoint = "G";
                    self.isPlayingAnimation = false;
                }
                failureTracePoint = "H";
            }
            catch(Exception ex)
            {
                Debug.LogError($"Exception in SetTime at trace point {failureTracePoint}: {ex}");
            }
        }

        public static void SetNotifPosition()
        {
            if (!zoneHudElement) return;
            if (!canLoad) return;
            zoneHudElement.transform.localPosition = new Vector3(cfgXHeight.Value, cfgYHeight.Value);
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

        [ConCommand(commandName = "show_zones", flags = ConVarFlags.None, helpText = "CalloutZones debug command for painting all the nodes with their name")]
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