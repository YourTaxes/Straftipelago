using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Straftapelago.Finnegan_McD.org.Archipelago;

public class ArchipelagoData
{
    public string Uri;
    public string SlotName;
    public string Password;
    public int Index;

    public List<long> CheckedLocations;

    /// <summary>
    /// seed for this archipelago data. Can be used when loading a file to verify the session the player is trying to
    /// load is valid to the room it's connecting to.
    /// </summary>
    private string seed;

    private Dictionary<string, object> slotData;

    public bool NeedSlotData => slotData == null;

    /// <summary>
    /// whether the room wants this slot linked to the multiworld's deaths. Read out of slot data
    /// on connect, so the room's YAML is the only thing that decides it.
    /// </summary>
    public bool DeathLink { get; private set; }

    /// <summary>
    /// the slot data key the room publishes the death link option under
    /// </summary>
    private const string DeathLinkKey = "death_link";

    public ArchipelagoData()
    {
        Uri = "localhost";
        SlotName = "Player1";
        CheckedLocations = new();
    }

    public ArchipelagoData(string uri, string slotName, string password)
    {
        Uri = uri;
        SlotName = slotName;
        Password = password;
        CheckedLocations = new();
    }

    /// <summary>
    /// assigns the slot data and seed to our data handler. any necessary setup using this data can be done here.
    /// </summary>
    /// <param name="roomSlotData">slot data of your slot from the room</param>
    /// <param name="roomSeed">seed name of this session</param>
    public void SetupSession(Dictionary<string, object> roomSlotData, string roomSeed)
    {
        slotData = roomSlotData;
        seed = roomSeed;

        DeathLink = ReadDeathLink(roomSlotData);
    }

    /// <summary>
    /// pulls the death link option out of slot data, tolerating every shape it can arrive in
    /// </summary>
    /// <remarks>
    /// An Archipelago Toggle option is a 0/1 on the wire, and slot data is deserialized into
    /// object, so Newtonsoft hands this back as a long far more often than as a bool. A room
    /// built against a newer apworld could also send a real bool, and a hand-edited one a string.
    /// Convert.ToBoolean covers all three, and anything it cannot read is reported and treated as
    /// off rather than throwing out of a successful login.
    /// </remarks>
    private static bool ReadDeathLink(Dictionary<string, object> roomSlotData)
    {
        if (roomSlotData == null || !roomSlotData.TryGetValue(DeathLinkKey, out object value) || value == null)
        {
            // Logged rather than passed over: the Straftat apworld does not define this option
            // yet, and "death link never fires" is a lot easier to understand with this line in
            // LogOutput.log than without it.
            Plugin.BepinLogger.LogWarning(
                $"Slot data has no '{DeathLinkKey}' entry, so death link stays off. The room's " +
                "apworld does not offer the option.");
            return false;
        }

        try
        {
            return Convert.ToBoolean(value);
        }
        catch (Exception e)
        {
            Plugin.BepinLogger.LogError(
                $"Could not read '{DeathLinkKey}' from slot data (got '{value}'); death link stays " +
                $"off.{Environment.NewLine}{e}");
            return false;
        }
    }

    /// <summary>
    /// returns the object as a json string to be written to a file which you can then load
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return JsonConvert.SerializeObject(this);
    }
}