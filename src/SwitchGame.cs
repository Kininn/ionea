// Switch games: a game is paused in its Home menu and another one is picked on Home. Asks first:
// Close <open game> and start <picked game>, or Cancel (the open game stays paused, Home stays up).
using System;
using static Ionea.Loc;

namespace Ionea;

public sealed class SwitchGameCard : MenuCard
{
    public SwitchGameCard(MainWindow w, Game open, Game picked, Action go) : base(w, T("menu.switch_games"), 860)
    {
        Note(T("menu.unsaved_progress_in_is_lost", open.Name), 28, new System.Windows.Thickness(4, 0, 0, 24));
        WithIcon(AddRow(T("menu.close_and_start", open.Name, picked.Name), activate: () => { Close(false); go(); }), "play");
        AddRow(T("common.cancel"), activate: () => Close());
        FocusRow(T("common.cancel"));   // a button meant for the game must not close it: starting the other one is a choice
        Log.Write($"switch games: asked (\"{open.Name}\" open, \"{picked.Name}\" picked)");
    }
}
