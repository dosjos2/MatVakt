using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MatVakt;

public static class RecipeLogic
{
    // Basisvarer og krydder som ikke forventes registrert i appen
    static readonly string[] Staples =
    {
        "salt", "pepper", "olje", "olivenolje", "rapsolje", "smør", "margarin", "mel", "sukker", "vann",
        "krydder", "paprikapulver", "oregano", "timian", "basilikum", "kanel", "buljong", "kraft", "eddik",
        "soyasaus", "soya", "sennep", "laurbær", "karri", "spisskummen", "kardemomme", "gurkemeie",
        "chilipulver", "chiliflak", "hvitløkspulver", "løkpulver", "rosmarin", "persille", "vaniljesukker",
        "bakepulver", "natron", "maisenna", "potetmel", "honning", "ketchup", "tomatpuré", "worcestersaus"
    };

    public static bool IsStaple(string name)
    {
        var n = Norm(name);
        return Staples.Any(s => n == s || n.EndsWith(s) || n.Split(' ').Contains(s));
    }

    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^\p{L}\s]", " ").Trim();

    public static bool Matches(string itemName, string ingredient)
    {
        var a = Norm(itemName);
        var b = Norm(ingredient);
        if (a.Length < 3 || b.Length < 3) return a == b;
        return a.Contains(b) || b.Contains(a);
    }

    public static string? FindIngredientMatch(IEnumerable<string> have, string ingredient) =>
        have.FirstOrDefault(h => Matches(h, ingredient));

    static readonly Regex Qty = new(
        @"^\s*[\d\s/.,½¼¾\-–]+\s*(stk|dl|cl|l|liter|ml|g|gram|kg|ss|ts|klyper?|neve|pk|pakke|pakker|boks|bokser|skiver?|never?|bunt|pose|båt|båter|ca\.?|cm)?\.?\s+(?:av\s+)?",
        RegexOptions.IgnoreCase);

    public static (string Name, string Amount) ParseIngredient(string raw)
    {
        raw = WebUtility.HtmlDecode(raw).Trim();
        var m = Qty.Match(raw);
        var amount = m.Success ? m.Value.Trim() : "";
        var name = m.Success ? raw[m.Length..] : raw;
        name = Regex.Replace(name, @"\(.*?\)", "").Split(',')[0].Trim();
        return (name, amount);
    }

    public static async Task<Recipe?> ImportAsync(HttpClient http, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (MatVakt)");
        var html = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
        foreach (Match m in Regex.Matches(html, @"<script[^>]*application/ld\+json[^>]*>(.*?)</script>", RegexOptions.Singleline))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                var node = FindRecipe(doc.RootElement);
                if (node is null) continue;
                return ToRecipe(node.Value, url);
            }
            catch (JsonException) { }
        }
        return null;
    }

    static JsonElement? FindRecipe(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) { var r = FindRecipe(x); if (r is not null) return r; }
        else if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("@type", out var t) && t.ToString().Contains("Recipe")) return e;
            if (e.TryGetProperty("@graph", out var g)) return FindRecipe(g);
        }
        return null;
    }

    static Recipe ToRecipe(JsonElement e, string url)
    {
        var r = new Recipe
        {
            Title = WebUtility.HtmlDecode(e.GetProperty("name").GetString() ?? "Oppskrift"),
            Url = url,
            Source = new Uri(url).Host.Replace("www.", ""),
        };
        if (e.TryGetProperty("image", out var img))
            r.ImageUrl = img.ValueKind switch
            {
                JsonValueKind.String => img.GetString(),
                JsonValueKind.Array when img.GetArrayLength() > 0 => img[0].ValueKind == JsonValueKind.String ? img[0].GetString() : img[0].GetProperty("url").GetString(),
                JsonValueKind.Object => img.GetProperty("url").GetString(),
                _ => null
            };
        if (e.TryGetProperty("recipeYield", out var y))
        {
            var d = Regex.Match(y.ToString(), @"\d+");
            if (d.Success) r.Servings = int.Parse(d.Value);
        }
        if (e.TryGetProperty("recipeIngredient", out var ing))
            foreach (var i in ing.EnumerateArray())
            {
                var (n, a) = ParseIngredient(i.GetString() ?? "");
                if (n.Length > 0) r.Ingredients.Add(new RecipeIngredient { Name = n, Amount = a });
            }
        var steps = new List<string>();
        if (e.TryGetProperty("recipeInstructions", out var ins)) CollectSteps(ins, steps);
        r.Instructions = string.Join("\n", steps);
        return r;
    }

    static void CollectSteps(JsonElement e, List<string> steps)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: steps.Add(WebUtility.HtmlDecode(Regex.Replace(e.GetString()!, "<.*?>", " ").Trim())); break;
            case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) CollectSteps(x, steps); break;
            case JsonValueKind.Object:
                if (e.TryGetProperty("itemListElement", out var l)) CollectSteps(l, steps);
                else if (e.TryGetProperty("text", out var t)) steps.Add(WebUtility.HtmlDecode(t.GetString() ?? ""));
                break;
        }
    }

    public static List<Recipe> Seed()
    {
        Recipe R(string title, int s, string instr, params string[] ings) => new()
        {
            Title = title, Source = "Eksempel", Servings = s, Instructions = instr,
            Ingredients = ings.Select(i => { var p = i.Split('|'); return new RecipeIngredient { Name = p[0], Amount = p.Length > 1 ? p[1] : "" }; }).ToList()
        };
        return new()
        {
            R("Kjøttkaker i brun saus", 4, "Bland kjøttdeig med salt, pepper og melk.\nForm kaker og stek dem.\nLag saus av smør, mel og kraft.\nKok kakene i sausen. Server med poteter og gulrøtter.", "kjøttdeig|500 g", "melk|1 dl", "løk|1 stk", "poteter|800 g", "gulrot|4 stk", "salt", "pepper", "mel", "smør"),
            R("Pasta carbonara", 4, "Kok pasta.\nStek bacon sprøtt.\nVisp egg og parmesan.\nBland alt av varmen og server.", "spaghetti|400 g", "bacon|200 g", "egg|3 stk", "parmesan|50 g", "pepper"),
            R("Kyllinggryte med ris", 4, "Stek kyllingbitene.\nTilsett løk, paprika og fløte.\nKok i 15 min og server med ris.", "kyllingfilet|600 g", "løk|1 stk", "paprika|2 stk", "matfløte|2 dl", "ris|3 dl", "buljong", "salt", "pepper"),
            R("Pannekaker", 4, "Visp mel, melk og egg til en jevn røre.\nStek tynne pannekaker i smør.", "hvetemel|5 dl", "melk|1 l", "egg|4 stk", "smør", "salt"),
            R("Grønnsakssuppe", 4, "Kutt grønnsakene.\nKok i buljong i 20 min.\nSmak til med salt og pepper.", "gulrot|3 stk", "potet|3 stk", "løk|1 stk", "selleri|2 stk", "buljong", "salt", "pepper"),
            R("Omelett med ost og skinke", 2, "Visp egg og melk.\nStek i panne, legg på ost og skinke og brett sammen.", "egg|4 stk", "melk|1 dl", "ost|100 g", "skinke|4 skiver", "smør", "salt"),
            R("Tacogryte", 4, "Brun kjøttdeig med løk.\nTilsett tacokrydder, mais og hermetiske tomater.\nKok i 10 min. Server med nachos og rømme.", "kjøttdeig|500 g", "løk|1 stk", "mais|1 boks", "hermetiske tomater|1 boks", "tacokrydder|1 pose", "rømme|2 dl", "nachochips|1 pose"),
            R("Laks med ovnsbakte grønnsaker", 4, "Del grønnsakene og bak ved 200 grader i 20 min.\nLegg laksen på og bak 12 min til.", "laks|600 g", "brokkoli|1 stk", "gulrot|3 stk", "potet|600 g", "sitron|1 stk", "olivenolje", "salt", "pepper"),
            R("Fiskegrateng", 4, "Lag hvit saus av smør, mel og melk.\nBland med fisk og potetskiver.\nBak ved 200 grader i 35 min.", "torsk|500 g", "melk|5 dl", "poteter|600 g", "smør", "mel", "muskat", "salt"),
            R("Pizza på tortilla", 2, "Smør tortilla med tomatsaus.\nLegg på ost, skinke og paprika.\nBak i 8 min ved 225 grader.", "tortilla|4 stk", "tomatsaus|1 dl", "ost|150 g", "skinke|100 g", "paprika|1 stk"),
            R("Linsesuppe", 4, "Fres løk og gulrot.\nTilsett røde linser, tomater og kraft.\nKok i 20 min og server med yoghurt.", "røde linser|3 dl", "løk|1 stk", "gulrot|2 stk", "hermetiske tomater|1 boks", "yoghurt|1 dl", "kraft", "spisskummen"),
            R("Eggerøre med brød", 2, "Visp egg med melk.\nRør i smør til det er kremet.\nServer på brødskive med tomat.", "egg|6 stk", "melk|1 dl", "brød|4 skiver", "tomat|2 stk", "smør", "salt"),
        };
    }
}
