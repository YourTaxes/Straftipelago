using System.Collections.Generic;
using BepInEx;
using Straftapelago.Finnegan_McD.org.Archipelago;
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
    private const string APDisplayInfo = $"Archipelago v{ArchipelagoClient.APVersion}";

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

    private void OnGUI()
    {
        if (!loggedFirstOnGui)
        {
            loggedFirstOnGui = true;
            Plugin.BepinLogger.LogInfo($"[Overlay] drawing, frame={Time.frameCount}");
        }

        // show the mod is currently loaded in the corner
        GUI.Label(new Rect(16, 16, 300, 20), Plugin.ModDisplayInfo);
        ArchipelagoConsole.OnGUI();

        string statusMessage;
        // show the Archipelago Version and whether we're connected or not
        if (ArchipelagoClient.Authenticated)
        {
            // if your game doesn't usually show the cursor this line may be necessary
            // Cursor.visible = false;

            statusMessage = " Status: Connected";
            GUI.Label(new Rect(16, 50, 300, 20), APDisplayInfo + statusMessage);
        }
        else
        {
            // if your game doesn't usually show the cursor this line may be necessary
            // Cursor.visible = true;

            statusMessage = " Status: Disconnected";
            GUI.Label(new Rect(16, 50, 300, 20), APDisplayInfo + statusMessage);
            GUI.Label(new Rect(16, 70, 150, 20), "Host: ");
            GUI.Label(new Rect(16, 90, 150, 20), "Player Name: ");
            GUI.Label(new Rect(16, 110, 150, 20), "Password: ");

            ArchipelagoClient.ServerData.Uri = GUI.TextField(new Rect(150, 70, 150, 20),
                ArchipelagoClient.ServerData.Uri);
            ArchipelagoClient.ServerData.SlotName = GUI.TextField(new Rect(150, 90, 150, 20),
                ArchipelagoClient.ServerData.SlotName);
            ArchipelagoClient.ServerData.Password = GUI.TextField(new Rect(150, 110, 150, 20),
                ArchipelagoClient.ServerData.Password);

            // requires that the player at least puts *something* in the slot name
            if (GUI.Button(new Rect(16, 130, 100, 20), "Connect") &&
                !ArchipelagoClient.ServerData.SlotName.IsNullOrWhiteSpace())
            {
                Plugin.ArchipelagoClient.Connect();
            }
        }

        DrawObtainedWeapons();
        // this is a good place to create and add a bunch of debug buttons
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
    /// </remarks>
    private static void DrawObtainedWeapons()
    {
        if (PauseManager.Instance == null || !PauseManager.Instance.pause) return;

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
}
