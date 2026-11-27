using HabilisTerminal.App.Hosting;

namespace HabilisTerminal.App.Widgets;

/// <summary>What the tab strip shows of a tab: a console (issue #119) or the results of a search (issue #415).</summary>
internal interface IStripTab
{
    string Title { get; }

    /// <summary>Shown after the title, dimmer: the folder, the clock, the count.</summary>
    string Detail { get; }

    string Description { get; }

    int ColorIndex { get; }

    ConsoleTabMark Mark { get; }

    /// <summary>Its texts change by themselves: the strip refreshes them every second.</summary>
    bool CommandRunning { get; }

    /// <summary>The close button's tooltip.</summary>
    string CloseTip { get; }
}
