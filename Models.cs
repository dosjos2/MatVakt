using Microsoft.EntityFrameworkCore;

namespace MatVakt;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
}

public class FoodItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public double Quantity { get; set; } = 1;
    public string Unit { get; set; } = "stk";
    public DateOnly? ExpiryDate { get; set; }
    public bool UsedUp { get; set; }
}

public class Recipe
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string? Url { get; set; }
    public string? ImageUrl { get; set; }
    public int Servings { get; set; } = 4;
    public string Instructions { get; set; } = "";
    public List<RecipeIngredient> Ingredients { get; set; } = new();
}

public class RecipeIngredient
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public string Name { get; set; } = "";
    public string Amount { get; set; } = "";
}

public class ShoppingItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public string Amount { get; set; } = "";
    public string? RecipeTitle { get; set; }
    public bool Done { get; set; }
}

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<FoodItem> FoodItems => Set<FoodItem>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeIngredient> RecipeIngredients => Set<RecipeIngredient>();
    public DbSet<ShoppingItem> ShoppingItems => Set<ShoppingItem>();
}
