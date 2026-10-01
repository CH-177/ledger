using Ledger.Core.Models;
using Ledger.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Core.Tests;

public class CategoryServiceTests
{
    private static CategoryService Service(TestDatabase t) => new(t.Factory);

    private static async Task<Category> ByNameAsync(TestDatabase t, string name)
    {
        await using var db = t.Context();
        return await db.Categories.AsNoTracking().SingleAsync(c => c.Name == name);
    }

    [Fact]
    public async Task Tree_groups_defaults_by_type_with_children_in_order()
    {
        await using var t = await TestDatabase.CreateAsync();
        var tree = await Service(t).GetTreeAsync();

        var types = tree.Select(n => n.Category.CategoryType).ToList();
        Assert.Equal(types.Order(), types); // each type's groups are contiguous
        Assert.All(tree.SelectMany(n => n.Children.Select(c => (n.Category, c))),
            pair => Assert.Equal(pair.Item1.CategoryType, pair.c.CategoryType));
        var food = tree.Single(n => n.Category.Name == "Food");
        Assert.Equal(["Groceries", "Restaurants", "Fast Food"], food.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task Subcategory_inherits_its_parents_type_and_goes_last()
    {
        await using var t = await TestDatabase.CreateAsync();
        var food = await ByNameAsync(t, "Food");

        // The type argument is ignored when there is a parent.
        var coffee = await Service(t).CreateAsync("Coffee", food.Id, CategoryType.Income);

        Assert.Equal(CategoryType.Expense, coffee.CategoryType);
        var node = (await Service(t).GetTreeAsync()).Single(n => n.Category.Id == food.Id);
        Assert.Equal("Coffee", node.Children[^1].Name);
    }

    [Fact]
    public async Task Top_level_category_uses_the_given_type()
    {
        await using var t = await TestDatabase.CreateAsync();
        var pets = await Service(t).CreateAsync("  Pets  ", null, CategoryType.Expense);

        Assert.Equal("Pets", pets.Name);
        Assert.Null(pets.ParentCategoryId);
        Assert.Contains(await Service(t).GetTreeAsync(), n => n.Category.Name == "Pets");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_names_are_rejected(string name)
    {
        await using var t = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).CreateAsync(name, null, CategoryType.Expense));
    }

    [Fact]
    public async Task Overlong_names_are_rejected()
    {
        await using var t = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            Service(t).CreateAsync(new string('x', CategoryService.MaxNameLength + 1), null, CategoryType.Expense));
    }

