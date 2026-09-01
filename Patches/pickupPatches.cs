using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using DG.Tweening;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

// The roulette pools moved to rouletteState.cs, where RouletteState is now a normal class
// with one instance owned by Plugin (Plugin.RouletteState) rather than a static class.

/// <summary>
/// One-slot latch for a roll that has been sent to the server and is waiting on the
/// SetSpawnedObject observers RPC to come back. Only ever set on the peer that owns the
/// grabbing player, so a single slot is enough.
/// </summary>
internal static class PendingRoll
{
    // Generous: this covers a network round trip, not a game rule. If it ever expires we
    // want the log line, not a silently swallowed roll.
    private const int TimeoutFrames = 300;

    private static int nextRollId = 1;

    public static bool IsArmed;
    public static int RollId;
    public static PlayerPickup Pickup;
    public static bool RightHand;
    public static ItemBehaviour Roulette;
    public static int ArmedFrame;
    public static string LastStep = "none";

    public static int NextRollId() => nextRollId++;

    public static void Arm(int rollId, PlayerPickup pickup, bool rightHand, ItemBehaviour roulette)
    {
        IsArmed = true;
        RollId = rollId;
        Pickup = pickup;
        RightHand = rightHand;
        Roulette = roulette;
        ArmedFrame = Time.frameCount;
        LastStep = "send";
    }

    public static void Disarm()
    {
        IsArmed = false;
        Pickup = null;
        Roulette = null;
    }

    /// <summary>
    /// Pumped from PlayerPickupUpdatePatch. Without this a lost request would leave the
    /// latch set, and the next unrelated PlayerSpawnObject spawn would be mistaken for the
    /// answer to it.
    /// </summary>
    public static void CheckTimeout()
    {
        if (!IsArmed || Time.frameCount - ArmedFrame < TimeoutFrames) return;

        DiagLog.RR(RollId, "timeout",
            $"waitedFrames={Time.frameCount - ArmedFrame} lastStepReached={LastStep} — " +
            "no SetSpawnedObject came back. Check the HOST's log for a matching [RR:server-spawn] " +
            "to tell 'never sent' from 'never came back'.");

        // The roulette is still in hand and would stay there forever otherwise.
        if (Roulette != null) GrabPatches.DespawnRoulette(RollId, Roulette);
        Disarm();
    }
}

//all patches for the itembehaviour class related to the roulette item. This is where the pickup and drop logic is handled.

[HarmonyPatch(typeof(ItemSpawner), "Start")]
public class ItemSpawnerStartPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        //Plugin.BepinLogger.LogInfo("ItemSpawner Start called");

        // Last-resort backstop only. The prefab is registered from NetworkManager.Awake
        // (see RoulettePrefabRegistration), which is the only point early enough for a
        // client joining a match in progress - by the time this Start runs, that client has
        // already parsed the server's spawn batch and failed on it. This call normally finds
        // the work already done and logs nothing; it stays because it costs one Contains and
        // it is the only registration site that is guaranteed to run in an arena.
        RoulettePrefabRegistration.EnsureRegistered("ItemSpawner.Start");

        if (!FishNet.InstanceFinder.IsServer) return;

        if (DiagnosticFlags.SkipRouletteSpawnerReplacement)
        {
            DiagLog.Log("SpawnerReplacement", "SKIPPED via DiagnosticFlags.SkipRouletteSpawnerReplacement");
            return;
        }

        // Spawn the roulette item at the item spawner's position
        string itemName = __instance.itemToSpawn?.name ?? "null";
        __instance.itemToSpawn = Plugin.RouletteItemPrefab;
        ItemBehaviour item = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        Traverse t = Traverse.Create(item);
        t.Field("dispenserStart").SetValue(false);

        // DIAGNOSTIC (candidate C1): `item` here is the component on the shared PREFAB
        // asset, not on a scene instance — so this dispenserStart=false write persists
        // into every roulette ever spawned, on every round. Vanilla ItemBehaviour.Start()
        // dereferences transform.parent.up when dispenserStart is false, and while the
        // server parents its instance to the spawner, the copies FishNet creates on
        // clients have no parent at all. scene==null below means "this is the prefab
        // asset", which is the condition that makes the write global.
        DiagLog.Log("SpawnerReplacement",
            $"spawner={__instance.gameObject.name} replaced '{itemName}' with roulette prefab. " +
            $"mutatedObject={item.gameObject.name} " +
            $"isPrefabAsset={(!item.gameObject.scene.IsValid() ? "YES (write is global)" : "no (scene instance)")} " +
            $"dispenserStart now={t.Field("dispenserStart").GetValue<bool>()}");
        //Plugin.BepinLogger.LogInfo("dispenserStart is now " + t.Field("dispenserStart").GetValue<bool>());
        //Plugin.BepinLogger.LogInfo("Replaced " + itemName + " spawnerwith Roulette Item Prefab");
        //Plugin.BepinLogger.LogInfo("ItemSpawner Start called, spawning Roulette Item at " + __instance.transform.position);
    }
}

[HarmonyPatch(typeof(ItemSpawner), "Spawn")]
public class ItemSpawnerSpawnPatch
{
    static void Prefix(ItemSpawner __instance)
    {
        ItemBehaviour item = __instance.itemToSpawn.GetComponent<ItemBehaviour>();
        bool dispenserStart = Traverse.Create(item).Field("dispenserStart").GetValue<bool>();
        //Plugin.BepinLogger.LogInfo("dispenserStart is now " + dispenserStart);
    }
}

//all patches for the item behaviour class related to the roulette item. This is where the pickup and drop logic is handled.

