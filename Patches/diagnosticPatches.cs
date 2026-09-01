using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

// ============================================================================
// TEMPORARY INSTRUMENTATION — no behaviour changes.
//
// Two crashes only reproduce in multiplayer and never in the unmodded game:
//
//   (1) On a joining CLIENT (never the host): PlayerValues.Update() and
//       HUDTween.Update() throw NullReferenceException every frame, and the
//       client is told "You joined mid match. You will be spawned next round."
//       even though the host sees it fully in the game.
//
//   (2) After round 1: weapons only land on the ground, with NREs out of
//       ItemBehaviour.OnDrop, ItemBehaviour.Start, and the drop-observer RPC,
//       plus FishNet "server is not active" warnings.
//
// Everything in this file is read-and-log only. Every prefix here returns void
// so the original method still runs and still throws exactly as it does today —
// the goal of this pass is evidence, not a fix. Fixes come after a repro
// session identifies which candidate below is the real one.
//
// The vanilla trigger for the "joined mid match" text is (decompiled):
//     if (!midMatchJoin && !midMatchTrigger
//         && GameObject.FindGameObjectsWithTag("PlayerGraphics").Length != 0
//         && LobbyController.Instance.LocalPlayerController.PlayerSpawner.player == null)
// i.e. the client's own PlayerManager.player is null. That field is assigned in
// exactly one place — inside PlayerManager.SpawnPlayer(), reached only through a
// [ServerRpc] with no RunLocally — so it is set on the SERVER only. That is why
// the host is unaffected, and it means the real question is "why did this
// client's player object fail to come up locally".
// ============================================================================

/// <summary>
/// Switches for bisecting which patch is responsible. Flip one, rebuild, rejoin,
/// and see whether the symptoms disappear — far more conclusive than reading
/// logs alone. These gate mod behaviour that is suspected of causing the bugs,
/// so with a flag set to true the mod is deliberately partly disabled.
/// </summary>
public static class DiagnosticFlags
{
    /// <summary>Skips RouletteState.Reset() in PlayerPickupAwakePatch (tests candidate A2).</summary>
    public static bool SkipRouletteResetOnAwake = false;

    /// <summary>Skips runtime SpawnablePrefabs registration in RoulettePrefabRegistration (tests candidate A1).</summary>
    public static bool SkipPrefabRegistration = false;

    /// <summary>Skips replacing ItemSpawner.itemToSpawn with the roulette prefab (tests candidate C1).</summary>
    public static bool SkipRouletteSpawnerReplacement = false;

    /// <summary>
    /// OnGrab still observes and logs, but never rolls or sends. Separates "the roll broke
    /// it" from "the Roulette Item itself broke it" — the first thing to try when the
    /// two-player run goes wrong.
    /// </summary>
    public static bool SkipRouletteRoll = false;

    /// <summary>
    /// The [RR:...] roll trace. On by default: these are one line per step per roll, not
    /// per frame, so the volume is fine, and a two-player repro is expensive enough that
    /// the first run needs to explain itself. Turn down once the trace is consistently
    /// clean — the instrumentation stays either way.
    /// </summary>
    public static bool VerboseRouletteLogging = true;
}

/// <summary>
/// Shared helpers. The Update() methods below run once per player object per
/// frame on every peer, so logging unconditionally would bury the log in
/// duplicates. LogOnChange only emits when an object's state string actually
/// differs from the last one recorded for it, which turns "spamming every
/// frame" into "one line per transition" — including the exact frame a value
/// finally resolves, or silence proving it never did.
/// </summary>
public static class DiagLog
{
    private static readonly Dictionary<int, string> lastState = new();

    // Every round respawns the player objects, so each round contributes a fresh set
    // of instance IDs that will never be seen again. Drop the whole table once it gets
    // large rather than leaking for a long session; the only cost is one extra
    // "first observation" line per live object afterwards.
    private const int MaxTrackedObjects = 512;

