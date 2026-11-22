using System.Windows.Controls;

namespace Utility.App.Controls;

/// <summary>
/// A radio-style segment whose checked state comes only from its binding. Clicking runs the command but
/// does not check the button itself, so the segment never shows a state the view-model does not have
/// (e.g. after a cancelled UAC prompt, or when the tray changed the profile meanwhile).
/// </summary>
public sealed class StateRadioButton : RadioButton
{
    protected override void OnToggle()
    {
        // Intentionally empty: IsChecked follows the bound view-model state.
    }
}