// The roulette roll, in three parts across two machines:
//
//   1. GrabPatches.Postfix   — on the peer that OWNS the grabbing player: roll from the
//                              local pool, then hand the chosen prefab to the server through
//                              the vanilla PlayerSpawnObject.SpawnObject ServerRpc.
//   2. vanilla server logic  — RpcLogic___SpawnObject instantiates and network-spawns it,
//                              then answers every peer with the SetSpawnedObject observers RPC.
//   3. PlayerSpawnObjectSetSpawnedPatch — back on the owner: equip it through the same
//                              vanilla path RightHandPickup() uses.
//
// Only the one chosen prefab ever crosses the wire, so no peer learns another peer's pool.
// FishNet serializes an UNSPAWNED prefab by PrefabId + SpawnableCollectionId (see
// Writer.WriteNetworkObject), which is what makes step 1 possible without any custom
// networking of our own — and also why the peers' SpawnablePrefabs tables have to agree.
[HarmonyPatch(typeof(ItemBehaviour), "OnGrab")]
public class GrabPatches
{
    static void Postfix(ItemBehaviour __instance, bool owner, bool rightHand)
    {
        if (__instance.weaponName != "Roulette Item") return;

        int rollId = PendingRoll.NextRollId();
        try
        {
            BeginRoll(rollId, __instance, owner, rightHand);
        }
        catch (Exception e)
        {
            // Deliberately swallowed. OnGrab is reached from FishNet-generated RPC logic, and
            // an exception escaping a Harmony patch there abandons the rest of that RPC's work
            // on this peer — the same failure shape as crash candidate A2, one level up.
            Plugin.BepinLogger.LogError($"[RR:grab #{rollId}] THREW{Environment.NewLine}{e}");
        }
    }

    static void BeginRoll(int rollId, ItemBehaviour ib, bool onGrabOwnerParam, bool rightHand)
    {
        GameObject root = ib.rootObject;
        PlayerPickup pp = root == null ? null : root.GetComponent<PlayerPickup>();

        // onGrabOwnerParam is logged next to pp.IsOwner because vanilla's `owner` argument
        // looks like it means the same thing, and one run of this line settles whether it
        // does. pp.IsOwner is what the gate below actually trusts either way.
        DiagLog.RR(rollId, "grab",
            $"weapon={ib.weaponName} rootObject={(root == null ? "NULL" : root.name)} " +
            $"pp={(pp == null ? "NULL" : "ok")} pp.IsOwner={(pp == null ? "n/a" : pp.IsOwner.ToString())} " +
            $"onGrabOwnerParam={onGrabOwnerParam} rightHand={rightHand} {DiagLog.NetRoles()}");

        // OnGrab runs on every observer, so exactly one peer passes this gate: the one whose
        // local player did the grabbing. It rolls from ITS OWN pool. This used to be gated on
        // IsServer, which is why every player was really rolling from the host's unlocks.
        if (pp == null || !pp.IsOwner) return;

        if (DiagnosticFlags.SkipRouletteRoll)
        {
            DiagLog.RR(rollId, "grab", "roll SKIPPED via DiagnosticFlags.SkipRouletteRoll");
            return;
        }

        GameObject prefab = Plugin.RouletteState.Roll(rollId);
        if (prefab == null)
        {
            Plugin.BepinLogger.LogError(
                $"[RR:roll #{rollId}] obtained_Items is empty; cannot roll a weapon for the Roulette Item");
            return;
        }

        // Arm BEFORE sending. On the host, Mycelium delivers a message addressed to the local
        // Steam id synchronously, so the host's reply can come back inside this very call —
        // arming afterwards would mean the reply arrives to an unarmed latch and is dropped.
        PendingRoll.Arm(rollId, pp, rightHand, ib);

        DiagLog.RR(rollId, "send",
            $"weapon={prefab.name} to host via Mycelium " +
            $"IsClient={FishNet.InstanceFinder.IsClient} IsServer={FishNet.InstanceFinder.IsServer}");

        if (!RouletteNet.RequestSpawn(rollId, prefab.name, rightHand))
        {
            PendingRoll.Disarm();
        }
    }

