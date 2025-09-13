using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Zorro.Core;
using Logger = UnityEngine.Logger;
using Random = UnityEngine.Random;

namespace RandomAirstrikes;

[BepInAutoPlugin]
internal partial class Plugin : BaseUnityPlugin {
    internal static new ManualLogSource Logger;
    public bool IsNetHost => PhotonNetwork.IsMasterClient;

    // fix an issue that causes people to break when the mod is uninstalled after beating an ascent higher than 7
    internal static ConfigEntry<float> meanSecondsBetweenStrikesCfg;
    internal static ConfigEntry<float> rangeSecondsBetweenStrikesCfg;
    internal static ConfigEntry<float> QueueProcessFrameDelaySecondsCfg;
    internal static ConfigEntry<float> AirstrikeDropDistanceCfg;
    internal static ConfigEntry<float> AirstrikeDropDistanceSidewaysCfg;

    internal static ConfigEntry<uint> AirstrikeDynamiteCountPerLineCfg;
    internal static ConfigEntry<uint> AirstrikeDynamiteLineCountCfg;

    internal static ConfigEntry<float> AirstrikeFuseDurationCfg;
    internal static ConfigEntry<bool> ReduceExplosionCloudDensityCfg;
    internal static ConfigEntry<float> CampfireSafeZoneRadiusCfg;
    internal static ConfigEntry<float> CampfirePostLitGracePeriodSecondsCfg;
    private float MeanSecondsBetweenStrikes => meanSecondsBetweenStrikesCfg.Value;
    private float RangeSecondsBetweenStrikes => rangeSecondsBetweenStrikesCfg.Value;
    private float QueueProcessFrameDelaySeconds => QueueProcessFrameDelaySecondsCfg.Value;
    private float GetNextStrikeDelay() => Random.Range(MeanSecondsBetweenStrikes - RangeSecondsBetweenStrikes, MeanSecondsBetweenStrikes + RangeSecondsBetweenStrikes);
    private float AirstrikeDropDistance => AirstrikeDropDistanceCfg.Value;
    private float AirstrikeDropDistanceSideways => AirstrikeDropDistanceSidewaysCfg.Value;

    private uint AirstrikeDynamiteCountPerLine => AirstrikeDynamiteCountPerLineCfg.Value;
    private uint AirstrikeDynamiteLineCount => AirstrikeDynamiteLineCountCfg.Value;

    private float AirstrikeFuseDuration => AirstrikeFuseDurationCfg.Value;
    private float CampfireSafeZoneRadius => CampfireSafeZoneRadiusCfg.Value;
    private float CampfirePostLitGracePeriodSeconds => CampfirePostLitGracePeriodSecondsCfg.Value;
    private static bool ReduceExplosionCloudDensity => ReduceExplosionCloudDensityCfg.Value;