    [Fact]
    public async Task Sibling_names_must_be_unique_ignoring_case()
    {
        await using var t = await TestDatabase.CreateAsync();
        var food = await ByNameAsync(t, "Food");

        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).CreateAsync("groceries", food.Id, CategoryType.Expense));
        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).CreateAsync("FOOD", null, CategoryType.Expense));
    }

    [Fact]
    public async Task Same_name_is_allowed_in_different_groups()
    {
        await using var t = await TestDatabase.CreateAsync();
        // "Other" already exists under Income.
        var personal = await ByNameAsync(t, "Personal");
        var other = await Service(t).CreateAsync("Other", personal.Id, CategoryType.Expense);
        Assert.Equal(personal.Id, other.ParentCategoryId);
    }

    [Fact]
    public async Task Top_level_names_only_clash_within_the_same_type()
    {
        await using var t = await TestDatabase.CreateAsync();
        // "Savings" exists as a Savings group; an Income group may share the name.
        var created = await Service(t).CreateAsync("Savings", null, CategoryType.Income);
        Assert.Equal(CategoryType.Income, created.CategoryType);
    }

    [Fact]
    public async Task Categories_are_at_most_two_levels_deep()
    {
        await using var t = await TestDatabase.CreateAsync();
        var groceries = await ByNameAsync(t, "Groceries");
        await Assert.ThrowsAsync<LedgerValidationException>(() =>
            Service(t).CreateAsync("Produce", groceries.Id, CategoryType.Expense));
    }

    [Fact]
    public async Task Rename_trims_and_checks_siblings_but_allows_changing_case_of_itself()
    {
        await using var t = await TestDatabase.CreateAsync();
        var groceries = await ByNameAsync(t, "Groceries");

        await Service(t).RenameAsync(groceries.Id, "GROCERIES ");
        Assert.Equal("GROCERIES", (await ByNameAsync(t, "GROCERIES")).Name);

        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).RenameAsync(groceries.Id, "restaurants"));
    }

    [Fact]
    public async Task Move_between_groups_of_the_same_type()
    {
        await using var t = await TestDatabase.CreateAsync();
        var subscriptions = await ByNameAsync(t, "Subscriptions");
        var entertainment = await ByNameAsync(t, "Entertainment");

        await Service(t).MoveAsync(subscriptions.Id, entertainment.Id);

        Assert.Equal(entertainment.Id, (await ByNameAsync(t, "Subscriptions")).ParentCategoryId);
    }

    [Fact]
    public async Task Move_across_types_is_rejected()
    {
        await using var t = await TestDatabase.CreateAsync();
        var groceries = await ByNameAsync(t, "Groceries");
        var income = await ByNameAsync(t, "Income");
        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).MoveAsync(groceries.Id, income.Id));
    }

    [Fact]
    public async Task A_group_with_children_cannot_become_a_subcategory()
    {
        await using var t = await TestDatabase.CreateAsync();
        var food = await ByNameAsync(t, "Food");
        var housing = await ByNameAsync(t, "Housing");
        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).MoveAsync(food.Id, housing.Id));
    }

    [Fact]
    public async Task Moving_into_itself_or_under_a_subcategory_is_rejected()
    {
        await using var t = await TestDatabase.CreateAsync();
        var entertainment = await ByNameAsync(t, "Entertainment");
        var gas = await ByNameAsync(t, "Gas");
        var parking = await ByNameAsync(t, "Parking");

        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).MoveAsync(entertainment.Id, entertainment.Id));
        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).MoveAsync(parking.Id, gas.Id));
    }

    [Fact]
    public async Task Moving_to_top_level_makes_a_new_group()
    {
        await using var t = await TestDatabase.CreateAsync();
        var gas = await ByNameAsync(t, "Gas");
        await Service(t).MoveAsync(gas.Id, null);

        Assert.Contains(await Service(t).GetTreeAsync(), n => n.Category.Name == "Gas");
    }

    [Fact]
    public async Task Move_rejects_a_name_clash_in_the_destination()
    {
        await using var t = await TestDatabase.CreateAsync();
        var personal = await ByNameAsync(t, "Personal");
        var otherExpense = await Service(t).CreateAsync("Paycheck", personal.Id, CategoryType.Expense);
        var housing = await ByNameAsync(t, "Housing");
        await Service(t).CreateAsync("Paycheck", housing.Id, CategoryType.Expense);

        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).MoveAsync(otherExpense.Id, housing.Id));
    }

    [Fact]
    public async Task Archiving_a_group_archives_its_children_and_restoring_brings_them_back()
    {
        await using var t = await TestDatabase.CreateAsync();
        var food = await ByNameAsync(t, "Food");
        var service = Service(t);

        await service.SetArchivedAsync(food.Id, true);
        var active = await service.GetTreeAsync();
        Assert.DoesNotContain(active, n => n.Category.Name == "Food");
        Assert.DoesNotContain(active.SelectMany(n => n.Children), c => c.Name == "Groceries");

        await service.SetArchivedAsync(food.Id, false);
        var restored = (await service.GetTreeAsync()).Single(n => n.Category.Name == "Food");
        Assert.Equal(3, restored.Children.Count);
    }

    [Fact]
    public async Task Restoring_a_subcategory_restores_its_group()
    {
        await using var t = await TestDatabase.CreateAsync();
        var food = await ByNameAsync(t, "Food");
        var groceries = await ByNameAsync(t, "Groceries");
        var service = Service(t);

        await service.SetArchivedAsync(food.Id, true);
        await service.SetArchivedAsync(groceries.Id, false);

        var node = (await service.GetTreeAsync()).Single(n => n.Category.Name == "Food");
        Assert.Equal(["Groceries"], node.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task Cannot_add_to_an_archived_group()
    {
        await using var t = await TestDatabase.CreateAsync();
        var food = await ByNameAsync(t, "Food");
        await Service(t).SetArchivedAsync(food.Id, true);

        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).CreateAsync("Coffee", food.Id, CategoryType.Expense));
    }

    [Fact]
    public async Task Archived_names_still_count_as_taken()
    {
        await using var t = await TestDatabase.CreateAsync();
        var groceries = await ByNameAsync(t, "Groceries");
        await Service(t).SetArchivedAsync(groceries.Id, true);

        // Prevents two "Groceries" appearing if the archived one is restored.
        var food = await ByNameAsync(t, "Food");
        await Assert.ThrowsAsync<LedgerValidationException>(() => Service(t).CreateAsync("Groceries", food.Id, CategoryType.Expense));
    }

    [Fact]
    public async Task Archiving_never_touches_transactions_that_use_the_category()
    {
        await using var t = await TestDatabase.CreateAsync();
        var account = await t.AddAccountAsync("Checking", AccountType.Checking);
        var groceries = await ByNameAsync(t, "Groceries");
        await t.AddTransactionAsync(new Transaction
        {
            AccountId = account.Id, CategoryId = groceries.Id, AmountCents = 4200,
            TransactionType = TransactionType.Expense, Date = new(2026, 9, 1),
        });

        await Service(t).SetArchivedAsync(groceries.Id, true);

        await using var db = t.Context();
        var tx = await db.Transactions.SingleAsync();
        Assert.Equal(groceries.Id, tx.CategoryId);
    }
}
