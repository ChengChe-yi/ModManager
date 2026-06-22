using Microsoft.UI.Xaml;

namespace ModManager.Views
{
    public sealed partial class AnnouncementWindow : Window
    {
        public AnnouncementWindow()
        {
            InitializeComponent();
            AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 480));
        }
    }
}