    internal void Awake() {
        Logger = base.Logger;

        meanSecondsBetweenStrikesCfg = Config.Bind("General", "MeanTimeBetweenStrikesInSeconds", 15f, "Time in seconds between strikes, on average.");
        rangeSecondsBetweenStrikesCfg = Config.Bind("General", "TimeBetweenStrikesRangeInSeconds", 10f,
            "How much the drop delay may deviate from the mean (+/-), so the delay is somewhere between [MeanTime-Range, MeanTime+Range]; Min value: MeanTime");

        if (rangeSecondsBetweenStrikesCfg.Value > meanSecondsBetweenStrikesCfg.Value) { // enforce the minimum 
            Logger.LogWarning($"TimeBetweenStrikesRangeInSeconds value of {rangeSecondsBetweenStrikesCfg.Value} is too large, " +
                $"setting it to MeanTimeBetweenStrikesInSeconds={meanSecondsBetweenStrikesCfg.Value}");
            rangeSecondsBetweenStrikesCfg.Value = rangeSecondsBetweenStrikesCfg.Value;
        }


        QueueProcessFrameDelaySecondsCfg = Config.Bind("General", "DynamiteSpawnDelayInSeconds", 0.05f, "Delay between dynamite spawns.");
        AirstrikeDropDistanceCfg = Config.Bind("General", "AirstrikeDropDistance", 2f, "Distance in meters between the individual pieces of dynamite in the drop-line.");
        AirstrikeDropDistanceSidewaysCfg = Config.Bind("General", "AirstrikeDropDistance", 2f, "Distance in meters between the individual lines of dynamite drops.");
        AirstrikeFuseDurationCfg = Config.Bind("General", "AirstrikeFuseDuration", 15f, "The fuse of the dynamite when it starts dropping.");
        CampfireSafeZoneRadiusCfg = Config.Bind("General", "CampfireSafeZoneRadius", 35f,
            "Distance to the next unlit campfire which is considered a safe-zone, in which, players will not be targeted by airstrikes.");
        CampfirePostLitGracePeriodSecondsCfg = Config.Bind("General", "CampfirePostLitGracePeriodSeconds", 15f, "After lighting a campfire, for this duration no airstrikes will be sent.");
        AirstrikeDynamiteCountPerLineCfg = Config.Bind("General", "AirstrikeDynamiteCountPerLine", 7u, "Number of dynamite pieces that a single airstrike line consists of.");
        AirstrikeDynamiteLineCountCfg = Config.Bind("General", "AirstrikeDynamiteLineCount", 1u, "Number of dynamite lines per airstrike.");

        ReduceExplosionCloudDensityCfg = Config.Bind("Performance", "ReduceExplosionCloudDensity", true,
            "Decreases the amount of explosion cloud objects spawned (by PEAK) per explosion to 1 instead of 13. This affects ALL dynamite explosions but " +
            "massively helps performance. Set to 'false' for vanilla behaviour.");


        this.secondsTillNextDrop = MeanSecondsBetweenStrikes;

        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        Harmony harmony = new(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();
    }

    private static bool Initted = false;
    internal static void Init() {
        if (Initted) { return; }
        Initted = true;

        Logger.LogInfo($"Initted");
    }

    //private static readonly MethodInfo itemForceSyncForFrame = typeof(Item).GetMethod("ForceSyncForFrames", BindingFlags.NonPublic | BindingFlags.Instance);
    private static void ForceSyncForFrames(Item i, int frames = 10) => i.ForceSyncForFrames(frames); // itemForceSyncForFrame.Invoke(i, [frames]);

    private float secondsTillNextDrop = 0;
    private Vector3 lastPos = Vector3.zero;
    private Character airstrikeTarget = null;
    private float timeTillAirstrike = -1;
    private const float airstrikePlayerVelocityEstimationTime = 1;

    private Vector3? lastCampfirePos = null;
    private Queue<Action> SpawnQueue = new();
    private float queueProcessCountdownSeconds = 0;
    internal void Update() {
        if (!IsNetHost) return;
        var dt = Time.deltaTime;

        var charData = Character.localCharacter?.data;
        if (charData == null || charData.passedOutOnTheBeach > 0) {
            this.SpawnQueue.Clear();
            this.lastCampfirePos = null;
            return;
        }

        if (this.SpawnQueue.Count > 0 && (this.queueProcessCountdownSeconds -= dt) <= 0) {
            this.SpawnQueue.Dequeue().Invoke();
            this.queueProcessCountdownSeconds = QueueProcessFrameDelaySeconds;
        }

        var progressHandler = Singleton<MountainProgressHandler>.Instance;
        if (progressHandler == null || progressHandler.progressPoints.Single(x => x.biome == Biome.BiomeType.Peak).Reached) return; // could replace with Maphandler check maybe

        this.secondsTillNextDrop -= dt;
        if (this.airstrikeTarget != null && (this.timeTillAirstrike -= dt) < 0) {
            var currPos = this.airstrikeTarget.Center;
            Vector3 playerVelocity = (currPos - this.lastPos) / airstrikePlayerVelocityEstimationTime;
            playerVelocity.y = 0;
            var playerDirection = Vector3.zero;
            if (playerVelocity.sqrMagnitude > 0.01) {
                playerDirection = playerVelocity.normalized;
            }
            //Logger.LogWarning($"Current biome: {progressHandler.progressPoints[progressHandler.maxProgressPointReached].biome.ToString()}");
            AirstrikeAtPlayerPosition(currPos + playerVelocity + Vector3.up * 20, playerDirection);

            this.airstrikeTarget = null;
        }

        if (this.secondsTillNextDrop > 0) { return; }
        this.secondsTillNextDrop = GetNextStrikeDelay();

        var mapHandler = Singleton<MapHandler>.Instance;
        var currentMapSegment = mapHandler.segments[mapHandler.currentSegment];
        var currentCampfire = currentMapSegment.segmentCampfire;
        if (currentCampfire == null) { return; }
        var currentCampfirePos = currentCampfire.transform.position;

        if (this.lastCampfirePos == null) {
            this.lastCampfirePos = currentCampfirePos;
        } else if (Vector3.SqrMagnitude(currentCampfirePos - this.lastCampfirePos.Value) > 30 * 30f) { // if campfire moved by more than 30 units its definitely a new one
            this.secondsTillNextDrop = CampfirePostLitGracePeriodSeconds;
            lastCampfirePos = currentCampfirePos;
            return;
        }

        var chars = Character.AllCharacters
            .Where(x => !x.data.dead && currentCampfire != null && Vector3.Distance(x.Center, currentCampfirePos) > CampfireSafeZoneRadius)
            .ToArray();

        if (chars.Length == 0) { return; }
        var targetChar = chars[Random.Range(0, chars.Length)];

        // prepare and then run airstrike
        if (this.airstrikeTarget == null) {
            this.timeTillAirstrike = airstrikePlayerVelocityEstimationTime;
            this.lastPos = targetChar.Center;
            this.airstrikeTarget = targetChar;
        }

    }

    private void AirstrikeAtPlayerPosition(Vector3 playerPos, Vector3 planeFlightDirection) {
        var normal = Vector3.Cross(Vector3.up, planeFlightDirection);
        for (int i = 0; i < AirstrikeDynamiteCountPerLine; i++) {
            var lineCenterOffset = (0 + AirstrikeDynamiteLineCount - 1) / 2f;
            for (int j = 0; j < AirstrikeDynamiteLineCount; j++) {
                DropLitDynamiteAtStartPosition(playerPos + planeFlightDirection * i * AirstrikeDropDistance + normal * (j - lineCenterOffset) * AirstrikeDropDistanceSideways);
            }
        }
    }

    private static void DisableShadowsOnGameObject(GameObject obj) {
        foreach (var c in obj.GetComponentsInChildren<Renderer>()) {
            //Logger.LogWarning($"{c.name} {c.GetType().FullName.ToString()} mode was: {c.shadowCastingMode.ToString()}");
            c.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
    }

    private void DropLitDynamiteAtStartPosition(Vector3 pos) {
        this.SpawnQueue.Enqueue(() => {
            GameObject component = PhotonNetwork.InstantiateItemRoom("Dynamite", pos, Quaternion.Euler(Vector3.up)); // +10 feels fine when dropped onto player
            DisableShadowsOnGameObject(component);
            Item spawnedItem = component.GetComponent<Item>();
            var dynamite = spawnedItem.GetComponentInParent<Dynamite>();
            dynamite.startingFuseTime = AirstrikeFuseDuration;
            //DisableShadowsOnGameObject(dynamite.explosionPrefab);

            //Logger.LogWarning($"smoke vfx prefab name: {(dynamite.smokeVFXPrefab == null ? "null" : "nonnull")} {dynamite.smokeVFXPrefab?.name}");

            GlobalEvents.TriggerItemThrown(spawnedItem);
            ForceSyncForFrames(spawnedItem);
            var r = component.GetComponent<Rigidbody>();
            //r.constraints &= RigidbodyConstraints.None;
            r.isKinematic = false;
            r.angularVelocity = Vector3.up * 15;
            r.AddForce(Vector3.down * 5);
            //r.useGravity = true;
            component.GetComponent<PhotonView>().RPC("SetItemInstanceDataRPC", RpcTarget.All, spawnedItem.data);
            component.GetComponent<PhotonView>().RPC("SetKinematicRPC", RpcTarget.AllBuffered, false, spawnedItem.transform.position, spawnedItem.transform.rotation);
            dynamite.LightFlare();
        });
    }

    /// <summary>
    /// By default peak seems to spawn 13 cloud instances for explosions. This reduces this _drastically_ to just one. 
    /// While it appears to not have much effect, besides making it a tad more see-through, it massively improves performance.
    /// </summary>
    [HarmonyPatch(typeof(ExplosionEffect), nameof(ExplosionEffect.GetPoints))]
    private static class ReduceDynamiteCloudInstanceCount {
        public static void Postfix(ExplosionEffect __instance) {
            if (ReduceExplosionCloudDensity) {
                __instance.explosionPoints = [__instance.explosionPoints[0]];
                //DisableShadowsOnGameObject(__instance.explosionOrb);
            }
        }
    }
}