#define DEBUG
#pragma warning disable IDE0051
#nullable enable

using uMod.Common;
using uMod.Common.Command;
using uMod.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace uMod.Plugins
{
  [Info("Resource Master", "BlueBeka", "0.0.0")]
  [Description("Change the amount or even kind of resource gained from gathering resources.")]
  class ResourceMaster : Plugin
  {
#nullable disable
    private ModifiersConfig modifiersConfig;
    private OverridesConfig overridesConfig;
#nullable enable
    private readonly Rates rates;
    private readonly Rates vanillaRates;

    private bool serverInitialized;

    public const string WildCard = "*";

    public ResourceMaster()
    {
      serverInitialized = false;
      rates = new Rates();
      vanillaRates = new Rates();
    }

    #region Hooks

    void OnServerInitialized()
    {
      Permissions.Register(this);

      //var loadData = Promise.All(
      //  Rates.LoadVanillaRates(this),
      //  Rates.LoadRates(this)
      //);

      var loadData = new Promise((resolve, reject) =>
      {
        var all = new IPromise[] {
          Rates.LoadVanillaRates(this),
          Rates.LoadRates(this)
        };

        int finished = 0;
        bool done = false;

        foreach (var promise in all)
        {
          promise
            .Then(() =>
            {
              if (done) return;

              finished++;
              if (finished == all.Length)
              {
                done = true;
                resolve();
              }
            })
            .Fail((Exception ex) =>
            {
              if (done) return;

              done = true;
              reject(ex);
            });
        }
      });

      loadData
        .Then(() => {
          serverInitialized = true;

          IEnumerable<ResourceDispenser> dispensers = Utils.GetAllResourceDispensers();
          foreach (var dispenser in dispensers)
          {
            EnsureRecorded(dispenser);
          }

          Rates.SaveVanillaRates(this);
          Rates.SaveRates(this);

          SetModdedResourceRates();
        })
        .Fail((Exception exception) => {
          Logger.Error("Something went wrong while trying to initialize the plugin.");
#if Debug
          Logger.Report(exception);
#endif
        });
    }

    void OnServerSave()
    {
      if (serverInitialized) {
        Rates.SaveVanillaRates(this);
        Rates.SaveRates(this);
      }
    }

    void Unload()
    {
      if (serverInitialized) {
        RestoreResourceRates();
      }
    }

    void Loaded(ModifiersConfig config)
    {
      modifiersConfig = config;
    }

    void Loaded(OverridesConfig config)
    {
      overridesConfig = config;
    }

    void OnEntitySpawned(BaseNetworkable entity)
    {
      if (!serverInitialized)
        return;

      ResourceDispenser? dispenser = entity.GetComponent<ResourceDispenser>();
      if (dispenser == null)
        return;

      EnsureRecorded(dispenser);
      SetModdedResourceRate(dispenser);
    }

    #endregion

    #region API

    #endregion

    /// <summary>
    /// Generate what the rates for the given prefab should be based on the configs.
    /// </summary>
    private void GenerateRates(string prefab)
    {
      // Already have this item?
      if (rates.Dispensers.ContainsKey(prefab))
        return;

      if (overridesConfig.Overrides.Dispensers.ContainsKey(prefab))
      {
        // TODO:
      }
      else if (vanillaRates.Dispensers.ContainsKey(prefab))
        foreach (var category in vanillaRates.Dispensers[prefab].Keys)
        {
          if (vanillaRates.Dispensers[prefab].ContainsKey(category))
            foreach (var item in vanillaRates.Dispensers[prefab][category])
            {
              float rateModifer = 1f;
              if (modifiersConfig.ResourceDispensers.ContainsKey(item.Key))
                rateModifer = modifiersConfig.ResourceDispensers[item.Key];
              else if (modifiersConfig.ResourceDispensers.ContainsKey(WildCard))
                rateModifer = modifiersConfig.ResourceDispensers[WildCard];

              if (!rates.Dispensers.ContainsKey(prefab))
                rates.Dispensers.Add(prefab, new Dictionary<string, Dictionary<string, float>>());
              if (!rates.Dispensers[prefab].ContainsKey(category))
                rates.Dispensers[prefab].Add(category, new Dictionary<string, float>());

              rates.Dispensers[prefab][category].Add(item.Key, item.Value * rateModifer);
            }
        }
    }

    /// <summary>
    /// Make sure we have the given dispenser recorded.
    /// </summary>
    private void EnsureRecorded(ResourceDispenser dispenser)
    {
      string prefab = dispenser.gameObject.ToBaseEntity().PrefabName;

      // Already have this item?
      if (!vanillaRates.Dispensers.ContainsKey(prefab))
      {
        vanillaRates.Dispensers.Add(prefab, new Dictionary<string, Dictionary<string, float>>());
        vanillaRates.Dispensers[prefab].Add("containedItems", new Dictionary<string, float>());
        vanillaRates.Dispensers[prefab].Add("finishBonus", new Dictionary<string, float>());

        foreach (ItemAmount itemAmount in dispenser.containedItems)
        {
          vanillaRates.Dispensers[prefab]["containedItems"].Add(itemAmount.itemDef.shortname, itemAmount.startAmount);
        }

        foreach (ItemAmount itemAmount in dispenser.finishBonus)
        {
          vanillaRates.Dispensers[prefab]["finishBonus"].Add(itemAmount.itemDef.shortname, itemAmount.startAmount);
        }
      }

      GenerateRates(prefab);
    }

    /// <summary>
    /// Sets the resource on the all dispensers to the modded amounts.
    /// </summary>
    private void SetModdedResourceRates()
    {
      SetResourceRates(rates);
    }

    /// <summary>
    /// Sets the resource on the given dispenser to the modded amounts.
    /// </summary>
    /// <returns>"true" if the dispenser was updated, "false" if not change was needed.</returns>
    private bool SetModdedResourceRate(ResourceDispenser dispenser)
    {
      return SetResourceRate(dispenser, rates);
    }

    /// <summary>
    /// Restore the resources on all dispensers back to vanilla amounts.
    /// </summary>
    private void RestoreResourceRates()
    {
      SetResourceRates(vanillaRates);
    }

    /// <summary>
    /// Restore the resources on the given dispenser back to vanilla amounts.
    /// </summary>
    /// <returns>"true" if the dispenser was updated, "false" if not change was needed.</returns>
    private bool RestoreResourceRate(ResourceDispenser dispenser)
    {
      return SetResourceRate(dispenser, vanillaRates);
    }

    /// <summary>
    /// Sets the resource on the all dispensers to the given rate.
    /// Note: This is an expensive operation.
    /// </summary>
    private void SetResourceRates(Rates rates)
    {
      IEnumerable<ResourceDispenser> dispensers = Utils.GetAllResourceDispensers();

      int updates = 0;
      foreach (var dispenser in dispensers)
      {
        if (SetResourceRate(dispenser, rates))
          updates++;
      }

      Logger.Info(
        string.Format(
          "Updated {0} resource dispensers of {1}.",
          updates,
          dispensers.Count()
        )
      );
    }

    /// <summary>
    /// Set the resources on the given dispenser back to the given rate.
    /// </summary>
    /// <returns>"true" if the dispenser was updated, "false" if not change was needed.</returns>
    private bool SetResourceRate(ResourceDispenser dispenser, Rates rates)
    {
      string prefab = dispenser.gameObject.ToBaseEntity().PrefabName;

      List<(string Category, ItemAmount Item)> resources = new List<(string, ItemAmount)>();
      resources.AddRange(dispenser.containedItems.Select(item => ("containedItems", item)));
      resources.AddRange(dispenser.finishBonus.Select(item => ("finishBonus", item)));

      bool changeMade = false;

      foreach (var resource in resources)
      {
        if (!rates.Dispensers.ContainsKey(prefab) || !rates.Dispensers[prefab].ContainsKey(resource.Category) || !rates.Dispensers[prefab][resource.Category].ContainsKey(resource.Item.itemDef.shortname))
        {
          //Logger.Debug(string.Format("Missing rates for \"{0}[{1}][{2}]\".", prefab, resource.Category, resource.Item.itemDef.shortname));
          continue;
        }

        float rate = rates.Dispensers[prefab][resource.Category][resource.Item.itemDef.shortname];

        if (resource.Item.startAmount != rate)
        {
          resource.Item.startAmount = rate;
          resource.Item.amount = rate * dispenser.fractionRemaining;
          changeMade = true;
        }
      }

      if (changeMade)
        dispenser.Initialize();

      return changeMade;
    }

    #region Localization

    /// <summary>
    /// Localization for this plugin.
    /// </summary>
    [Localization]
    private interface IPluginLocale : ILocale
    {
      public ICommandsLocales Commands { get; }

      /// <summary>
      /// The local for each command.
      /// </summary>
      public interface ICommandsLocales
      {
        public CommandShowData.ILocale Initialize { get; }
      }
    }

    /// <summary>
    /// The default (English) localization of this plugin.
    /// </summary>
    [Locale]
    private class LocaleEnglish : IPluginLocale
    {
      public IPluginLocale.ICommandsLocales Commands => new CommandsLocales();

      public class CommandsLocales : IPluginLocale.ICommandsLocales
      {
        public CommandShowData.ILocale Initialize => new CommandShowDataLocale();

        public class CommandShowDataLocale : CommandShowData.ILocale
        {
          public string Command => "ResourceMaster.showdata";
        }
      }
    }

    #endregion

    /// <summary>
    /// The modifier config for this plugin.
    /// </summary>
    [Config("Modifiers", Version = "1.0.0"), Toml]
    public class ModifiersConfig
    {
      // The filename for this config file.
      private const string Filename = "Modifiers";

      public Dictionary<string, float> ResourceDispensers = new Dictionary<string, float>() { { WildCard, 1.0f } };
      public Dictionary<string, float> ResourcePickups = new Dictionary<string, float>() { { WildCard, 1.0f } };
      public Dictionary<string, float> SurveyChargeExplo = new Dictionary<string, float>() { { WildCard, 1.0f } };

      public QuarriesDef MiningQuarries = new QuarriesDef();
      public class QuarriesDef
      {
        public QuarryOptions Stone = new QuarryOptions();
        public QuarryOptions HighQuality = new QuarryOptions();
        public QuarryOptions Sulfur = new QuarryOptions();
        public QuarryOptions PlayerPlaced = new QuarryOptions();

        public class QuarryOptions
        {
          public Dictionary<string, float> Modifiers = new Dictionary<string, float>() { { WildCard, 1.0f } };
          public float TickRate = 5.0f;
        };
      };

      public PumpJacksDef PumpJacks = new PumpJacksDef();
      public class PumpJacksDef
      {
        public QuarriesDef.QuarryOptions Monument = new QuarriesDef.QuarryOptions();
        public QuarriesDef.QuarryOptions PlayerPlaced = new QuarriesDef.QuarryOptions();
      };

      public ExcavatorDef Excavator = new ExcavatorDef();
      public class ExcavatorDef
      {
        public float TickRate = 5.0f;
        public float TimeForFullResources = 120.0f;
      }
    }

    /// <summary>
    /// The overrides config for this plugin.
    /// </summary>
    [Config(Filename, Version = "1.0.0"), Toml]
    public class OverridesConfig
    {
      // The filename for this config file.
      private const string Filename = "Overrides";

      public OverridesDef Overrides = new OverridesDef();
      public class OverridesDef
      {
        public Dictionary<string, DispensersDef> Dispensers = new Dictionary<string, DispensersDef>();
        public class DispensersDef
        {
          public Dictionary<string, float> ContainedItems = new Dictionary<string, float>();
          public Dictionary<string, float> FinishBonus = new Dictionary<string, float>();
        }

        public Dictionary<string, Dictionary<string, float>> Pickup = new Dictionary<string, Dictionary<string, float>>();
        public Dictionary<string, float> Survey = new Dictionary<string, float>();
      }
    }

    /// <summary>
    /// The data this plugin save.
    /// </summary>
    [Json]
    private class Rates
    {
      // The filename for this data file.
      private const string RatesFilename = "Rates";
      private const string VanillaRatesFilename = "VanillaRates";

      // The data file.
      private IDataFile<Rates>? DataFile;

      // Dispensers data.
      public Dictionary<string, Dictionary<string, Dictionary<string, float>>> Dispensers { get; protected set; }

      public Rates()
      {
        Dispensers = new Dictionary<string, Dictionary<string, Dictionary<string, float>>>();
      }

      /// <summary>
      /// Initialize this data file.
      /// </summary>
      private void Init(ResourceMaster plugin, string filename)
      {
        DataFile = plugin.Files.GetDataFile<Rates>(filename);
      }

      /// <summary>
      /// Save the rates data.
      /// </summary>
      public static IPromise SaveRates(ResourceMaster plugin)
      {
        return Save(plugin, RatesFilename, plugin.rates);
      }

      /// <summary>
      /// Save the vanilla rates data.
      /// </summary>
      public static IPromise SaveVanillaRates(ResourceMaster plugin)
      {
        return Save(plugin, VanillaRatesFilename, plugin.vanillaRates);
      }

      /// <summary>
      /// Save the data.
      /// </summary>
      private static IPromise Save(ResourceMaster plugin, string filename, Rates data)
      {
        if (data.DataFile == null)
          data.Init(plugin, filename);

        data.DataFile!.Object = data;
        return data.DataFile.SaveAsync()
          .Then(() => {});
      }

      /// <summary>
      /// Load the data.
      /// </summary>
      public static IPromise LoadRates(ResourceMaster plugin)
      {
        return Load(plugin, RatesFilename, plugin.rates);
      }

      /// <summary>
      /// Load the rates data.
      /// </summary>
      public static IPromise LoadVanillaRates(ResourceMaster plugin)
      {
        return Load(plugin, VanillaRatesFilename, plugin.vanillaRates);
      }

      /// <summary>
      /// Load the vanilla rates data.
      /// </summary>
      private static IPromise Load(ResourceMaster plugin, string filename, Rates data)
      {
        if (data.DataFile == null)
          data.Init(plugin, filename);

        void onDone(Rates rates)
        {
          if (rates.Dispensers.Count == 0)
            plugin.Logger.Error("No data was loaded - something went wrong.");

          data.Dispensers = rates.Dispensers;
        }

        if (data.DataFile!.IsLoaded)
        {
          onDone(data.DataFile.Object);
          return new Promise(PromiseState.Resolved);
        }
        else
        {
          return data.DataFile.LoadAsync()
            .Then(onDone)
            .Fail((Exception exception) =>
            {
              // No existing settings?
              if (exception is System.IO.FileNotFoundException)
              {
                plugin.Logger.Debug(string.Format("No existing file {0} found.", filename));
                return;
              }

              throw exception;
            });
        }
      }
    }

    /// <summary>
    /// The permissions this plugin uses.
    /// </summary>
    private static class Permissions
    {
      // Permissions.
      public const string Admin = "ResourceMaster.admin";

      /// <summary>
      /// Register the permissions.
      /// </summary>
      public static void Register(ResourceMaster plugin)
      {
        if (!plugin.permission.PermissionExists(Admin, plugin))
        {
          plugin.permission.RegisterPermission(Admin, plugin);
        }
      }
    }

    /// <summary>
    /// The command to generate the data this plugin is using internally.
    /// </summary>
    [Command("Commands.ShowData.Command"), Permission(Permissions.Admin)]
    [Locale(typeof(IPluginLocale))]
    public CommandState ShowDataCommand(IPlayer? player, IArgs args)
    {
      return CommandShowData.Handle(this);
    }

    /// <summary>
    /// The "show data" command.
    /// </summary>
    private static class CommandShowData
    {
      /// <summary>
      /// The implementation of the show data command.
      /// </summary>
      public static CommandState Handle(ResourceMaster plugin)
      {
        Rates.SaveVanillaRates(plugin);
        Rates.SaveRates(plugin);
        return CommandState.Completed;
      }

      /// <summary>
      /// All the data that needs to be localized.
      /// </summary>
      public interface ILocale
      {
        public string Command { get; }
      }
    }

    /// <summary>
    /// Utility functions.
    /// </summary>
    private static class Utils
    {
      /// <summary>
      /// Get all the resource dispensers on the server.
      /// </summary>
      public static IEnumerable<ResourceDispenser> GetAllResourceDispensers()
      {
        return BaseNetworkable.serverEntities
          .Select(entity => entity.GetComponent<ResourceDispenser>())
          .Where(dispenser => dispenser != null);
      }
    }
  }
}
