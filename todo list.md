# Straftipelago — TODO

Status as of 2026-08-27. Four issues came out of a two-player test session
(friend hosting). Two are fixed (Issues 3 and 4); the other two are instrumented
and waiting on a repro.

---

## Blocked on a repro session ← do this first

### 1. Two-player repro run for Issues 1 and 2

All instrumentation is written, built and deployed. Nothing further can be
diagnosed without a live session.

- [ ] Play a match with a friend hosting and you joining as client.
- [ ] Play **two full rounds**, and be **holding a weapon when round 1 ends** —
      that is the state that triggers Issue 2.
- [ ] **Capture `LogOutput.log` from BOTH machines.** This is not optional: the
      leading theory for Issue 1 is only proven or killed by diffing the host's
      and the client's `PrefabId` / `countAfter` for the roulette prefab.
- [ ] Read the log across the `===== ROUND N =====` markers.
- [ ] If still ambiguous, flip each `DiagnosticFlags` bool in turn, rebuild,
      rejoin, and note whether the symptoms disappear. For anything involving the
      roll, start with `SkipRouletteRoll` — it separates "the roll broke it" from
      "the Roulette Item broke it".

The Issue 3 work rides along on the same session, and needs the same two logs:

- [ ] Grow **only the client's** pool with `P`, then have the client pick up a
      Roulette Item — it must roll a weapon the client owns and the host does not.
      `[RR:roll]` should appear only in the client's log and `[RR:server-spawn]`
      only in the host's.
- [ ] Confirm the client's rolled weapon actually lands in hand, and check
      `[RR:setspawned] resolved locally after N frame(s)` — a large N means the
      Mycelium reply is consistently beating FishNet's spawn, which is expected
      to be small but is the thing most likely to differ over a real connection.
- [ ] Check the host's `[RR:server-spawn] resolvedPlayer=` line names the right
      player. That mapping goes Steam id -> `ClientInstance` -> `PlayerSpawner`
      -> `SpawnedObject`, and is the part with no vanilla precedent.

---

## Open

### 2. Issue 1 — Client joins are broken

**Symptoms (client only, never the host):** `PlayerValues.Update()` and
`HUDTween.Update()` throw `NullReferenceException` every frame; the client is
told "you joined mid match, you will be spawned next round" while the host sees
and can fully interact with that player.

Does not happen in the unmodded game, so the mod causes it.

Candidates, in order of confidence:

- [ ] **A1 (leading) — `SpawnablePrefabs` desync.** `ItemSpawnerStartPatch`
      mutates FishNet's prefab table at runtime on every peer, behind
      `InstanceFinder.NetworkManager?.SpawnablePrefabs`. If NetworkManager is
      not ready yet, registration is **silently skipped**. FishNet's `PrefabId`
      is a *positional index*, so a client whose table differs from the host's
      resolves spawns against the wrong index → its player object never comes up
      → `PlayerManager.player` stays null → "joined mid match" *and* the
      per-frame NREs. Accounts for all three symptoms, for host immunity, and
      for it being mod-caused.
- [ ] **A2 — `PlayerPickupAwakePatch` prefix can throw.** `RouletteState.Reset()`
      does an unguarded `unowned_items[30]`. A throwing prefix means FishNet's
      `NetworkInitialize___Early()` never runs and that behaviour's SyncVars
      never register.
- [ ] **A3 — `playerClient` resolves null.** It is a `[SyncVar]` holding a
      `ClientInstance`, serialized by object id, so it is null on any peer that
      has not spawned that object locally. `HUDTween.Start()` caches it once,
      unguarded, so a single null read is permanent. Probably downstream of A1.
- [ ] **A4 — ordinary nulls.** Rule out rather than assume: `setup`,
      `voiceChatSource`, `typingIndicator`, `PauseManager.Instance`, and
      `hudUp` / `hudDown`.

### 3. Issue 2 — Items break after round 1

