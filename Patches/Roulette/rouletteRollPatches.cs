using System;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Utils;
using UnityEngine;

namespace Straftapelago.Finnegan_McD.org.Patches;

/// <summary>
/// The roulette roll, in three parts across two machines: this postfix rolls from the local
/// pool on the peer that owns the grabbing player and asks the host for the chosen prefab, the
/// host spawns it (see <see cref="RouletteNet"/>), and <see cref="EquipRolledWeapon"/> puts it
/// in the player's hands when it comes back. Only the one chosen prefab ever crosses the wire,
/// so no peer learns another peer's pool.
/// </summary>
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
            // OnGrab is reached from FishNet-generated RPC logic, and an exception escaping a
            // Harmony patch there abandons the rest of that RPC's work on this peer.
            Plugin.BepinLogger.LogError($"[RR:grab #{rollId}] THREW{Environment.NewLine}{e}");
        }
    }

    static void BeginRoll(int rollId, ItemBehaviour ib, bool onGrabOwnerParam, bool rightHand)
    {
        GameObject root = ib.rootObject;
        PlayerPickup pp = root == null ? null : root.GetComponent<PlayerPickup>();

        DiagLog.RR(rollId, "grab",
            $"weapon={ib.weaponName} rootObject={(root == null ? "NULL" : root.name)} " +
            $"pp={(pp == null ? "NULL" : "ok")} pp.IsOwner={(pp == null ? "n/a" : pp.IsOwner.ToString())} " +
            $"onGrabOwnerParam={onGrabOwnerParam} rightHand={rightHand} {DiagLog.NetRoles()}");

        // OnGrab runs on every observer, so exactly one peer passes this gate: the one whose
        // local player did the grabbing. It rolls from its own pool.
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

        // Armed BEFORE sending. On the host, Mycelium delivers a message addressed to the local
        // Steam id synchronously, so the reply can come back inside this very call.
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
    /// Equips the weapon the host just spawned, using the same sequence vanilla's own
    /// RightHandPickup/LeftHandPickup use on the owner: set the sync vars, then call the
    /// SetObjectInHandServer ServerRpc.
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

        // Off: a two-handed weapon is only taken when the roulette was in the right hand and
        // the left is empty, otherwise it is left on the ground. On: both hands are emptied and
        // the weapon is taken in both, the way a floor pickup of one works.
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
        // camChildIndexLeftHand.
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
            $"cam={(cam == null ? "NULL (assigned in OnStartClient, not Awake)" : "ok")} " +
            $"gripRight={(gripRightT == null ? "null" : "ok")} gripLeft={(gripLeftT == null ? "null" : "ok")} " +
            $"camAnimScript={(ppT.Field("camAnimScript").GetValue<CameraShakeConstrains>() == null ? "NULL" : "ok")}");

        // Without a camera nothing below is safe - RightHandDrop reaches StickOnGround and the
        // mod's OnDropPatch reads tempCam.transform. Leave the roulette in hand instead, which
        // the player can still drop by hand.
        if (cam == null)
        {
            Plugin.BepinLogger.LogError(
                $"[RR:equip #{rollId}] PlayerPickup.cam is null (it is assigned in " +
                "OnStartClient, not Awake); aborting the equip and leaving the roulette in hand.");
            return;
        }

        // Take the roulette out of hand first, whichever way this goes: it is about to be
        // despawned, and leaving it registered as objInHand while its layer changes is what
        // makes RightHandFix force-drop it.
        if (useBothHands && otherHandOccupied)
        {
            // LEFT FIRST, unlike vanilla's order: RightHandDrop ends by calling SwitchWeapons
            // when the left hand still holds something and there is no currentInteractable,
            // which moves that item into the right hand instead of dropping it. A roll has no
            // currentInteractable, so the right hand is emptied last.
            pp.LeftHandDrop();
            pp.RightHandDrop();
        }
        else if (rightHand) pp.RightHandDrop(); else pp.LeftHandDrop();

        if (branch == "ground") return;

        // ItemBehaviour.Start is what normally assigns these, and vanilla's "already holding
        // something" branches re-read them off objInHand to re-target IK. Assign them up front
        // so that re-target lands on the real grips rather than on nulls.
        spawnedIb.gripRight = gripRightT;
        spawnedIb.gripLeft = gripLeftT;

        // Weapon.cam is refreshed every frame by WeaponUpdate, but camAnimScript is assigned
        // exactly once by the vanilla post-OnGrab continuation and nulled by every drop.
        // Fire's recoil path needs both.
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
    /// which is allowed to: ownership of anything picked up is transferred to it, and Weapon's
    /// DespawnObjectServer only requires IsClient anyway.
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

    // Deferred by one frame: the vanilla caller of OnGrab force-sets obj.layer back to 8
    // immediately after OnGrab returns, and Weapon.DespawnObject refuses to despawn while the
    // layer is 8 or 9.
    static System.Collections.IEnumerator DelayedRouletteDespawn(int rollId, ItemBehaviour rouletteIb, Gun rouletteGun)
    {
        yield return null;
        if (rouletteIb == null || rouletteGun == null) yield break;
        rouletteIb.gameObject.layer = 7;
        rouletteIb.UnsetLayer();
        yield return new WaitForSeconds(0.65f);
        if (rouletteGun == null) yield break;

        // DespawnObject plays the depop effect and then calls its own DespawnObjectServer
        // ServerRpc, which is what removes it on every peer.
        Traverse.Create(rouletteGun).Method("DespawnObject").GetValue();
        DiagLog.RR(rollId, "despawn", "executed");
    }
}
