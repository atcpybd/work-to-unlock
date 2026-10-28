using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkToUnlock.Core.Domain;
using WorkToUnlock.Core.Ipc;

namespace WorkToUnlock.App.Views;

/// <summary>Shows what the agent learned about a chosen executable before it is added.</summary>
public sealed partial class AddApplicationDialog : ContentDialog
{
    public AddApplicationDialog(ExecutableInspection inspection, RuleType type)
    {
        InitializeComponent();
        Title = type == RuleType.Work ? "Add work application" : "Add restricted application";
        NameBox.Text = inspection.SuggestedName;
        PathText.Text = inspection.ExecutablePath;

        var identity = inspection.Identity;
        PublisherText.Text = identity.Publisher is { } publisher
            ? $"Signed by {publisher}{(identity.SignatureTrusted ? string.Empty : " (signature not trusted)")}"
            : "Not digitally signed: only this exact file will match.";
        ScopeText.Text = identity.Scope switch
        {
            MatchScope.InstallFolder => $"Matches programs in {identity.InstallRoot}" + (identity.Publisher is null ? " with known names." : $" signed by {identity.Publisher}."),
            MatchScope.PublisherAndFileName => "Matches this program even after updates move it, as long as the publisher signature matches.",
            _ => "Matches this exact file.",
        };
        RelatedText.Text = identity.RelatedExecutables.Count > 0
            ? $"Also covers: {string.Join(", ", identity.RelatedExecutables.Take(12))}{(identity.RelatedExecutables.Count > 12 ? ", …" : string.Empty)}"
            : string.Empty;
        RelatedText.Visibility = identity.RelatedExecutables.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        CountsBox.Visibility = type == RuleType.Work ? Visibility.Visible : Visibility.Collapsed;

        var problems = inspection.Problems.Select(p => p.Message).ToList();
        ProblemsBar.IsOpen = problems.Count > 0 || !inspection.Exists;
        ProblemsBar.Message = string.Join(Environment.NewLine, problems);
        IsPrimaryButtonEnabled = inspection.Exists && problems.Count == 0;
    }

    public string ApplicationName => NameBox.Text;

    public bool CountsTowardGoal => CountsBox.IsChecked != false;
}
