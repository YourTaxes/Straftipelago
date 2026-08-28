using System.Collections.Generic;
using HarmonyLib;
using Straftapelago.Finnegan_McD.org.Patches;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Straftapelago.Finnegan_McD.org.Utils;

/// <summary>
/// Hosts the Archipelago overlay's IMGUI on a GameObject this mod owns, rather
/// than on BepInEx's manager object.
/// </summary>
/// <remarks>
/// BepInEx's configured entrypoint here is <c>UnityEngine.CoreModule</c> /
/// <c>Application</c> / <c>.cctor</c>, which runs before the first scene has
/// loaded. Unity resets the DontDestroyOnLoad scene when that first scene comes
/// up, so everything created that early is destroyed with it - including
/// BepInEx_Manager, and therefore every plugin component BepInEx hosts on it.
/// Diagnostics on this install caught it precisely: the Plugin component logged
/// OnEnable, then OnDisable and OnDestroy, all on frame 0, so its OnGUI never
/// ran once and the overlay never drew. (UnityExplorer was unaffected in the
/// same run only because UniverseLib defers creating its objects until after the
/// game is up, which is the same workaround this class applies.)
///
/// Harmony patches and static state are unaffected by any of this - the assembly
/// stays loaded - which is why the rest of the mod worked while the GUI did not,
/// and why re-creating the host object from a static hook works.
/// </remarks>
internal class ArchipelagoOverlay : MonoBehaviour
{
    private const string HostName = "Straftapelago_Overlay";

    /// <summary>Unity fake-null once destroyed, which is what drives re-creation.</summary>
    private static ArchipelagoOverlay instance;

    private static int spawnCount;
    private static bool warnedAboutChurn;