    /// <summary>
    /// Equips the weapon the server just spawned, using the same sequence vanilla
    /// RightHandPickup()/LeftHandPickup() use on the owner: set the sync vars, then call the
    /// SetObjectInHandServer ServerRpc (which this client may do, because PlayerPickup is
    /// its own). Nothing here is reentrant any more — vanilla's pickup of the Roulette Item
    /// finished several frames ago — so the sync-var normalisation and early cam assignment
    /// the old in-OnGrab version needed are gone.
    /// </summary>
    public static void EquipRolledWeapon(int rollId, PlayerPickup pp, bool rightHand, GameObject spawned)
    {
        Traverse ppT = Traverse.Create(pp);
        Camera cam = ppT.Field("cam").GetValue<Camera>();
        ItemBehaviour spawnedIb = spawned.GetComponent<ItemBehaviour>();
        Weapon spawnedWeapon = spawned.GetComponent<Weapon>();

        bool requireBothHands = spawnedWeapon != null && spawnedWeapon.requireBothHands;
        bool otherHandOccupied = rightHand
            ? pp.sync___get_value_hasObjectInLeftHand()
            : ppT.Method("sync___get_value_hasObjectInHand").GetValue<bool>();

        // Off: two-handed weapons have no vanilla left-hand pickup path, so a left-held
        // roulette rolling one - or a right-held one with the left hand full - leaves the
        // weapon on the ground. On: the roll behaves like a vanilla two-handed pickup off the
        // floor, which never refuses. HandleInteraction's rule for that case is
        // "RightHandDrop(); LeftHandDrop(); RightHandPickup();" - both hands are emptied and
        // the weapon is taken in both, whichever hand the player was holding things in - so
        // the both-hand branch is taken unconditionally and the drops below match it.
        bool overrideTwoHanded = ArchipelagoMenu.RolledTwoHandedWeaponsOverride.Value;
        bool useBothHands = requireBothHands && (overrideTwoHanded || (!otherHandOccupied && rightHand));
        bool canEquip = spawnedIb != null && spawnedWeapon != null && cam != null
            && (!requireBothHands || useBothHands);

        Transform[] pickupPos = useBothHands
            ? pp.pickupPositionBothHand
            : rightHand
                ? ppT.Field("pickupPositionRightHand").GetValue<Transform[]>()
                : ppT.Field("pickupPositionLeftHand").GetValue<Transform[]>();
        // pickupPositionBothHand is indexed by camChildIndex in every vanilla caller, never by
        // camChildIndexLeftHand - which matters now that the both-hand branch is reachable
        // from a left-held roulette.
        int camChildIndex = spawnedIb == null
            ? -1
            : (useBothHands || rightHand ? spawnedIb.camChildIndex : spawnedIb.camChildIndexLeftHand);
        bool indexInRange = pickupPos != null && camChildIndex >= 0 && camChildIndex < pickupPos.Length;

        Grip[] grips = spawned.GetComponentsInChildren<Grip>();
        Transform gripRightT = grips.Length > 0 ? grips[0].transform : null;
        Transform gripLeftT = grips.Length > 1 ? grips[1].transform : null;

        string branch = !canEquip || !indexInRange ? "ground" : useBothHands ? "both" : rightHand ? "right" : "left";

        DiagLog.RR(rollId, "equip",
            $"branch={branch} weapon={spawned.name} requireBothHands={requireBothHands} " +
            $"overrideTwoHanded={overrideTwoHanded} " +
            $"otherHandOccupied={otherHandOccupied} camChildIndex={camChildIndex} " +
            $"pickupPos.Length={(pickupPos == null ? "NULL" : pickupPos.Length.ToString())} " +
            $"indexInRange={indexInRange} " +
            $"cam={(cam == null ? "NULL (candidate C3 — assigned in OnStartClient, not Awake)" : "ok")} " +
            $"gripRight={(gripRightT == null ? "null" : "ok")} gripLeft={(gripLeftT == null ? "null" : "ok")} " +
            $"camAnimScript={(ppT.Field("camAnimScript").GetValue<CameraShakeConstrains>() == null ? "NULL" : "ok")}");

        // Without a camera nothing below is safe — RightHandDrop() reaches
        // ItemBehaviour.StickOnGround() and our own OnDropPatch reads tempCam.transform, so
        // dropping would throw before anything useful happened. Leave the roulette in hand
        // instead: the player can still drop it by hand, which is a better outcome than an
        // exception halfway through the swap.
        if (cam == null)
        {
            Plugin.BepinLogger.LogError(
                $"[RR:equip #{rollId}] PlayerPickup.cam is null (candidate C3 — it is assigned in " +
                "OnStartClient, not Awake); aborting the equip and leaving the roulette in hand.");
            return;
        }

        // Take the roulette out of hand first, whichever way this goes — it is about to be
        // despawned, and leaving it registered as objInHand while its layer changes is the
        // chain behind crash candidate C2 (RightHandFix force-drops layer 7/9 items).
        if (useBothHands && otherHandOccupied)
        {
            // The weapon is about to occupy both hands, so the hand that is not holding the
            // roulette has to be emptied too — vanilla does the same before a two-handed
            // pickup. LEFT FIRST, unlike vanilla's order: RightHandDrop() ends by calling
            // SwitchWeapons() when the left hand still holds something and there is no
            // currentInteractable, which moves that item into the right hand instead of
            // dropping it. Vanilla never sees that because a floor pickup always has a
            // currentInteractable; a roll has none, so the right hand is emptied last.
            pp.LeftHandDrop();
            pp.RightHandDrop();
        }
        else if (rightHand) pp.RightHandDrop(); else pp.LeftHandDrop();

        if (branch == "ground") return;

        // ItemBehaviour.Start() is normally what assigns gripRight/gripLeft, and vanilla's
        // "already holding something" branches re-read them off objInHand after
        // SetObjectInHandServer returns to re-target IK. Assign them up front so that
        // re-target lands on the real grips rather than on nulls, which is what used to
        // leave the hand position wrong until the weapon was dropped and picked back up.
        spawnedIb.gripRight = gripRightT;
        spawnedIb.gripLeft = gripLeftT;

        // Weapon.cam is refreshed every frame by WeaponUpdate(), so a late value self-corrects.
        // Weapon.camAnimScript does NOT — it is assigned exactly once, by the vanilla post-OnGrab
        // continuation, and every drop nulls it again. Fire()'s recoil path needs both, and a
        // stale-but-non-null camAnimScript skips recoil silently instead of throwing. Set them
        // explicitly rather than depending on timing this mod does not control.
        spawnedWeapon.cam = cam;
        spawnedWeapon.camAnimScript = ppT.Field("camAnimScript").GetValue<CameraShakeConstrains>();

        if (useBothHands)
        {
            ppT.Method("sync___set_value_objInHand", spawned, true).GetValue();
            ppT.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
            ppT.Method("SetObjectInHandServer", spawned,
                pickupPos[camChildIndex].position,
                pickupPos[camChildIndex].rotation,
                cam.gameObject, true).GetValue();
            ppT.Method("SetRightIKTarget", gripRightT).GetValue();
            ppT.Method("SetLeftIKTarget", gripLeftT).GetValue();
        }
        else if (rightHand)
        {
            ppT.Method("sync___set_value_objInHand", spawned, true).GetValue();
            ppT.Method("sync___set_value_hasObjectInHand", true, true).GetValue();
            ppT.Method("SetObjectInHandServer", spawned,
                pickupPos[camChildIndex].position,
                pickupPos[camChildIndex].rotation,
                cam.gameObject, true).GetValue();
            ppT.Method("SetRightIKTarget", gripRightT).GetValue();
        }
        else
        {
            ppT.Method("sync___set_value_objInLeftHand", spawned, true).GetValue();
            ppT.Method("sync___set_value_hasObjectInLeftHand", true, true).GetValue();
            ppT.Method("SetObjectInHandServer", spawned,
                pickupPos[camChildIndex].position,
                pickupPos[camChildIndex].rotation,
                cam.gameObject, false).GetValue();
            ppT.Method("SetLeftIKTarget", gripLeftT).GetValue();
        }

        Traverse.Create(ppT.Field("RigBuilder").GetValue()).Method("Build").GetValue();

        DiagLog.RR(rollId, "equipped",
            $"objInHand={DiagLog.Describe(pp.sync___get_value_objInHand())} " +
            $"objInLeftHand={DiagLog.Describe(pp.sync___get_value_objInLeftHand())} " +
            $"layer={spawned.layer} inRightHand={spawnedWeapon.inRightHand} inLeftHand={spawnedWeapon.inLeftHand} " +
            $"cam={(spawnedWeapon.cam == null ? "null" : spawnedWeapon.cam.name)} " +
            $"camAnimScript={(spawnedWeapon.camAnimScript == null ? "null" : "ok")}");
    }

