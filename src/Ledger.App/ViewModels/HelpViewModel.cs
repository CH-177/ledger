using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ledger.App.Help;
using Ledger.App.Services;

namespace Ledger.App.ViewModels;

/// <summary>A topic in the Help list.</summary>
public sealed record HelpTopicItem(HelpTopic Topic)
{
    public string Title => Topic.Title;
    public string Summary => Topic.Summary;
    public string Glyph => Topic.Glyph;
}

/// <summary>A heading in the Help list with its topics (the list is grouped by these).</summary>
public sealed class HelpGroup(string name, IEnumerable<HelpTopicItem> topics) : List<HelpTopicItem>(topics)
{
    public string Name { get; } = name;
}

/// <summary>One paragraph, bullet, heading or warning in the open topic.</summary>
public sealed record HelpBlockItem(HelpBlock Block)
{
    public string Text => Block.Text;
    public bool IsParagraph => Block.Kind == HelpBlockKind.Paragraph;
    public bool IsBullet => Block.Kind == HelpBlockKind.Bullet;
    public bool IsHeading => Block.Kind == HelpBlockKind.Heading;
    public bool IsImportant => Block.Kind == HelpBlockKind.Important;
}

/// <summary>A "See also" link to another topic.</summary>
public sealed record HelpLinkItem(string Id, string Title);

/// <summary>The Help page: searchable topics, the open topic, and the welcome tour.</summary>
public sealed partial class HelpViewModel(NavigationService navigation, WelcomeTour tour) : ObservableObject
{
    public ObservableCollection<HelpGroup> Groups { get; } = [];
    public ObservableCollection<HelpBlockItem> Blocks { get; } = [];
    public ObservableCollection<HelpLinkItem> Related { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Summary), nameof(Glyph), nameof(HasPage), nameof(OpenPageText), nameof(HasTopic))]
    public partial HelpTopicItem? Selected { get; set; }

    [ObservableProperty]
    public partial bool HasNoResults { get; set; }

    [ObservableProperty]
    public partial bool HasRelated { get; set; }

    public bool HasTopic => Selected is not null;
    public string Title => Selected?.Title ?? "";
    public string Summary => Selected?.Summary ?? "";
    public string Glyph => Selected?.Glyph ?? "\uE897";
    public bool HasPage => Selected?.Topic.Page is { } page && page != navigation.Current;

    public string OpenPageText => Selected?.Topic.Page switch
    {
        AppPage.Plan => "Open What if?",
        AppPage.Import => "Open Import",
        { } page => $"Open {page}",
        null => "",
    };

    /// <summary>Shows all topics, opening <paramref name="topicId"/> (or the first) on the right.</summary>
    public void Load(string? topicId)
    {
        Query = "";
        Filter();
        Open(topicId ?? "welcome");
    }

    partial void OnQueryChanged(string value) => Filter();

    partial void OnSelectedChanged(HelpTopicItem? value)
    {
        Blocks.Clear();
        Related.Clear();
        if (value is null)
        {
            HasRelated = false;
            return;
        }
        foreach (var block in value.Topic.Blocks)
        {
            Blocks.Add(new HelpBlockItem(block));
        }
        foreach (var id in value.Topic.Related ?? [])
        {
            if (HelpContent.Find(id) is { } topic)
            {
                Related.Add(new HelpLinkItem(topic.Id, topic.Title));
            }
        }
        HasRelated = Related.Count > 0;
    }

    /// <summary>Opens a topic by id, clearing a search that would hide it.</summary>
    public void Open(string id)
    {
        var item = Groups.SelectMany(g => g).FirstOrDefault(t => t.Topic.Id == id);
        if (item is null && HelpContent.Find(id) is not null)
        {
            Query = "";
            item = Groups.SelectMany(g => g).FirstOrDefault(t => t.Topic.Id == id);
        }
        Selected = item ?? Groups.SelectMany(g => g).FirstOrDefault();
    }

    private void Filter()
    {
        var current = Selected?.Topic.Id;
        Groups.Clear();

        if (string.IsNullOrWhiteSpace(Query))
        {
            foreach (var group in HelpContent.Topics.GroupBy(t => t.Group))
            {
                Groups.Add(new HelpGroup(group.Key, group.Select(t => new HelpTopicItem(t))));
            }
            var all = Groups.SelectMany(g => g).ToList();
            Selected = all.FirstOrDefault(t => t.Topic.Id == current) ?? all.FirstOrDefault();
        }
        else
        {
            // A search lists the best matches first, and opens the best one.
            var matches = HelpContent.Topics.Select(t => (Topic: t, Score: t.Score(Query))).Where(m => m.Score > 0)
                .OrderByDescending(m => m.Score).Select(m => new HelpTopicItem(m.Topic)).ToList();
            if (matches.Count > 0)
            {
                Groups.Add(new HelpGroup(matches.Count == 1 ? "1 topic" : $"{matches.Count} topics", matches));
            }
            Selected = matches.FirstOrDefault();
        }
        HasNoResults = Groups.Count == 0;
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (Selected?.Topic.Page is { } page)
        {
            navigation.Navigate(page);
        }
    }

    [RelayCommand]
    private void StartTour() => tour.Start();
}
