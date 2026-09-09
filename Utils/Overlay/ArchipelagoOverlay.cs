using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Hosts the Archipelago overlay's IMGUI on a GameObject this mod owns, rather than on
/// BepInEx's manager object. BepInEx's entrypoint here runs before the first scene has loaded,
/// and Unity resets the DontDestroyOnLoad scene when that scene comes up, taking BepInEx_Manager
/// and every plugin component on it along - so a component hosted there never gets an OnGUI.
/// Harmony patches and static state are unaffected, which is why re-creating the host object
/// from a static scene-load hook works.
/// </summary>
internal class ArchipelagoOverlay : MonoBehaviour
{
    private const string HostName = "Straftapelago_Overlay";

    /// <summary>Unity fake-nulls this once destroyed, which is what drives re-creation.</summary>
    private static ArchipelagoOverlay instance;

    private static int spawnCount;
    private static bool warnedAboutChurn;

    /// <summary>
    /// Guards against burning a spawn every scene load if something in the game actively
    /// destroys the host. Normal operation is exactly two spawns: one from <see cref="Install"/>
    /// that dies with the manager object on frame 0, and one from the first scene load.
    /// </summary>
    private const int MaxSpawns = 8;

    private bool loggedFirstOnGui;

    public static void Install()
    {
        // Static handler, so it survives this component being destroyed.
        SceneManager.sceneLoaded += OnSceneLoaded;
        Spawn("plugin Awake");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (instance != null) return;
        Spawn($"scene load '{scene.name}'");
    }

    private static void Spawn(string reason)
    {
        if (spawnCount >= MaxSpawns)
        {
            if (warnedAboutChurn) return;
            warnedAboutChurn = true;
            Plugin.BepinLogger.LogWarning(
                $"[Overlay] host destroyed {MaxSpawns} times; giving up re-creating it. " +
                "Something is actively tearing down this mod's GameObject, which is not " +
                "the frame-0 DontDestroyOnLoad reset this is meant to work around.");
            return;
        }

        spawnCount++;
        GameObject host = new GameObject(HostName);
        DontDestroyOnLoad(host);
        instance = host.AddComponent<ArchipelagoOverlay>();

        Plugin.BepinLogger.LogInfo(
            $"[Overlay] host created (#{spawnCount}) after {reason}, frame={Time.frameCount}");
    }

    private void OnDestroy()
    {
        // Expected exactly once, on frame 0, for the pre-first-scene instance.
        Plugin.BepinLogger.LogInfo($"[Overlay] host destroyed, frame={Time.frameCount} drewGui={loggedFirstOnGui}");
    }

    /// <summary>
    /// Pumps the two message queues, the Made in Heaven countdown and the action queue. This is
    /// the mod's one guaranteed per-frame main-thread callback, and all of them need exactly
    /// that: their contents are produced on the Archipelago client's websocket and ThreadPool
    /// threads, where no Unity API may be touched.
    /// </summary>
    private void Update()
    {
        ArchipelagoConsole.Pump();
        Killfeed.Pump();

        // Here rather than on PlayerHealth.Update, where the Metronome trap's countdown lives:
        // a Made in Heaven is the lobby's clock, so it has to keep running while the local
        // player is dead, spectating or waiting to respawn.
        MadeInHeaven.Tick();

        // An action here can write to either of the sinks above, so it is drained after them:
        // such a line waits a frame rather than sitting in a queue already pumped.
        MainThreadActions.Pump();
    }

    private void OnGUI()
    {
        if (!loggedFirstOnGui)
        {
            loggedFirstOnGui = true;
            Plugin.BepinLogger.LogInfo($"[Overlay] drawing, frame={Time.frameCount}");
        }

        // The Settings gate covers all three panels, countdown included: that screen is a
        // full-width layout every one of them would sit on top of.
        if (InSettingsMenu()) return;

        // Above the pause gate, because a countdown is a trap running against the player in
        // real time and one they could only read by pausing would be no countdown at all.
        OverlayPanels.DrawCountdowns();

        // The pause gate for the other two panels, here rather than in each of them: they are
        // one stacked block as far as the player is concerned. PauseManager.Instance is
        // null-checked rather than assumed.
        if (PauseManager.Instance == null || !PauseManager.Instance.pause) return;

        OverlayPanels.DrawSessionProgress();
        OverlayPanels.DrawObtainedWeapons();
    }

    /// <summary>Cached so the reflection below does not run on every OnGUI pass.</summary>
    private static PauseManager settingsMenuOwner;
    private static GameObject settingsMenuObject;
    private static bool warnedAboutMissingSettingsMenu;

    /// <summary>
    /// True while the Settings screen is up. PauseManager.pause means "the game is paused", not
    /// "the pause menu is what is on screen", so the private optionsMenu GameObject is what
    /// tells the two apart. This also covers Mod Menu, including this mod's own page, which is
    /// a tab inside the same screen.
    /// </summary>
    private static bool InSettingsMenu()
    {
        PauseManager pauseManager = PauseManager.Instance;
        if (pauseManager == null) return false;

        // Re-resolve when the singleton is replaced; Unity's fake-null makes this also fire
        // when the previous one was destroyed.
        if (pauseManager != settingsMenuOwner)
        {
            settingsMenuOwner = pauseManager;
            settingsMenuObject = Traverse.Create(pauseManager).Field("optionsMenu").GetValue<GameObject>();

            if (settingsMenuObject == null && !warnedAboutMissingSettingsMenu)
            {
                // Once, not every frame. Failing open leaves the panels visible, which is the
                // harmless direction, but a silent failure would look like the gate not working.
                warnedAboutMissingSettingsMenu = true;
                Plugin.BepinLogger.LogWarning(
                    "[Overlay] PauseManager.optionsMenu did not resolve, so the weapon list " +
                    "cannot tell the pause menu from the Settings screen and will stay visible " +
                    "on both. The field was probably renamed by a game update.");
            }
        }

        // activeInHierarchy rather than activeSelf: the question is whether it is on screen,
        // and a hidden parent would leave activeSelf true with nothing visible.
        return settingsMenuObject != null && settingsMenuObject.activeInHierarchy;
    }
}