    /// <summary>
    /// Retires the Roulette Item through vanilla's own despawn. Called on the owner client,
    /// which is allowed to: PlayerPickup.HandleInteraction transfers ownership of anything
    /// picked up via the GiveOwnerToObj ServerRpc, and Weapon's DespawnObjectServer only
    /// requires IsClient anyway.
    /// </summary>
    public static void DespawnRoulette(int rollId, ItemBehaviour rouletteIb)
    {
        if (rouletteIb == null) return;
        Gun rouletteGun = rouletteIb.GetComponent<Gun>();
        if (rouletteGun == null)
        {
            Plugin.BepinLogger.LogError($"[RR:despawn #{rollId}] Roulette Item has no Gun component");
            return;
        }

        DiagLog.RR(rollId, "despawn", $"scheduled layer={rouletteIb.gameObject.layer}");
        rouletteIb.StartCoroutine(DelayedRouletteDespawn(rollId, rouletteIb, rouletteGun));
    }

    // The vanilla caller of OnGrab (PlayerPickup's SetObjectInHandObserver RPC logic)
    // force-sets obj.layer back to 8 immediately after OnGrab returns, regardless of what
    // happens in this postfix. Weapon.DespawnObject() refuses to despawn while layer is 8
    // or 9, so scheduling the despawn inline here would silently no-op forever. Deferring
    // by one frame lets that caller finish first before we force the layer back down.
    static System.Collections.IEnumerator DelayedRouletteDespawn(int rollId, ItemBehaviour rouletteIb, Gun rouletteGun)
    {
        yield return null;
        if (rouletteIb == null || rouletteGun == null) yield break;
        rouletteIb.gameObject.layer = 7;
        rouletteIb.UnsetLayer();
        yield return new WaitForSeconds(0.65f);
        if (rouletteGun == null) yield break;

        // Vanilla DespawnObject() plays the depop effect and then calls its own
        // DespawnObjectServer ServerRpc, which is what actually removes it on every peer.
        Traverse.Create(rouletteGun).Method("DespawnObject").GetValue();
        DiagLog.RR(rollId, "despawn", "executed");
    }
}

// Steps 2 and 3 of the roll (host spawns it, requester equips it) live in
// Utils/RouletteNet.cs, because they travel over Mycelium rather than over a game RPC.
//
// They were originally written against vanilla's PlayerSpawnObject.SpawnObject ServerRpc,
// which is exactly the right shape — client picks a prefab, server spawns it and answers the
// caller. A runtime probe then showed PlayerSpawnObject is not actually a component on the
// player prefab, and a client may only invoke a ServerRpc on a NetworkObject it owns, so that
// route is unreachable no matter how well it fits. RouletteNet's class comment records the
// full search and the one pure-vanilla alternative still open.


[HarmonyPatch(typeof(ItemBehaviour), "OnDrop")]
public class OnDropPatch
{
    static bool Prefix(ItemBehaviour __instance, Camera tempCam)
    {
        if (__instance.weaponName != "Roulette Item") return true;

        Traverse t = Traverse.Create(__instance);
        t.Field("dispenserStart").SetValue(false);
        Rigidbody rb = __instance.GetComponent<Rigidbody>() ?? __instance.gameObject.AddComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.drag = 0f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        // Vanilla OnDrop() assigns this private field; since we skip the vanilla body (return false)
        // it never gets set, leaving Update()'s "tempRb == null" perpetual transform.Rotate() active forever.
        t.Field("tempRb").SetValue(rb);
        float ejectForce = t.Field<float>("ejectForce").Value;
        float torqueForce = t.Field<float>("torqueForce").Value;
        rb.AddForce(tempCam.transform.forward * ejectForce, ForceMode.Impulse);
        rb.AddTorque(tempCam.transform.forward * torqueForce + __instance.transform.right * torqueForce, ForceMode.Impulse);

        return false;
    }
}

[HarmonyPatch(typeof(ItemBehaviour), "Start")]
public class StartPatches
{
    // Crash candidate C1. Vanilla Start() ends with
    //     if (!dispenserStart && gameObject.name != "Pig Held Item")
    //         groundMov = transform.DOLocalMove(transform.localPosition + transform.parent.up / 2f, ...)
    // so transform.parent.up throws for any item with no parent while dispenserStart is
    // false. ItemSpawner.Spawn() parents its instance to the spawner on the server, but the
    // copies FishNet spawns on clients — and anything spawned by PlayerSpawnObject, which is
    // now how rolled weapons arrive — have no parent at all.
    //
    // This replaces a write the mod used to do on the single instance it created itself, and
    // is strictly better for it: parentless is exactly the condition the idle-bob tween
    // cannot handle, and unlike the old write this also runs on clients, where the old
    // server-side one never did.
    static void Prefix(ItemBehaviour __instance)
    {
        if (__instance.transform.parent != null) return;

        Traverse dispenserStart = Traverse.Create(__instance).Field("dispenserStart");
        if (dispenserStart.GetValue<bool>()) return;

        dispenserStart.SetValue(true);
        DiagLog.Log("ItemBehaviour.Start",
            $"forced dispenserStart=true on parentless '{__instance.weaponName}' " +
            $"({__instance.gameObject.name}) — vanilla Start would have NREd on transform.parent.up");
    }

    static void Postfix(ItemBehaviour __instance)
    {
        // if (__instance.weaponName != "Roulette Item")
        // {
        //     Plugin.BepinLogger.LogInfo("Start called on weapon " + __instance.weaponName);  
        // }
        if (__instance.weaponName == "Roulette Item")
        {
            //Plugin.BepinLogger.LogInfo("Roulette Item Start called");

            Sprite sprintCrosshair = Resources.FindObjectsOfTypeAll<Sprite>()
                .FirstOrDefault(s => s.name == "Straftat_Crosshair03_1");
            Sprite standCrosshair = Resources.FindObjectsOfTypeAll<Sprite>()
                .FirstOrDefault(s => s.name == "Straftat_Crosshair02_0");
            if (sprintCrosshair == null || standCrosshair == null)
            {
                Plugin.BepinLogger.LogError($"Roulette Item crosshair sprites not found (sprint: {sprintCrosshair != null}, stand: {standCrosshair != null})");
            }
            Traverse.Create(__instance).Field("sprintCrosshair").SetValue(sprintCrosshair);
            Traverse.Create(__instance).Field("standCrosshair").SetValue(standCrosshair);

            GameObject depopVfx = Resources.FindObjectsOfTypeAll<GameObject>()
                .FirstOrDefault(g => g.name == "WFX_Explosion StarSmoke");
            if (depopVfx == null)
            {
                Plugin.BepinLogger.LogError("Roulette Item: WFX_Explosion StarSmoke VFX not found");
            }
            __instance.depopVFX = depopVfx;

            CreateColors.Apply(__instance.gameObject);

            var allMaterials = new System.Collections.Generic.List<Material>();
            int counter = 0;
            //Plugin.BepinLogger.LogInfo($"Renderer length is {__instance.GetComponentsInChildren<Renderer>().Length}");
            foreach (Renderer renderer in __instance.GetComponentsInChildren<Renderer>())
            {
                foreach (Material mat in renderer.materials)
                {
                    counter++;
                    //Plugin.BepinLogger.LogInfo($"Material {counter}: {mat.name}, Renderer: {renderer.name}, Shader: {mat.shader.name}");
                    allMaterials.Add(mat);
                }
            }
            Traverse.Create(__instance).Field("hoveredObjectMat").SetValue(allMaterials);
            //Plugin.BepinLogger.LogInfo("Roulette Item materials set");
        }
    }
}

