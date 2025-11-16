using PluginContracts;
using System.Net.Http;
using System.Text.Json;

namespace PokemonPlugin;

public sealed class PokemonEndpoints : IEndpointModule
{
    public string Name => "pokemon";

    private readonly HttpClient _http = new();
    // Caches
    private readonly Dictionary<int, JsonElement> _basicCache = new();     // raw pokemon/{id}
    private readonly Dictionary<int, JsonElement> _speciesCache = new();   // raw pokemon-species/{id}
    private readonly Dictionary<int, (string de, string en)> _nameCache = new();
    // NEU: Lokalisierungs-Caches für Typen und Fähigkeiten
    private readonly Dictionary<string, string> _typeDeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _abilityDeCache = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IPluginEndpointRegistry r)
    {
        // Basic: stats, types, abilities
        r.AddGet("/pokemon/basic/{id:int}", (Func<int, Task<object>>)(async id =>
        {
            var basic = await GetBasicAsync(id);
            if (basic is null) return new { id, error = "Not found" };
            return new
            {
                id,
                name_en = basic.Value.GetProperty("name").GetString(),
                types = basic.Value.GetProperty("types").EnumerateArray()
                    .Select(t => t.GetProperty("type").GetProperty("name").GetString()).ToArray(),
                abilities = basic.Value.GetProperty("abilities").EnumerateArray()
                    .Select(a => a.GetProperty("ability").GetProperty("name").GetString()).ToArray(),
                stats = basic.Value.GetProperty("stats").EnumerateArray()
                    .Select(s => new
                    {
                        stat = s.GetProperty("stat").GetProperty("name").GetString(),
                        baseValue = s.GetProperty("base_stat").GetInt32()
                    }).ToArray(),
                height = basic.Value.GetProperty("height").GetInt32(),
                weight = basic.Value.GetProperty("weight").GetInt32()
            };
        }));

        // Species: German + English name, flavor texts (latest per language)
        r.AddGet("/pokemon/species/{id:int}", (Func<int, Task<object>>)(async id =>
        {
            var species = await GetSpeciesAsync(id);
            if (species is null) return new { id, error = "Not found" };
            var names = await GetLocalizedNamesAsync(id);
            var flavor = ExtractFlavors(species.Value);
            return new
            {
                id,
                name_de = names?.de,
                name_en = names?.en,
                genus_de = flavor.genus_de,
                genus_en = flavor.genus_en,
                flavor_de = flavor.flavor_de,
                flavor_en = flavor.flavor_en
            };
        }));

        // Combined: basic + species + localized names
        r.AddGet("/pokemon/all/{id:int}", (Func<int, Task<object>>)(async id =>
        {
            var basic = await GetBasicAsync(id);
            var species = await GetSpeciesAsync(id);
            if (basic is null || species is null) return new { id, error = "Not found" };
            var names = await GetLocalizedNamesAsync(id);
            var flavor = ExtractFlavors(species.Value);
            return new
            {
                id,
                name_en = names?.en,
                name_de = names?.de,
                types = basic.Value.GetProperty("types").EnumerateArray()
                    .Select(t => t.GetProperty("type").GetProperty("name").GetString()).ToArray(),
                abilities = basic.Value.GetProperty("abilities").EnumerateArray()
                    .Select(a => a.GetProperty("ability").GetProperty("name").GetString()).ToArray(),
                stats = basic.Value.GetProperty("stats").EnumerateArray()
                    .Select(s => new
                    {
                        stat = s.GetProperty("stat").GetProperty("name").GetString(),
                        baseValue = s.GetProperty("base_stat").GetInt32()
                    }).ToArray(),
                height = basic.Value.GetProperty("height").GetInt32(),
                weight = basic.Value.GetProperty("weight").GetInt32(),
                genus_de = flavor.genus_de,
                genus_en = flavor.genus_en,
                flavor_de = flavor.flavor_de,
                flavor_en = flavor.flavor_en
            };
        }));

        // Name lookup by German name (reverse)
        r.AddGet("/pokemon/by-de/{name}", (Func<string, Task<object>>)(async name =>
        {
            var id = await FindByLocalizedNameAsync(name, "de");
            return id is null ? new { name, error = "Not found" } : new { name, id };
        }));

        // Name lookup by English name
        r.AddGet("/pokemon/by-en/{name}", (Func<string, Task<object>>)(async name =>
        {
            var id = await FindByLocalizedNameAsync(name, "en");
            return id is null ? new { name, error = "Not found" } : new { name, id };
        }));

        // DE: Basisdaten mit deutschen Typ- und Fähigkeitsnamen
        r.AddGet("/pokemon/de/basic/{id:int}", (Func<int, Task<object>>)(async id =>
        {
            var basic = await GetBasicAsync(id);
            if (basic is null) return new { id, fehler = "Nicht gefunden" };
            var names = await GetLocalizedNamesAsync(id);
            var typesEn = basic.Value.GetProperty("types").EnumerateArray()
                .Select(t => t.GetProperty("type").GetProperty("name").GetString()!).ToArray();
            var typesDe = await Task.WhenAll(typesEn.Select(GetTypeDeAsync));
            var abilitiesEn = basic.Value.GetProperty("abilities").EnumerateArray()
                .Select(a => a.GetProperty("ability").GetProperty("name").GetString()!).ToArray();
            var abilitiesDe = await Task.WhenAll(abilitiesEn.Select(GetAbilityDeAsync));

            return new
            {
                id,
                name_de = names?.de,
                typ_de = typesDe,
                faehigkeiten_de = abilitiesDe,
                groesse = basic.Value.GetProperty("height").GetInt32(),
                gewicht = basic.Value.GetProperty("weight").GetInt32()
            };
        }));

        // DE: Speziesdaten (Name, Gattung, Flavor)
        r.AddGet("/pokemon/de/species/{id:int}", (Func<int, Task<object>>)(async id =>
        {
            var species = await GetSpeciesAsync(id);
            if (species is null) return new { id, fehler = "Nicht gefunden" };
            var names = await GetLocalizedNamesAsync(id);
            var flavor = ExtractFlavors(species.Value);
            return new
            {
                id,
                name_de = names?.de,
                gattung_de = flavor.genus_de,
                text_de = flavor.flavor_de
            };
        }));

        // DE: Alles kombiniert (Basis + Spezies)
        r.AddGet("/pokemon/de/all/{id:int}", (Func<int, Task<object>>)(async id =>
        {
            var basic = await GetBasicAsync(id);
            var species = await GetSpeciesAsync(id);
            if (basic is null || species is null) return new { id, fehler = "Nicht gefunden" };
            var names = await GetLocalizedNamesAsync(id);
            var flavor = ExtractFlavors(species.Value);
            var typesEn = basic.Value.GetProperty("types").EnumerateArray()
                .Select(t => t.GetProperty("type").GetProperty("name").GetString()!).ToArray();
            var typesDe = await Task.WhenAll(typesEn.Select(GetTypeDeAsync));
            var abilitiesEn = basic.Value.GetProperty("abilities").EnumerateArray()
                .Select(a => a.GetProperty("ability").GetProperty("name").GetString()!).ToArray();
            var abilitiesDe = await Task.WhenAll(abilitiesEn.Select(GetAbilityDeAsync));

            var stats = basic.Value.GetProperty("stats").EnumerateArray()
                .Select(s => new
                {
                    stat_en = s.GetProperty("stat").GetProperty("name").GetString(),
                    basiswert = s.GetProperty("base_stat").GetInt32()
                }).ToArray();

            return new
            {
                id,
                name_de = names?.de,
                typ_de = typesDe,
                faehigkeiten_de = abilitiesDe,
                groesse = basic.Value.GetProperty("height").GetInt32(),
                gewicht = basic.Value.GetProperty("weight").GetInt32(),
                gattung_de = flavor.genus_de,
                text_de = flavor.flavor_de,
                stats
            };
        }));

        // DE: Lookup nach deutschem Namen
        r.AddGet("/pokemon/de/by-name/{name}", (Func<string, Task<object>>)(async name =>
        {
            var id = await FindByLocalizedNameAsync(name, "de");
            return id is null ? new { name, fehler = "Nicht gefunden" } : new { name, id };
        }));

        // DE: Liste Bereich deutscher Namen
        r.AddGet("/pokemon/de/list/{start:int}/{end:int}", (Func<int,int,Task<object>>)(async (start, end) =>
        {
            if (start < 1 || end < start || end - start > 1000) return new { fehler = "Ungültiger Bereich" };
            var result = new List<object>();
            for (int id = start; id <= end; id++)
            {
                var names = await GetLocalizedNamesAsync(id);
                if (names != null) result.Add(new { id, name_de = names.Value.de });
            }
            return new { start, end, anzahl = result.Count, eintraege = result };
        }));

        // Clear caches (admin)
        r.AddPost("/pokemon/cache/clear", (Func<object>)(() =>
        {
            _basicCache.Clear();
            _speciesCache.Clear();
            _nameCache.Clear();
            _typeDeCache.Clear();
            _abilityDeCache.Clear();
            return new { cleared = true };
        }));
    }

    private async Task<JsonElement?> GetBasicAsync(int id)
    {
        if (id < 1) return null;
        if (_basicCache.TryGetValue(id, out var elem)) return elem;
        using var resp = await _http.GetAsync($"https://pokeapi.co/api/v2/pokemon/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement.Clone();
        _basicCache[id] = root;
        return root;
    }

    private async Task<JsonElement?> GetSpeciesAsync(int id)
    {
        if (id < 1) return null;
        if (_speciesCache.TryGetValue(id, out var elem)) return elem;
        using var resp = await _http.GetAsync($"https://pokeapi.co/api/v2/pokemon-species/{id}");
        if (!resp.IsSuccessStatusCode) return null;
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement.Clone();
        _speciesCache[id] = root;
        return root;
    }

    private async Task<(string de, string en)?> GetLocalizedNamesAsync(int id)
    {
        if (_nameCache.TryGetValue(id, out var tuple)) return tuple;
        var species = await GetSpeciesAsync(id);
        if (species is null) return null;

        string? de = null;
        string? en = null;
        if (species.Value.TryGetProperty("names", out var namesElem))
        {
            foreach (var n in namesElem.EnumerateArray())
            {
                var lang = n.GetProperty("language").GetProperty("name").GetString();
                var val = n.GetProperty("name").GetString();
                if (lang == "de") de = val;
                else if (lang == "en") en = val;
                if (de != null && en != null) break;
            }
        }
        if (de == null && en == null) return null;
        var result = (de ?? en ?? "unknown", en ?? de ?? "unknown");
        _nameCache[id] = result;
        return result;
    }

    private (string? genus_de, string? genus_en, string? flavor_de, string? flavor_en) ExtractFlavors(JsonElement species)
    {
        string? genus_de = null;
        string? genus_en = null;
        if (species.TryGetProperty("genera", out var generaElem))
        {
            foreach (var g in generaElem.EnumerateArray())
            {
                var lang = g.GetProperty("language").GetProperty("name").GetString();
                var val = g.GetProperty("genus").GetString();
                if (lang == "de") genus_de = val;
                else if (lang == "en") genus_en = val;
                if (genus_de != null && genus_en != null) break;
            }
        }

        string? flavor_de = null;
        string? flavor_en = null;
        if (species.TryGetProperty("flavor_text_entries", out var flavorElem))
        {
            // Pick latest entries (higher variety) - we just take first matching if none cached
            foreach (var f in flavorElem.EnumerateArray())
            {
                var lang = f.GetProperty("language").GetProperty("name").GetString();
                var val = f.GetProperty("flavor_text").GetString()?.Replace('\n', ' ').Replace('\f', ' ').Trim();
                if (lang == "de" && flavor_de == null) flavor_de = val;
                else if (lang == "en" && flavor_en == null) flavor_en = val;
                if (flavor_de != null && flavor_en != null) break;
            }
        }
        return (genus_de, genus_en, flavor_de, flavor_en);
    }

    private async Task<int?> FindByLocalizedNameAsync(string name, string lang)
    {
        // Brute force limited range; optimize with external indexing if needed.
        // Here we scan first 1000 species; adjust as required.
        name = name.Trim();
        for (int id = 1; id <= 1000; id++)
        {
            var names = await GetLocalizedNamesAsync(id);
            if (names is null) continue;
            var match = (lang == "de" ? names.Value.de : names.Value.en);
            if (string.Equals(match, name, StringComparison.OrdinalIgnoreCase))
                return id;
        }
        return null;
    }

    // NEU: Deutscher Typname
    private async Task<string> GetTypeDeAsync(string typeNameEn)
    {
        if (_typeDeCache.TryGetValue(typeNameEn, out var de)) return de;
        using var resp = await _http.GetAsync($"https://pokeapi.co/api/v2/type/{typeNameEn}");
        if (!resp.IsSuccessStatusCode) return typeNameEn;
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        string? nameDe = null;
        if (doc.RootElement.TryGetProperty("names", out var namesElem))
        {
            foreach (var n in namesElem.EnumerateArray())
            {
                var lang = n.GetProperty("language").GetProperty("name").GetString();
                if (lang == "de")
                {
                    nameDe = n.GetProperty("name").GetString();
                    break;
                }
            }
        }
        nameDe ??= typeNameEn;
        _typeDeCache[typeNameEn] = nameDe;
        return nameDe;
    }

    // NEU: Deutscher Fähigkeitsname
    private async Task<string> GetAbilityDeAsync(string abilityNameEn)
    {
        if (_abilityDeCache.TryGetValue(abilityNameEn, out var de)) return de;
        using var resp = await _http.GetAsync($"https://pokeapi.co/api/v2/ability/{abilityNameEn}");
        if (!resp.IsSuccessStatusCode) return abilityNameEn;
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        string? nameDe = null;
        if (doc.RootElement.TryGetProperty("names", out var namesElem))
        {
            foreach (var n in namesElem.EnumerateArray())
            {
                var lang = n.GetProperty("language").GetProperty("name").GetString();
                if (lang == "de")
                {
                    nameDe = n.GetProperty("name").GetString();
                    break;
                }
            }
        }
        nameDe ??= abilityNameEn;
        _abilityDeCache[abilityNameEn] = nameDe;
        return nameDe;
    }

    public void Dispose()
    {
        _http.Dispose();
        _basicCache.Clear();
        _speciesCache.Clear();
        _nameCache.Clear();
        _typeDeCache.Clear();
        _abilityDeCache.Clear();
    }
}
