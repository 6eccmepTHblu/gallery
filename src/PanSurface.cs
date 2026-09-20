using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Gallery;

/// <summary>
/// ScrollView, которому можно поменять курсор. В WinUI 3 <c>ProtectedCursor</c>
/// защищённое — публичного способа задать курсор элементу нет, наследник это
/// единственный обходной путь. Четыре строки ради affordance «здесь можно тащить».
/// </summary>
public partial class PanSurface : ScrollView
{
    public void SetCursor(InputSystemCursorShape shape) =>
        ProtectedCursor = InputSystemCursor.Create(shape);

    /// <summary>Курсор прячется присваиванием null — другого способа нет.</summary>
    public void HideCursor() => ProtectedCursor = null;
}
