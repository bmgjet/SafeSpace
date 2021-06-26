using Newtonsoft.Json;
using Oxide.Core;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = System.Random;

namespace Oxide.Plugins
{
    [Info("SafeSpace", "bmgjet", "1.0.2")]
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
        private Coroutine _routine;
        private Dictionary<SleepingBag, ulong> SafeSpaceBags = new Dictionary<SleepingBag, ulong>();
        private Dictionary<BasePlayer, Dictionary<bool, ulong>> Viewers = new Dictionary<BasePlayer, Dictionary<bool, ulong>>();
        private uint RanMapSize;
        private Random rnd;
        #endregion

        #region Language
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
            {"Valid", "({0}) No Valid Locations Found Try Place Again in 10 seconds!"},
            {"Receive", "You received SafeSpace token!"},
            {"Welcome", "Welcome to your safespace <color=red>{0}</color>," + Environment.NewLine + "<color=orange>You can get back here with /safespace</color>"},
            {"Permission", "You need permission to {0} a SafeSpace!"},
            {"View", "{0} SafeSpace view!"},
            {"No", "You have no safespaces!"},
            {"Already", "{0}"},
            {"Have", "You have {0} safe spaces select one with /safespace number"},
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
        protected override void SaveConfig()
        {
            Config.WriteObject(config, true);
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
            config = Config.ReadObject<PluginConfig>();
            if (config == null)
            {
                LoadDefaultConfig();
            }
        }

        void OnServerInitialized(bool initial)
        {
            RanMapSize = (uint)(World.Size / config.gridtrim);
            if (RanMapSize >= 4000)
            {
                RanMapSize = 3900; //Limits player from going past 4000 kill point
            }
        }

        void Loaded()
        {
            rnd = new Random(DateTime.Now.Millisecond);
        }

        private void Unload()
        {
            if (_routine != null)
            {
                try
                {
                    ServerMgr.Instance.StopCoroutine(_routine);
                    foreach (KeyValuePair<BasePlayer, Dictionary<bool, ulong>> viewer in Viewers)
                    {
                        message(viewer.Key, "View", "Stopped");
                    }
                }
                catch { }
                _routine = null;
            }
        }

        object OnEntityKill(BaseNetworkable entity)
        {
            //Stops whole thing gutting its self if they use /remove on wrong bit.
            BuildingBlock block = entity as BuildingBlock;
            if (!block) return null;
            if ((block.blockDefinition.hierachyName.Contains("floor") || block.blockDefinition.hierachyName.Contains("wall")) && block.transform.position.y > 800f)
            {
                //Allow bypass for block if owner is holding a hammer
                BasePlayer player =  BasePlayer.FindAwakeOrSleeping(block.OwnerID.ToString());
                if (player != null)
                {
                    HeldEntity checkhammer = player.GetHeldEntity();
                    if (checkhammer != null)
                        if (checkhammer.ShortPrefabName.Contains("hammer"))
                        {
                            Puts(checkhammer.ToString());
                            return null;
                        }
                }
                //Makes HQM invinsable unless bypassed with hammer.
                if (block.grade == (BuildingGrade.Enum)4)
                {
                    block.grounded = true;
                    return false;
                }
            }
                return null;
        }

        object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            //Blocks damage to safe space floors to stop being shot out from ground with sniper and expo ammo if its HQM
            var block = entity as BuildingBlock;
            if (!block) return null;
            if (block.blockDefinition.hierachyName.Contains("floor") && block.transform.position.y > 800f && block.grade == (BuildingGrade.Enum)4)
            {
                return false;
            }
            return null;
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

        private void ShowSafeSpaces(BasePlayer viewplayer, ulong filter)
        {
            foreach (KeyValuePair<SleepingBag, ulong> ent in SafeSpaceBags)
            {
                //Put in try/catch since some strange non-enlish names caused issues.
                try
                {
                    if (ent.Value == filter || filter == 0)
                    {
                        SleepingBag bag = ent.Key as SleepingBag;
                        if (bag == null || bag.transform.position == null) { continue; }
                        viewplayer.SendConsoleCommand("ddraw.text", config.RefreshTimer, config.textcolor, bag.transform.position, "<size=" + config.textsize + ">" + BasePlayer.FindAwakeOrSleeping(ent.Value.ToString()).displayName + "</size>");
                    }
                }
                catch { }
            }
        }