**Symptoms:** weapons only ever land on the ground instead of being equipped;
NREs in `ItemBehaviour.OnDrop`, `PlayerPickup.RpcLogic___DropObjectObserver_*`
and `ItemBehaviour.Start`; FishNet warns "Cannot complete action because server
is not active".

- [ ] **C1 — prefab, not instance.** `ItemSpawnerStartPatch` sets
      `dispenserStart = false` on the shared *prefab*'s `ItemBehaviour`, so every
      roulette instance inherits it. Vanilla `ItemBehaviour.Start()` then reads
      `transform.parent.up`. Server instances are parented; client-side
      network-spawned copies are **not** → client-only NRE, matching the
      reported trace. Recurs every round via `ItemSpawner.StartNewRound()`.
- [ ] **C2 — layer 7 force-drop.** `DelayedRouletteDespawn` sets layer 7 while
      the roulette may still be `objInHand`, and vanilla `RightHandFix()`
      force-drops anything in hand on layer 7 or 9 — exactly the chain in the
      reported stack trace.
- [ ] **C3 — `PlayerPickup.cam` / `pickupPosition*` null.** These are assigned in
      `OnStartClient()`, not `Awake()`. Also explains "only puts weapons on the
      ground": an NRE in `GrabPatches.Postfix` aborts *after* the weapon is
      instantiated, leaving it where it spawned.
- [ ] **C4 / C5** — the A2 throw, and A1's PrefabId churn across rounds.

### 4. Not yet started

- [ ] Wire anything to Archipelago. Nothing is connected yet, by choice.
- [ ] `Plugin.cs` reads the embedded asset bundle with an inexact
      `stream.Read(data, 0, data.Length)` that ignores the return value. Works
      today; a short read would corrupt the bundle. Low priority.
- [ ] Make all vending machines produce roulette items.

---

## Done

### Issue 3 — Per-player item pools ✅

Every player used to roll from the **host's** pool, because the roll in
`GrabPatches.Postfix` was gated on `IsServer` and `RouletteState` was one global list.

**Design.** The pool is now **local to each machine** and never leaves it. The roll happens
on the peer that owns the grabbing player, and the only thing that crosses the wire is the
single chosen prefab — no peer is ever told what another peer has unlocked. `RouletteState`
stays a single static because a process only ever tracks the unlocks of the player in front
of it, which made the per-`NetworkConnection` dictionary originally planned unnecessary.

**Transport: Mycelium.** The first attempt used vanilla `PlayerSpawnObject.SpawnObject`, a
ServerRpc whose server body Instantiates, `ServerManager.Spawn`s and answers the caller —
exactly the right shape, and FishNet even serializes an unspawned prefab by `PrefabId` so a
weapon prefab can be passed by reference. **A runtime probe killed it:** `PlayerSpawnObject`
is not a component on the player prefab, and a client may only invoke a ServerRpc on a
NetworkObject it owns. Of every vanilla ServerRpc that spawns an arbitrary prefab, none is
reachable from a client:

| Candidate | Why not |
|---|---|
| `PlayerSpawnObject.SpawnObject` | component is not on the player (proved at runtime) |
| `WeaponHandSpawner.SpawnObject` | only on the placeable mine/claymore weapons |
| `ItemDispenser.SpawnWeapon` | no ownership guard so a client *can* call it, but it spawns at the dispenser, needs one on the map, and drags dispenser side effects along |

So the roll goes over Mycelium (already a common STRAFTAT mod dependency), declared with
`[BepInDependency]`. Two messages: requester → host `(rollId, weaponName, rightHand)`, host →
requester `(rollId, spawnedObjectId, rightHand)`. **The weapon name, not a `PrefabId`** — so
the peers' `SpawnablePrefabs` tables no longer have to agree for the roll to be correct.

The equip still goes through vanilla `PlayerPickup.SetObjectInHandServer`, which the client
may call because `PlayerPickup.HandleInteraction` transfers ownership of anything picked up
via `GiveOwnerToObj`.