// All patches for the gun class related to the roulette item.

[HarmonyPatch(typeof(Gun), "Update")]
public class RouletteGunUpdatePatch
{
    // The Roulette Item binds to the game's real Gun component (via the assetbundle's
    // stub script) so vanilla pickup/hold logic works, but none of Gun/Weapon's private
    // fields (fpArms, playerController, pauseManager, etc.) get wired up like a normal
    // spawned weapon, so its Update/WeaponUpdate NREs every frame. Skip it entirely.
    static bool Prefix(Gun __instance)
    {
        ItemBehaviour ib = __instance.GetComponent<ItemBehaviour>();
        return ib == null || ib.weaponName != "Roulette Item";
    }
}

// Temporary diagnostics: cam/camAnimScript are confirmed correctly wired on roulette-given
// weapons, but their recoil "punch" animation still doesn't play. WeaponAnimation() is a
// strict if/else-if chain (holdback / instantPush / horizontalAnimation / requireBothHands /
// plain else), each branch calling a DOTween Do*Rotation/Do*Move on base.transform or fpArms
// unconditionally — so if the animator "Shoot" trigger fires but the punch doesn't visually
// show, either a different branch is active than expected, or the DOTween call itself isn't
// having an effect. Log every field these branches read, for every weapon fire (roulette-given
// or normal), so a normal weapon's log can be diffed against a roulette-given one's.
[HarmonyPatch(typeof(Weapon), "WeaponAnimation")]
public class WeaponAnimationDebugPatch
{
    static void Prefix(Weapon __instance)
    {
        Traverse wt = Traverse.Create(__instance);
        Plugin.BepinLogger.LogInfo(
            $"[Roulette:WeaponAnimation] weapon={__instance.gameObject.name} layer={__instance.gameObject.layer} " +
            $"holdback={wt.Field("holdback").GetValue<bool>()} instantPush={wt.Field("instantPush").GetValue<bool>()} " +
            $"horizontalAnimation={wt.Field("horizontalAnimation").GetValue<bool>()} requireBothHands={__instance.requireBothHands} " +
            $"instantComebackOnFire={wt.Field("instantComebackOnFire").GetValue<bool>()} " +
            $"animationPunch={wt.Field("animationPunch").GetValue<Vector3>()} animationDuration={wt.Field("animationDuration").GetValue<float>()} " +
            $"animationVibrato={wt.Field("animationVibrato").GetValue<int>()} animationElasticity={wt.Field("animationElasticity").GetValue<float>()} " +
            $"fpArms={(__instance.fpArms == null ? "null" : __instance.fpArms.name)} elbowPivot={(__instance.elbowPivot == null ? "null" : __instance.elbowPivot.name)} " +
            $"transform.localPosition={__instance.transform.localPosition} transform.parent={(__instance.transform.parent == null ? "null" : __instance.transform.parent.name)} " +
            $"activeInHierarchy={__instance.gameObject.activeInHierarchy}");
    }
}

//all overrides for the player pickup script related to the roulette item. This is where the pickup and drop logic is handled.

[HarmonyPatch(typeof(PlayerPickup), "Awake")]
public class PlayerPickupAwakePatch
{
    static void Prefix(PlayerPickup __instance)
    {
        // DIAGNOSTIC: this prefix injects synchronous work into the Awake() of every
        // player object on every peer. On a joining client those Awakes all happen in
        // one burst as FishNet processes the initial spawn messages, so anything slow
        // here shifts the timing of everything else coming up on that prefab
        // (PlayerValues / HUDTween live on the same object). Time it to see whether
        // that is plausible or negligible. See also the A2 note in RouletteState.Reset.
        if (DiagnosticFlags.SkipRouletteResetOnAwake)
        {
            DiagLog.Log("PlayerPickup.Awake", "RouletteState init SKIPPED via DiagnosticFlags");
            return;
        }

        // EnsureInitialized, NOT Reset. PlayerManager respawns every player object every
        // round, so Awake() fires fresh each round — calling Reset() here wiped the pool
        // mid-match, repeatedly, which is why a roll never had more than the one starter
        // weapon to pick between.
        Stopwatch sw = Stopwatch.StartNew();
        Plugin.RouletteState.EnsureInitialized();
        sw.Stop();

        DiagLog.Log("PlayerPickup.Awake",
            $"RouletteState.EnsureInitialized() took {sw.Elapsed.TotalMilliseconds:F2}ms " +
            $"obtained={Plugin.RouletteState.obtained_Items.Count} " +
            $"obj={__instance.gameObject.name} {DiagLog.NetRoles()}");
    }
}

