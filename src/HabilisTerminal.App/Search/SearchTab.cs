using HabilisTerminal.App.Hosting;
using HabilisTerminal.App.Widgets;
using HabilisTerminal.Core.Search;

namespace HabilisTerminal.App.Search;

/// <summary>
/// The results of one Alt+F7 search as a tab beside the consoles (issue #415), as Visual Studio's Find Results: the
/// search runs on while the tab is in front or not, and the tab lives until it is closed.
/// </summary>
internal sealed class SearchTab : IStripTab, IDisposable
{
    public SearchTab(FindSession session)
    {
        Session = session;
        Tree = new FindTree(session.Roots);
        View = new SearchResultsView(this);
    }

    public FindSession Session { get; private set; }

    public FindTree Tree { get; private set; }

    public SearchResultsView View { get; }

    public FindOptions Options => Session.Options;

    /// <summary>Results taken from the session into the tree so far.</summary>
    public int Taken { get; set; }

    public string Title => string.IsNullOrEmpty(Options.Text) ? $"Find {Options.Masks}" : $"Find \"{Options.Text}\"";

    public string Detail => Session.IsDone ? Count() : $"{Count()}…";

    public string Description =>
        $"{(string.IsNullOrEmpty(Options.Text) ? "Files" : $"\"{Options.Text}\" in files")} matching {Options.Masks}, in {string.Join(", ", Session.Roots)}. {Status}";

    public int ColorIndex => 0;

    public ConsoleTabMark Mark => Session.IsDone ? ConsoleTabMark.None : ConsoleTabMark.Running;

    public bool CommandRunning => !Session.IsDone;

    public string CloseTip => "Close these results (Ctrl+Shift+W)";

    /// <summary>The status line under the toolbar.</summary>
    public string Status
    {
        get
        {
            var found = string.IsNullOrEmpty(Options.Text)
                ? $"{Tree.Files:N0} found"
                : $"Matching lines: {Tree.Lines:N0} · Matching files: {Tree.Files:N0}";
            var state = Session.IsDone
                ? Session.Stopped ? "Stopped." : "Done."
                : $"Searching {Session.Folder}";
            var notes = new List<string>();
            if (Session.Unreadable > 0)
            {
                notes.Add($"{Session.Unreadable:N0} folders could not be read");
            }

            if (Session.LinesCut)
            {
                notes.Add("not every line is listed");
            }

            if (Session.CacheHits > 0)
            {
                notes.Add($"{Session.CacheHits:N0} files answered from the last search");
            }

            return $"{found}. {state}{(notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : string.Empty)}";
        }
    }

    /// <summary>Modify find: a new search with new options in this tab.</summary>
    public void Replace(FindSession session)
    {
        Session.Dispose();
        Session = session;
        Tree = new FindTree(session.Roots);
        Taken = 0;
        View.Restart();
    }

    public void Dispose()
    {
        View.Stop();
        Session.Dispose();
    }

    private string Count() => string.IsNullOrEmpty(Options.Text) ? $"{Tree.Files:N0}" : $"{Tree.Lines:N0}";
}