        IEnumerator SafeSpaceScanRoutine()
        {
            do //start loop
            {
                foreach (KeyValuePair<BasePlayer, Dictionary<bool, ulong>> viewer in Viewers.ToList())
                {
                    foreach (KeyValuePair<bool, ulong> viewerinfo in viewer.Value.ToList())
                    {
                        //toggle admin flag so you can show a normal user with out it auto banning them for cheating
                        if (!viewerinfo.Key)
                        {
                            viewer.Key.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                            viewer.Key.SendNetworkUpdateImmediate();
                        }

                        ShowSafeSpaces(viewer.Key, viewerinfo.Value);

                        if (!viewerinfo.Key && viewer.Key.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                        {
                            viewer.Key.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                            viewer.Key.SendNetworkUpdateImmediate();
                        }
                    }
                    if (!viewer.Key.IsConnected || viewer.Key.IsSleeping())
                    {
                        //Remove from viewers list
                        Viewers.Remove(viewer.Key);
                        message(viewer.Key, "View", "Stopped");
                    }
                }
                yield return CoroutineEx.waitForSeconds(config.RefreshTimer);
            } while (Viewers.Count != 0);
            _routine = null;
            Puts("SafeSpace View Thread Stopped!");
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

        private void ClearSpace(Dictionary<SleepingBag, ulong> RemoveBags)
        {
            foreach (KeyValuePair<SleepingBag, ulong> oldsafespace in RemoveBags.ToList())
            {
                try
                {
                    Vector3 oldpos = oldsafespace.Key.transform.position;

                    var hits = Physics.SphereCastAll(oldsafespace.Key.transform.position, 3f, Vector3.up);
                    var x = new List<BaseEntity>();
                    foreach (var hit in hits)
                    {
                        var entity = hit.GetEntity()?.GetComponent<BuildingBlock>();
                        if (entity && !x.Contains(entity)) 
                        { 
                            try
                            {
                                entity.SetGrade((BuildingGrade.Enum)1);
                                entity.Kill(); 
                            } catch { } 
                        };
                    }
                    SafeSpaceBags.Remove(oldsafespace.Key);
                }
                catch { }
            }
        }

        private void RefreshSafeSpaceList()
        {
            SafeSpaceBags.Clear();
            foreach (SleepingBag bags in GameObject.FindObjectsOfType<SleepingBag>())
            {
                if (IsSafeSpace(bags.skinID))
                {
                    SafeSpaceBags.Add(bags, bags.OwnerID);
                }
            }
        }

        private void CheckIfViewed()
        {
            if (Viewers.Count == 0) //If no viewers left remove routine
            {
                ServerMgr.Instance.StopCoroutine(_routine);
                _routine = null;
                Puts("SafeSpace View Thread Stopped!");
            }
        }
        #endregion

        #region ChatCommand
        [ChatCommand("safespace")]
        private void CmdSafeSpaceTeleport(BasePlayer player, string command, string[] args)
        {
            //Takes player to there safespace.
            if (!player.IPlayer.HasPermission(permUse))
            {
                message(player, "Permission", "teleport to");
                return;
            }
            int Selection = 0;
            if (args.Count() > 0)
            {
                try
                {
                    Selection = int.Parse(args[0]);
                }
                catch { }
            }
            RefreshSafeSpaceList();
            Dictionary<SleepingBag, ulong> PlayersSafeSpaces = new Dictionary<SleepingBag, ulong>();
            foreach (KeyValuePair<SleepingBag, ulong> TPs in SafeSpaceBags.ToList())
            {
                if (TPs.Value == player.userID)
                {
                    PlayersSafeSpaces.Add(TPs.Key, TPs.Value);
                }
            }

            switch (PlayersSafeSpaces.Count)
            {
                case 0:
                    //Error no safe spaces found
                    message(player, "No");
                    return;
                case 1:
                    //Teleport them to first/only safe space
                    Teleport(player, PlayersSafeSpaces.ElementAt(0).Key.transform.position);
                    return;
                default:
                    //Check if they have selected
                    if (Selection != 0)
                    {
                        Teleport(player, PlayersSafeSpaces.ElementAt(Selection - 1).Key.transform.position);
                        return;
                    }
                    //Message about multipal safe spaces
                    message(player, "Have", PlayersSafeSpaces.Count.ToString());
                    int i = 1;
                    foreach (KeyValuePair<SleepingBag, ulong> locations in PlayersSafeSpaces)
                    {
                        message(player, "Already", i++.ToString() + ":" + locations.Key.transform.position.ToString());
                    }
                    return;
            }
        }
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
            RefreshSafeSpaceList();
            if (args.Length == 0)
            {
                message(player, "Count", SafeSpaceBags.Count.ToString());
                return;
            }

            BasePlayer filteredplayer = BasePlayer.FindAwakeOrSleeping(args[0]);
            if (filteredplayer == null)
            {
                message(player, "Cant");
                return;
            }

            int c = 0;
            foreach (KeyValuePair<SleepingBag, ulong> counter in SafeSpaceBags)
            {
                if (counter.Value == filteredplayer.userID) c++;
            }
            message(player, "Count", c.ToString() + " " + filteredplayer.displayName);
        }

        [ChatCommand("safespace.clear")]
        private void CmdSafeSpaceClear(BasePlayer player, string command, string[] args)
        {
            if (!player.IPlayer.HasPermission(permClear))
            {
                message(player, "Permission", "clear");
                return;
            }

            RefreshSafeSpaceList();
            if (args.Length == 0)
            {
                ClearSpace(SafeSpaceBags);
                message(player, "Clear", SafeSpaceBags.Count.ToString());
                return;
            }

            BasePlayer filteredplayer = BasePlayer.FindAwakeOrSleeping(args[0]);
            if (filteredplayer == null)
            {
                message(player, "Cant");
                return;
            }
            Dictionary<SleepingBag, ulong> removebags = new Dictionary<SleepingBag, ulong>();
            foreach (KeyValuePair<SleepingBag, ulong> counter in SafeSpaceBags)
            {
                if (counter.Value == filteredplayer.userID)
                {
                    removebags.Add(counter.Key, counter.Value);
                }
            }
            ClearSpace(removebags);
            message(player, "Clear", removebags.Count.ToString() + " " + filteredplayer.displayName);
            return;
        }

        [ChatCommand("safespace.view")]
        private void CmdSafeSpaceView(BasePlayer player, string command, string[] args)
        {
            if (!player.IPlayer.HasPermission(permView))
            {
                message(player, "Permission", "view");
                return;
            }

            if (_routine != null) //Check if already running
            {
                if (args.Length == 0) //No Args passed
                {
                    if (Viewers.ContainsKey(player))
                    {
                        Viewers.Remove(player); //Remove player from list
                        message(player, "View", "Stopped");
                        CheckIfViewed();
                        return;
                    }
                }
                CheckIfViewed();
            }

            if (args.Length > 0) //args passed
            {
                BasePlayer filteredplayer = BasePlayer.FindAwakeOrSleeping(args[0]);
                if (filteredplayer == null)
                {
                    message(player, "Cant");
                    return;
                }

                if (Viewers.ContainsKey(player)) //Updates filter on player
                {
                    Viewers[player] = new Dictionary<bool, ulong> { { player.IsAdmin, filteredplayer.userID } };
                }
                else
                {
                    Viewers.Add(player, new Dictionary<bool, ulong> { { player.IsAdmin, filteredplayer.userID } });
                }
                message(player, "View", "Started filtered");
            }
            else
            {
                Viewers.Add(player, new Dictionary<bool, ulong> { { player.IsAdmin, 0 } }); //Filter id 0 = all players
                message(player, "View", "Started");
            }
            RefreshSafeSpaceList(); //Make sure all bags are in list.

            if (_routine == null) //Start routine
            {
                Puts("SafeSpace View Thread Started");
                _routine = ServerMgr.Instance.StartCoroutine(SafeSpaceScanRoutine());
            }
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