[HarmonyPatch(typeof(PlayerPickup), "Update")]
public class PlayerPickupUpdatePatch
{
    static bool Prefix(PlayerPickup __instance)
    {
        // The debug keys are gated on IsOwner because this prefix runs once per
        // PlayerPickup instance per frame — without the gate a single P press granted one
        // weapon per player in the match, and O reset the pool that many times over.
        if (__instance.IsOwner)
        {
            PendingRoll.CheckTimeout();
            DebugKeys();
        }

        Traverse trav = Traverse.Create(__instance);
        if (trav.Field("weaponInHand").GetValue<Weapon>() != null) return true;

        GameObject objInHand = trav.Method("sync___get_value_objInHand").GetValue<GameObject>();
        if (objInHand == null) return true;

        ItemBehaviour item = objInHand.GetComponent<ItemBehaviour>();
        if (item == null || item.weaponName != "Roulette Item") return true;

        // weaponInHand IS set (the roulette item's assetbundle-bound Gun component), but none of
        // Gun/Weapon's fields (fpArms, camAnimScript, etc.) are wired up like a real weapon, so
        // vanilla RightHandFix/LeftHandFix would NRE dereferencing them — run Update manually instead.
        trav.Method("UpdateIKPoistion").GetValue();

        if (!__instance.IsOwner) return false;

        // RightHandFix/LeftHandFix internally call RightHandDrop/LeftHandDrop which also
        // dereference weaponInHand — skip them when the roulette item is held
        trav.Field("dropTimer").SetValue(trav.Field("dropTimer").GetValue<float>() - Time.deltaTime);
        trav.Field("interactTimer").SetValue(trav.Field("interactTimer").GetValue<float>() - Time.deltaTime);

        if (!trav.Method("sync___get_value_hasObjectInHand").GetValue<bool>())
        {
            var pc = trav.Field("playerController").GetValue<FirstPersonController>();
            pc.movementFactor = 1f;
            pc.jumpFactor = 1f;
            pc.maxWallJumps = 1;
            pc.wallJumpFactor = 1f;
        }

        Camera cam = trav.Field("cam").GetValue<Camera>();
        if (cam != null)
        {
            trav.Method("HandleInteractionCheck").GetValue();
            trav.Method("HandleInteractEnvironment").GetValue();
            trav.Method("HandleAboubiGrab").GetValue();

            Animator animator = trav.Field("animator").GetValue<Animator>();
            Animator globalAnimator = trav.Field("globalAnimator").GetValue<Animator>();

            if (item.rightHandAnim == "")
            {
                animator.SetBool("TwoHanded", false);
                animator.SetBool("DoubleHanded", false);
                animator.SetBool("RightHanded", true);
            }
            globalAnimator.SetBool("TwoHanded", false);
            globalAnimator.SetBool("DoubleSingle", false);
            globalAnimator.SetBool("SingleHanded", true);
            globalAnimator.SetBool("LeftHanded", false);
        }

        return false;
    }

    /// <summary>
    /// The roulette debug keys, which do nothing unless Debug Buttons is ticked on this
    /// mod's Mod Menu page.
    /// </summary>
    /// <remarks>
    /// The entry is read live rather than cached, so ticking the box takes effect on the
    /// next frame instead of on the next launch. Null-checked because the entry does not
    /// exist until <c>ArchipelagoMenu.Install</c> has run, and an Install that threw would
    /// otherwise turn every frame of every match into an NRE.
    /// </remarks>
    static void DebugKeys()
    {
        if (ArchipelagoMenu.DebugButtons == null || !ArchipelagoMenu.DebugButtons.Value) return;

        RouletteState roulette = Plugin.RouletteState;

        if (Input.GetKeyDown(KeyCode.O))
        {
            roulette.Reset();
            Plugin.BepinLogger.LogInfo($"Reset roulette item lists. unowned_items: {roulette.unowned_items.Count}, obtained_Items: {roulette.obtained_Items.Count}, hasKill_Items: {roulette.hasKill_Items.Count}");
        }

        if (Input.GetKeyDown(KeyCode.P) && roulette.unowned_items.Count > 0)
        {
            int randomIndex = UnityEngine.Random.Range(0, roulette.unowned_items.Count);
            GameObject randomItem = roulette.unowned_items[randomIndex];
            if (roulette.Grant(randomItem))
            {
                Plugin.BepinLogger.LogInfo($"Added random item {randomItem.name} to obtained_Items. Remaining unowned_items: {roulette.unowned_items.Count}");
            }
        }

        // Unlocks everything at once, which is what makes the unobtainable-pickup rule
        // testable without playing far enough to earn the weapons.
        if (Input.GetKeyDown(KeyCode.I))
        {
            int moved = roulette.GrantAllUnowned();
            Plugin.BepinLogger.LogInfo($"Moved {moved} weapon(s) from unowned_items to obtained_Items. obtained_Items: {roulette.obtained_Items.Count}, unowned_items: {roulette.unowned_items.Count}");
        }

        // The other half of I: everything unlocked is marked as already used. Two keypresses
        // then put the pool in its finished state, which is how the weapon-goal percentage and
        // its pause-menu tick get tested without playing out every first kill. Local only - no
        // checks are sent, so a Reset takes it back.
        if (Input.GetKeyDown(KeyCode.L))
        {
            int earned = roulette.MarkAllObtainedKillEarned();
            Plugin.BepinLogger.LogInfo($"Moved {earned} weapon(s) from obtained_Items to hasKill_Items. hasKill_Items: {roulette.hasKill_Items.Count}, obtained_Items: {roulette.obtained_Items.Count}");
        }

        // Proves the "New Weapon Chance is honoured, and the draw inside each list is
        // fair" requirement as a number in the log rather than as a claim about the code.
        if (Input.GetKeyDown(KeyCode.K))
        {
            roulette.SelfTest(100000);
        }
    }
}



[HarmonyPatch(typeof(PlayerPickup), "RpcLogic___DropObjectObserver_2127535046")]
public class DropObserverPatch
{
    static bool Prefix(PlayerPickup __instance, GameObject obj, bool rightHand)
    {
        ItemBehaviour item = obj?.GetComponent<ItemBehaviour>();
        if (item == null || item.weaponName != "Roulette Item") return true;

        Traverse trav = Traverse.Create(__instance);

        if (!__instance.IsOwner)
            item.StickOnGroundObservers();

        obj.transform.DOKill(false);

        if (__instance.IsOwner)
        {
            PauseManager.Instance.MoveAmmoDisplay(false, rightHand);
            trav.Field("playerController").GetValue<FirstPersonController>().isScopeAiming = false;
            if (rightHand)
            {
                trav.Field("weaponInHand").SetValue(null);
                trav.Field("behaviourInHand").SetValue(null);
            }
            else
            {
                trav.Field("weaponInLeftHand").SetValue(null);
                trav.Field("behaviourInLeftHand").SetValue(null);
            }
        }

        if (item.rightHandAnim != "")
        {
            Animator anim = trav.Field("animator").GetValue<Animator>();
            anim.SetBool(rightHand ? item.rightHandAnim : item.leftHandAnim, false);
        }

        object camAnimScript = trav.Field("camAnimScript").GetValue();
        Traverse.Create(camAnimScript).Field("rotateBack").SetValue(true);

        item.playerPickup = null;
        item.playerController = null;
        item.rootObject = null;
        item.OnDrop(trav.Field("cam").GetValue<Camera>());
        item.cam = null;
        obj.transform.parent = null;
        obj.transform.localScale = new Vector3(2f, 2f, 2f);
        item.UnsetLayer();
        obj.layer = 7;
        object rigBuilder = trav.Field("RigBuilder").GetValue();
        Traverse.Create(rigBuilder).Method("Build").GetValue();

        return false;
    }
}

