using System.Security.Claims;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MatVakt;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=matvakt.db"));
builder.Services.AddHttpClient();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "matvakt";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    if (!db.Recipes.Any()) { db.Recipes.AddRange(RecipeLogic.Seed()); db.SaveChanges(); }
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var googleClientId = app.Configuration["Google:ClientId"];
var demoEnabled = string.IsNullOrWhiteSpace(googleClientId);

async Task SignIn(HttpContext ctx, User u)
{
    var id = new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
        new Claim(ClaimTypes.Name, u.Name),
        new Claim(ClaimTypes.Email, u.Email)
    }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(id));
}

int Uid(ClaimsPrincipal p) => int.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

var auth = app.MapGroup("/api/auth");
auth.MapGet("/config", () => new { googleClientId = demoEnabled ? null : googleClientId, demo = demoEnabled });
auth.MapGet("/me", (ClaimsPrincipal p) => p.Identity?.IsAuthenticated == true
    ? Results.Ok(new { name = p.Identity.Name, email = p.FindFirstValue(ClaimTypes.Email) })
    : Results.Unauthorized());
auth.MapPost("/google", async (GoogleLogin body, AppDb db, HttpContext ctx) =>
{
    if (demoEnabled) return Results.BadRequest("Google-innlogging er ikke konfigurert");
    GoogleJsonWebSignature.Payload payload;
    try
    {
        payload = await GoogleJsonWebSignature.ValidateAsync(body.Credential,
            new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { googleClientId! } });
    }
    catch (InvalidJwtException) { return Results.Unauthorized(); }
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == payload.Email);
    if (user is null) { user = new User { Email = payload.Email, Name = payload.Name ?? payload.Email }; db.Users.Add(user); await db.SaveChangesAsync(); }
    await SignIn(ctx, user);
    return Results.Ok();
});
auth.MapPost("/demo", async (AppDb db, HttpContext ctx) =>
{
    if (!demoEnabled) return Results.NotFound();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == "demo@matvakt.local");
    if (user is null)
    {
        user = new User { Email = "demo@matvakt.local", Name = "Demo-bruker" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        db.FoodItems.AddRange(
            new FoodItem { UserId = user.Id, Name = "Melk", Quantity = 2, Unit = "l", ExpiryDate = today.AddDays(2) },
            new FoodItem { UserId = user.Id, Name = "Melk", Quantity = 2, Unit = "l", ExpiryDate = today.AddDays(9) },
            new FoodItem { UserId = user.Id, Name = "Egg", Quantity = 6, Unit = "stk", ExpiryDate = today.AddDays(5) },
            new FoodItem { UserId = user.Id, Name = "Kjøttdeig", Quantity = 500, Unit = "g", ExpiryDate = today.AddDays(1) },
            new FoodItem { UserId = user.Id, Name = "Gulrot", Quantity = 5, Unit = "stk", ExpiryDate = today.AddDays(8) },
            new FoodItem { UserId = user.Id, Name = "Løk", Quantity = 3, Unit = "stk", ExpiryDate = today.AddDays(20) },
            new FoodItem { UserId = user.Id, Name = "Poteter", Quantity = 1, Unit = "kg", ExpiryDate = today.AddDays(14) });
        await db.SaveChangesAsync();
    }
    await SignIn(ctx, user);
    return Results.Ok();
});
auth.MapPost("/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); });

var api = app.MapGroup("/api").RequireAuthorization();

// ---- Matvarer ----
api.MapGet("/food", async (ClaimsPrincipal p, AppDb db, bool? includeUsed) =>
{
    var uid = Uid(p);
    var q = db.FoodItems.Where(f => f.UserId == uid);
    if (includeUsed != true) q = q.Where(f => !f.UsedUp);
    return (await q.ToListAsync()).OrderBy(f => f.UsedUp).ThenBy(f => f.ExpiryDate ?? DateOnly.MaxValue).ThenBy(f => f.Name);
});
api.MapPost("/food", async (FoodItem body, ClaimsPrincipal p, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest("Navn mangler");
    body.Id = 0; body.UserId = Uid(p); body.UsedUp = false; body.Name = body.Name.Trim();
    if (body.Quantity <= 0) body.Quantity = 1;
    db.FoodItems.Add(body);
    await db.SaveChangesAsync();
    return Results.Ok(body);
});
api.MapPut("/food/{id:int}", async (int id, FoodItem body, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var f = await db.FoodItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (f is null) return Results.NotFound();
    f.Name = body.Name.Trim(); f.Quantity = body.Quantity; f.Unit = body.Unit; f.ExpiryDate = body.ExpiryDate; f.UsedUp = body.UsedUp;
    if (f.Quantity <= 0) f.UsedUp = true;
    await db.SaveChangesAsync();
    return Results.Ok(f);
});
api.MapPost("/food/{id:int}/use", async (int id, UseBody body, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var f = await db.FoodItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (f is null) return Results.NotFound();
    f.Quantity = Math.Max(0, f.Quantity - (body.Amount ?? f.Quantity));
    if (f.Quantity <= 0) f.UsedUp = true;
    await db.SaveChangesAsync();
    return Results.Ok(f);
});
api.MapPost("/food/{id:int}/restore", async (int id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var f = await db.FoodItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (f is null) return Results.NotFound();
    f.UsedUp = false; if (f.Quantity <= 0) f.Quantity = 1;
    await db.SaveChangesAsync();
    return Results.Ok(f);
});
api.MapDelete("/food/{id:int}", async (int id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var f = await db.FoodItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (f is null) return Results.NotFound();
    db.FoodItems.Remove(f);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// ---- Oppskrifter ----
async Task<List<RecipeView>> BuildViews(int uid, AppDb db, IEnumerable<Recipe> recipes)
{
    var items = await db.FoodItems.Where(f => f.UserId == uid && !f.UsedUp).ToListAsync();
    var today = DateOnly.FromDateTime(DateTime.Today);
    var views = new List<RecipeView>();
    foreach (var r in recipes)
    {
        var ings = new List<IngredientView>();
        double score = 0;
        foreach (var i in r.Ingredients)
        {
            var staple = RecipeLogic.IsStaple(i.Name);
            var have = items.Where(f => RecipeLogic.Matches(f.Name, i.Name)).OrderBy(f => f.ExpiryDate ?? DateOnly.MaxValue).ToList();
            var first = have.FirstOrDefault();
            int? days = first?.ExpiryDate is { } d ? d.DayNumber - today.DayNumber : null;
            ings.Add(new IngredientView(i.Name, i.Amount, staple, first is not null, days));
            if (!staple && first is not null)
                score += 1 + (days is null ? 0 : days <= 0 ? 4 : days <= 2 ? 3 : days <= 5 ? 2 : days <= 10 ? 1 : 0);
        }
        var main = ings.Where(i => !i.Staple).ToList();
        var missing = main.Count(i => !i.Have);
        var coverage = main.Count == 0 ? 0 : (double)(main.Count - missing) / main.Count;
        views.Add(new RecipeView(r.Id, r.Title, r.Source, r.Url, r.ImageUrl, r.Servings, r.Instructions, ings, missing,
            Math.Round(score * (0.5 + coverage), 2),
            ings.Count(i => i.Have && i.DaysToExpiry is <= 3)));
    }
    return views;
}

api.MapGet("/recipes", async (ClaimsPrincipal p, AppDb db, string? q) =>
{
    var rs = db.Recipes.Include(r => r.Ingredients).AsQueryable();
    if (!string.IsNullOrWhiteSpace(q)) rs = rs.Where(r => r.Title.Contains(q));
    return (await BuildViews(Uid(p), db, await rs.ToListAsync())).OrderBy(v => v.Missing).ThenBy(v => v.Title);
});
api.MapGet("/recipes/{id:int}", async (int id, ClaimsPrincipal p, AppDb db) =>
{
    var r = await db.Recipes.Include(x => x.Ingredients).FirstOrDefaultAsync(x => x.Id == id);
    return r is null ? Results.NotFound() : Results.Ok((await BuildViews(Uid(p), db, new[] { r }))[0]);
});
api.MapGet("/suggestions", async (ClaimsPrincipal p, AppDb db) =>
{
    var views = await BuildViews(Uid(p), db, await db.Recipes.Include(r => r.Ingredients).ToListAsync());
    return views.Where(v => v.Score > 0).OrderByDescending(v => v.Score).ThenBy(v => v.Missing).Take(10);
});
api.MapPost("/recipes/import", async (ImportBody body, IHttpClientFactory hf, AppDb db) =>
{
    if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
        return Results.BadRequest("Ugyldig URL");
    // Kun kjente oppskriftssider, for å hindre at serveren brukes til vilkårlige forespørsler (SSRF)
    string[] allowed = { "matprat.no", "gladkokken.no", "trinesmatblogg.no", "oda.com", "idagranjansen.no" };
    if (!allowed.Any(a => uri.Host == a || uri.Host.EndsWith("." + a)))
        return Results.BadRequest("Støttede sider: " + string.Join(", ", allowed));
    try
    {
        var recipe = await RecipeLogic.ImportAsync(hf.CreateClient(), uri.ToString());
        if (recipe is null || recipe.Ingredients.Count == 0) return Results.BadRequest("Fant ingen oppskrift på siden");
        db.Recipes.Add(recipe);
        await db.SaveChangesAsync();
        return Results.Ok(new { recipe.Id, recipe.Title });
    }
    catch (HttpRequestException) { return Results.BadRequest("Kunne ikke hente siden"); }
});

// ---- Handleliste ----
api.MapGet("/shopping", async (ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    return await db.ShoppingItems.Where(s => s.UserId == uid).OrderBy(s => s.Done).ThenBy(s => s.Name).ToListAsync();
});
api.MapPost("/shopping", async (ShoppingItem body, ClaimsPrincipal p, AppDb db) =>
{
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest("Navn mangler");
    body.Id = 0; body.UserId = Uid(p); body.Done = false;
    db.ShoppingItems.Add(body);
    await db.SaveChangesAsync();
    return Results.Ok(body);
});
api.MapPost("/shopping/from-recipe/{id:int}", async (int id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var r = await db.Recipes.Include(x => x.Ingredients).FirstOrDefaultAsync(x => x.Id == id);
    if (r is null) return Results.NotFound();
    var view = (await BuildViews(uid, db, new[] { r }))[0];
    var open = await db.ShoppingItems.Where(s => s.UserId == uid && !s.Done).ToListAsync();
    var added = 0;
    foreach (var i in view.Ingredients.Where(i => !i.Staple && !i.Have))
    {
        if (open.Any(s => RecipeLogic.Matches(s.Name, i.Name))) continue;
        db.ShoppingItems.Add(new ShoppingItem { UserId = uid, Name = i.Name, Amount = i.Amount, RecipeTitle = r.Title });
        added++;
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { added });
});
api.MapPut("/shopping/{id:int}", async (int id, ShoppingItem body, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var s = await db.ShoppingItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (s is null) return Results.NotFound();
    s.Done = body.Done;
    await db.SaveChangesAsync();
    return Results.Ok(s);
});
api.MapDelete("/shopping/{id:int}", async (int id, ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var s = await db.ShoppingItems.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid);
    if (s is null) return Results.NotFound();
    db.ShoppingItems.Remove(s);
    await db.SaveChangesAsync();
    return Results.Ok();
});
api.MapDelete("/shopping", async (ClaimsPrincipal p, AppDb db, bool? doneOnly) =>
{
    var uid = Uid(p);
    var q = db.ShoppingItems.Where(s => s.UserId == uid);
    if (doneOnly == true) q = q.Where(s => s.Done);
    db.ShoppingItems.RemoveRange(q);
    await db.SaveChangesAsync();
    return Results.Ok();
});
// Flytt avkryssede varer til matvarelageret
api.MapPost("/shopping/to-pantry", async (ClaimsPrincipal p, AppDb db) =>
{
    var uid = Uid(p);
    var done = await db.ShoppingItems.Where(s => s.UserId == uid && s.Done).ToListAsync();
    foreach (var s in done) db.FoodItems.Add(new FoodItem { UserId = uid, Name = s.Name, Quantity = 1, Unit = "stk" });
    db.ShoppingItems.RemoveRange(done);
    await db.SaveChangesAsync();
    return Results.Ok(new { moved = done.Count });
});

app.Run();

record GoogleLogin(string Credential);
record UseBody(double? Amount);
record ImportBody(string Url);
record IngredientView(string Name, string Amount, bool Staple, bool Have, int? DaysToExpiry);
record RecipeView(int Id, string Title, string Source, string? Url, string? ImageUrl, int Servings, string Instructions,
    List<IngredientView> Ingredients, int Missing, double Score, int ExpiringUsed);