**The one pure-vanilla route still open**, if the dependency is ever unwanted: bind a
`WeaponHandSpawner` onto the Roulette Item prefab in the asset bundle the same way `Gun` is
already bound. The roulette is owned by whoever picked it up, so its
`SpawnObject(prefab, position, rotation)` would then be callable and takes a spawn position
directly. Needs a rebuild of the bundle in Unity.

Also fixed here:

- `PlayerPickup.Awake()` calls `EnsureInitialized()` instead of `Reset()`, so the pool no
  longer gets wiped every round for every player. `Reset()` still does a full rebuild on
  every call, but only the `O` key reaches it.
- The unguarded `unowned_items[30]` starter index is gone, replaced by a case-insensitive
  `SpawnerManager.NameToWeaponDict` lookup seeded from the design doc's starter set
  (`glock`, `taser`, `stungrenade`, `stunmine`), with a bounds-checked fallback.
  `GrantByName` is the seam for the eventual Archipelago hook. **This closes candidate A2** —
  nothing in that `Awake` prefix can throw any more.
- `Grant` refuses duplicates and `Roll` compacts out nulls, since either would have skewed
  the draw. `Random.Range(0, count)` is uniform; the `K` key runs 100,000 draws and logs the
  spread so that is a number rather than a claim.
- The `O`/`P` debug keys are gated on `IsOwner` — they used to fire once per player in the
  match, so one `P` press granted N weapons.

**Since the roll is no longer reentrant** (it used to run *inside* `OnGrab`, nested in
`SetObjectInHandServer`), the workarounds that existed only to survive that are gone:
`LeftHandPickupPatch` (a full-method overwrite of vanilla `LeftHandPickup`), the
"normalize both hands" sync-var block, and the early `ItemBehaviour.cam` assignment.

**Timing hazard handled:** Mycelium (Steam messaging) and the weapon's FishNet spawn travel
over different transports, so the host's "here is your weapon" reply can arrive before the
object itself exists locally. The requester waits up to 180 frames for the object id to show
up before equipping, and logs `[RR:timeout]` if it never does.

The equip is also always deferred by at least one frame. Mycelium delivers a message
addressed to the local Steam id **synchronously** (`SendBytes` short-circuits when the target
is yourself), so on a host the whole request/spawn/reply/equip chain would otherwise run
inside `OnGrab` — putting back the reentrancy this design removed.

### Issue 4 — Archipelago GUI never appeared ✅

**Root cause.** BepInEx's entrypoint is `UnityEngine.CoreModule` / `Application`
/ `.cctor`, which runs *before the first scene loads*. Unity resets the
`DontDestroyOnLoad` scene when that scene comes up and destroys everything
already parked there — including `BepInEx_Manager`, and therefore every plugin
component BepInEx hosts on it. `Plugin.OnGUI` could never have run, in any build.

Found by elimination: `Awake()` completing proved it was not a load failure;
removing the stray DLLs proved it was not deployment; then `OnEnable` /
`OnDisable` / `OnDestroy` probes showed all three firing on **frame 0**. The key
step was noticing that `Awake()` is *also* a private Unity message and dispatched
fine — so dispatch worked, and the difference is that `OnGUI` needs the component
to still be alive.

**Fix.** `Utils/ArchipelagoOverlay.cs` hosts the IMGUI on a GameObject the mod
owns, created from a **static** `SceneManager.sceneLoaded` hook so it survives
the frame-0 wipe. Spawn cap of 8 with a warning, so active teardown cannot thrash
silently. Confirmed working.

This explains three things that had looked contradictory: Harmony patches were
never affected (they are tied to the loaded assembly, not to a GameObject),
UnityExplorer was immune because UniverseLib defers object creation the same way,
and the friend's r2modman profile ships its own BepInEx config. The fix is
in-mod, so it needs no config change on any install.

Healthy log signature: two `[Overlay] host created` lines (frame 0, then after
the first scene load) followed by `[Overlay] drawing`. A `host created (#3)` or
higher would mean something other than the frame-0 reset is destroying the host.

### Plugin folder cleanup ✅

