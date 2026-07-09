using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

using CitizenFX.Core;

using MenuAPI;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using vMenuClient.data;
using vMenuClient.menus;

using static CitizenFX.Core.Native.API;
using static CitizenFX.Core.UI.Screen;
using static vMenuClient.CommonFunctions;
using static vMenuShared.ConfigManager;
using static vMenuShared.PermissionsManager;

namespace vMenuClient
{
    public class AddonVehicle
    {
        public string Name { get; set; }
        public string SpawnCode { get; set; }

        public AddonVehicle(string name, string spawnCode)
        {
            Name = name;
            SpawnCode = spawnCode;
        }
    }
    public class EventManager : BaseScript
    {
        public static WeatherOptions WeatherOptionsMenu { get; private set; }
        public static bool IsSnowEnabled => GetSettingsBool(Setting.vmenu_enable_snow);
        public static int GetServerMinutes => MathUtil.Clamp(GetSettingsInt(Setting.vmenu_current_minute), 0, 59);
        public static int GetServerHours => MathUtil.Clamp(GetSettingsInt(Setting.vmenu_current_hour), 0, 23);
        public static int GetServerMinuteDuration => GetSettingsInt(Setting.vmenu_ingame_minute_duration);
        public static bool IsServerTimeFrozen => GetSettingsBool(Setting.vmenu_freeze_time);
        public static string GetServerWeather => GetSettingsString(Setting.vmenu_current_weather, "CLEAR");
        public static bool DynamicWeatherEnabled => GetSettingsBool(Setting.vmenu_enable_dynamic_weather);
        public static bool IsBlackoutEnabled => GetSettingsBool(Setting.vmenu_blackout_enabled);
        public static bool IsVehicleLightsEnabled => GetSettingsBool(Setting.vmenu_vehicle_blackout_enabled);
        public static int WeatherChangeTime => MathUtil.Clamp(GetSettingsInt(Setting.vmenu_weather_change_duration), 0, 45);
        public static bool IsServerTimeSyncedWithMachineTime => GetSettingsBool(Setting.vmenu_sync_to_machine_time);
        private static bool DontDoTimeSyncRightNow = false;
        private static bool SmoothTimeTransitionsEnabled => true;
        public static bool freezeTime = GetSettingsBool(Setting.vmenu_freeze_time);
        public static int currentHours = 7;
        public static int currentMinutes = 0;
        private int currentServerHours = currentHours;
        private int currentServerMinutes = currentMinutes;
        private int minuteClockSpeed = 2000;
        private int minuteTimer = GetGameTimer();

        /// <summary>
        /// Constructor.
        /// </summary>
        public EventManager()
        {
            // Add event handlers.
            EventHandlers.Add("vMenu:SetAddons", new Action(SetConfigOptions)); // DEPRECATED: Backwards-compatible event handler; use 'vMenu:SetConfigOptions' instead
            EventHandlers.Add("vMenu:SetConfigOptions", new Action(SetConfigOptions));
            EventHandlers.Add("vMenu:SetPermissions", new Action<string>(MainMenu.SetPermissions));
            EventHandlers.Add("vMenu:SetSupplementaryPermissions", new Action<string>(MainMenu.SetSupplementaryPermissions));
            EventHandlers.Add("vMenu:KillMe", new Action<string>(KillMe));
            EventHandlers.Add("vMenu:Notify", new Action<string>(NotifyPlayer));
            EventHandlers.Add("vMenu:SetClouds", new Action<float, string>(SetClouds));
            EventHandlers.Add("vMenu:SetTime", new Action<int, int, bool>(SetTime));
            EventHandlers.Add("vMenu:GoodBye", new Action(GoodBye));
            EventHandlers.Add("vMenu:SetBanList", new Action<string>(UpdateBanList));
            EventHandlers.Add("vMenu:ClearArea", new Action<Vector3>(ClearAreaNearPos));
            EventHandlers.Add("vMenu:updatePedDecors", new Action(UpdatePedDecors));
            EventHandlers.Add("playerSpawned", new Action(SetAppearanceOnFirstSpawn));
            EventHandlers.Add("vMenu:PrivateMessage", new Action<string, string>(PrivateMessage));
            EventHandlers.Add("vMenu:UpdateTeleportLocations", new Action<string>(UpdateTeleportLocations));

            if (GetSettingsBool(Setting.vmenu_enable_weather_sync))
            {
                Tick += WeatherSync;
            }

            if (GetSettingsBool(Setting.vmenu_enable_time_sync))
            {
                Tick += TimeSync;
            }
        }

