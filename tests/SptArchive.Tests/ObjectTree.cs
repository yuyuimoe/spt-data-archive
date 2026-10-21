namespace SptArchive.Tests;

/// <summary>
/// A baseline and a run that change the five tables that have no <c>mod_*</c> table of their own: a
/// location, a bot type, a hideout part, a price and a handbook entry (ADR-0041). The trees are small
/// and written by the test, so a case states exactly which value moved.
/// </summary>
internal static class ObjectTree
{
    public const string NodeId = "aaaaaaaaaaaaaaaaaaaaaaaa";
    public const string ItemId = "bbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>An item that only the run holds, under the id the run gave it.</summary>
    public const string AddedItemId = "dddddddddddddddddddddddd";

    public const string AreaId = "e1e1e1e1e1e1e1e1e1e1e1e1";
    public const string RecipeId = "e2e2e2e2e2e2e2e2e2e2e2e2";
    public const string AddedRecipeId = "e3e3e3e3e3e3e3e3e3e3e3e3";
    public const string CategoryId = "c1c1c1c1c1c1c1c1c1c1c1c1";
    public const string AddedCategoryId = "c2c2c2c2c2c2c2c2c2c2c2c2";

    public static Dictionary<string, string> Baseline() => new()
    {
        ["templates/items.json"] = Items(addedItem: false),
        ["templates/quests.json"] = "{}",
        ["templates/customization.json"] = "{}",

        ["locations/base.json"] = """{"locations":{},"paths":[{"Source":"woods","Destination":"bigmap","Event":false}]}""",
        ["locations/woods/base.json"] = """{"_Id":"woods","Name":"Woods","Insurance":true,"BossLocationSpawn":[{"BossName":"bossBully","BossChance":35}]}""",
        ["locations/woods/looseLoot.json"] = """{"spawnpointCount":{"mean":10,"std":2},"spawnpoints":[{"locationId":"woods_1","probability":0.1,"template":{"Id":"woods_1","Items":[{"_id":"i1","_tpl":"bbbbbbbbbbbbbbbbbbbbbbbb"}]}}]}""",

        ["bots/base.json"] = """{"_id":"bot","Info":{"Side":"Savage"}}""",
        ["bots/core.json"] = """{"SAVAGE_KILL_DIST":100}""",
        ["bots/types/assault.json"] = """{"chances":{"weaponMods":{"mod_scope":50}},"inventory":{"items":{"Backpack":{"bbbbbbbbbbbbbbbbbbbbbbbb":1}}}}""",

        ["hideout/areas.json"] = """[{"_id":"e1e1e1e1e1e1e1e1e1e1e1e1","type":1,"enabled":true,"requirements":[]}]""",
        ["hideout/customAreas.json"] = "[]",
        ["hideout/production.json"] = """{"recipes":[{"_id":"e2e2e2e2e2e2e2e2e2e2e2e2","areaType":10,"productionTime":100,"endProduct":"bbbbbbbbbbbbbbbbbbbbbbbb","requirements":[{"templateId":"bbbbbbbbbbbbbbbbbbbbbbbb","count":2}]}],"scavRecipes":[],"cultistRecipes":[]}""",
        ["hideout/settings.json"] = """{"generatorSpeedWithoutFuel":0.15}""",

        ["templates/prices.json"] = """{"bbbbbbbbbbbbbbbbbbbbbbbb":1000}""",
        ["templates/handbook.json"] = """{"Categories":[{"Id":"c1c1c1c1c1c1c1c1c1c1c1c1","ParentId":null,"Icon":"/icon.png","Color":"","Order":"100"}],"Items":[{"Id":"bbbbbbbbbbbbbbbbbbbbbbbb","ParentId":"c1c1c1c1c1c1c1c1c1c1c1c1","Price":1000}]}""",
    };

