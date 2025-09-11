using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using System.Linq;
using UnityEngine;
using Zorro.Core;
using Logger = UnityEngine.Logger;

namespace RandomAirstrikes;

[BepInAutoPlugin]
internal partial class Plugin : BaseUnityPlugin {
    internal static new ManualLogSource Logger;
    public bool IsNetHost => PhotonNetwork.IsMasterClient;

    // fix an issue that causes people to break when the mod is uninstalled after beating an ascent higher than 7
    internal static ConfigEntry<int> meanSecondsBetweenStrikes;
    private int SecondsBetweenStrikes => meanSecondsBetweenStrikes.Value;

    internal void Awake() {
        Logger = base.Logger;

        meanSecondsBetweenStrikes = Config.Bind("General", "MeanTimeBetweenStrikesInSeconds", 15, "");

        this.secondsTillNextDrop = SecondsBetweenStrikes;

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
    private const float airstrikeDropDistance = 2;

    internal void Update() {
        if (!IsNetHost) return;
        var charData = Character.localCharacter?.data;
        if (charData == null || charData.passedOutOnTheBeach > 0) return;

        var progressHandler = Singleton<MountainProgressHandler>.Instance;        
        if (progressHandler == null || progressHandler.progressPoints.Single(x => x.biome == Biome.BiomeType.Peak).Reached) return;

        this.secondsTillNextDrop -= Time.deltaTime;
        if (this.airstrikeTarget != null && (this.timeTillAirstrike -= Time.deltaTime) < 0) {
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
        this.secondsTillNextDrop = SecondsBetweenStrikes;

        var chars = Character.AllCharacters.ToArray();
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
            DropLitDynamiteAtStartPosition(playerPos + planeFlightDirection * i * airstrikeDropDistance);
        }
    }

    private void DropLitDynamiteAtStartPosition(Vector3 pos) {
        GameObject component = PhotonNetwork.InstantiateItemRoom("Dynamite", pos, Quaternion.Euler(Vector3.up)); // +10 feels fine when dropped onto player
        Item spawnedItem = component.GetComponent<Item>();
        var dynamite = spawnedItem.GetComponentInParent<Dynamite>();
        dynamite.startingFuseTime = 15;

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
    }
}