/*
Prefix for right hand pickup, which 
*/
[HarmonyPatch(typeof(PlayerPickup), "RightHandPickup")]
public class RightHandPickupPatch
{
    static void DoSingleHandPickup(PlayerPickup instance, Traverse trav, ItemBehaviour item, Camera cam)
    {
        Transform[] pickupPos = trav.Field("pickupPositionRightHand").GetValue<Transform[]>();
        trav.Method("SetObjectInHandServer",
            instance.sync___get_value_objInHand(),
            pickupPos[item.camChildIndex].position,
            pickupPos[item.camChildIndex].rotation,
            cam.gameObject,
            true).GetValue();
        trav.Method("SetRightIKTarget", item.gripRight).GetValue();
        object rigBuilder = trav.Field("RigBuilder").GetValue();
        Traverse.Create(rigBuilder).Method("Build").GetValue();
    }

    static bool Prefix(PlayerPickup __instance)
    {
        Traverse trav = Traverse.Create(__instance);
        Camera cam = trav.Field("cam").GetValue<Camera>();
        float interactionDistance = trav.Field("interactionDistance").GetValue<float>();
        LayerMask interactionLayer = trav.Field("interactionLayer").GetValue<LayerMask>();
        float sphereRadius = trav.Field("sphereRadius").GetValue<float>();
        float currentHitDistance = trav.Field("currentHitDistance").GetValue<float>();

        GameObject hitObj = null;
        RaycastHit hit, hit2;
        if (Physics.Raycast(cam.transform.position, cam.transform.forward, out hit, interactionDistance, interactionLayer))
            hitObj = hit.transform.gameObject;
        else if (Physics.SphereCast(cam.transform.position, sphereRadius, cam.transform.forward, out hit2, currentHitDistance, interactionLayer))
            hitObj = hit2.transform.gameObject;

        if (hitObj == null || hitObj.GetComponent<Weapon>() != null) return true;
        ItemBehaviour item = hitObj.GetComponent<ItemBehaviour>();
        if (item?.weaponName != "Roulette Item") return true;
        
        __instance.LeftHandDrop();
        __instance.RightHandDrop();
        //Plugin.BepinLogger.LogInfo("Picked up Roulette Item");

        return false;
    }
}

// Locked weapons cannot be taken off the floor.
//
// A weapon in unowned_items is one this player has not unlocked, so the only legitimate way
// to get it is a roulette roll. Both patches below fail OPEN: anything RouletteState cannot
// match to a pool weapon (the Roulette Item itself, an Aboubi, a Pig Held Item) behaves
// exactly as it does in vanilla.

/// <summary>
/// Blocks the interact key on a weapon the player has not unlocked.
/// </summary>
/// <remarks>
/// HandleInteraction is the single entry point for the interact key, and it is the only safe
/// place to refuse. Its two-handed branch runs
///     RightHandDrop(); LeftHandDrop(); RightHandPickup();
/// so blocking further down - in RightHandPickup/LeftHandPickup - would empty both of the
/// player's hands and then hand back nothing.
///
/// The cost of stopping this early is that the currentEnvironmentInteractable branch is
/// skipped for that frame too, so an environment interactable cannot be used while a locked
/// weapon is focused. Focus is a raycast, so the two rarely coincide.
///
/// The vanilla parameter (an InputAction.CallbackContext) is omitted; Harmony only injects
/// the arguments a patch actually asks for.
/// </remarks>
[HarmonyPatch(typeof(PlayerPickup), "HandleInteraction")]
public class UnobtainableInteractionPatch
{
    static bool Prefix(PlayerPickup __instance)
    {
        try
        {
            // Runs for every PlayerPickup on this peer; only the local player's input is
            // this player's to refuse.
            if (!__instance.IsOwner) return true;

            RouletteState roulette = Plugin.RouletteState;
            if (roulette == null) return true;

            Interactable focused = Traverse.Create(__instance)
                .Field("currentInteractable").GetValue<Interactable>();
            if (focused == null) return true;

            return !roulette.IsUnobtainable(focused.GetComponent<ItemBehaviour>());
        }
        catch (Exception error)
        {
            // Never let a throw here cost the player their interact key entirely.
            Plugin.BepinLogger.LogError($"[Unobtainable] HandleInteraction prefix failed: {error}");
            return true;
        }
    }
}

/// <summary>
/// Says so on the look-at popup, replacing the interact-key prompt.
/// </summary>
/// <remarks>
/// Vanilla OnFocus sets grabPopup.text to
///     weaponName.ToLower() + " [" + PauseManager.Instance.InteractPromptLetter.ToLower() + "]"
/// and that prompt is a lie on a weapon that cannot be taken, so the whole string is rebuilt
/// rather than appended to. Rebuilding from weaponName (instead of doing string surgery on
/// vanilla's output) keeps this correct if the prompt or its formatting ever changes.
/// </remarks>
[HarmonyPatch(typeof(ItemBehaviour), "OnFocus")]
public class UnobtainableFocusPatch
{
    static void Postfix(ItemBehaviour __instance)
    {
        try
        {
            RouletteState roulette = Plugin.RouletteState;
            if (roulette == null || !roulette.IsUnobtainable(__instance)) return;

            // PauseManager.Instance is null-checked rather than assumed - HUDTween's own
            // null-dereference of that singleton is one of the open crash candidates, so it
            // is demonstrably not always there.
            PauseManager pauseManager = PauseManager.Instance;
            if (pauseManager == null || pauseManager.grabPopup == null) return;

            pauseManager.grabPopup.text = $"{__instance.weaponName.ToLower()} - Unobtainable";
        }
        catch (Exception error)
        {
            Plugin.BepinLogger.LogError($"[Unobtainable] OnFocus postfix failed: {error}");
        }
    }
}



