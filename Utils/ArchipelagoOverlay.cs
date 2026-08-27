using BepInEx;
using Straftapelago.Finnegan_McD.org.Archipelago;
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
        // this is a good place to create and add a bunch of debug buttons
    }
}
