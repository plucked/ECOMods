using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Eco.Core.Plugins;
using Eco.Gameplay.Items;

namespace Eco.Mods
{
    using Shared.Localization;
    using Eco.Core.Utils;
    using Gameplay.Components;
    using Eco.Shared.Utils;
    using Core.Plugins.Interfaces;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Core.Controller;
    using Gameplay.Objects;
    using Shared.IoC;

    [Localized]
    public class TradeOfferLimitsConfig : Singleton<TradeOfferLimitsConfig>
    {
        [LocDescription("The minimum price for selling items to a store")]
        public SerializedSynchronizedCollection<ItemPricePair> MinSellPrices { get; set; } = new();

        [LocDescription("The maximum price for buying items from a store")]
        public SerializedSynchronizedCollection<ItemPricePair> MaxBuyPrices { get; set; } = new();

        [LocDescription("The interval in seconds in which the prices are checked and updated")]
        public int TickIntervalSeconds { get; set; } = 600;

        // lazy caching
        private Dictionary<string, float> CachedMinSellPrices = new();
        private Dictionary<string, float> CachedMaxBuyPrices = new();

        public (Dictionary<string, float> minSellPrices, Dictionary<string, float> maxBuyPrices) GetCachedPrices()
        {
            return (CachedMinSellPrices, CachedMaxBuyPrices);
        }

        public void UpdateCache()
        {
            CachedMinSellPrices = MinSellPrices.ToDictionary(p => p.ItemName, p => p.Price);
            CachedMaxBuyPrices = MaxBuyPrices.ToDictionary(p => p.ItemName, p => p.Price);
        }

        public class ItemPricePair
        {
            public string ItemName { get; set; }
            public float Price { get; set; }

            public override string ToString() => $"{ItemName}: {Price}";
        }
    }


    [Worker(Repeatable = true)]
    public class TradeOfferLimits : IModKitPlugin, IInitializablePlugin, IWorkerPlugin, IConfigurablePlugin
    {
        PluginConfig<TradeOfferLimitsConfig> config;

        public IPluginConfig PluginConfig
        {
            get => this.config;
        }

        public ThreadSafeAction<object, string> ParamChanged { get; set; } = new();

        public object GetEditObject() => this.config.Config;

        public void OnEditObjectChanged(object o, string param)
        {
            this.config.Config.UpdateCache();
           this.SaveConfig();
        }

        string status = string.Empty;
        public string GetStatus() => this.status;
        public string GetCategory() => "TradeOfferLimits";

        public async Task DoWork(CancellationToken token)
        {
            try
            {
                var storeComponents = ServiceHolder<IWorldObjectManager>.Obj.All
                    .Where(wo => wo.HasComponent<StoreComponent>())
                    .Select(wo => wo.GetComponents<StoreComponent>());

                foreach (var storeComponent in storeComponents)
                {
                    foreach (var store in storeComponent)
                    {
                        CorrectStorePrices(store);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(config.Config.TickIntervalSeconds), token);
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
            catch (Exception)
            {
                // lets not crash the server
                config.Config.TickIntervalSeconds = (int)TimeSpan.FromHours(1).TotalSeconds;
            }
        }

        public void Initialize(TimedTask timer)
        {
            this.status = "Ready.";
            this.config = new PluginConfig<TradeOfferLimitsConfig>("TradeOfferLimits");

            // get all items that can be sold to a store
            var allItems = Item.AllItems;

            // check if config contains entries for all items
            foreach (var item in allItems)
            {
                if (!this.config.Config.MinSellPrices.Any(p => p.ItemName == item.Name))
                {
                    this.config.Config.MinSellPrices.Add(new TradeOfferLimitsConfig.ItemPricePair
                    {
                        ItemName = item.Name,
                        Price = -100_000.0f
                    });
                }

                if (!this.config.Config.MaxBuyPrices.Any(p => p.ItemName == item.Name))
                {
                    this.config.Config.MaxBuyPrices.Add(new TradeOfferLimitsConfig.ItemPricePair
                    {
                        ItemName = item.Name,
                        Price = 100_000.0f
                    });
                }
            }

            config.Config.UpdateCache();

            this.SaveConfig();
        }

        private int activeControllerId = int.MinValue;

        private void CorrectStorePrices(StoreComponent storeComponent)
        {
            try
            {
                // guard against infinite recursion by callbacks
                if (activeControllerId == storeComponent.ControllerID)
                {
                    return;
                }

                activeControllerId = storeComponent.ControllerID;

                bool changed = false;
                if (LimitOfferPrices(true, storeComponent.StoreData))
                {
                    Log.WriteLine(new LocString($"Updating sell prices for {storeComponent.Parent.ID}"));
                    storeComponent.StoreData.Changed(nameof(storeComponent.StoreData.SellCategories));
                    changed = true;
                }

                if (LimitOfferPrices(false, storeComponent.StoreData))
                {
                    Log.WriteLine(new LocString($"Updating buy prices for {storeComponent.Parent.ID}"));
                    storeComponent.StoreData.Changed(nameof(storeComponent.StoreData.BuyCategories));
                    changed = true;
                }

                if (changed)
                {
                    var method = typeof(StoreComponent).GetMethod("UpdateStock",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (method != null) method.Invoke(storeComponent, new object[] { false });
                }
            }
            finally
            {
                activeControllerId = int.MinValue;
            }
        }

        /// <summary>
        /// Limits the sell and buy prices according to the priceLimit.
        ///
        /// Returns false if no price update was needed.
        /// </summary>
        private bool LimitOfferPrices(bool sell, StoreItemData storeItemData)
        {
            var (minSellPrices, maxBuyPrices) = config.Config.GetCachedPrices();

            bool changed = false;
            if (sell)
            {
                foreach (var sellCategory in storeItemData.SellCategories)
                {
                    foreach (var offer in sellCategory.Offers)
                    {
                        if (offer == null || offer.Stack == null || offer.Stack.Item == null)
                        {
                            continue;
                        }

                        if (minSellPrices.TryGetValue(offer.Stack.Item.Name, out var priceLimit) &&
                            offer.Price < priceLimit)
                        {
                            offer.Price = priceLimit;
                            offer.Changed(nameof(TradeOffer.Price));
                            changed = true;
                        }
                    }
                }
            }
            else
            {
                foreach (var buyCategory in storeItemData.BuyCategories)
                {
                    foreach (var offer in buyCategory.Offers)
                    {
                        if (offer == null || offer.Stack == null || offer.Stack.Item == null)
                        {
                            continue;
                        }

                        if (maxBuyPrices.TryGetValue(offer.Stack.Item.Name, out var priceLimit) &&
                            offer.Price > priceLimit)
                        {
                            offer.Price = priceLimit;
                            offer.Changed(nameof(TradeOffer.Price));
                            changed = true;
                        }
                    }
                }
            }

            return changed;
        }
    }
}
