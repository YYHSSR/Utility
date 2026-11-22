using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Utility.App.Controls;

/// <summary>
/// A TextBlock whose text can be selected with a left-button drag and copied (Ctrl+C / right-click).
/// WPF has no public API for this, so it attaches WPF's own internal read-only TextEditor
/// (the engine behind TextBox) to the TextBlock's text container. If that internal API is not
/// available in the running WPF build, it silently behaves as a plain TextBlock.
/// </summary>
public class SelectableTextBlock : TextBlock
{
    private static readonly bool Supported;
    private static readonly ConstructorInfo? EditorConstructor;
    private static readonly PropertyInfo? EditorIsReadOnly;
    private static readonly PropertyInfo? EditorTextView;
    private static readonly PropertyInfo? TextContainerProperty;
    private static readonly PropertyInfo? ContainerTextView;

    static SelectableTextBlock()
    {
        FocusableProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(true));
        FocusVisualStyleProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(null));
        // Clicking focuses it (needed for Ctrl+C), but Tab navigation skips plain text.
        System.Windows.Input.KeyboardNavigation.IsTabStopProperty.OverrideMetadata(typeof(SelectableTextBlock), new FrameworkPropertyMetadata(false));

        try
        {
            var assembly = typeof(TextBlock).Assembly;
            var editorType = assembly.GetType("System.Windows.Documents.TextEditor", throwOnError: false);
            var containerType = assembly.GetType("System.Windows.Documents.ITextContainer", throwOnError: false);
            if (editorType is null || containerType is null)
                return;

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            EditorConstructor = editorType.GetConstructor(instance, null,
                [containerType, typeof(FrameworkElement), typeof(bool)], null);
            EditorIsReadOnly = editorType.GetProperty("IsReadOnly", instance);
            EditorTextView = editorType.GetProperty("TextView", instance);
            TextContainerProperty = typeof(TextBlock).GetProperty("TextContainer", instance);
            ContainerTextView = containerType.GetProperty("TextView", instance);
            var register = editorType.GetMethod("RegisterCommandHandlers", BindingFlags.Static | BindingFlags.NonPublic,
                null, [typeof(Type), typeof(bool), typeof(bool), typeof(bool)], null);

            if (EditorConstructor is null || EditorIsReadOnly is null || EditorTextView is null ||
                TextContainerProperty is null || ContainerTextView is null || register is null)
                return;

            // Class-level mouse/keyboard/command handlers (selection, Ctrl+C, Ctrl+A) for this type only.
            register.Invoke(null, [typeof(SelectableTextBlock), true, true, true]);
            Supported = true;
        }
        catch (Exception)
        {
            Supported = false;
        }
    }

    private object? _editor;
    private static ContextMenu? _copyMenu;

    /// <summary>Right-click: one shared "复制" menu, targeted at whichever text block was clicked.</summary>
    private void OnRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_copyMenu is null)
        {
            var item = new MenuItem
            {
                Header = Utility.App.Localization.Loc.Get("MenuCopy"),
                InputGestureText = "Ctrl+C"
            };
            item.Click += (_, _) =>
            {
                if (_copyMenu?.PlacementTarget is IInputElement target)
                    System.Windows.Input.ApplicationCommands.Copy.Execute(null, target);
            };
            _copyMenu = new ContextMenu { Items = { item } };
        }

        _copyMenu.PlacementTarget = this;
        _copyMenu.IsOpen = true;
        e.Handled = true;
    }

    public SelectableTextBlock()
    {
        if (!Supported)
            return;
        Cursor = System.Windows.Input.Cursors.IBeam;
        MouseRightButtonUp += OnRightButtonUp;
    }

    protected override void OnPreviewMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        EnsureEditor();
        base.OnPreviewMouseDown(e);
    }

    protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        EnsureEditor();
        base.OnGotKeyboardFocus(e);
    }

    private void EnsureEditor()
    {
        if (!Supported || _editor is not null)
            return;
        try
        {
            var container = TextContainerProperty!.GetValue(this);
            _editor = EditorConstructor!.Invoke([container, this, false]);
            EditorIsReadOnly!.SetValue(_editor, true);
            EditorTextView!.SetValue(_editor, ContainerTextView!.GetValue(container));
        }
        catch (Exception)
        {
            _editor = null;
        }
    }
}
