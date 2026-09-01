# Straftipelago
A mod for the game Straftat that gives it integration with archipelago multiworlds.

Also adds Green Mode.
Challenge Me.

## Installation and Use

Installation is very simple, move the mod into your Unity Modloader of your choice, and include the dependancies. 

Said Dependancies include -

### MyceliumNetworking

Download - **[MyceliumNetworking for STRAFTAT](https://thunderstore.io/c/straftat/p/straftatmodding/MyceliumNetworking/)**

MyceliumNetworking is used to send new RPC messages to and from the server. In the context of this mod, it is used to notify the lobby host that a client player has picked up a Roulette item, so that the host can spawn it for everyone. 

Without this, the game is just bros holding circles. 

### ModMenu

Download - **[ModMenu for STRAFTAT](https://thunderstore.io/c/straftat/p/kestrel/Mod_Menu/)**

ModMenu adds a custom UI in the options menu for changing the configurations of other mods that are installed. Typically this would not be a required dependancy, but I use the API provided by this mod in order to make the actual Archipelago login screen. So it's kinda required to join any room.


### ChatCommands

Download - **[ChatCommands](https://thunderstore.io/c/straftat/p/kestrel/Chat_Commands/)**

I was a big fan of the chat commands mod, as it was super useful for spawning weapons durring development to test things like kill interactions.
ChatCommands is the Archipelago console. Every one of the server's `!commands` is registered as a chat command prefixed with `ap_` - `/ap_help`, `/ap_hint 'Progressive Sword'`, `/ap_missing` and so on - and whatever the room says back is printed into the chat, the same way `/help` prints its output. `/ap` sends a line to the room verbatim if you need a command this list does not name.

FYI, this mod is not vanilla compatable, so all players need the mod.

### Use in game

When you launch the game, your first step is to go to options and go to the mods tab (provided with ModMenu), and then go to the page for Straftipeago. Here, you will be able to enter the information and enter the room for Archipelago. Also, I would take the time to configre the other settings there, such as Green Mode. 

Challenge Me


Next, go back to the main menu, and then join your lobby for STRAFTAT, and get started.



## Licences

All the code was done by me, as were all the assets were painstakingly modeled (the single cylelder with 12 circles). 

However, I did not make the picture of the Archipelago logo, so here is the copyright for that.
Archipelago Logo: © 2022 by Krista Corkos and Christopher Wilson is licensed under Attribution-NonCommercial 4.0 International. To view a copy of this license, visit http://creativecommons.org/licenses/by-nc/4.0/