    public static void LogOnChange(Object owner, string label, string state)
    {
        int key = owner.GetInstanceID();
        if (lastState.TryGetValue(key, out string previous) && previous == state) return;

        if (lastState.Count >= MaxTrackedObjects) lastState.Clear();

        lastState[key] = state;
        Plugin.BepinLogger.LogInfo($"[Diag:{label}] frame={Time.frameCount} id={key} {state}");
    }

    public static void Log(string label, string message)
    {
        Plugin.BepinLogger.LogInfo($"[Diag:{label}] frame={Time.frameCount} {message}");
    }

    /// <summary>
    /// One step of a roulette roll. The roll crosses two machines, so a failure can land in
    /// any of six places and the visible symptom (a weapon on the ground, or nothing at all)
    /// is identical for all of them — these lines are what tell them apart, and
    /// `grep '\[RR:'` over either peer's LogOutput.log extracts the whole trace.
    ///
    /// The rollId matters more than it looks: two players rolling in the same second produce
    /// interleaved lines across two separate log files, and without the id there is no way
    /// to stitch a client's [RR:roll] to the host's [RR:server-spawn].
    ///
    /// Expected healthy sequence for one roll:
    ///     grab -> roll -> send      (owner client)
    ///     server-spawn              (host)
    ///     setspawned -> equip -> equipped -> despawn   (owner client)
    /// </summary>
    public static void RR(int rollId, string step, string message)
    {
        if (!DiagnosticFlags.VerboseRouletteLogging) return;
        Plugin.BepinLogger.LogInfo($"[RR:{step} #{rollId}] frame={Time.frameCount} {message}");
    }

    /// <summary>Unity's == is overloaded so destroyed-but-not-collected objects report null too, which is what we want here.</summary>
    public static string Describe(Object o) => o == null ? "null" : o.name;

    public static string NetRoles() =>
        $"IsServer={FishNet.InstanceFinder.IsServer} IsClient={FishNet.InstanceFinder.IsClient}";
}

// ---------------------------------------------------------------------------
// Issue 1 — client-join failure
// ---------------------------------------------------------------------------