    /// <summary>
    /// What a map mod, a bot mod, a hideout mod and a price mod do together. The run adds the item
    /// <see cref="AddedItemId"/>, so every file that names it names an object that has no id in the baseline.
    /// </summary>
    public static Dictionary<string, string> Run() => new()
    {
        ["templates/items.json"] = Items(addedItem: true),

        // A new map, and a path from an old one to it.
        ["locations/base.json"] = """{"locations":{},"paths":[{"Source":"woods","Destination":"bigmap","Event":false},{"Source":"woods","Destination":"arena_airpit","Event":false}]}""",
        ["locations/arena_airpit/base.json"] = """{"_Id":"arena_airpit","Name":"Airpit","Insurance":false}""",
        ["locations/arena_airpit/looseLoot.json"] = """{"spawnpointCount":{"mean":0,"std":0},"spawnpoints":[]}""",

        // One value of an old map moves; the boss chance is volatile and moves too (ADR-0028).
        ["locations/woods/base.json"] = """{"_Id":"woods","Name":"Woods","Insurance":false,"BossLocationSpawn":[{"BossName":"bossBully","BossChance":0}]}""",
        ["locations/woods/looseLoot.json"] = """{"spawnpointCount":{"mean":10,"std":2},"spawnpoints":[{"locationId":"woods_1","probability":0.1,"template":{"Id":"woods_1","Items":[{"_id":"i1","_tpl":"bbbbbbbbbbbbbbbbbbbbbbbb"}]}},{"locationId":"woods_2","probability":0.5,"template":{"Id":"woods_2","Items":[{"_id":"i2","_tpl":"dddddddddddddddddddddddd"}]}}]}""",

        ["bots/core.json"] = """{"SAVAGE_KILL_DIST":150}""",
        ["bots/types/assault.json"] = """{"chances":{"weaponMods":{"mod_scope":80}},"inventory":{"items":{"Backpack":{"bbbbbbbbbbbbbbbbbbbbbbbb":1,"dddddddddddddddddddddddd":1}}}}""",
        ["bots/types/newbot.json"] = """{"chances":{"weaponMods":{"mod_scope":10}}}""",

        ["hideout/areas.json"] = """[{"_id":"e1e1e1e1e1e1e1e1e1e1e1e1","type":1,"enabled":false,"requirements":[]}]""",
        ["hideout/production.json"] = """{"recipes":[{"_id":"e2e2e2e2e2e2e2e2e2e2e2e2","areaType":10,"productionTime":100,"endProduct":"bbbbbbbbbbbbbbbbbbbbbbbb","requirements":[{"templateId":"bbbbbbbbbbbbbbbbbbbbbbbb","count":3}]},{"_id":"e3e3e3e3e3e3e3e3e3e3e3e3","areaType":7,"productionTime":60,"endProduct":"dddddddddddddddddddddddd","requirements":[{"templateId":"bbbbbbbbbbbbbbbbbbbbbbbb","count":1}]}],"scavRecipes":[],"cultistRecipes":[]}""",

        ["templates/prices.json"] = """{"bbbbbbbbbbbbbbbbbbbbbbbb":1200,"dddddddddddddddddddddddd":4000}""",
        ["templates/handbook.json"] = """{"Categories":[{"Id":"c1c1c1c1c1c1c1c1c1c1c1c1","ParentId":null,"Icon":"/icon.png","Color":"","Order":"100"},{"Id":"c2c2c2c2c2c2c2c2c2c2c2c2","ParentId":"c1c1c1c1c1c1c1c1c1c1c1c1","Icon":"/new.png","Color":"","Order":"200"}],"Items":[{"Id":"bbbbbbbbbbbbbbbbbbbbbbbb","ParentId":"c1c1c1c1c1c1c1c1c1c1c1c1","Price":1200},{"Id":"dddddddddddddddddddddddd","ParentId":"c2c2c2c2c2c2c2c2c2c2c2c2","Price":4000}]}""",
    };

    /// <summary>The run tree laid over the baseline tree: a file the run does not state is unchanged.</summary>
    public static Dictionary<string, string> RunOverBaseline()
    {
        var tree = Baseline();

        foreach (var (path, content) in Run())
        {
            tree[path] = content;
        }

        return tree;
    }

    public static void Write(string root, Dictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private static string Items(bool addedItem)
    {
        var added = addedItem
            ? ",\"" + AddedItemId + "\":{\"_id\":\"" + AddedItemId + "\",\"_name\":\"Item D\",\"_parent\":\"" + NodeId + "\",\"_type\":\"Item\",\"_props\":{\"Weight\":3}}"
            : string.Empty;

        return "{\"" + NodeId + "\":{\"_id\":\"" + NodeId + "\",\"_name\":\"Node A\",\"_parent\":\"\",\"_type\":\"Node\",\"_props\":{}},"
            + "\"" + ItemId + "\":{\"_id\":\"" + ItemId + "\",\"_name\":\"Item B\",\"_parent\":\"" + NodeId + "\",\"_type\":\"Item\",\"_props\":{\"Weight\":1.5}}"
            + added + "}";
    }
}
