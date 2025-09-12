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
    internal static ConfigEntry<float> AirstrikeFuseDurationCfg;
    private float MeanSecondsBetweenStrikes => meanSecondsBetweenStrikesCfg.Value;
    private float RangeSecondsBetweenStrikes => rangeSecondsBetweenStrikesCfg.Value;
    private float QueueProcessFrameDelaySeconds => QueueProcessFrameDelaySecondsCfg.Value;
    private float GetNextStrikeDelay() => Random.Range(MeanSecondsBetweenStrikes - RangeSecondsBetweenStrikes, MeanSecondsBetweenStrikes + RangeSecondsBetweenStrikes);
    private float AirstrikeDropDistance => AirstrikeDropDistanceCfg.Value;
    private float AirstrikeFuseDuration => AirstrikeFuseDurationCfg.Value;

    internal void Awake() {
        Logger = base.Logger;

        meanSecondsBetweenStrikesCfg = Config.Bind("General", "MeanTimeBetweenStrikesInSeconds", 15f, "Time in seconds between strikes, on average");
        rangeSecondsBetweenStrikesCfg = Config.Bind("General", "TimeBetweenStrikesRangeInSeconds", 10f,
            "How much the drop delay may deviate from the mean (+/-), so the delay is somewhere between [MeanTime-Range, MeanTime+Range]; Min value: MeanTime");

        if (rangeSecondsBetweenStrikesCfg.Value > meanSecondsBetweenStrikesCfg.Value) { // enforce the minimum 
            Logger.LogWarning($"TimeBetweenStrikesRangeInSeconds value of {rangeSecondsBetweenStrikesCfg.Value} is too large, setting it to MeanTimeBetweenStrikesInSeconds={meanSecondsBetweenStrikesCfg.Value}");
            rangeSecondsBetweenStrikesCfg.Value = rangeSecondsBetweenStrikesCfg.Value;
        }


        QueueProcessFrameDelaySecondsCfg = Config.Bind("General", "DynamiteSpawnDelayInSeconds", 0.05f, "Delay between dynamite spawns");
        AirstrikeDropDistanceCfg = Config.Bind("General", "AirstrikeDropDistance", 2f, "Distance in meters between the individual pieces of dynamite in the drop-line");
        AirstrikeFuseDurationCfg = Config.Bind("General", "AirstrikeFuseDuration", 15f, "The fuse of the dynamite when it starts dropping");

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

    private Queue<Action> SpawnQueue = new();
    private float queueProcessCountdownSeconds = 0;
    internal void Update() {
        if (!IsNetHost) return;
        var dt = Time.deltaTime;

        var charData = Character.localCharacter?.data;
        if (charData == null || charData.passedOutOnTheBeach > 0) { this.SpawnQueue.Clear(); return; }
        if (this.SpawnQueue.Count > 0 && (this.queueProcessCountdownSeconds -= dt) <= 0) {
            this.SpawnQueue.Dequeue().Invoke();
            this.queueProcessCountdownSeconds = QueueProcessFrameDelaySeconds;
        }

        var progressHandler = Singleton<MountainProgressHandler>.Instance;
        if (progressHandler == null || progressHandler.progressPoints.Single(x => x.biome == Biome.BiomeType.Peak).Reached) return;

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

        var chars = Character.AllCharacters.Where(x => !x.data.dead).ToArray();
        if (chars.Length == 0) { return; }
        var targetChar = chars[Random.Range(0, chars.Length)];
        // IF HOST

        // prepare and then run airstrike
        if (this.airstrikeTarget == null) {
            this.timeTillAirstrike = airstrikePlayerVelocityEstimationTime;
            this.lastPos = targetChar.Center;
            this.airstrikeTarget = targetChar;
        }

    }

    private void AirstrikeAtPlayerPosition(Vector3 playerPos, Vector3 planeFlightDirection) {
        for (int i = -1; i < 6; i++) {
            DropLitDynamiteAtStartPosition(playerPos + planeFlightDirection * i * AirstrikeDropDistance);
        }
    }

    private void DropLitDynamiteAtStartPosition(Vector3 pos) {
        this.SpawnQueue.Enqueue(() => {
            GameObject component = PhotonNetwork.InstantiateItemRoom("Dynamite", pos, Quaternion.Euler(Vector3.up)); // +10 feels fine when dropped onto player
            Item spawnedItem = component.GetComponent<Item>();
            var dynamite = spawnedItem.GetComponentInParent<Dynamite>();
            dynamite.startingFuseTime = AirstrikeFuseDuration;

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
}