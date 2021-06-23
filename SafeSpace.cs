using Newtonsoft.Json;
using Oxide.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = System.Random;

namespace Oxide.Plugins
{
    [Info("SafeSpace", "bmgjet", "1.0.0")]
    [Description("Create a personal safe space for players.")]
    public class SafeSpace : RustPlugin
    {
        #region Vars
        private PluginConfig config;
        private const ulong skinID = 2521811412;
        private const string permUse = "SafeSpace.use";
        private const string permCraft = "SafeSpace.craft";
        private const string permCount = "SafeSpace.count";
        private const string permClear = "SafeSpace.clear";
        private const string permView = "SafeSpace.view";
        private BasePlayer viewplayer;
        private Coroutine _routine;
        static Dictionary<SleepingBag, ulong> SafeSpaceBags = new Dictionary<SleepingBag, ulong>();
        private uint MapSize;
        private uint RanMapSize;
        #endregion

        #region Language
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
            {"Valid", "({0}) No Valid Locations Found Try Place Again!"},
            {"Receive", "You received SafeSpace token!"},
            {"Welcome", "<color=red>{0}</color>, <color=orange>Remember to /sethome safespace</color>"},
            {"Permission", "You need permission to {0} a SafeSpace!"},
            {"View", "{0} SafeSpace view!"},
            {"Cant", "Cant find any player by that name/id!"},
            {"Clear", "Cleared {0} SafeSpaces!"},
            {"Count", "There are {0} SafeSpaces active!"}
            }, this);
        }

        private void message(BasePlayer chatplayer, string key, params object[] args)
        {
            if (chatplayer == null && !chatplayer.IsConnected) { return; }
            var message = string.Format(lang.GetMessage(key, this, chatplayer.UserIDString), args);
            chatplayer.ChatMessage(message);
        }
        #endregion

        #region Configuration
        private class PluginConfig
        {
            [JsonProperty(PropertyName = "How many times to retry finding random spot : ")] public int randomloop { get; set; }
            [JsonProperty(PropertyName = "How far to spread bases out (HV rockets max distance is 200) : ")] public int basespread { get; set; }
            [JsonProperty(PropertyName = "How far on map to spawn safe spaces, (2.0 = only in grid, 1.0 = to edge of map kill zone) : ")] public float gridtrim { get; set; }
            [JsonProperty(PropertyName = "How fast safespace.view refreshes for admin : ")] public float RefreshTimer { get; set; }
            [JsonProperty(PropertyName = "Text size for safespace.view admin : ")] public byte textsize { get; set; }
            [JsonProperty(PropertyName = "Colour of text : ")] public Color textcolor { get; set; }
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig
            {
                randomloop = 9999,  //High number since placing token seems to have more of a impact then a big loop.
                basespread = 210,   //210 since HVs can fly 200 and each player can build 4 foundations from safe bag.
                gridtrim = 1.25f,   //Allow to spawn outside of grid
                RefreshTimer = 6f,
                textsize = 22,
                textcolor = Color.red,
            };
        }

        protected override void LoadDefaultConfig()
        {
            Config.Clear();
            Config.WriteObject(GetDefaultConfig(), true);
            config = Config.ReadObject<PluginConfig>();
        }
        #endregion

        #region Oxide Hooks
        private void Init()
        {
            permission.RegisterPermission(permUse, this);
            permission.RegisterPermission(permCraft, this);
            permission.RegisterPermission(permCount, this);
            permission.RegisterPermission(permClear, this);
            permission.RegisterPermission(permView, this);
            MapSize = World.Size;
            config = Config.ReadObject<PluginConfig>();
            if (config == null)
            {
                LoadDefaultConfig();
            }
             RanMapSize = (uint)(MapSize / config.gridtrim);
            if (RanMapSize >= 4000)
            {
                RanMapSize = 3900; //Limits player from going past 4000 kill point
            }
        }

        private void Unload()
        {
            if (_routine != null)
            {
                try
                {
                    ServerMgr.Instance.StopCoroutine(_routine);
                    message(viewplayer, "View", "Stopped");
                }
                catch { }
                _routine = null;
            }
            if (SafeSpaceBags != null)
            {
                SafeSpaceBags = null;
            }
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            if (go == null) { return; }

            BaseEntity entity = go.ToBaseEntity();
            if (entity == null)
            {
                return;
            }

            BasePlayer player = BasePlayer.FindByID(entity.OwnerID);
            if (player == null)
            {
                return;
            }

            if (!IsSafeSpace(entity.skinID)) { return; }

            if (GenSafeSpace(player)) //Check if valid safe space and remove dummy placed bag.
            {
                NextTick(() => { entity?.Kill(); });
                return;
            }

            player.GiveItem(CreateItem()); //Failed to find safe space location so give item back.
            NextTick(() => { entity?.Kill(); });
        }
        #endregion

        #region Helpers
        bool isAdmin { get; set; }
        public void StartSleeping(BasePlayer player)
        {
            if (!player.IsSleeping())
            {
                Interface.CallHook("OnPlayerSleep", player);
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Sleeping, true);
                player.sleepStartTime = Time.time;
                BasePlayer.sleepingPlayerList.Add(player);
                BasePlayer.bots.Remove(player);
                player.CancelInvoke("InventoryUpdate");
                player.CancelInvoke("TeamUpdate");
            }
        }

        private void Wakeup(BasePlayer player)
        {
            if (player.IsConnected == false)
            {
                return;
            }

            if (player.IsReceivingSnapshot == true)
            {
                timer.Once(1f, () => Wakeup(player));
                return;
            }
            player.EndSleeping();
        }

        private bool IsSafeSpace(ulong skin) { return skin != 0 && skin == skinID; }

        private bool AreaClear(Vector3 target)
        {
            //Check if area is clear by layers. multiply by 2 since its a sphere
            var layers = LayerMask.GetMask("Construction", "Prevent Building", "Construction Trigger", "Trigger", "Deployed", "Default", "World");
            return !GamePhysics.CheckSphere(target, (config.basespread * 2), layers, QueryTriggerInteraction.Collide);
        }

        private Vector3 RandomLocation()
        {
        Random rnd = new Random(DateTime.Now.Millisecond); //Reseed other wise isnt very random and bases clump together.
            //Gen random height to allow for more spaces 943 max height so if they get that and max of 19 foundations high they are still under 1000 heigh kill point.
            //820 as min height since cargo plane flys at 800
            return new Vector3(rnd.Next(Math.Abs((int)RanMapSize) * (-1), (int)RanMapSize), rnd.Next(820, 943), rnd.Next(Math.Abs((int)RanMapSize) * (-1), ((int)RanMapSize)));
        }
        #endregion

        #region Core 
        public void Teleport(BasePlayer player, Vector3 newPosition)
        {
            //Slight delay to allow entitys to spawn in.
            Timer DelayTP = timer.Once(1.0f, () =>
            {
                try
                {
                    player.EnsureDismounted();
                    if (player.HasParent())
                    {
                        player.SetParent(null, true, true);
                    }

                    if (player.IsConnected)
                    {
                        player.EndLooting();
                        StartSleeping(player);
                    }

                    player.RemoveFromTriggers();
                    player.EnableServerFall(true);
                    player.Teleport(newPosition);

                    if (player.IsConnected && !Network.Net.sv.visibility.IsInside(player.net.group, newPosition))
                    {
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot, true);
                        player.ClientRPCPlayer(null, player, "StartLoading");
                        player.SendEntityUpdate();
                        player.UpdateNetworkGroup();
                        player.SendNetworkUpdateImmediate(false);
                    }
                }
                finally
                {
                    player.EnableServerFall(false);
                    player.ForceUpdateTriggers();
                }

                Wakeup(player);
                message(player, "Welcome", player.displayName);
            });
        }

        private void ShowSafeSpaces()
        {
            foreach (KeyValuePair<SleepingBag, ulong> ent in SafeSpaceBags)
            {
                //Put in try/catch since some strange non-enlish names caused issues.
                try
                {
                    SleepingBag bag = ent.Key as SleepingBag;
                    if (bag == null || bag.transform.position == null) { continue; }
                    viewplayer.SendConsoleCommand("ddraw.text", config.RefreshTimer, config.textcolor, bag.transform.position, "<size=" + config.textsize + ">" + BasePlayer.FindAwakeOrSleeping(ent.Value.ToString()).displayName + "</size>");
                }
                catch { }
            }
        }

        IEnumerator SafeSpaceScanRoutine()
        {
            do
            {
                //toggle admin flag so you can show a normal user with out it auto banning them for cheating.
                if (!viewplayer || !viewplayer.IsConnected) { yield break; }
                if (!isAdmin)
                {
                    viewplayer.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    viewplayer.SendNetworkUpdateImmediate();
                }
                ShowSafeSpaces();
                if (!isAdmin && viewplayer.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                {
                    viewplayer.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    viewplayer.SendNetworkUpdateImmediate();
                }
                yield return CoroutineEx.waitForSeconds(config.RefreshTimer);
            } while (viewplayer.IsValid() && viewplayer.IsConnected && !viewplayer.IsSleeping());
            message(viewplayer, "View", "Stopped");
            _routine = null;
        }

        private bool GenSafeSpace(BasePlayer player)
        {
            if (!player.IPlayer.HasPermission(permUse))
            {
                message(player, "Use", "use");
                return false;
            }
            //Creates a random location
            Vector3 RandomSafeSpace = RandomLocation();
            //For loop since player spamming bag placement has larger perfrmance hit than a large for loop.
            for (int i = 0; i <= config.randomloop; i++)
            {
                if (AreaClear(RandomSafeSpace)) //Checks if area is clear.
                {
                    //Debug info for console
                    Puts(i.ToString() + " Location Attemps!");
                    break;
                }
                RandomSafeSpace = RandomLocation();
            }

            //Final check that theres no other objects nearby.
            if (!AreaClear(RandomSafeSpace))
            {
                message(player, "Valid", config.randomloop);
                return false;
            }

            //Creates 1 floor entity
            var entity = GameManager.server.CreateEntity("assets/prefabs/building core/floor/floor.prefab", RandomSafeSpace, player.transform.rotation);
            if (entity == null)
                return false;

            entity.Spawn();
            entity.OwnerID = player.userID;

            //stuff to make it valid for player use.
            var buildingBlock = entity as BuildingBlock;
            if (buildingBlock != null)
            {
                buildingBlock.SetGrade((BuildingGrade.Enum)4);
                buildingBlock.grounded = true;
                buildingBlock.AttachToBuilding(BuildingManager.server.NewBuildingID());
                buildingBlock.health = buildingBlock.MaxHealth();
            }

            //creates a sleeping bag for them so they can get back there if they dont set a TP.
            var sleepingbag = GameManager.server.CreateEntity("assets/prefabs/deployable/sleeping bag/sleepingbag_leather_deployed.prefab", RandomSafeSpace + new Vector3(.8f, 0.0f, .4f), Quaternion.Euler(new Vector3(entity.transform.rotation.x, 180, entity.transform.rotation.z)));
            if (sleepingbag == null)
            {
                //If bag failed to be made kill building block.
                entity.Kill();
                return false;
            }

            //Setup bag so it cant be renamed/picked up
            SleepingBag bag = sleepingbag as SleepingBag;
            bag.deployerUserID = player.userID;
            bag.OwnerID = player.userID;
            bag.health = bag.MaxHealth();
            bag.skinID = skinID;
            bag.niceName = "SafeSpace " + bag.transform.position.ToString();
            bag.Spawn();
            bag.UpdateNetworkGroup();
            entity.EnableSaving(true);
            SafeSpaceBags.Add(bag, player.userID); //Adds it to list incase theres a active viewer already.

            Teleport(player, RandomSafeSpace); //Sends player there
            return true;
        }

        private Item CreateItem()
        {
            //create sleeping bag with safe space skin.
            var item = ItemManager.CreateByName("sleepingbag", 1, skinID);
            if (item != null)
            {
                item.text = "Safe Space";
                item.name = item.text;
            }
            return item;
        }

        private void GiveSafeSpace(BasePlayer player)
        {
            //Give safe space item to player
            var item = CreateItem();
            if (item != null && player != null)
            {
                player.GiveItem(item);
                message(player, "Receive");
            }
        }

        private void ClearSpace()
        {
            foreach (KeyValuePair<SleepingBag, ulong> oldsafespace in SafeSpaceBags)
            {
                Vector3 oldpos = oldsafespace.Key.transform.position;

                var hits = Physics.SphereCastAll(oldsafespace.Key.transform.position, 3f, Vector3.up);
                var x = new List<BaseEntity>();
                foreach (var hit in hits)
                {
                    var entity = hit.GetEntity()?.GetComponent<BaseEntity>();
                    if (entity && !x.Contains(entity)) { try { entity.Kill(); } catch { } };
                }
            }
        }

        private void RefreshSafeSpaceList(ulong Filter)
        {
            SafeSpaceBags.Clear();
            foreach (SleepingBag bags in GameObject.FindObjectsOfType<SleepingBag>())
            {
                if (IsSafeSpace(bags.skinID))
                {
                    if (Filter == 0)
                    {
                        SafeSpaceBags.Add(bags, bags.OwnerID);
                        continue;
                    }
                    if (bags.OwnerID == Filter)
                    {
                        SafeSpaceBags.Add(bags, bags.OwnerID);
                    }
                }
            }
        }
        #endregion

        #region ChatCommand
        [ChatCommand("safespace.craft")]
        private void CmdSafeSpace(BasePlayer player, string command, string[] args)
        {
            if (!player.IPlayer.HasPermission(permCraft))
            {
                message(player, "Permission", "craft");
                return;
            }
            GiveSafeSpace(player);
        }

        [ChatCommand("safespace.count")]
        private void CmdSafeSpaceCount(BasePlayer player, string command, string[] args)
        {
            if (!player.IPlayer.HasPermission(permCount))
            {
                message(player, "Permission", "count");
                return;
            }

            if (args.Length == 0)
            {
                RefreshSafeSpaceList(0);
                message(player, "Count", SafeSpaceBags.Count.ToString());
                return;
            }

            BasePlayer filteredplayer = BasePlayer.FindAwakeOrSleeping(args[0]);
            if (filteredplayer == null)
            {
                message(player, "Cant");
                return;
            }
            RefreshSafeSpaceList(filteredplayer.userID);
            message(player, "Count", SafeSpaceBags.Count.ToString() + " " + filteredplayer.displayName);
        }

        [ChatCommand("safespace.clear")]
        private void CmdSafeSpaceClear(BasePlayer player, string command, string[] args)
        {
            if (!player.IPlayer.HasPermission(permClear))
            {
                message(player, "Permission", "clear");
                return;
            }

            if (args.Length == 0)
            {
                RefreshSafeSpaceList(0);
                ClearSpace();
                message(player, "Clear", SafeSpaceBags.Count.ToString());
                return;
            }

            BasePlayer filteredplayer = BasePlayer.FindAwakeOrSleeping(args[0]);
            if (filteredplayer == null)
            {
                message(player, "Cant");
                return;
            }
            RefreshSafeSpaceList(filteredplayer.userID);
            ClearSpace();
            message(player, "Clear", SafeSpaceBags.Count.ToString() + " " + filteredplayer.displayName);
        }

        [ChatCommand("safespace.view")]
        private void CmdSafeSpaceView(BasePlayer player, string command, string[] args)
        {
            //Note only 1 viewer at a time.
            if (!player.IPlayer.HasPermission(permView))
            {
                message(player, "Permission", "view");
                return;
            }

            if (_routine != null)
            {
                ServerMgr.Instance.StopCoroutine(_routine);
                _routine = null;
                if (args.Length == 0)
                {
                    message(player, "View", "Stopped");
                    return;
                }
            }

            isAdmin = player.IsAdmin;
            viewplayer = player;

            if (args.Length > 0)
            {
                BasePlayer filteredplayer = BasePlayer.FindAwakeOrSleeping(args[0]);
                if (filteredplayer == null)
                {
                    message(player, "Cant");
                    return;
                }

                RefreshSafeSpaceList(filteredplayer.userID);
                _routine = ServerMgr.Instance.StartCoroutine(SafeSpaceScanRoutine());
                message(player, "View", "Started filtered");
            }

            RefreshSafeSpaceList(0);
            _routine = ServerMgr.Instance.StartCoroutine(SafeSpaceScanRoutine());
            message(player, "View", "Started");
            return;
        }
        #endregion

        #region Command
        [ConsoleCommand("safespace.give")]
        private void Cmd(ConsoleSystem.Arg arg)
        {
            if (arg.IsAdmin && arg.Args?.Length > 0)
            {
                var player = BasePlayer.Find(arg.Args[0]) ?? BasePlayer.FindAwakeOrSleeping(arg.Args[0]);
                if (player == null)
                {
                    PrintWarning($"Can't find player with that name/ID! {arg.Args[0]}");
                    return;
                }
                //Give selected ammount
                if (arg.Args.Length == 2)
                {
                    int giveammount = int.Parse(arg.Args[1]);
                    for (int i = 1; i < giveammount; i++)
                    {
                        GiveSafeSpace(player);
                    }
                }
                GiveSafeSpace(player);
            }
        }
        #endregion
    }
}