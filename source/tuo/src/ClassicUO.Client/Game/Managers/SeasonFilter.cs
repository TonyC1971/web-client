using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicUO.Configuration;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Game.Managers
{
    public class SeasonFilter
    {
        private Dictionary<Season, Season> _seasonFilters;
        private bool _isLoaded;

        public static SeasonFilter Instance
        {
            get
            {
                field ??= new SeasonFilter();
                return field;
            }
        }

        public Dictionary<Season, Season> Filters
        {
            get
            {
                EnsureLoaded();
                return _seasonFilters;
            }
        }

        private SeasonFilter()
        {
            _seasonFilters = new Dictionary<Season, Season>();
            _isLoaded = false;
        }

        private void EnsureLoaded()
        {
            if (!_isLoaded) Load();
        }

        private void Load()
        {
            if (Client.Settings == null)
            {
                Log.Warn("SQLSettings not available for SeasonFilter");
                _seasonFilters = new Dictionary<Season, Season>();
                _isLoaded = true;
                return;
            }

            try
            {
                string json = Client.Settings.Get(SettingsScope.Account, Constants.SqlSettings.SEASON_FILTER, "{}");

                if (!string.IsNullOrWhiteSpace(json))
                    _seasonFilters = JsonSerializer.Deserialize(json, SeasonFilterJsonContext.Default.DictionarySeasonSeason)
                                     ?? new Dictionary<Season, Season>();
                else
                    _seasonFilters = new Dictionary<Season, Season>();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load season filters: {ex.Message}");
                _seasonFilters = new Dictionary<Season, Season>();
            }

            _isLoaded = true;
        }

        private void Save()
        {
            if (Client.Settings == null)
            {
                Log.Warn("SQLSettings not available for SeasonFilter save");
                return;
            }

            try
            {
                string json = JsonSerializer.Serialize(_seasonFilters, SeasonFilterJsonContext.Default.DictionarySeasonSeason);
                Client.Settings.Set(SettingsScope.Account, Constants.SqlSettings.SEASON_FILTER, json);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to save season filters: {ex.Message}");
            }
        }

        public Season ApplyFilter(Season incoming)
        {
            EnsureLoaded();

            // If filter exists for this season, return replacement
            if (_seasonFilters.TryGetValue(incoming, out Season replacement)) return replacement;

            // No filter, return original
            return incoming;
        }

        public void SetFilter(Season from, Season to)
        {
            EnsureLoaded();
            _seasonFilters[from] = to;

            if (World.Instance != null && World.Instance.RealSeason == from) World.Instance.ChangeSeason(to);

            Save();
        }

        public void RemoveFilter(Season from)
        {
            EnsureLoaded();
            if (_seasonFilters.Remove(from)) Save();
        }

        public void Clear()
        {
            EnsureLoaded();
            _seasonFilters.Clear();
            Save();
        }
    }

    [JsonSerializable(typeof(Dictionary<Season, Season>))]
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        IgnoreReadOnlyProperties = false,
        IncludeFields = false)]
    public partial class SeasonFilterJsonContext : JsonSerializerContext
    {
    }
}