//NEED TO USE THE Player pickup right hand positions to dictate where the item will be.

// [HarmonyPatch(typeof(PlayerPickup), "OnStartClient")]
// public class PlayerPickupOnStartClientPatch
// {
//     static bool Prefix(PlayerPickup __instance)
//     {
//         Transform[] pickupPositionRightHand = Traverse.Create(__instance).Field("pickupPositionRightHand").GetValue<Transform[]>();

//         GameObject rouletteRight = new("Roulette Right");
//         rouletteRight.transform.SetParent(pickupPositionRightHand[0], false);
//         rouletteRight.transform.localPosition = Vector3.zero; // TODO: set desired local position/rotation
//         rouletteRight.AddComponent<ItemPosition>();
//         Plugin.BepinLogger.LogInfo("Added ItemPosition to Roulette Right");
//         return true;
//     }
// }


//NOTE TO SELF ABOUT HOW TO SPAWN FROM A CHANGING LIST OF RANDDOM ITEMS - 
//THIS IS HOW THE ItemDispenser DOES IT -
// Token: 0x06000457 RID: 1111 RVA: 0x0001E466 File Offset: 0x0001C666
// private IEnumerator SpawnItem()
// {
//     yield return new WaitForSeconds(0.2f);
//     if (SpawnerManager.Instance.SyncAccessor_randomiseWeapons)
//     {
//         this.item = SpawnerManager.Instance.GetRandomSpawnableWeapon();
//     }
//     else
//     {
//         this.item = this.itemsToSpawn[UnityEngine.Random.Range(0, this.itemsToSpawn.Length)];
//         string text;
//         if (SpawnerManager.Instance.SyncAccessor_swapGuns && SpawnerManager.Instance.Swaps.TryGetValue(this.item.name, out text))
//         {
//             if (string.IsNullOrEmpty(text))
//             {
//                 yield break;
//             }
//             GameObject gameObject;
//             if (SpawnerManager.NameToWeaponDict.TryGetValue(text, out gameObject))
//             {
//                 this.item = gameObject;
//             }
//         }
//     }
//     if (this.item == null)
//     {
//         yield break;
//     }
//     this.SpawnWeapon(this.item);
//     yield break;
// }



/*
Plan for putting random item into player's hand 
1. the starting ammo for the weapon will be 1, and it will have it's Gun component's
     base.inHandDespawn set to false, noAmmoClicks is set to 1,
    so that when the item has no ammo, it will not despawn immedietly in the player's hand, 
    so that when it is on the ground it can finish running the code
    for setting the player's weapon in their hand.
2a. then save the reference to the hand that the gun is in now, so after it is dropped,
     it can still modify it.
2b. at the end of onGrab, it will set the gun's ammo to -1 and call Gun.Fire(), so that after 
    a set amount of time, it will despawn the item 
    using vanilla methods. 
3a. then, create the random item as a gameobject, instantiate it, and then do the pickup code 
    after the raycast, and just have the hit object be the new item. 
3b. if the item that was spawned was a 2 handed weapon and the player was already holding something, 
    then it will not do this, and just spawn the 2 handed weapon on the ground in front of the player.
4. because the out of ammo code is what is run, then the roulette item should be destroyed by this point.
*/

/*
The code in the Weapon class in WeaponUpdate() checks if the gun's ammo is 0, and the player is not holding it
This code destroys the gun after time seconds when despawn object is called. 

if (this.SyncAccessor_currentAmmo <= 0 && this.SyncAccessor_currentAmmo > -100 && base.gameObject.layer == 7)
		{
			base.Invoke("DespawnObject", 0.65f);
			this.sync___set_value_currentAmmo(-103, true);
		}

/*
Plan for choosing random item
1. each player has a list instance of gameobjects that they can randomly get.
2. every time they earn a new weapon, it is simpily added to the list.
3. when a roulette item is picked up it will choose the weapon from the list with the random(0, item_list.length)
4. the item is spawned in the player's hand using thr mehod from above.
*/


/*
================================================================================
DONE: the roulette pool is now per-player, and it never leaves the machine it
belongs to.
================================================================================
Each peer keeps exactly one pool — the local player's (RouletteState). The roll
happens on the peer that owns the grabbing player, and the only thing that
crosses the wire is the single chosen prefab. No peer is ever told what another
peer has unlocked, which is a deliberate design constraint, not an oversight.

This is done entirely with the game's own RPCs. Two facts made that possible,
both established by disassembling Assembly-CSharp/FishNet.Runtime:

1. PlayerSpawnObject (a NetworkBehaviour on the player, owner-only — its
   OnStartClient disables itself when !IsOwner) already has the exact round trip
   needed:
       SpawnObject(GameObject obj, Transform player, PlayerSpawnObject script)
   is a ServerRpc; its server body Instantiates, ServerManager.Spawns, and then
   answers every peer with the SetSpawnedObject observers RPC — so the caller
   learns what it got.

2. FishNet serializes an UNSPAWNED prefab. Writer.WriteNetworkObject is
       if (nob.IsSpawned) WriteNetworkObjectId(nob.ObjectId)
       else { WriteNetworkObjectId(nob.PrefabId);
              WriteUInt16(nob.SpawnableCollectionId);
              WriteSByte(nob.GetInitializeOrder()); }
   so a weapon PREFAB can be handed to the server by reference and resolved
   there through the spawnable-prefab table.

That removed the need for the custom FishNet Broadcast layer this comment used
to describe (this mod builds without the codegen weaver, so new [ServerRpc]s are
unavailable and GenericWriter<T>.Write / GenericReader<T>.Read would have had to
be assigned by hand), and the need for the Mycelium RPC library as well.

The one consequence worth remembering: because the prefab travels as a
POSITIONAL PrefabId, peers whose SpawnablePrefabs tables disagree will resolve
different weapons. ItemSpawnerStartPatch mutates that table at runtime, so it
must only ever append. That is crash-investigation candidate A1, and it is now
load-bearing for the roll itself, not just for the roulette item's own spawn.
Both sides log prefabId ([RR:roll] on the client, [RR:server-spawn] on the host)
precisely so the two can be diffed.
================================================================================
*/