    /// <summary>
    /// Guards against burning a spawn every scene load if something in the game
    /// actively destroys the host. Normal operation is exactly two spawns: one
    /// from <see cref="Install"/> that dies with the manager object on frame 0,
    /// and one from the first scene load, which then persists for the session.
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
        // Anything later means the host is being torn down for another reason.
        Plugin.BepinLogger.LogInfo($"[Overlay] host destroyed, frame={Time.frameCount} drewGui={loggedFirstOnGui}");
    }

    /// <summary>
    /// Pumps both message queues. This is the mod's one guaranteed per-frame main-thread
    /// callback, and both sinks need exactly that: their messages are produced on the
    /// Archipelago client's websocket and ThreadPool threads, where no Unity API may be
    /// touched.
    /// </summary>
    private void Update()
    {
        ArchipelagoConsole.Pump();
        Killfeed.Pump();
    }

    private void OnGUI()
    {
        if (!loggedFirstOnGui)
        {
            loggedFirstOnGui = true;
            Plugin.BepinLogger.LogInfo($"[Overlay] drawing, frame={Time.frameCount}");
        }

        // The connection UI that used to be here - the Archipelago version/status labels, the
        // host/slot/password text fields, the Connect button and the console window - is now
        // the mod's Mod Menu page (ArchipelagoMenu) and the killfeed (ArchipelagoConsole).
        // What is left is this mod's own unlocked-weapons panel, which was never part of that
        // default Archipelago GUI.
        DrawObtainedWeapons();
    }

    /// <summary>
    /// The local player's unlocked weapons, shown only while the game is paused.
    /// </summary>
    /// <remarks>
    /// RouletteState holds this machine's player's pool and nothing else — the roll happens
    /// locally and only the chosen prefab is ever sent — so this needs no networking and is
    /// already the right list for whoever is looking at the screen.
    ///
    /// Confined to the left half of the screen so it cannot cover the pause menu, and gated
    /// on pause so it never sits on top of gameplay. PauseManager.Instance is null-checked
    /// rather than assumed: HUDTween's own null-dereference of that singleton is one of the
    /// open crash candidates, so it is demonstrably not always there.
    ///
    /// Hidden again once the player opens Settings from the pause menu: that screen is a
    /// full-width layout the list would sit on top of, and pause stays true the whole time
    /// it is up. See <see cref="InSettingsMenu"/>.
    /// </remarks>
    private static void DrawObtainedWeapons()
    {
        if (PauseManager.Instance == null || !PauseManager.Instance.pause) return;
        if (InSettingsMenu()) return;

        List<GameObject> obtained = RouletteState.obtained_Items;

        // Sized off the screen like ArchipelagoConsole does, so it scales with resolution
        // instead of assuming one.
        float panelLeft = Screen.width * 0.02f;
        float panelTop = Screen.height * 0.22f;
        float panelPadding = Screen.width * 0.006f;
        float entryHeight = Screen.height * 0.022f;
        float headerHeight = entryHeight * 1.5f;

        // A weapon name is much narrower than the old 22%-of-screen column, so the columns are
        // packed to the width the text actually needs plus a gap. That is what buys the third
        // column inside the same left-half budget.
        float entryWidth = Screen.width * 0.145f;
        float columnGap = Screen.width * 0.008f;
        float columnStride = entryWidth + columnGap;

        // Still confined to the left half of the screen so it cannot cover the pause menu, so
        // the number of columns is however many fit in that budget rather than however many
        // the list would like.
        float maxPanelWidth = Screen.width * 0.5f - panelLeft;
        float maxColumnsWidth = maxPanelWidth - panelPadding * 2f + columnGap;
        int entriesPerColumn = Mathf.Max(1, (int)((Screen.height * 0.68f - headerHeight) / entryHeight));
        int maxColumns = Mathf.Max(1, (int)(maxColumnsWidth / columnStride));

        // Hard cap: with 71 weapons in the game the full list can outgrow even the columns,
        // and a panel that runs off the screen is worse than one that says how much it is
        // hiding. The "... and N more" line costs an entry, so it comes out of the capacity.
        int capacity = entriesPerColumn * maxColumns;
        bool truncated = obtained.Count > capacity;
        int weaponCount = truncated ? capacity - 1 : obtained.Count;
        int entryCount = weaponCount + (truncated ? 1 : 0);

        int columnCount = Mathf.Max(1, Mathf.CeilToInt(entryCount / (float)entriesPerColumn));
        int rowCount = Mathf.Min(Mathf.Max(entryCount, 1), entriesPerColumn);

        float panelWidth = columnCount * columnStride - columnGap + panelPadding * 2f;
        float panelHeight = headerHeight + rowCount * entryHeight + entryHeight * 0.5f;

        GUI.Box(new Rect(panelLeft, panelTop, panelWidth, panelHeight), "");

        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = (int)(Screen.height * 0.016f),
            normal = { textColor = Color.white },
        };

        float firstColumnLeft = panelLeft + panelPadding;
        float firstEntryTop = panelTop + headerHeight;

        GUI.Label(new Rect(firstColumnLeft, panelTop + entryHeight * 0.25f,
            panelWidth - panelPadding * 2f, headerHeight),
            $"Unlocked weapons ({obtained.Count})", style);

        for (int i = 0; i < entryCount; i++)
        {
            // Fill each column top to bottom before starting the next one, so the numbering
            // reads down a column the way the single-column list used to.
            float entryLeft = firstColumnLeft + i / entriesPerColumn * columnStride;
            float entryTop = firstEntryTop + i % entriesPerColumn * entryHeight;

            string text;
            if (truncated && i == entryCount - 1)
            {
                text = $"... and {obtained.Count - weaponCount} more";
            }
            else
            {
                GameObject weapon = obtained[i];
                text = $"{i + 1}. {(weapon == null ? "<missing>" : weapon.name)}";
            }

            GUI.Label(new Rect(entryLeft, entryTop, entryWidth, entryHeight), text, style);
        }
    }
    /// <summary>Cached so the reflection below does not run on every OnGUI pass.</summary>
    private static PauseManager settingsMenuOwner;
    private static GameObject settingsMenuObject;
    private static bool warnedAboutMissingSettingsMenu;

    /// <summary>
    /// True while the Settings screen is up.
    /// </summary>
    /// <remarks>
    /// <para>PauseManager.pause stays true the whole time Settings is open — it means "the
    /// game is paused", not "the pause menu is the thing on screen" — so it cannot tell the
    /// two apart on its own. The private <c>optionsMenu</c> GameObject can: vanilla
    /// <c>PauseManager.Menu()</c> (the Escape handler) reads its <c>activeSelf</c> and toggles
    /// it, which is exactly the state being asked about here.</para>
    /// <para>This also covers Mod Menu, including this mod's own page, without a second check:
    /// its <c>PauseManagerPatch.InitModsTab</c> postfix adds a "ModsTab" next to the vanilla
    /// PcTab/AudioTab/GraphTab under the same OPTIONS HUD, so the mod list is a tab inside
    /// this screen rather than a separate one.</para>
    /// <para><c>activeInHierarchy</c> rather than <c>activeSelf</c>, which is what vanilla
    /// happens to use: the question here is "is it on screen", and a parent being hidden would
    /// leave activeSelf true while nothing is actually visible.</para>
    /// </remarks>
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
                // Once, not every frame. Failing open (list stays visible) is the harmless
                // direction, but a silent failure would look like the gate simply not working.
                warnedAboutMissingSettingsMenu = true;
                Plugin.BepinLogger.LogWarning(
                    "[Overlay] PauseManager.optionsMenu did not resolve, so the weapon list " +
                    "cannot tell the pause menu from the Settings screen and will stay visible " +
                    "on both. The field was probably renamed by a game update.");
            }
        }

        return settingsMenuObject != null && settingsMenuObject.activeInHierarchy;
    }
}