32 wrongly-deployed DLLs removed from `BepInEx/plugins` — 23 game-assembly
duplicates (including a stale `ComputerysModdingUtilities` from an unrelated
`R:\` install, and a `Newtonsoft.Json` 11 shadowing the game's 13), plus 9
`System.*` netstandard facades pulled in transitively by `BepInEx.Core` →
HarmonyX → MonoMod. Backed up to `STRAFTAT/removed_plugin_dlls_backup/`. Build
output went from 34 files to 3.

Guards so it cannot regress: `Private="false"` on every game `<Reference>`,
`ExcludeAssets="runtime"` on `BepInEx.Core`, a `Newtonsoft.Json` removal target,
a single `StraftatManagedDir` property, and a refusal check in `build.ps1`.

### Instrumentation landed ✅

`Patches/diagnosticPatches.cs` (new), plus logging inside `pickupPatches.cs`.
Change-only logging keyed by instance id, so per-frame patches do not flood the
log. `DiagnosticFlags` provides a bisect harness. All prefixes return `void`, so
vanilla still runs and still throws exactly as before — this observes, it does
not fix.

---

## Notes worth remembering

- `DMD<...>` frames in a Unity stack trace are Harmony-patched dynamic methods.
  That is how to tell which frames belong to this mod.
- FishNet's `PrefabId` is a **positional index** into a `List<NetworkObject>`,
  which is why mutating `SpawnablePrefabs` at runtime is dangerous.
- `StraftatModAttribute.Documentation` explicitly warns against full-overwrite
  prefix patches. This mod does exactly that in `PlayerPickupUpdatePatch`,
  `DropObserverPatch`, `LeftHandPickupPatch`, `RightHandPickupPatch`,
  `OnDropPatch` and `RouletteGunUpdatePatch`. Not currently causing a known bug,
  but it is the first thing to revisit if vanilla behaviour goes missing.
- The "Incompatible assemblies found" warning at startup is **benign**. It comes
  from `CreateMatchMakingKey()` reading assembly names; the only consequence is
  being unable to join vanilla lobbies, which is intended given
  `[assembly: StraftatMod(isVanillaCompatible: false)]`.
- The O, P, I and K debug keybinds live in the `PlayerPickup.Update` Harmony
  patch, so they only respond in-game, not in the menu. Keeping them. O = full
  pool reset, P = grant one random locked weapon, I = unlock every locked weapon
  at once, K = 100k-draw distribution self-test (which now checks the New Weapon
  Chance split as well as fairness within each list).
- **Reading a roulette roll in the log.** Every step is tagged `[RR:<step> #<id>]`,
  so `grep '\[RR:'` (or `findstr /c:"[RR:"`) pulls the whole trace. The `#id` is
  what stitches a client's lines to the host's when two players roll at once.
  Healthy sequence for one roll:

      [RR:grab #7]        owner peer only, pp.IsOwner=True
      [RR:roll #7]        poolCount=4 index=2 prefab=Glock
      [RR:send #7]        weapon=Glock to host via Mycelium
      [RR:server-spawn]   HOST's log: resolvedPlayer=... resolvedPrefab=Glock
      [RR:server-spawn]   HOST's log: spawnedObjectId=412
      [RR:setspawned #7]  armed=True, then "resolved locally after N frame(s)"
      [RR:equip #7]       branch=right
      [RR:equipped #7]    layer=8 inRightHand=True cam=ok camAnimScript=ok
      [RR:despawn #7]     executed

  `[RR:timeout]` means the round trip never completed. Which half failed is only
  visible by checking the **host's** log for a matching `[RR:server-spawn]`:
  present means the reply or the FishNet spawn was lost on the way back, absent
  means the request never arrived.
- A `[Diag:PlayerManager.player] player=null` line early in a session is **not**
  the "joined mid match" bug. `LogOnChange` prints the first observation of every
  object, so it is normal before `SpawnPlayer()` runs; look for a later line on
  the same id reporting `player=ok`. It only matters if it stays null.