        private bool firstSpawn = true;
        /// <summary>
        /// Sets the saved character whenever the player first spawns.
        /// </summary>
        private async void SetAppearanceOnFirstSpawn()
        {
            if (firstSpawn)
            {
                firstSpawn = false;
                if (MainMenu.MiscSettingsMenu != null && MainMenu.MpPedCustomizationMenu != null && MainMenu.MiscSettingsMenu.MiscRespawnDefaultCharacter && !string.IsNullOrEmpty(GetResourceKvpString("vmenu_default_character")) && !GetSettingsBool(Setting.vmenu_disable_spawning_as_default_character))
                {
                    await MainMenu.MpPedCustomizationMenu.SpawnThisCharacter(GetResourceKvpString("vmenu_default_character"), false);
                }
                while (!IsScreenFadedIn() || IsPlayerSwitchInProgress() || IsPauseMenuActive() || GetIsLoadingScreenActive())
                {
                    await Delay(0);
                }
                if (MainMenu.WeaponLoadoutsMenu != null && MainMenu.WeaponLoadoutsMenu.WeaponLoadoutsSetLoadoutOnRespawn && IsAllowed(Permission.WLEquipOnRespawn))
                {
                    var saveName = GetResourceKvpString("vmenu_string_default_loadout");
                    if (!string.IsNullOrEmpty(saveName))
                    {
                        await SpawnWeaponLoadoutAsync(saveName, true, false, true);
                    }

                }
            }
        }

        /// <summary>
        /// Sets the addon models from the addons.json file.
        /// </summary>
        private void SetConfigOptions()
        {
            SetAddons();
            SetWhiteLists();
            SetExtras();
            SetTattoos();

            MainMenu.ConfigOptionsSetupComplete = true;
        }

