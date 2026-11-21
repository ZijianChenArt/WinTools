using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinTools;

public sealed partial class RoundedComboBox : ComboBox
{
    public RoundedComboBox() => DropDownOpened += (_, _) => ApplyDropDownCorners();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        ApplyDropDownCorners();
    }

    private Border? DropDownBorder => (GetTemplateChild("Popup") as Popup)?.Child as Border
        ?? GetTemplateChild("PopupBorder") as Border;

    private void ApplyDropDownCorners()
    {
        // 展开的下拉框有自己的边框，与输入面分开绘制。
        if (DropDownBorder is { } popupBorder)
            popupBorder.CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"];
    }

#if DEBUG
    internal Border? GetDropDownBorderForDiagnostics() => DropDownBorder;
#endif
}
