using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;
using Oxide.Core.Plugins;
using Newtonsoft.Json;
using System.Linq;

using System.Reflection;
using Facepunch.Steamworks;
using Rust;

namespace Oxide.Plugins
{
    [Info("Skin", "Ubuntu", "0.0.1", ResourceId = 1242)]
    class Skin : RustPlugin
    {
        private string box = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        private BasePlayer _Player;
        private StorageContainer _View;
        private readonly Dictionary<string, List<int>> skinsCache = new Dictionary<string, List<int>>();
        private readonly FieldInfo skins2 = typeof (ItemDefinition).GetField("_skins2", BindingFlags.NonPublic | BindingFlags.Instance);
        private Dictionary<string, string> displaynameToShortname;

        private void OnServerInitialized()
        {
            displaynameToShortname.Clear();
            List<ItemDefinition> ItemsDefinition = ItemManager.GetItemDefinitions();
            foreach (ItemDefinition itemdef in ItemsDefinition)
            {
                displaynameToShortname.Add(itemdef.displayName.english.ToLower(), itemdef.shortname);
            }

            webrequest.EnqueueGet("http://s3.amazonaws.com/s3.playrust.com/icons/inventory/rust/schema.json", ReadScheme, this);
        }

        void Loaded()
        {
            permission.RegisterPermission("skin.use", this);
            displaynameToShortname = new Dictionary<string, string>();
        }

        void Unloaded()
        {
            if (_View != null)
            {
                CloseBoxView();
            }
        }

        [ChatCommand("skin")]
        void cmdSkin(BasePlayer player, string command, string[] args)
        {
            _Player = player;
            if (_View == null)
            {
                OpenBoxView(player, player);
                return;
            }
            CloseBoxView();
            timer.In(1f, () => OpenBoxView(player, player));
        }

        void OpenBoxView(BasePlayer player, BaseEntity targArg)
        {
            var pos = new Vector3(player.transform.position.x, player.transform.position.y - 1, player.transform.position.z);
            var corpse = GameManager.server.CreateEntity(box, pos) as StorageContainer;
            corpse.transform.position = pos;

            if (!corpse) return;

            _View = corpse as StorageContainer;
            player.EndLooting();
            if (targArg is BasePlayer)
            {

                BasePlayer target = targArg as BasePlayer;
                ItemContainer container = new ItemContainer();
                container.playerOwner = player;
                container.ServerInitialize((Item)null, 30);
                if ((int)container.uid == 0)
                    container.GiveUID();

                _View.enableSaving = false;
                _View.Spawn();
                _View.inventory = container;

                timer.In(0.1f, () => _View.PlayerOpenLoot(_Player));
            }
        }

        void CloseBoxView()
        {
            if (_View == null) return;

            /*if (_View.inventory.itemList.Count > 0)
            {
                foreach (Item item in _View.inventory.itemList.ToArray())
                {
                    if (item.position != -1)
                    {
                        item.MoveToContainer(_Player.inventory.containerMain);
                    }
                }
            }*/

            if (_Player.inventory.loot.entitySource != null)
            {
                _Player.inventory.loot.Invoke("SendUpdate", 0.1f);
                _View.SendMessage("PlayerStoppedLooting", _Player, SendMessageOptions.DontRequireReceiver);
                _Player.SendConsoleCommand("inventory.endloot", null);
            }

            _Player.inventory.loot.entitySource = null;
            _Player.inventory.loot.itemSource = null;
            _Player.inventory.loot.containers = new List<ItemContainer>();

            _View.inventory = new ItemContainer();
            _View.Kill(BaseNetworkable.DestroyMode.None);
            _View = null;
            _Player = null;
        }

        void OnPlayerDisconnected(BasePlayer player)
        {
            if (_View == null || _Player == null) return;

            if (player == _Player)
            {
                CloseBoxView();
            }
        }

        void OnPlayerLootEnd(PlayerLoot inventory)
        {
            if (_View == null || _Player == null) return;

            var player = inventory.GetComponent<BasePlayer>();
            if (player != _Player)
                return;
            
            CloseBoxView();
        }

        void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (_View == null || _Player == null) return;
            if (container.playerOwner is BasePlayer && container.playerOwner == _Player && _View.inventory == container)
            {
                //if(item.GetOwnerPlayer() != _Player) return;

                Puts("Item {0} added {1}", item.info.displayName.english.ToLower(), item.amount);
                string itemname = item.info.displayName.english.ToLower();

                if (displaynameToShortname.ContainsKey(itemname))
                    itemname = displaynameToShortname[itemname];
                Puts("Item name: {0}", itemname);
                var definition = ItemManager.FindItemDefinition(itemname);
                if (definition == null) return;

                var skins = GetSkins(definition);
                if (skins.Count == 0) return;

                foreach(int skin in skins) {
                    var newItem = ItemManager.CreateByItemID(definition.itemid, 1, Convert.ToUInt64(skin));
                    //newItem.MoveToContainer(container);
                    _Player.inventory.GiveItem(newItem, _Player.inventory.containerMain);
                }
            }
        }

        void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (_View == null || _Player == null) return;
            if (container.playerOwner is BasePlayer && container.playerOwner == _Player && _View.inventory == container)
            {
                Puts("Item {0} removed {1}", item.info.displayName.english, item.amount);
            }
        }

        private void ReadScheme(int code, string response)
        {
            if (response != null && code == 200)
            {
                var schema = JsonConvert.DeserializeObject<Rust.Workshop.ItemSchema>(response);
                var defs = new List<Inventory.Definition>();
                foreach (var item in schema.items)
                {
                    if (string.IsNullOrEmpty(item.itemshortname)) continue;
                    var steamItem = Global.SteamServer.Inventory.CreateDefinition((int)item.itemdefid);
                    steamItem.Name = item.name;
                    steamItem.SetProperty("itemshortname", item.itemshortname);
                    steamItem.SetProperty("workshopid", item.workshopid);
                    steamItem.SetProperty("workshopdownload", item.workshopdownload);
                    defs.Add(steamItem);
                }

                Global.SteamServer.Inventory.Definitions = defs.ToArray();

                foreach (var item in ItemManager.itemList)
                    skins2.SetValue(item, Global.SteamServer.Inventory.Definitions.Where(x => (x.GetStringProperty("itemshortname") == item.shortname) && !string.IsNullOrEmpty(x.GetStringProperty("workshopdownload"))).ToArray());

                Puts($"Loaded {Global.SteamServer.Inventory.Definitions.Length} approved workshop skins.");
            }
            else
            {
                PrintWarning($"Failed to load approved workshop skins... Error {code}");
            }
        }

        private List<int> GetSkins(ItemDefinition def)
        {
            List<int> skins;
            if (skinsCache.TryGetValue(def.shortname, out skins)) return skins;
            skins = new List<int>();
            if (def.skins != null) skins.AddRange(def.skins.Select(skin => skin.id));
            if (def.skins2 != null) skins.AddRange(def.skins2.Select(skin => skin.Id));
            skinsCache.Add(def.shortname, skins);
            return skins;
        }
    }

}