        /// <summary>
        /// Sets the addon models from the addons.json file.
        /// Supports two formats for the "vehicles" key:
        ///   - Flat list:      "vehicles": ["model1", "model2"]                         (all placed under a default "Cars" category)
        ///   - Grouped by category: "vehicles": { "Civilian": ["model1"], "LEO": ["model2"] }
        /// </summary>
        private void SetAddons()
        {
            // reset addons
            VehicleSpawner.AddonVehicles = new Dictionary<string, uint>();
            VehicleSpawner.AddonVehicleCategories = new Dictionary<string, Dictionary<string, uint>>();
            WeaponOptions.AddonWeapons = new Dictionary<string, uint>();
            PlayerAppearance.AddonPeds = new Dictionary<string, uint>();
            MpPedCustomization.ExtraBlendableFaces = [];

            var jsonData = LoadResourceFile(GetCurrentResourceName(), "config/addons.json") ?? "{}";
            try
            {
                var root = JObject.Parse(jsonData);

                // load vehicles (flat list OR grouped by category, see summary above)
                if (root.TryGetValue("vehicles", out var vehiclesToken))
                {
                    void AddVehicle(string category, string modelName)
                    {
                        if (!VehicleSpawner.AddonVehicles.ContainsKey(modelName))
                        {
                            var hash = Game.GenerateHashASCII(modelName);
                            VehicleSpawner.AddonVehicles.Add(modelName, hash);

                            if (!VehicleSpawner.AddonVehicleCategories.TryGetValue(category, out var categoryDict))
                            {
                                categoryDict = new Dictionary<string, uint>();
                                VehicleSpawner.AddonVehicleCategories[category] = categoryDict;
                            }
                            categoryDict.Add(modelName, hash);
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your addons.json file contains 2 or more entries with the same vehicle name! ({modelName}) Please remove duplicate lines!");
                        }
                    }

                    if (vehiclesToken.Type == JTokenType.Array)
                    {
                        // Old / simple format: flat list, no categories -> default them all to "Cars".
                        foreach (var addon in vehiclesToken.ToObject<List<string>>())
                        {
                            AddVehicle("Cars", addon);
                        }
                    }
                    else if (vehiclesToken.Type == JTokenType.Object)
                    {
                        // New format: { "CategoryName": ["model1", "model2"], ... }
                        var grouped = vehiclesToken.ToObject<Dictionary<string, List<string>>>();
                        foreach (var category in grouped)
                        {
                            foreach (var addon in category.Value)
                            {
                                AddVehicle(category.Key, addon);
                            }
                        }
                    }
                }

                // load weapons
                if (root.TryGetValue("weapons", out var weaponsToken))
                {
                    foreach (var addon in weaponsToken.ToObject<List<string>>())
                    {
                        if (!WeaponOptions.AddonWeapons.ContainsKey(addon))
                        {
                            WeaponOptions.AddonWeapons.Add(addon, Game.GenerateHashASCII(addon));
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your addons.json file contains 2 or more entries with the same weapon name! ({addon}) Please remove duplicate lines!");
                        }
                    }
                }

                // load peds.
                if (root.TryGetValue("peds", out var pedsToken))
                {
                    foreach (var addon in pedsToken.ToObject<List<string>>())
                    {
                        if (!PlayerAppearance.AddonPeds.ContainsKey(addon))
                        {
                            PlayerAppearance.AddonPeds.Add(addon, Game.GenerateHashASCII(addon));
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your addons.json file contains 2 or more entries with the same ped name! ({addon}) Please remove duplicate lines!");
                        }
                    }
                }

                if (root.TryGetValue("extra_blendable_faces", out var facesToken))
                {
                    foreach (var addon in facesToken.ToObject<List<string>>())
                    {
                        if (!MpPedCustomization.ExtraBlendableFaces.Contains(addon))
                        {
                            MpPedCustomization.ExtraBlendableFaces.Add(addon);
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your addons.json file contains 2 or more entries with the same extra blendable face name! ({addon}) Please remove duplicate lines!");
                        }
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Debug.WriteLine($"\n\n^1[vMenu] [ERROR] ^7Your addons.json file contains a problem! Error details: {ex.Message}\n\n");
            }
        }

        /// <summary>
        /// Sets the addon models from the addons.json file.
        /// </summary>
        private void SetWhiteLists()
        {
            // reset addons
            VehicleSpawner.WhitelistVehicles = new Dictionary<string, uint>();
            PlayerAppearance.WhitelistedPeds = new Dictionary<string, uint>();
            WeaponOptions.WeaponWhitelist = new Dictionary<string, uint>();

            var jsonData = LoadResourceFile(GetCurrentResourceName(), "config/model-whitelists.json") ?? "{}";
            try
            {
                // load new addons.
                var whitelists = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(jsonData);

                // load whitelist vehicles
                if (whitelists.ContainsKey("whitelistedvehicle"))
                {
                    foreach (var whitelist in whitelists["whitelistedvehicle"])
                    {
                        if (!VehicleSpawner.WhitelistVehicles.ContainsKey(whitelist))
                        {
                            VehicleSpawner.WhitelistVehicles.Add(whitelist, Game.GenerateHashASCII(whitelist));
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your model-whitelists.json file contains 2 or more entries with the same vehicle name! ({whitelist}) Please remove duplicate lines!");
                        }
                    }
                }

                // load whitelisted peds.
                if (whitelists.ContainsKey("whitelistedpeds"))
                {
                    foreach (var whitelist in whitelists["whitelistedpeds"])
                    {
                        if (!PlayerAppearance.WhitelistedPeds.ContainsKey(whitelist))
                        {
                            PlayerAppearance.WhitelistedPeds.Add(whitelist, Game.GenerateHashASCII(whitelist));
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your model-whitelists.json file contains 2 or more entries with the same ped name! ({whitelist}) Please remove duplicate lines!");
                        }
                    }
                }

                // load whitelisted weapon.
                if (whitelists.ContainsKey("whitelistedweapons"))
                {
                    foreach (var whitelist in whitelists["whitelistedweapons"])
                    {
                        if (!WeaponOptions.WeaponWhitelist.ContainsKey(whitelist))
                        {
                            WeaponOptions.WeaponWhitelist.Add(whitelist, Game.GenerateHashASCII(whitelist));
                        }
                        else
                        {
                            Debug.WriteLine($"[vMenu] [Error] Your model-whitelists.json file contains 2 or more entries with the same ped name! ({whitelist}) Please remove duplicate lines!");
                        }
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Debug.WriteLine($"\n\n^1[vMenu] [ERROR] ^7Your model-whitelists.json file contains a problem! Error details: {ex.Message}\n\n");
            }
        }
        /// <summary>
        /// Sets the extras labels from the extras.json file.
        /// </summary>
        private void SetExtras()
        {
            // reset addons
            VehicleOptions.VehicleExtras = new Dictionary<uint, Dictionary<int, string>>();

            string jsonData = LoadResourceFile(GetCurrentResourceName(), "config/extras.json") ?? "{}";

            try
            {
                // load new extras.
                var extras = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<int, string>>>(jsonData);

                foreach (string model in extras.Keys)
                {
                    uint modelHash = Game.GenerateHashASCII(model);

                    if (extras[model] != null && extras[model].Count > 0)
                    {
                        if (!VehicleOptions.VehicleExtras.ContainsKey(modelHash) || VehicleOptions.VehicleExtras[modelHash] == null)
                            VehicleOptions.VehicleExtras.Add(modelHash, extras[model]);
                        else
                        {
                            foreach (int extra in extras[model].Keys)
                            {
                                if (!VehicleOptions.VehicleExtras[modelHash].ContainsKey(extra))
                                    VehicleOptions.VehicleExtras[modelHash].Add(extra, extras[model][extra]);
                                else
                                    Debug.WriteLine($"[vMenu] [Warning] Your extras.json file contains 2 or more entries with the same extra index! ({model}, Extra {extra}) Please remove duplicate!");
                            }
                        }
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Debug.WriteLine($"\n\n^1[vMenu] [ERROR] ^7Your extras.json file contains a problem! Error details: {ex.Message}\n\n");
            }
        }

        private void SetTattoos()
        {
            TattoosData.Addons = [];

            var jsonData = LoadResourceFile(GetCurrentResourceName(), "config/tattoos.json") ?? "[]";
            try
            {
                List<Tattoo> addons = JsonConvert.DeserializeObject<List<Tattoo>>(jsonData);

                foreach (Tattoo tattoo in addons)
                {
                    if (!TattoosData.Addons.Exists(i => i.collectionName == tattoo.collectionName && i.name == tattoo.name))
                    {
                        TattoosData.Addons.Add(tattoo);
                    }
                    else
                    {
                        Debug.WriteLine($"[vMenu] [Error] Your tattoos.json file contains 2 or more entries with the same collection and tattoo names! ({tattoo.collectionName} & {tattoo.name}) Please remove duplicate lines!");
                    }
                }
            }
            catch (JsonReaderException ex)
            {
                Debug.WriteLine($"\n\n^1[vMenu] [ERROR] ^7Your tattoos.json file contains a problem! Error details: {ex.Message}\n\n");
            }
        }

        /// <summary>
        /// Update ban list.
        /// </summary>
        /// <param name="list"></param>
        private void UpdateBanList(string list)
        {
            MainMenu.BannedPlayersMenu?.UpdateBanList(list);
        }

        /// <summary>
        /// Used for cheaters.
        /// </summary>
        private void GoodBye()
        {
            ForceSocialClubUpdate();
        }

        /// <summary>
        /// Loads/unloads the snow fx particles if needed.
        /// </summary>
        private async Task UpdateWeatherParticles()
        {
            ForceSnowPass(IsSnowEnabled);
            SetForceVehicleTrails(IsSnowEnabled);
            SetForcePedFootstepsTracks(IsSnowEnabled);
            if (IsSnowEnabled)
            {
                if (!HasNamedPtfxAssetLoaded("core_snow"))
                {
                    RequestNamedPtfxAsset("core_snow");
                    while (!HasNamedPtfxAssetLoaded("core_snow"))
                    {
                        await Delay(0);
                    }
                }
                UseParticleFxAssetNextCall("core_snow");
            }
            else
            {
                RemoveNamedPtfxAsset("core_snow");
            }
        }

        /// <summary>
        /// OnTick loop to keep the weather synced.
        /// </summary>
        /// <returns></returns>
        private async Task WeatherSync()
        {
            await UpdateWeatherParticles();
            SetArtificialLightsState(IsBlackoutEnabled);
            SetArtificialLightsStateAffectsVehicles(!IsVehicleLightsEnabled);

            if (GetNextWeatherType() != GetHashKey(GetServerWeather))
            {
                SetWeatherTypeOvertimePersist(GetServerWeather, (float)WeatherChangeTime);
                await Delay((WeatherChangeTime * 1000) + 2000);

                TriggerEvent("vMenu:WeatherChangeComplete", GetServerWeather);
            }

            await Delay(1000);
        }


        /// <summary>
        /// This function will take care of time sync. It'll be called once, and never stop.
        /// </summary>
        /// <returns></returns>
        private async Task TimeSync()
        {
            NetworkOverrideClockTime(GetServerHours, GetServerMinutes, 0);
            if (IsServerTimeFrozen || IsServerTimeSyncedWithMachineTime)
            {
                await Delay(5);
            }
            else
            {
                await Delay(MathUtil.Clamp(GetServerMinuteDuration, 100, 2000));
            }
        }

        /// <summary>
        /// Set the cloud hat type.
        /// </summary>
        /// <param name="opacity"></param>
        /// <param name="cloudsType"></param>
        private void SetClouds(float opacity, string cloudsType)
        {
            if (opacity == 0f && cloudsType == "removed")
            {
                ClearCloudHat();
            }
            else
            {
                SetCloudHatOpacity(opacity);
                SetCloudHatTransition(cloudsType, 4f);
            }
        }

        /// <summary>
        /// Used by events triggered from the server to notify a user.
        /// </summary>
        /// <param name="message"></param>
        private void NotifyPlayer(string message)
        {
            Notify.Custom(message, true, true);
        }

        /// <summary>
        /// Kill this player, poor thing, someone wants you dead... R.I.P.
        /// </summary>
        private void KillMe(string sourceName)
        {
            Notify.Alert($"You have been killed by <C>{GetSafePlayerName(sourceName)}</C>~s~ using the ~r~Kill Player~s~ option in vMenu.");
            SetEntityHealth(Game.PlayerPed.Handle, 0);
        }

        /// <summary>
        /// Clear the area around the provided x, y, z coordinates. Clears everything like (destroyed) objects, peds, (ai) vehicles, etc.
        /// Also restores broken streetlights, etc.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        private void ClearAreaNearPos(Vector3 position) => ClearAreaOfEverything(position.X, position.Y, position.Z, 100f, false, false, false, false);

        /// <summary>
        /// Updates ped decorators for the clothing animation when players have joined.
        /// </summary>
        private async void UpdatePedDecors()
        {
            await Delay(1000);
            var backup = PlayerAppearance.ClothingAnimationType;
            PlayerAppearance.ClothingAnimationType = -1;
            await Delay(100);
            PlayerAppearance.ClothingAnimationType = backup;
        }

        /// <summary>
        /// Updates the teleports locations data from the server side locations.json, because that doesn't update client side on change.
        /// </summary>
        /// <param name="jsonData"></param>
        private void UpdateTeleportLocations(string jsonData)
        {
            // MiscSettingsMenu may not be constructed yet when this event arrives right after joining.
            if (MainMenu.MiscSettingsMenu != null)
            {
                bool blips = MainMenu.MiscSettingsMenu.ShowPlayerBlips;
                bool global = MainMenu.MiscSettingsMenu.ShowGlobalPlayerBlips;
                TriggerEvent("vMenu:showPlayerBlips", blips, global);
            }

            // Assuming TpLocations is a proper config setup elsewhere
            MiscSettings.TpLocations = JsonConvert.DeserializeObject<List<vMenuShared.ConfigManager.TeleportLocation>>(jsonData);
        }

        /// <summary>
        /// Sets the in-game time, optionally with a smooth transition.
        /// </summary>
        private async void SetTime(int newHours, int newMinutes, bool freeze)
        {
            // Smooth transition implementation
            if (SmoothTimeTransitionsEnabled)
            {
                DontDoTimeSyncRightNow = true;
                freezeTime = false;

                var oldSpeed = minuteClockSpeed;

                // Begin transition
                while (currentHours != newHours || currentMinutes != newMinutes)
                {
                    await Delay(10); // Smooth transition delay
                                     // Adjust the new time gradually
                    if (currentMinutes < newMinutes)
                    {
                        currentMinutes++;
                    }
                    else if (currentMinutes > newMinutes)
                    {
                        currentMinutes--;
                    }

                    if (currentHours < newHours && currentMinutes == newMinutes)
                    {
                        currentHours++;
                    }
                    else if (currentHours > newHours && currentMinutes == newMinutes)
                    {
                        currentHours--;
                    }

                    if (currentMinutes > 59)
                    {
                        currentMinutes = 0;
                        currentHours++;
                    }
                    if (currentHours > 23)
                    {
                        currentHours = 0;
                    }

                    NetworkOverrideClockTime(currentHours, currentMinutes, 0);
                }

                minuteClockSpeed = oldSpeed;
                DontDoTimeSyncRightNow = false;
            }
            else
            {
                // Instantly set the time if transitions are disabled
                NetworkOverrideClockTime(newHours, newMinutes, 0);
            }

            freezeTime = freeze;
        }
    }
}