/// <summary>
/// Candidate A4: rule out the ordinary nulls before assuming a networking race.
/// Vanilla PlayerValues.Update() dereferences several things, any of which
/// produces the reported NRE:
///     if (!IsOwner) {
///         voiceChatSource.mute != SyncAccessor_playerClient.voiceChatSource.mute
///         typingIndicator.SetActive(SyncAccessor_playerClient.SyncAccessor_IsTyping)
///     }
///     if ((bool)setup.hat && !setup.hat.activeSelf)   // runs for owner AND non-owner
/// `setup` is assigned only in Start(), and that last line runs on every peer —
/// worth watching given the host never crashes.
/// </summary>
[HarmonyPatch(typeof(PlayerValues), "Update")]
public class PlayerValuesUpdateDiagPatch
{
    static void Prefix(PlayerValues __instance)
    {
        // FishNet's generated SyncAccessor_ property isn't expressible in C#; call the
        // generated getter directly, same as the rest of this mod does for sync vars.
        ClientInstance client = __instance.sync___get_value_playerClient();
        Traverse t = Traverse.Create(__instance);

        DiagLog.LogOnChange(__instance, "PlayerValues.Update",
            $"obj={__instance.gameObject.name} IsOwner={__instance.IsOwner} {DiagLog.NetRoles()} " +
            $"playerClient={(client == null ? "NULL" : "ok")} " +
            $"playerClient.voiceChatSource={(client == null ? "n/a" : (client.voiceChatSource == null ? "NULL" : "ok"))} " +
            $"voiceChatSource={(t.Field("voiceChatSource").GetValue<AudioSource>() == null ? "NULL" : "ok")} " +
            $"typingIndicator={(__instance.typingIndicator == null ? "NULL" : "ok")} " +
            $"setup={(t.Field("setup").GetValue<PlayerSetup>() == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// Pins down ordering: did playerClient exist by the time Start() finished?
/// PlayerValues.Start() only does `setup = GetComponent&lt;PlayerSetup&gt;()`, so if
/// setup is null here the component is missing outright rather than racing.
/// </summary>
[HarmonyPatch(typeof(PlayerValues), "Start")]
public class PlayerValuesStartDiagPatch
{
    static void Postfix(PlayerValues __instance)
    {
        DiagLog.Log("PlayerValues.Start",
            $"obj={__instance.gameObject.name} IsOwner={__instance.IsOwner} {DiagLog.NetRoles()} " +
            $"playerClient={(__instance.sync___get_value_playerClient() == null ? "NULL" : "ok")} " +
            $"setup={(Traverse.Create(__instance).Field("setup").GetValue<PlayerSetup>() == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// Candidate A3. HUDTween.Start() caches
///     clientScript = GetComponentInParent&lt;PlayerValues&gt;().SyncAccessor_playerClient;
/// exactly once and never re-reads it, so if that SyncVar has not arrived yet the
/// field stays null for the lifetime of the object and Update() NREs forever.
/// This logs whether the one-shot cache succeeded — the difference between "a
/// transient race" and "permanently broken from frame one".
/// </summary>
[HarmonyPatch(typeof(HUDTween), "Start")]
public class HUDTweenStartDiagPatch
{
    static void Postfix(HUDTween __instance)
    {
        PlayerValues pv = __instance.GetComponentInParent<PlayerValues>();
        DiagLog.Log("HUDTween.Start",
            $"obj={__instance.gameObject.name} {DiagLog.NetRoles()} " +
            $"parentPlayerValues={(pv == null ? "NULL" : "ok")} " +
            $"cachedClientScript={(Traverse.Create(__instance).Field("clientScript").GetValue<ClientInstance>() == null ? "NULL (will NRE every frame)" : "ok")}");
    }
}

/// <summary>
/// Vanilla HUDTween.Update() dereferences clientScript, hudUp/hudDown, and
/// PauseManager.Instance — the singleton is a real candidate on a joining client
/// that has not finished setting up its UI yet, so log all of them, not just the
/// one we suspect.
/// </summary>
[HarmonyPatch(typeof(HUDTween), "Update")]
public class HUDTweenUpdateDiagPatch
{
    static void Prefix(HUDTween __instance)
    {
        Traverse t = Traverse.Create(__instance);
        DiagLog.LogOnChange(__instance, "HUDTween.Update",
            $"obj={__instance.gameObject.name} {DiagLog.NetRoles()} " +
            $"clientScript={(t.Field("clientScript").GetValue<ClientInstance>() == null ? "NULL" : "ok")} " +
            $"hudUp={(t.Field("hudUp").GetValue<Transform>() == null ? "NULL" : "ok")} " +
            $"hudDown={(t.Field("hudDown").GetValue<Transform>() == null ? "NULL" : "ok")} " +
            $"PauseManager.Instance={(PauseManager.Instance == null ? "NULL" : "ok")}");
    }
}

/// <summary>
/// The "joined mid match" symptom reduces to PlayerManager.player being null on
/// this client. Watching it on change timestamps exactly when the client gives
/// up on its own player object, which can then be lined up against the prefab
/// registration and spawn logs to see what happened just before.
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "Update")]
public class PlayerManagerPlayerDiagPatch
{
    static void Prefix(PlayerManager __instance)
    {
        // NOTE ON READING THIS LINE: LogOnChange prints the first observation of every object,
        // so a `player=null` here is normal before SpawnPlayer() has run and only means
        // "joined mid match" if it STAYS null — the game's own check runs much later. Look for
        // a following line on the same id with player=ok before concluding anything; if one
        // arrives, this was just startup ordering.
        DiagLog.LogOnChange(__instance, "PlayerManager.player",
            $"IsOwner={__instance.IsOwner} {DiagLog.NetRoles()} " +
            $"player={(__instance.player == null ? "null (fine before SpawnPlayer; only a problem if it stays null)" : "ok")} " +
            $"SpawnedObject={DiagLog.Describe(Traverse.Create(__instance).Field("SpawnedObject").GetValue<GameObject>())}");
    }
}

/// <summary>
/// Confirms whether SpawnPlayer actually ran on this peer, and with what owner.
/// This is the only place PlayerManager.player is ever assigned.
/// </summary>
[HarmonyPatch(typeof(PlayerManager), "SpawnPlayer", typeof(int), typeof(int), typeof(Vector3), typeof(Quaternion))]
public class PlayerManagerSpawnPlayerDiagPatch
{
    static void Postfix(PlayerManager __instance)
    {
        DiagLog.Log("PlayerManager.SpawnPlayer",
            $"ran on this peer. {DiagLog.NetRoles()} IsOwner={__instance.IsOwner} " +
            $"owner={(__instance.Owner == null ? "null" : __instance.Owner.ClientId.ToString())} " +
            $"player={(__instance.player == null ? "NULL" : "ok")}");
    }
}

// ---------------------------------------------------------------------------
// Issue 2 — post-round-1 item breakage
// ---------------------------------------------------------------------------

/// <summary>
/// Candidate C1, and the single cheapest test in this file. Vanilla
/// ItemBehaviour.Start() ends with:
///     if (!dispenserStart &amp;&amp; gameObject.name != "Pig Held Item")
///         groundMov = transform.DOLocalMove(transform.localPosition + transform.parent.up / 2f, ...)
/// so `transform.parent.up` NREs for any item with no parent while dispenserStart
/// is false. ItemSpawner.Spawn() parents its instance to the spawner on the
/// SERVER (`Instantiate(item, pos, rot, base.transform)`), but the copies FishNet
/// spawns on clients have no parent — and ItemSpawnerStartPatch sets
/// dispenserStart=false on the shared roulette PREFAB, so every instance
/// inherits it. If that is the bug, this logs parent=NULL dispenserStart=False on
/// the client and nowhere else.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class ItemBehaviourStartDiagPatch
{
    static void Prefix(ItemBehaviour __instance)
    {
        DiagLog.Log("ItemBehaviour.Start",
            $"weapon={__instance.weaponName} obj={__instance.gameObject.name} " +
            $"id={__instance.GetInstanceID()} {DiagLog.NetRoles()} " +
            $"dispenserStart={Traverse.Create(__instance).Field("dispenserStart").GetValue<bool>()} " +
            $"parent={(__instance.transform.parent == null ? "NULL (Start will NRE unless dispenserStart)" : __instance.transform.parent.name)} " +
            $"layer={__instance.gameObject.layer}");
    }
}

/// <summary>
/// The reported stack ends inside OnDrop. There are two distinct ways that frame
/// throws and the log cannot tell them apart on its own:
///   - vanilla body: `weaponScript.isClicked = false` with weaponScript null
///     (it is assigned only in Start(), so a half-initialized item hits this), or
///   - the mod's OnDropPatch for the Roulette Item: `tempCam.transform.forward`
///     with a null camera (PlayerPickup.cam is assigned in OnStartClient, not
///     Awake, so a fresh post-round player object may not have it yet).
/// Logging both here separates them in one run. Ordered before OnDropPatch is
/// not guaranteed, but this only reads state, so either order is fine.
/// </summary>
[HarmonyPatch(typeof(ItemBehaviour), "OnDrop")]
public class ItemBehaviourOnDropDiagPatch
{
    static void Prefix(ItemBehaviour __instance, Camera tempCam)
    {
        DiagLog.Log("ItemBehaviour.OnDrop",
            $"weapon={__instance.weaponName} obj={__instance.gameObject.name} " +
            $"id={__instance.GetInstanceID()} {DiagLog.NetRoles()} " +
            $"tempCam={(tempCam == null ? "NULL (mod OnDropPatch would NRE)" : "ok")} " +
            $"weaponScript={(Traverse.Create(__instance).Field("weaponScript").GetValue<Weapon>() == null ? "NULL (vanilla OnDrop would NRE)" : "ok")} " +
            $"parent={DiagLog.Describe(__instance.transform.parent)} layer={__instance.gameObject.layer}");
    }
}

/// <summary>
/// Candidate C3. cam / playerController / RigBuilder / the pickupPosition arrays
/// are all populated in PlayerPickup.OnStartClient(), NOT Awake(). If a
/// post-round player object never completes OnStartClient, all of them stay null
/// and both the drop path and GrabPatches' equip path fall over — which would
/// also explain rolled weapons being left on the ground, since an NRE partway
/// through GrabPatches.Postfix aborts it after the weapon was already spawned.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "OnStartClient")]
public class PlayerPickupOnStartClientDiagPatch
{
    static void Postfix(PlayerPickup __instance)
    {
        DiagLog.Log("PlayerPickup.OnStartClient", SnapshotFields(__instance, "completed"));
    }

    /// <summary>Shared with the RightHandFix probe so both report identically.</summary>
    public static string SnapshotFields(PlayerPickup pp, string note)
    {
        Traverse t = Traverse.Create(pp);
        Transform[] right = t.Field("pickupPositionRightHand").GetValue<Transform[]>();
        Transform[] left = t.Field("pickupPositionLeftHand").GetValue<Transform[]>();
        Transform[] both = pp.pickupPositionBothHand;

        // The PlayerSpawnObject probe that used to be here has served its purpose: it proved
        // that component is NOT on the player prefab, which is why the roulette roll goes over
        // Mycelium instead of a vanilla ServerRpc. Recorded in RouletteNet's class comment
        // rather than re-tested every spawn.
        return $"{note} obj={pp.gameObject.name} IsOwner={pp.IsOwner} {DiagLog.NetRoles()} " +
               $"cam={(t.Field("cam").GetValue<Camera>() == null ? "NULL" : "ok")} " +
               $"playerController={(t.Field("playerController").GetValue<FirstPersonController>() == null ? "NULL" : "ok")} " +
               $"RigBuilder={(t.Field("RigBuilder").GetValue() == null ? "NULL" : "ok")} " +
               $"pickupRight={(right == null ? "NULL" : right.Length.ToString())} " +
               $"pickupLeft={(left == null ? "NULL" : left.Length.ToString())} " +
               $"pickupBoth={(both == null ? "NULL" : both.Length.ToString())}";
    }
}

/// <summary>
/// Candidate C2. Vanilla RightHandFix() force-drops whatever is in hand when its
/// layer is 7 or 9 — and the mod's own DelayedRouletteDespawn sets the roulette
/// to layer 7 while it may still be registered as objInHand, which would make the
/// mod trigger exactly the RightHandFix -> RightHandDrop -> DropObjectServer ->
/// DropObjectObserver -> OnDrop chain from the reported stack. Logging the layer
/// and the object identity here shows whether that is what fired.
/// </summary>
[HarmonyPatch(typeof(PlayerPickup), "RightHandFix")]
public class RightHandFixDiagPatch
{
    static void Prefix(PlayerPickup __instance)
    {
        GameObject inHand = __instance.sync___get_value_objInHand();
        bool hasInHand = __instance.sync___get_value_hasObjectInHand();

        // Only interesting when it is actually about to do something.
        if (!hasInHand) return;

        DiagLog.LogOnChange(__instance, "PlayerPickup.RightHandFix",
            $"hasObjectInHand={hasInHand} objInHand={DiagLog.Describe(inHand)} " +
            $"layer={(inHand == null ? -1 : inHand.layer)} " +
            $"willForceDrop={(inHand != null && (inHand.layer == 7 || inHand.layer == 9))} | " +
            PlayerPickupOnStartClientDiagPatch.SnapshotFields(__instance, "fields:"));
    }
}

/// <summary>
/// The other place the mod injects unconditional per-peer work into scene load.
/// Times the whole of ItemSpawner.Start() (mod prefix plus vanilla body) so it
/// can be weighed against the spawn burst a joining client is processing at the
/// same moment. __state carries the timer from prefix to postfix, which keeps
/// this correct even with several spawners initialising in sequence.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "Start")]
public class ItemSpawnerStartTimingDiagPatch
{
    static void Prefix(ref Stopwatch __state) => __state = Stopwatch.StartNew();

    static void Postfix(ItemSpawner __instance, Stopwatch __state)
    {
        __state.Stop();
        DiagLog.Log("ItemSpawner.Start",
            $"spawner={__instance.gameObject.name} took {__state.Elapsed.TotalMilliseconds:F2}ms " +
            $"{DiagLog.NetRoles()}");
    }
}

/// <summary>
/// The server half of a roulette roll: vanilla PlayerSpawnObject's ServerRpc body, which
/// Instantiates the prefab the client chose and network-spawns it.
///
/// The reason this exists is the prefabId field. A prefab crosses the wire as a POSITIONAL
/// PrefabId into each peer's SpawnablePrefabs list, so if the client's table and the host's
/// table disagree, the server spawns a DIFFERENT weapon than the client rolled — silently,
/// and with no symptom other than "I got the wrong gun". Diffing this line's prefabId
/// against the client's [RR:roll] prefabId is the only way to catch that, and it cannot be
/// reconstructed after the run. (That table disagreement is crash candidate A1.)
///
/// There is no rollId here: the id lives on the client that issued the roll, and vanilla's
/// RPC has nowhere to carry it. Match these up by weapon name and frame instead.
/// </summary>
[HarmonyPatch(typeof(PlayerSpawnObject), "RpcLogic___SpawnObject_1585589339")]
public class PlayerSpawnObjectServerDiagPatch
{
    static void Postfix(PlayerSpawnObject __instance, GameObject obj, Transform player)
    {
        FishNet.Object.NetworkObject prefabNob = obj == null ? null : obj.GetComponent<FishNet.Object.NetworkObject>();

        // spawnedObject is assigned by the SetSpawnedObject OBSERVERS rpc, which the server
        // only sends here — it arrives back on a later tick — so this reads as not-yet-set
        // and that is normal, not a failure. resolvedPrefab/prefabId are the fields that
        // matter for the cross-log diff; they come straight from the RPC argument.
        GameObject spawned = __instance.spawnedObject;

        DiagLog.Log("RR:server-spawn",
            $"requestedBy={(__instance.Owner == null ? "null" : __instance.Owner.ClientId.ToString())} " +
            $"resolvedPrefab={DiagLog.Describe(obj)} " +
            $"prefabId={(prefabNob == null ? "NO-NETWORKOBJECT" : prefabNob.PrefabId.ToString())} " +
            $"collectionId={(prefabNob == null ? "n/a" : prefabNob.SpawnableCollectionId.ToString())} " +
            $"previousSpawnedObject={DiagLog.Describe(spawned)} " +
            $"position={(player == null ? "player NULL" : player.position.ToString())} " +
            $"{DiagLog.NetRoles()}");
    }
}

/// <summary>
/// Round boundary marker, so the log can be read as "before round 1 / after
/// round 1" — the whole reported symptom is that things break only after the
/// first round. ItemSpawner.StartNewRound() is what re-spawns items each round.
/// </summary>
[HarmonyPatch(typeof(ItemSpawner), "StartNewRound")]
public class RoundMarkerDiagPatch
{
    private static int roundCounter;

    static void Prefix(ItemSpawner __instance)
    {
        // StartNewRound fires once per spawner; only announce the first one per round.
        if (RoundGate.ShouldAnnounce())
        {
            roundCounter++;
            Plugin.BepinLogger.LogInfo(
                $"========== ROUND {roundCounter} (frame={Time.frameCount}, {DiagLog.NetRoles()}) ==========");
        }
    }

    /// <summary>
    /// Collapses the burst of per-spawner StartNewRound calls that all happen on
    /// the same frame into a single round announcement.
    /// </summary>
    private static class RoundGate
    {
        private static int lastAnnouncedFrame = -1;

        public static bool ShouldAnnounce()
        {
            if (Time.frameCount == lastAnnouncedFrame) return false;
            lastAnnouncedFrame = Time.frameCount;
            return true;
        }
    }
}
