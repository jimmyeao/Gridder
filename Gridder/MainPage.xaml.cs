using Gridder.ViewModels;

namespace Gridder;

public partial class MainPage : ContentPage
{
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    private async void OnButtonPressed(object sender, EventArgs e)
    {
        if (sender is View view)
        {
            Microsoft.Maui.Controls.ViewExtensions.CancelAnimations(view);
            await view.ScaleToAsync(0.92, 60, Easing.CubicIn);
        }
    }

    private async void OnButtonReleased(object sender, EventArgs e)
    {
        if (sender is View view)
        {
            await view.ScaleToAsync(1.0, 60, Easing.CubicOut);
        }
    }
}
